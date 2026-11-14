using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SenX.Monitor.Contracts;

/// <summary>Who's reading a reply, which decides how it's laid out.</summary>
public enum ChatStyle
{
    /// <summary>Game chat: plain text in a proportional font, so columns can't line up.</summary>
    InGame,

    /// <summary>Anyone not in the game, which usually means a bot putting the reply in a Discord embed.</summary>
    Discord,
}

/// <summary>One fact: what it is, its value, and what that's measured in.</summary>
public readonly struct ChatFigure
{
    public ChatFigure(string label, string value, string unit = "")
    {
        Label = label;
        Value = value;
        Unit = unit;
    }

    public string Label { get; }

    public string Value { get; }

    public string Unit { get; }

    public static ChatFigure Fig(string label, string value, string unit = "") => new(label, value, unit);
}

/// <summary>A value over time, drawn as a small bar chart in text.</summary>
public readonly struct ChatTrend
{
    public ChatTrend(string label, IReadOnlyList<double> values, double lowest, double highest, string latest)
    {
        Label = label;
        Values = values;
        Lowest = lowest;
        Highest = highest;
        Latest = latest;
    }

    public string Label { get; }

    public IReadOnlyList<double> Values { get; }

    public double Lowest { get; }

    public double Highest { get; }

    public string Latest { get; }
}

/// <summary>Rows that share columns. Add it to a report with <see cref="ChatReport.Table"/>.</summary>
public sealed class ChatTable
{
    internal ChatTable(string nameHeader, string[] headers)
    {
        NameHeader = nameHeader;
        Headers = headers;
    }

    internal string NameHeader { get; }

    internal string[] Headers { get; }

    internal List<(int Depth, string Name, string[] Cells)> Rows { get; } = new();

    /// <summary>One row, with a cell for each column.</summary>
    public ChatTable Row(string name, params string[] cells) => Nested(0, name, cells);

    /// <summary>A row under another, as in the frame tree.</summary>
    public ChatTable Nested(int depth, string name, params string[] cells)
    {
        Rows.Add((depth, name, cells));
        return this;
    }
}

/// <summary>
/// A command's answer, kept apart from how it looks, so each reader gets a layout that suits them.
///
/// Bots differ, so this assumes little. A reply usually lands in an embed, so markdown works there, but
/// it may be joined with others and split on newlines to fit, which could cut a code block in half. So
/// no two lines may depend on each other. Each row is a padded inline code cell of figures followed by
/// its name, under a quote bar. Inline code is monospaced, so the figures line up as columns, and every
/// line stands alone, so any split is safe. Facts are stacked one under another, so no line carries
/// too much. Game chat has a proportional font, so a row there is its name with the figures under it.
/// </summary>
public sealed class ChatReport
{
    /// <summary>Below where the game and Torch start refusing long messages, which is 2048.</summary>
    public const int InGameLimit = 1000;

    /// <summary>Below the 2000 a Discord message holds, so a page fits whatever a bot puts it in.</summary>
    public const int DiscordLimit = 1800;

    /// <summary>How long a line of facts gets in game before it wraps.</summary>
    public const int InGameLineWidth = 46;

    private const string Bars = "▁▂▃▄▅▆▇█";
    private const string ZeroWidthSpace = "​";

    // Discord keeps these at the start of a name, where it would trim ordinary spaces.
    private const string EmSpace = " ";
    private const string CellGap = "  ";
    private const string RowIndent = "  ";
    private const string FiguresIndent = "   ";

    private enum Kind
    {
        Title,
        Section,
        Text,
        Note,
        Warning,
        Item,
        Facts,
        Trends,
        Table,
    }

    private sealed class Entry
    {
        public Kind Kind;
        public string Text = string.Empty;
        public ChatFigure[] Facts = Array.Empty<ChatFigure>();
        public ChatTrend[] Trends = Array.Empty<ChatTrend>();
        public ChatTable? Table;
    }

    private sealed class Piece
    {
        public Piece(string text, bool keepWithNext = false)
        {
            Text = text;
            KeepWithNext = keepWithNext;
        }

        public string Text { get; }

        public bool KeepWithNext { get; }
    }

    private readonly List<Entry> _entries = new();

    /// <summary>A plain sentence is a report too, so simple answers can stay as they are.</summary>
    public static implicit operator ChatReport(string text) => new ChatReport().Text(text);

    public ChatReport Title(string text) => Add(new Entry { Kind = Kind.Title, Text = text });

    public ChatReport Section(string heading) => Add(new Entry { Kind = Kind.Section, Text = heading });

