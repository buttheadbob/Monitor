using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Serialization;
using SenX.Monitor.Services;

namespace SenX.Monitor.Torch.Web;

/// <summary>
/// A very small web server inside the plugin, so Torch gets the same full boards as Quasar without
/// Node.js, WPF charts or anything else to install. It starts when an admin first opens a board and only
/// listens on this computer. A random part of every address keeps out anything that doesn't have the link,
/// and all it does is read what's been collected. It's a plain socket rather than HttpListener because that needs a
/// URL reservation or admin rights, and http.sys isn't there under Proton.
/// </summary>
internal sealed class BoardWebServer : IDisposable
{
    // A browser opens a few connections at once, and some of them sit idle in case they're wanted.
    private const int Threads = 4;
    private const int MostRequestBytes = 8192;
    private const int ReadTimeoutMs = 2000;
    private const int RequestDeadlineMs = 5000;
    private const int AcceptRetryMs = 200;
    private const int WriteTimeoutMs = 15000;
    private const int WriteBuffer = 16384;
    private const int SecretBytes = 16;
    private const int MostFaultsLogged = 5;
    private const string FilesRoute = "files/";

    private static readonly UTF8Encoding s_utf8 = new(false);

    private static readonly JsonSerializer s_json = JsonSerializer.Create(new JsonSerializerSettings
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        NullValueHandling = NullValueHandling.Ignore,
    });

    private static readonly byte[] s_headerEnd = Encoding.ASCII.GetBytes("\r\n\r\n");

    private readonly MonitorServices _services;
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _secret = NewSecret();
    private readonly string _page = BoardPage.Html(FilesRoute);
    private readonly int _port;

    private volatile bool _stopping;
    private int _faults;

    public BoardWebServer(MonitorServices services)
    {
        _services = services;
        _listener.Start();
        _port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        for (int thread = 0; thread < Threads; thread++)
            new Thread(Serve) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "Monitor board" }.Start();
    }

    /// <summary>The board's address, which only works until the server restarts.</summary>
    public string Address => FormattableString.Invariant($"http://127.0.0.1:{_port}/{_secret}/{BoardPage.PageRoute}");

    public void Dispose()
    {
        _stopping = true;
        _listener.Stop();
    }

    private static string NewSecret()
    {
        var bytes = new byte[SecretBytes];

        using (var random = RandomNumberGenerator.Create())
            random.GetBytes(bytes);

        return string.Concat(bytes.Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
    }

    private void Serve()
    {
        while (!_stopping)
        {
            TcpClient client;

            try
            {
                client = _listener.AcceptTcpClient();
            }
            catch (Exception exception) when (exception is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // Stopping does this. So can a connection dropping as it's accepted, and that's no reason to lose a thread.
                if (_stopping)
                    return;

                Thread.Sleep(AcceptRetryMs);
                continue;
            }

            try
            {
                using (client)
                {
                    client.NoDelay = true;
                    client.ReceiveTimeout = ReadTimeoutMs;
                    client.SendTimeout = WriteTimeoutMs;
                    Answer(client.GetStream());
                }
            }
            catch (IOException)
            {
                // The browser went away, or an idle connection timed out. Nothing was wrong.
            }
            catch (Exception exception)
            {
                if (Interlocked.Increment(ref _faults) <= MostFaultsLogged)
                    MonitorLogSink.Write?.Invoke("A Monitor board request failed.", exception);
            }
        }
    }

    private void Answer(NetworkStream stream)
    {
        string? head = ReadHead(stream);

        if (head == null)
            return;

        if (!Parse(head, out string method, out string target, out string host))
        {
            Plain(stream, "400 Bad Request");
            return;
        }

        if (method != "GET")
        {
            Plain(stream, "405 Method Not Allowed");
            return;
        }

        // A web page can point a name it owns at 127.0.0.1, and this is what stops that reaching here.
        if (host != FormattableString.Invariant($"127.0.0.1:{_port}") && host != FormattableString.Invariant($"localhost:{_port}"))
        {
            Plain(stream, "403 Forbidden");
            return;
        }

        string start = "/" + _secret + "/";

        if (!target.StartsWith(start, StringComparison.Ordinal))
        {
            Plain(stream, "404 Not Found");
            return;
        }

        int query = target.IndexOf('?');
        string path = (query < 0 ? target : target.Substring(0, query)).Substring(start.Length);

        if (path == BoardPage.PageRoute)
            Send(stream, s_utf8.GetBytes(_page), "text/html; charset=utf-8");
        else if (path == BoardPage.DataRoute)
            SendBoard(stream, query < 0 ? string.Empty : target.Substring(query + 1));
        else if (path.StartsWith(FilesRoute, StringComparison.Ordinal) && BoardFiles.Find(path.Substring(FilesRoute.Length)) is { } file)
            Send(stream, file.Bytes, file.Type);
        else
            Plain(stream, "404 Not Found");
    }

    private static string? ReadHead(NetworkStream stream)
    {
        var buffer = new byte[MostRequestBytes];
        int length = 0;
        long started = Environment.TickCount;

        while (length < buffer.Length)
        {
            // The read timeout starts over with every byte, so a client trickling them in could keep a thread for hours.
            if (Environment.TickCount - started > RequestDeadlineMs)
                return null;

            int read = stream.Read(buffer, length, buffer.Length - length);

            if (read == 0)
                return null;

            length += read;

            if (EndsHead(buffer, length))
                return Encoding.ASCII.GetString(buffer, 0, length);
        }

        return null;
    }

    private static bool EndsHead(byte[] buffer, int length)
    {
        for (int at = 0; at <= length - s_headerEnd.Length; at++)
        {
            if (buffer[at] == s_headerEnd[0] && buffer[at + 1] == s_headerEnd[1] && buffer[at + 2] == s_headerEnd[2] && buffer[at + 3] == s_headerEnd[3])
                return true;
        }

        return false;
    }

    private static bool Parse(string head, out string method, out string target, out string host)
    {
        method = string.Empty;
        target = string.Empty;
        host = string.Empty;

        // Only the headers count, and whatever follows the blank line isn't one.
        string[] lines = head.Substring(0, head.IndexOf("\r\n\r\n", StringComparison.Ordinal)).Split(["\r\n"], StringSplitOptions.None);
        string[] request = lines[0].Split(' ');

        if (request.Length != 3)
            return false;

        method = request[0];
        target = request[1];

        foreach (string line in lines)
        {
            if (host.Length == 0 && line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                host = line.Substring("Host:".Length).Trim();
        }

        return true;
    }

    private void SendBoard(NetworkStream stream, string query)
    {
        Dictionary<string, string> values = Query(query);

        BoardRequest.Answer<BoardResponse> answer = BoardRequest.Build(
            _services.Boards,
            _services.Store,
            _services.Collector,
            _services.Settings,
            values.GetValueOrDefault("server"),
            Whole(values, "minutes"),
            Long(values, "from"),
            Long(values, "to"));

        if (answer.Value is not { } board)
        {
            Plain(stream, answer.Status == 404 ? "404 Not Found" : "429 Too Many Requests");
            return;
        }

        // Written straight to the connection, so a big board never becomes one big string in the game's process.
        WriteHeader(stream, "200 OK", "application/json; charset=utf-8", null);

        using var writer = new StreamWriter(stream, s_utf8, WriteBuffer, leaveOpen: true);
        using var json = new JsonTextWriter(writer) { CloseOutput = false };
        s_json.Serialize(json, board);
    }

    private static Dictionary<string, string> Query(string query)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (string pair in query.Split('&'))
        {
            int at = pair.IndexOf('=');

            if (at > 0)
                values[pair.Substring(0, at)] = Uri.UnescapeDataString(pair.Substring(at + 1).Replace('+', ' '));
        }

        return values;
    }

    private static int? Whole(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;

    private static long? Long(Dictionary<string, string> values, string name) =>
        values.TryGetValue(name, out string? text) && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) ? value : null;

    private static void Plain(NetworkStream stream, string status)
    {
        byte[] body = Encoding.ASCII.GetBytes(status);
        WriteHeader(stream, status, "text/plain", body.Length);
        stream.Write(body, 0, body.Length);
    }

    private static void Send(NetworkStream stream, byte[] body, string type)
    {
        WriteHeader(stream, "200 OK", type, body.Length);
        stream.Write(body, 0, body.Length);
    }

    private static void WriteHeader(NetworkStream stream, string status, string type, int? length)
    {
        var text = new StringBuilder();

        text.Append("HTTP/1.1 ").Append(status).Append("\r\n");
        text.Append("Content-Type: ").Append(type).Append("\r\n");

        if (length.HasValue)
            text.Append("Content-Length: ").Append(length.Value.ToString(CultureInfo.InvariantCulture)).Append("\r\n");

        // The address holds the secret, so it isn't handed on to anywhere else.
        text.Append("Cache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n");

        byte[] bytes = Encoding.ASCII.GetBytes(text.ToString());
        stream.Write(bytes, 0, bytes.Length);
    }
}

/// <summary>The board's files, built into the plugin so there's nothing else to ship. Only these names can be asked for.</summary>
internal static class BoardFiles
{
    private static readonly Dictionary<string, string> s_types = new(StringComparer.Ordinal)
    {
        ["board.js"] = "text/javascript; charset=utf-8",
        ["board.css"] = "text/css; charset=utf-8",
        ["lib/uplot/uPlot.iife.min.js"] = "text/javascript; charset=utf-8",
        ["lib/uplot/uPlot.min.css"] = "text/css; charset=utf-8",
    };

    private static readonly Dictionary<string, (byte[] Bytes, string Type)> s_loaded = new(StringComparer.Ordinal);

    public static (byte[] Bytes, string Type)? Find(string name)
    {
        if (!s_types.TryGetValue(name, out string? type))
            return null;

        lock (s_loaded)
        {
            if (!s_loaded.TryGetValue(name, out (byte[] Bytes, string Type) file))
            {
                file = (Read(name), type);
                s_loaded[name] = file;
            }

            return file;
        }
    }

    private static byte[] Read(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MonitorBoard/" + name)
            ?? throw new InvalidOperationException("The plugin is missing " + name + ".");

        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
