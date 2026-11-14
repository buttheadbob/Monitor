using System.IO.Compression;

namespace SenX.Monitor.Services;

/// <summary>
/// A plain RGB picture with smooth lines and a PNG encoder. It's all managed code, because a server may have no
/// graphics libraries, System.Drawing is Windows only, and anything native is one more thing to ship per platform.
/// </summary>
internal sealed class ImageCanvas
{
    private static readonly byte[] s_signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly uint[] s_crcTable = MakeCrcTable();

    private readonly byte[] _pixels;
    private readonly byte[] _mask;

    public ImageCanvas(int width, int height, int background)
    {
        Width = width;
        Height = height;
        _pixels = new byte[width * height * 3];
        _mask = new byte[width * height];

        Fill(0, 0, width, height, background, 1d);
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>One colour as 0xRRGGBB.</summary>
    public (byte Red, byte Green, byte Blue) PixelAt(int x, int y)
    {
        int at = ((y * Width) + x) * 3;
        return (_pixels[at], _pixels[at + 1], _pixels[at + 2]);
    }

    public void Fill(int left, int top, int width, int height, int color, double alpha)
    {
        for (int y = Math.Max(top, 0); y < Math.Min(top + height, Height); y++)
        {
            for (int x = Math.Max(left, 0); x < Math.Min(left + width, Width); x++)
                Blend(x, y, color, alpha);
        }
    }

    /// <summary>
    /// Fills between two heights in one column of pixels, with its ends softened so a slope looks smooth. The alpha
    /// fades from <paramref name="alphaAtTop"/> at <paramref name="fadeTop"/> to <paramref name="alphaAtBottom"/> at <paramref name="fadeBottom"/>.
    /// </summary>
    public void FillColumn(int x, double top, double bottom, int color, double alphaAtTop, double alphaAtBottom, double fadeTop, double fadeBottom)
    {
        if (x < 0 || x >= Width || bottom <= top)
            return;

        for (int y = Math.Max((int)Math.Floor(top), 0); y < Math.Min((int)Math.Ceiling(bottom), Height); y++)
        {
            double covered = Math.Min(y + 1d, bottom) - Math.Max(y, top);
            double along = fadeBottom <= fadeTop ? 0d : Math.Min(Math.Max((y - fadeTop) / (fadeBottom - fadeTop), 0d), 1d);

            Blend(x, y, color, covered * (alphaAtTop + ((alphaAtBottom - alphaAtTop) * along)));
        }
    }

    /// <summary>A line through these points, drawn all at once, so its joins aren't darker than the rest of it.</summary>
    public void Stroke(IReadOnlyList<(double X, double Y)> points, double thickness, int color, double alpha = 1d)
    {
        if (points.Count < 2)
            return;

        var area = new Area(this);

        for (int point = 1; point < points.Count; point++)
            Mark(points[point - 1], points[point], thickness, ref area);

        Composite(ref area, color, alpha);
    }

    /// <summary>Several separate strokes as one shape, which is how a letter is drawn.</summary>
    public void StrokeAll(IEnumerable<IReadOnlyList<(double X, double Y)>> strokes, double thickness, int color)
    {
        var area = new Area(this);

        foreach (IReadOnlyList<(double X, double Y)> stroke in strokes)
        {
            for (int point = 1; point < stroke.Count; point++)
                Mark(stroke[point - 1], stroke[point], thickness, ref area);
        }

        Composite(ref area, color, 1d);
    }

    public void Dashed((double X, double Y) from, (double X, double Y) to, double thickness, int color, double dash = 6d, double gap = 4d)
    {
        double length = Math.Sqrt(((to.X - from.X) * (to.X - from.X)) + ((to.Y - from.Y) * (to.Y - from.Y)));

        for (double at = 0d; at < length; at += dash + gap)
        {
            double start = at / length;
            double end = Math.Min(at + dash, length) / length;

            Stroke(
                [
                    (from.X + ((to.X - from.X) * start), from.Y + ((to.Y - from.Y) * start)),
                    (from.X + ((to.X - from.X) * end), from.Y + ((to.Y - from.Y) * end)),
                ],
                thickness,
                color);
        }
    }

    private struct Area(ImageCanvas canvas)
    {
        public int Left = canvas.Width;
        public int Right = -1;
        public int Top = canvas.Height;
        public int Bottom = -1;
    }

    // Each pixel keeps how much of it the line covers, measured by its distance from the line.
    private void Mark((double X, double Y) from, (double X, double Y) to, double thickness, ref Area area)
    {
        double half = thickness / 2d;
        int left = Math.Max((int)Math.Floor(Math.Min(from.X, to.X) - half - 1d), 0);
        int right = Math.Min((int)Math.Ceiling(Math.Max(from.X, to.X) + half + 1d), Width - 1);
        int top = Math.Max((int)Math.Floor(Math.Min(from.Y, to.Y) - half - 1d), 0);
        int bottom = Math.Min((int)Math.Ceiling(Math.Max(from.Y, to.Y) + half + 1d), Height - 1);

        double runX = to.X - from.X;
        double runY = to.Y - from.Y;
        double squared = (runX * runX) + (runY * runY);

        for (int y = top; y <= bottom; y++)
        {
            for (int x = left; x <= right; x++)
            {
                double pixelX = x + 0.5d;
                double pixelY = y + 0.5d;
                double along = squared <= 0d ? 0d : Math.Min(Math.Max((((pixelX - from.X) * runX) + ((pixelY - from.Y) * runY)) / squared, 0d), 1d);
                double offX = pixelX - (from.X + (along * runX));
                double offY = pixelY - (from.Y + (along * runY));
                double covered = half + 0.5d - Math.Sqrt((offX * offX) + (offY * offY));

                if (covered <= 0d)
                    continue;

                byte value = (byte)(Math.Min(covered, 1d) * 255d);
                int at = (y * Width) + x;

                if (value > _mask[at])
                    _mask[at] = value;
            }
        }

        area.Left = Math.Min(area.Left, left);
        area.Right = Math.Max(area.Right, right);
        area.Top = Math.Min(area.Top, top);
        area.Bottom = Math.Max(area.Bottom, bottom);
    }

    private void Composite(ref Area area, int color, double alpha)
    {
        for (int y = area.Top; y <= area.Bottom; y++)
        {
            for (int x = area.Left; x <= area.Right; x++)
            {
                int at = (y * Width) + x;
                byte value = _mask[at];

                if (value == 0)
                    continue;

                Blend(x, y, color, value / 255d * alpha);
                _mask[at] = 0;
            }
        }
    }

    private void Blend(int x, int y, int color, double alpha)
    {
        int at = ((y * Width) + x) * 3;

        _pixels[at] = Mix(_pixels[at], (color >> 16) & 0xFF, alpha);
        _pixels[at + 1] = Mix(_pixels[at + 1], (color >> 8) & 0xFF, alpha);
        _pixels[at + 2] = Mix(_pixels[at + 2], color & 0xFF, alpha);
    }

    private static byte Mix(byte under, int over, double alpha) => (byte)(under + ((over - under) * alpha) + 0.5d);

    /// <summary>The picture as a PNG file.</summary>
    public byte[] ToPng()
    {
        using var file = new MemoryStream();

        file.Write(s_signature, 0, s_signature.Length);

        var header = new byte[13];
        WriteBigEndian(header, 0, (uint)Width);
        WriteBigEndian(header, 4, (uint)Height);
        header[8] = 8; // Bits a channel
        header[9] = 2; // Red, green and blue
        WriteChunk(file, "IHDR", header, header.Length);

        byte[] packed = Packed();
        WriteChunk(file, "IDAT", packed, packed.Length);
        WriteChunk(file, "IEND", [], 0);

        return file.ToArray();
    }

    // Each row is stored as its difference from the row above, which leaves a chart's flat colours mostly zeros.
    private byte[] Packed()
    {
        const byte UpFilter = 2;
        int rowBytes = Width * 3;
        var row = new byte[rowBytes + 1];
        row[0] = UpFilter;

        using var packed = new MemoryStream();

        // The two bytes zlib starts with: deflate with a 32 KB window, at the default level.
        packed.WriteByte(0x78);
        packed.WriteByte(0x9C);

        uint low = 1U;
        uint high = 0U;

        using (var deflate = new DeflateStream(packed, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < Height; y++)
            {
                int start = y * rowBytes;

                for (int i = 0; i < rowBytes; i++)
                    row[i + 1] = (byte)(_pixels[start + i] - (y == 0 ? 0 : _pixels[start + i - rowBytes]));

                deflate.Write(row, 0, row.Length);

                // Adler-32, which is the check zlib ends with.
                foreach (byte value in row)
                {
                    low = (low + value) % 65521U;
                    high = (high + low) % 65521U;
                }
            }
        }

        var check = new byte[4];
        WriteBigEndian(check, 0, (high << 16) | low);
        packed.Write(check, 0, check.Length);

        return packed.ToArray();
    }

    private static void WriteChunk(Stream file, string kind, byte[] data, int length)
    {
        var size = new byte[4];
        WriteBigEndian(size, 0, (uint)length);
        file.Write(size, 0, size.Length);

        byte[] name = System.Text.Encoding.ASCII.GetBytes(kind);
        file.Write(name, 0, name.Length);
        file.Write(data, 0, length);

        uint crc = 0xFFFFFFFFU;

        foreach (byte value in name)
            crc = s_crcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);

        for (int i = 0; i < length; i++)
            crc = s_crcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);

        var end = new byte[4];
        WriteBigEndian(end, 0, crc ^ 0xFFFFFFFFU);
        file.Write(end, 0, end.Length);
    }

    private static void WriteBigEndian(byte[] target, int at, uint value)
    {
        target[at] = (byte)(value >> 24);
        target[at + 1] = (byte)(value >> 16);
        target[at + 2] = (byte)(value >> 8);
        target[at + 3] = (byte)value;
    }

    private static uint[] MakeCrcTable()
    {
        var table = new uint[256];

        for (uint entry = 0U; entry < 256U; entry++)
        {
            uint value = entry;

            for (int bit = 0; bit < 8; bit++)
                value = (value & 1U) != 0U ? 0xEDB88320U ^ (value >> 1) : value >> 1;

            table[entry] = value;
        }

        return table;
    }
}
