using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using SenX.Monitor.Contracts;
using Xunit;
using static SenX.Monitor.Contracts.ChatFigure;

namespace SenX.Monitor.Core.Tests;

public class ChatReportTests
{
    private const string ZeroWidthSpace = "​";
    private const string EmSpace = " ";

    private static ChatReport Sample()
    {
        var report = new ChatReport()
            .Title("Grids")
            .Facts(Fig("Measured", "10.0", "s"), Fig("Sim speed", "0.97"))
            .Note("Main and Work are ms/f, Peak is ms.")
            .Warning("The server paused.")
            .Section("Top grids");

        report.Table("Grid", "Main", "Frame", "Work", "Peak")
            .Row("Big Ship", "3.20", "22.5%", "1.10", "9.04")
            .Row("Small", "10.75", "2.3%", "0.40", "14.80");

        return report;
    }

    private static ChatReport ManyRows(int rows, string name = "Big Ship")
    {
        var report = new ChatReport().Title("Diagnosis");

        for (int section = 0; section < 8; section++)
        {
            report.Section("Section " + section);
            ChatTable table = report.Table("Grid", "Main", "Frame", "Work", "Peak");

            for (int row = 0; row < rows; row++)
                table.Row(name + " " + section + "." + row, "3.20", "4.1%", "1.10", "9.00");
        }

        return report;
    }

    // What the bots read so far do with a run of replies: join them, then cut them up on newlines.
    private static List<string> SplitLikeABot(IEnumerable<string> replies)
    {
        const int MaxLength = 2000;

        var chunks = new List<string>();
        var current = new StringBuilder();

        foreach (string line in string.Join("\n", replies).Split('\n'))
        {
            if (current.Length + line.Length + 1 > MaxLength && current.Length > 0)
            {
                chunks.Add(current.ToString().TrimEnd());
                current.Clear();
            }

            current.Append(line).Append('\n');
        }

        if (current.Length > 0)
            chunks.Add(current.ToString().TrimEnd());

        return chunks;
    }

    [Fact]
    public void In_game_a_row_is_a_name_with_its_figures_under_it()
    {
        Assert.Equal(
            "Grids\n" +
            "Measured 10.0 s, Sim speed 0.97\n" +
            "Main and Work are ms/f, Peak is ms.\n" +
            "Warning: The server paused.\n" +
            "-- Top grids --\n" +
            "Big Ship\n" +
            "   Main 3.20  Frame 22.5%  Work 1.10  Peak 9.04\n" +
            "Small\n" +
            "   Main 10.75  Frame 2.3%  Work 0.40  Peak 14.80",
            Assert.Single(Sample().Render(ChatStyle.InGame)));
    }

    [Fact]
    public void For_discord_figures_line_up_ahead_of_each_name()
    {
        Assert.Equal(
            "### Grids\n" +
            "`Measured ` **10.0 s**\n" +
            "`Sim speed` **0.97**\n" +
            "*Main and Work are ms/f, Peak is ms.*\n" +
            "⚠️ **The server paused.**\n" +
            "**Top grids**\n" +
            "> ` Main  Frame  Work   Peak` Grid\n" +
            "> ` 3.20  22.5%  1.10   9.04` Big Ship\n" +
            "> `10.75   2.3%  0.40  14.80` Small",
            Assert.Single(Sample().Render(ChatStyle.Discord)));
    }

    [Fact]
    public void No_line_of_a_table_is_long()
    {
        // The figures are short whatever the name is, which is what keeps a row readable.
        string[] rows = Assert.Single(Sample().Render(ChatStyle.InGame)).Split('\n');

        Assert.All(rows, line => Assert.True(line.Length <= ChatReport.InGameLineWidth + 4, line));
    }

    [Fact]
    public void Every_line_of_figures_is_the_same_width()
    {
        string page = Assert.Single(Sample().Render(ChatStyle.Discord));

        int[] widths = page.Split('\n').Where(line => line.StartsWith("> `", StringComparison.Ordinal)).Select(line => line.LastIndexOf('`')).Distinct().ToArray();

        Assert.Single(widths);
    }

    [Fact]
    public void Facts_stack_one_under_another_for_discord()
    {
        string page = Assert.Single(new ChatReport().Facts(Fig("One", "1"), Fig("Two", "2"), Fig("Three", "3")).Render(ChatStyle.Discord));

        Assert.Equal("`One  ` **1**\n`Two  ` **2**\n`Three` **3**", page);
    }