    public ChatReport Text(string sentence) => Add(new Entry { Kind = Kind.Text, Text = sentence });

    /// <summary>A small aside, such as what a table's units are.</summary>
    public ChatReport Note(string sentence) => Add(new Entry { Kind = Kind.Note, Text = sentence });

    public ChatReport Warning(string sentence) => Add(new Entry { Kind = Kind.Warning, Text = sentence });

    /// <summary>One thing in a plain list, with nothing to line up.</summary>
    public ChatReport Item(string text) => Add(new Entry { Kind = Kind.Item, Text = text });

    /// <summary>Facts about the whole report, one under another.</summary>
    public ChatReport Facts(params ChatFigure[] facts) => Add(new Entry { Kind = Kind.Facts, Facts = facts });

    /// <summary>
    /// Values over time under a heading. These are left out in game, because the game's chat font has
    /// no bar characters.
    /// </summary>
    public ChatReport Trends(string heading, params ChatTrend[] trends) => Add(new Entry { Kind = Kind.Trends, Text = heading, Trends = trends });

    /// <summary>Starts a table. Give it a heading for the name column and one for each other column.</summary>
    public ChatTable Table(string nameHeader, params string[] headers)
    {
        var table = new ChatTable(nameHeader, headers);
        Add(new Entry { Kind = Kind.Table, Table = table });
        return table;
    }

    /// <summary>
    /// The report in pages, each one safe to send as a message. Pages only break between lines, and a
    /// heading always stays with what follows it.
    /// </summary>
    public List<string> Render(ChatStyle style)
    {
        int limit = style == ChatStyle.InGame ? InGameLimit : DiscordLimit;
        List<Piece> pieces = PiecesOf(style);
        var pages = new List<string>();
        var page = new StringBuilder();

        for (int i = 0; i < pieces.Count; i++)
        {
            string text = Cut(pieces[i].Text, limit);
            int needed = text.Length;

            for (int next = i; pieces[next].KeepWithNext && next + 1 < pieces.Count; next++)
                needed += 1 + Math.Min(pieces[next + 1].Text.Length, limit);

            if (page.Length > 0 && page.Length + 1 + Math.Min(needed, limit) > limit)
            {
                pages.Add(page.ToString());
                page.Clear();
            }

            if (page.Length > 0)
                page.Append('\n');

            page.Append(text);
        }

        if (page.Length > 0)
            pages.Add(page.ToString());

        return pages;
    }

    /// <summary>The whole report as plain text, in one piece, for the log.</summary>
    public override string ToString() =>
        string.Join(Environment.NewLine, PiecesOf(ChatStyle.InGame).Select(piece => piece.Text.Replace("\n", Environment.NewLine)));

    private ChatReport Add(Entry entry)
    {
        _entries.Add(entry);
        return this;
    }

    private List<Piece> PiecesOf(ChatStyle style)
    {
        bool discord = style == ChatStyle.Discord;
        var pieces = new List<Piece>(_entries.Count);

        foreach (Entry entry in _entries)
        {
            string text = discord ? SafeForDiscord(entry.Text) : OneLine(entry.Text);

            switch (entry.Kind)
            {
                case Kind.Title:
                    pieces.Add(new Piece(discord ? "### " + text : text, keepWithNext: true));
                    break;
                case Kind.Section:
                    pieces.Add(new Piece(discord ? "**" + text + "**" : "-- " + text + " --", keepWithNext: true));
                    break;
                case Kind.Note:
                    pieces.Add(new Piece(discord ? "*" + text + "*" : text));
                    break;
                case Kind.Warning:
                    pieces.Add(new Piece(discord ? "⚠️ **" + text + "**" : "Warning: " + text));
                    break;
                case Kind.Item:
                    pieces.Add(new Piece(discord ? "> " + text : text));
                    break;
                case Kind.Facts:
                    pieces.AddRange(discord ? DiscordFacts(entry.Facts) : GameFacts(entry.Facts));
                    break;
                case Kind.Trends:
                    if (discord)
                        pieces.AddRange(TrendPieces(text, entry.Trends));

                    break;
                case Kind.Table:
                    pieces.AddRange(discord ? DiscordTable(entry.Table!) : GameTable(entry.Table!));
                    break;
                default:
                    pieces.Add(new Piece(text));
                    break;
            }
        }

        return pieces;
    }

    private static string Cut(string text, int limit) => text.Length > limit ? text.Substring(0, limit) : text;

