using System.IO.Compression;
using SenX.Monitor.Api;
using SenX.Monitor.Components.Charts;
using SenX.Monitor.Services;
using Xunit;

namespace SenX.Monitor.Ui.Tests;

public sealed class ChartImageTests
{
    private const int Width = 640;
    private const int Height = 320;
    private const int BackgroundRed = 0x18;

    private static readonly double[] s_times = Enumerable.Range(0, 120).Select(i => 1793000000d + (i * 60d)).ToArray();

    private static double?[] Steady(double value) => s_times.Select(_ => (double?)value).ToArray();

    private static ChartData Line(params double?[][] lines) =>
        new("c", " ms", false, null, 0d, s_times, lines.Select((values, index) => new ChartLine("Line " + index, ChartColors.At(index), values)).ToList());

    private static byte[] Draw(ChartData chart, int width = Width, int height = Height) =>
        ChartImage.Render(chart, "Title", width, height, [])!;

    private sealed record Decoded(int Width, int Height, byte[] Pixels)
    {
        public (byte Red, byte Green, byte Blue) At(int x, int y)
        {
            int at = ((y * Width) + x) * 3;
            return (Pixels[at], Pixels[at + 1], Pixels[at + 2]);
        }
    }

    // A reader of its own, so this proves the file against the format and not against the code that wrote it.
    private static Decoded Decode(byte[] file)
    {
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, file.Take(8).ToArray());

        int at = 8;
        int width = 0;
        int height = 0;
        using var packed = new MemoryStream();
        var kinds = new List<string>();

        while (at < file.Length)
        {
            int length = (file[at] << 24) | (file[at + 1] << 16) | (file[at + 2] << 8) | file[at + 3];
            string kind = System.Text.Encoding.ASCII.GetString(file, at + 4, 4);

            uint crc = 0xFFFFFFFFU;

            for (int i = at + 4; i < at + 8 + length; i++)
            {
                crc ^= file[i];

                for (int bit = 0; bit < 8; bit++)
                    crc = (crc & 1U) != 0U ? 0xEDB88320U ^ (crc >> 1) : crc >> 1;
            }

            int end = at + 8 + length;
            uint stored = (uint)((file[end] << 24) | (file[end + 1] << 16) | (file[end + 2] << 8) | file[end + 3]);
            Assert.Equal(crc ^ 0xFFFFFFFFU, stored);

            if (kind == "IHDR")
            {
                width = (file[at + 8] << 24) | (file[at + 9] << 16) | (file[at + 10] << 8) | file[at + 11];
                height = (file[at + 12] << 24) | (file[at + 13] << 16) | (file[at + 14] << 8) | file[at + 15];
                Assert.Equal(8, file[at + 16]);
                Assert.Equal(2, file[at + 17]);
            }
            else if (kind == "IDAT")
            {
                packed.Write(file, at + 8, length);
            }

            kinds.Add(kind);
            at = end + 4;
        }

        Assert.Equal(["IHDR", "IDAT", "IEND"], kinds);

        packed.Position = 0L;
        using var inflate = new ZLibStream(packed, CompressionMode.Decompress);
        using var raw = new MemoryStream();
        inflate.CopyTo(raw);

        byte[] rows = raw.ToArray();
        int rowBytes = width * 3;
        Assert.Equal((rowBytes + 1) * height, rows.Length);

        var pixels = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            Assert.Equal(2, rows[y * (rowBytes + 1)]);