    [Fact]
    public void Facts_wrap_in_game_so_no_line_gets_long()
    {
        ChatReport report = new ChatReport().Facts(
            Fig("Measured", "10.0", "s"),
            Fig("Frames", "598"),
            Fig("Sim speed", "0.97"),
            Fig("Frame avg", "14.20", "ms"),
            Fig("Frame worst", "41.60", "ms"));

        string[] lines = Assert.Single(report.Render(ChatStyle.InGame)).Split('\n');

        Assert.True(lines.Length > 1);
        Assert.All(lines, line => Assert.True(line.Length <= ChatReport.InGameLineWidth, line));
    }

    [Fact]
    public void Nothing_depends_on_the_line_before_it()
    {
        // A bot can join what arrives together and cut it on newlines, so a code block could be split.
        foreach (string page in ManyRows(40).Render(ChatStyle.Discord))
            Assert.DoesNotContain("```", page);
    }

    [Fact]
    public void Every_line_survives_a_bot_splitting_on_newlines()
    {
        List<string> chunks = SplitLikeABot(ManyRows(40).Render(ChatStyle.Discord));

        Assert.True(chunks.Count > 1);

        foreach (string chunk in chunks)
        {
            Assert.True(chunk.Length <= 2000);

            // Inline code that's opened on a line is closed on it, so no cut can leave one hanging.
            Assert.All(chunk.Split('\n'), line => Assert.True(line.Count(character => character == '`') % 2 == 0, line));
        }
    }

    [Fact]
    public void A_nested_row_is_indented_in_both()
    {
        var report = new ChatReport();

        report.Table("Part", "ms/f")
            .Row("Update", "9.00")
            .Nested(1, "Session", "6.00");

        Assert.Equal("Update\n   ms/f 9.00\n  Session\n     ms/f 6.00", Assert.Single(report.Render(ChatStyle.InGame)));
        Assert.Equal("> `ms/f` Part\n> `9.00` Update\n> `6.00` " + EmSpace + "Session", Assert.Single(report.Render(ChatStyle.Discord)));
    }

    [Fact]
    public void A_trend_draws_its_values_as_bars()
    {
        ChatReport report = new ChatReport().Trends("Last hour", new ChatTrend("Sim", new[] { 0d, 0.5d, 1d }, 0d, 1d, "1.00"));

        Assert.Equal("**Last hour**\n`Sim` `▁▅█` 1.00", Assert.Single(report.Render(ChatStyle.Discord)));
    }