    private static IEnumerable<Piece> GameFacts(ChatFigure[] facts)
    {
        var line = new StringBuilder();

        foreach (ChatFigure fact in facts)
        {
            string text = OneLine(fact.Label) + " " + OneLine(fact.Value) + (fact.Unit.Length > 0 ? " " + fact.Unit : string.Empty);

            if (line.Length > 0 && line.Length + 2 + text.Length > InGameLineWidth)
            {
                yield return new Piece(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
                line.Append(", ");

            line.Append(text);
        }

        if (line.Length > 0)
            yield return new Piece(line.ToString());
    }

    private static IEnumerable<Piece> DiscordFacts(ChatFigure[] facts)
    {
        int width = facts.Length == 0 ? 0 : facts.Max(fact => fact.Label.Length);

        foreach (ChatFigure fact in facts)
        {
            string value = SafeForDiscord(fact.Value) + (fact.Unit.Length > 0 ? " " + fact.Unit : string.Empty);

            yield return new Piece(Code(fact.Label.PadRight(width)) + " **" + value + "**");
        }
    }

    private static IEnumerable<Piece> TrendPieces(string heading, ChatTrend[] trends)
    {
        if (trends.Length == 0)
            yield break;

        yield return new Piece("**" + heading + "**", keepWithNext: true);

        int width = trends.Max(trend => trend.Label.Length);

        foreach (ChatTrend trend in trends)
            yield return new Piece(Code(trend.Label.PadRight(width)) + " " + Code(BarsFor(trend)) + " " + trend.Latest);
    }

    private static string BarsFor(ChatTrend trend)
    {
        double range = trend.Highest - trend.Lowest;
        var bars = new StringBuilder(trend.Values.Count);

        foreach (double value in trend.Values)
        {
            double share = range > 0d && !double.IsNaN(value) ? (value - trend.Lowest) / range : 0d;
            int index = (int)Math.Round(Math.Max(0d, Math.Min(1d, share)) * (Bars.Length - 1));

            bars.Append(Bars[index]);
        }

        return bars.ToString();
    }

    private static IEnumerable<Piece> DiscordTable(ChatTable table)
    {
        int[] widths = ColumnWidths(table);

        if (table.Headers.Length > 0)
        {
            string header = string.Join(CellGap, table.Headers.Select((text, column) => text.PadLeft(widths[column])));

            yield return new Piece("> " + Code(header) + " " + table.NameHeader, keepWithNext: true);
        }

        foreach ((int Depth, string Name, string[] Cells) row in table.Rows)
        {
            string cells = string.Join(CellGap, Enumerable.Range(0, widths.Length).Select(column => CellOf(row.Cells, column).PadLeft(widths[column])));
            string name = string.Concat(Enumerable.Repeat(EmSpace, row.Depth)) + SafeForDiscord(row.Name);

            yield return new Piece(cells.Length == 0 ? "> " + name : "> " + Code(cells) + " " + name);
        }
    }

    private static IEnumerable<Piece> GameTable(ChatTable table)
    {
        foreach ((int Depth, string Name, string[] Cells) row in table.Rows)
        {
            string indent = string.Concat(Enumerable.Repeat(RowIndent, row.Depth));
            string figures = string.Join(CellGap, table.Headers.Select((header, column) => header + " " + CellOf(row.Cells, column)));

            yield return new Piece(figures.Length == 0
                ? indent + OneLine(row.Name)
                : indent + OneLine(row.Name) + "\n" + indent + FiguresIndent + figures);
        }
    }

    private static int[] ColumnWidths(ChatTable table)
    {
        var widths = new int[table.Headers.Length];

        for (int column = 0; column < widths.Length; column++)
        {
            widths[column] = table.Headers[column].Length;

            foreach ((int Depth, string Name, string[] Cells) row in table.Rows)
                widths[column] = Math.Max(widths[column], CellOf(row.Cells, column).Length);
        }

        return widths;
    }

    private static string CellOf(string[] cells, int column) => column < cells.Length ? OneLine(cells[column]).Replace('`', '\'') : string.Empty;

    private static string Code(string text) => "`" + text + "`";

    private static string OneLine(string text) => text.Replace('\r', ' ').Replace('\n', ' ');

    // Grid, player and script names are typed by players. An embed can't ping, but a bot might post a
    // message instead, so a name of @everyone is made harmless, and it can't pose as a link either.
    public static string SafeForDiscord(string text) =>
        OneLine(text)
            .Replace("@", "@" + ZeroWidthSpace)
            .Replace("](", "] (")
            .Replace('`', '\'');
}