            for (int i = 0; i < rowBytes; i++)
                pixels[(y * rowBytes) + i] = (byte)(rows[(y * (rowBytes + 1)) + 1 + i] + (y == 0 ? 0 : pixels[((y - 1) * rowBytes) + i]));
        }

        return new Decoded(width, height, pixels);
    }

    [Fact]
    public void A_picture_is_a_valid_png_of_the_size_asked_for()
    {
        Decoded image = Decode(Draw(Line(Steady(5d))));

        Assert.Equal(Width, image.Width);
        Assert.Equal(Height, image.Height);
        Assert.Equal(BackgroundRed, image.At(0, 0).Red);
    }

    [Theory]
    [InlineData(10, 10, ChartImage.LeastWidth, ChartImage.LeastHeight)]
    [InlineData(99999, 99999, ChartImage.MostWidth, ChartImage.MostHeight)]
    public void A_size_is_kept_within_limits(int width, int height, int expectedWidth, int expectedHeight)
    {
        Decoded image = Decode(Draw(Line(Steady(5d)), width, height));

        Assert.Equal(expectedWidth, image.Width);
        Assert.Equal(expectedHeight, image.Height);
    }

    [Fact]
    public void Nothing_to_draw_is_null()
    {
        Assert.Null(ChartImage.Render(new ChartData("c", string.Empty, false, null, 0d, [], []), "Title", Width, Height, []));
        Assert.Null(ChartImage.Render(new ChartData("c", string.Empty, false, null, 0d, [1d], [new ChartLine("A", "monitor-c-0", [1d])]), "Title", Width, Height, []));
    }

    [Fact]
    public void A_line_is_really_drawn()
    {
        byte[] drawn = Draw(Line(Steady(5d)));
        byte[] higher = Draw(Line(Steady(9d)));

        Assert.NotEqual(drawn, higher);
    }

    [Fact]
    public void A_gap_leaves_the_chart_empty_there()
    {
        double?[] broken = Steady(5d);

        for (int i = 40; i < 80; i++)
            broken[i] = null;

        Decoded solid = Decode(Draw(Line(Steady(5d))));
        Decoded gap = Decode(Draw(Line(broken)));

        int column = Width / 2;
        int differs = 0;

        for (int y = 0; y < Height; y++)
        {
            if (solid.At(column, y) != gap.At(column, y))
                differs++;
        }

        Assert.True(differs > 20, differs + " pixels differ.");
    }

    [Fact]
    public void A_bad_number_is_a_gap_and_not_a_failure()
    {
        double?[] values = Steady(5d);
        values[10] = double.NaN;
        values[11] = double.PositiveInfinity;

        Assert.NotNull(ChartImage.Render(Line(values), "Title", Width, Height, []));
    }

    [Fact]
    public void An_all_zero_chart_still_draws()
    {
        Decode(Draw(Line(Steady(0d))));
    }

    [Fact]
    public void Text_outside_ascii_doesnt_fail()
    {
        var chart = new ChartData("c", " ms", false, null, 0d, s_times, [new ChartLine("Мой корабль 飞船 \t", "monitor-c-0", Steady(1d))]);

        Assert.NotNull(ChartImage.Render(chart, "Ñame", Width, Height, []));
    }

    [Fact]
    public void A_legend_too_long_for_the_picture_is_cut_and_not_drawn_outside()
    {
        var lines = Enumerable.Range(0, 40).Select(i => new ChartLine(new string('x', 60) + i, ChartColors.At(i), Steady(i + 1d))).ToList();

        Decode(ChartImage.Render(new ChartData("c", " ms", true, null, 0d, s_times, lines), "Title", ChartImage.LeastWidth, ChartImage.LeastHeight, [])!);
    }

    [Fact]
    public void Every_chart_has_a_picture_or_says_it_has_nothing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "monitor-tests-" + Guid.NewGuid().ToString("N"));

        try
        {
            var settings = new MonitorSettings(directory);
            var store = new HistoryStore(directory, settings, Microsoft.Extensions.Logging.Abstractions.NullLogger<HistoryStore>.Instance);
            var charts = new ChartBuilder(store, new ScriptedServers());
            DateTime to = DateTime.UtcNow;

            foreach (MonitorChart chart in Enum.GetValues<MonitorChart>())
                Assert.Null(ChartImages.Draw(charts, "nobody", chart, to.AddHours(-1d), to, Width, Height));
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