    [Fact]
    public void A_trend_is_left_out_in_game()
    {
        // The game's chat font has no bar characters.
        ChatReport report = new ChatReport().Text("Before.").Trends("Last hour", new ChatTrend("Sim", new[] { 1d }, 0d, 1d, "1.00"));

        Assert.Equal("Before.", Assert.Single(report.Render(ChatStyle.InGame)));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-5d)]
    [InlineData(50d)]
    public void A_value_outside_a_trends_range_cant_break_it(double value)
    {
        ChatReport report = new ChatReport().Trends("Last hour", new ChatTrend("Sim", new[] { value }, 0d, 1d, "?"));

        Assert.Single(report.Render(ChatStyle.Discord));
    }

    [Fact]
    public void A_trend_with_no_range_stays_flat()
    {
        ChatReport report = new ChatReport().Trends("Last hour", new ChatTrend("Sim", new[] { 1d, 1d }, 1d, 1d, "1.00"));

        Assert.Contains("▁▁", Assert.Single(report.Render(ChatStyle.Discord)));
    }

    [Theory]
    [InlineData(ChatStyle.InGame, ChatReport.InGameLimit)]
    [InlineData(ChatStyle.Discord, ChatReport.DiscordLimit)]
    public void A_long_report_comes_in_pages_under_the_limit(ChatStyle style, int limit)
    {
        List<string> pages = ManyRows(40).Render(style);

        Assert.True(pages.Count > 1);
        Assert.All(pages, page => Assert.True(page.Length <= limit, page.Length.ToString()));
    }

    [Theory]
    [InlineData(ChatStyle.InGame)]
    [InlineData(ChatStyle.Discord)]
    public void Nothing_is_lost_or_moved_between_pages(ChatStyle style)
    {
        string whole = string.Join("\n", ManyRows(40).Render(style));
        int previous = -1;

        foreach (string name in Enumerable.Range(0, 8).SelectMany(section => Enumerable.Range(0, 40).Select(row => "Big Ship " + section + "." + row + "\n")))
        {
            // The last row of all has no line after it in the Discord layout.
            int at = whole.IndexOf(name, StringComparison.Ordinal);

            if (at < 0)
                at = whole.IndexOf(name.TrimEnd(), StringComparison.Ordinal);

            Assert.True(at > previous, name);
            previous = at;
        }
    }

    [Theory]
    [InlineData(ChatStyle.InGame, "-- ")]
    [InlineData(ChatStyle.Discord, "**")]
    public void A_heading_is_never_left_at_the_end_of_a_page(ChatStyle style, string heading)
    {
        foreach (string page in ManyRows(40).Render(style))
        {
            string last = page.Split('\n').Last();

            Assert.False(last.StartsWith(heading, StringComparison.Ordinal), last);
        }
    }

    [Fact]
    public void A_tables_headings_stay_with_its_first_row()
    {
        foreach (string page in ManyRows(40).Render(ChatStyle.Discord))
            Assert.DoesNotContain("Frame  Work  Peak` Grid", page.Split('\n').Last());
    }

    [Theory]
    [InlineData(ChatStyle.InGame, ChatReport.InGameLimit)]
    [InlineData(ChatStyle.Discord, ChatReport.DiscordLimit)]
    public void A_line_too_long_for_a_page_is_cut_and_the_rest_still_arrives(ChatStyle style, int limit)
    {
        List<string> pages = new ChatReport().Item(new string('x', limit * 3)).Text("After it.").Render(style);

        Assert.All(pages, page => Assert.True(page.Length <= limit));
        Assert.Contains("After it.", string.Join("\n", pages));
    }

    [Theory]
    [InlineData("@everyone")]
    [InlineData("@here")]
    [InlineData("<@123456789>")]
    [InlineData("<@&123456789>")]
    public void A_players_name_cant_ping_a_discord(string name)
    {
        var report = new ChatReport().Title(name).Section(name).Text(name).Note(name).Warning(name).Item(name);
        report.Table("Grid", "Main").Row(name, "1.00");

        string page = Assert.Single(report.Render(ChatStyle.Discord));

        Assert.DoesNotContain(name, page, StringComparison.Ordinal);
        Assert.Contains("@" + ZeroWidthSpace, page, StringComparison.Ordinal);
    }

    [Fact]
    public void A_players_name_is_left_as_typed_in_game()
    {
        Assert.Equal("@everyone", Assert.Single(new ChatReport().Item("@everyone").Render(ChatStyle.InGame)));
    }

    [Fact]
    public void A_name_cant_pose_as_a_link()
    {
        Assert.DoesNotContain("](", Assert.Single(new ChatReport().Item("[free nitro](http://example.com)").Render(ChatStyle.Discord)));
    }

    [Fact]
    public void A_backtick_in_a_name_or_a_cell_cant_break_the_figures()
    {
        var report = new ChatReport();
        report.Table("Grid", "Main").Row("Ship`", "1`00");

        string row = Assert.Single(report.Render(ChatStyle.Discord)).Split('\n').Last();

        Assert.Equal("> `1'00` Ship'", row);
    }

    [Theory]
    [InlineData(ChatStyle.InGame)]
    [InlineData(ChatStyle.Discord)]
    public void A_name_cant_start_a_line_of_its_own(ChatStyle style)
    {
        string page = Assert.Single(new ChatReport().Item("Ship\n### Fake heading\r\n").Render(style));

        Assert.DoesNotContain('\n', page);
        Assert.DoesNotContain('\r', page);
    }

    [Fact]
    public void A_row_can_be_short_of_cells()
    {
        var report = new ChatReport();
        report.Table("Grid", "Main", "Work").Row("Ship", "1.00");

        Assert.Equal("Ship\n   Main 1.00  Work ", Assert.Single(report.Render(ChatStyle.InGame)));
        Assert.Contains("> `1.00      ` Ship", Assert.Single(report.Render(ChatStyle.Discord)));
    }

    [Theory]
    [InlineData(ChatStyle.InGame)]
    [InlineData(ChatStyle.Discord)]
    public void A_plain_sentence_is_a_report(ChatStyle style)
    {
        ChatReport report = "Try again shortly.";

        Assert.Equal("Try again shortly.", Assert.Single(report.Render(style)));
    }

    [Fact]
    public void An_empty_report_sends_nothing()
    {
        Assert.Empty(new ChatReport().Render(ChatStyle.Discord));
    }

    [Fact]
    public void The_log_gets_the_whole_report_in_one_piece()
    {
        string text = ManyRows(40).ToString();

        Assert.True(text.Length > ChatReport.DiscordLimit);
        Assert.Contains("Big Ship 7.39", text);
        Assert.DoesNotContain("`", text);
    }
}
