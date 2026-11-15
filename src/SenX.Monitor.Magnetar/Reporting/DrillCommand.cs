using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using PluginSdk.Commands;
using SenX.Monitor.Contracts;
using SenX.Monitor.Magnetar.Patching;

namespace SenX.Monitor.Magnetar.Reporting;

/// <summary>Chat's way into a drill-down. Game thread, on demand only.</summary>
internal static class DrillCommand
{
    private const int DefaultSeconds = 10;
    private const int MatchesShown = 10;

    private static List<DrillRow> s_lastRows = new();
    private static List<DrillRoot> s_lastMatches = new();

    public static ChatReport Run(CommandContext context, string[] words)
    {
        int seconds = DefaultSeconds;
        var name = new List<string>();

        foreach (string word in words)
        {
            if (word.StartsWith("--secs=", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(word.Substring("--secs=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out seconds))
                    return "--secs needs a number.";
            }
            else if (word.StartsWith("--", StringComparison.Ordinal))
            {
                return "Unknown option '" + word + "'.";
            }
            else
            {
                name.Add(word);
            }
        }

        if (name.Count == 0)
            return "Give part of a method's name, such as !monitor drill MyPlugin.Update, or a row number from the last result.";

        string text = string.Join(" ", name);

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int row))
            return FromNumber(context, row, seconds);

        List<DrillRoot> matches = DrillDown.Roots(text);

        if (matches.Count == 0)
            return "Nothing matches '" + text + "'. Plugin updates, session components, patched methods and whatever timing has seen can be searched.";

        if (matches.Count == 1)
            return Start(context, matches[0].Id, matches[0].Name, seconds);

        s_lastMatches = matches;
        s_lastRows = new List<DrillRow>();

        var report = new ChatReport().Text(matches.Count + " match. Pick one with !monitor drill N:");

        foreach ((DrillRoot match, int index) in matches.Take(MatchesShown).Select((match, index) => (match, index)))
            report.Item("[" + index + "] " + match.Name + " (" + match.Owner + ")");

        return report;
    }

    private static string FromNumber(CommandContext context, int row, int seconds)
    {
        if (row >= 0 && row < s_lastRows.Count)
        {
            DrillRow call = s_lastRows[row];

            return call.Id.Length == 0
                ? call.Name + " has no code of its own to look inside, such as an interface or a delegate."
                : Start(context, call.Id, call.Name, seconds);
        }

        return row >= 0 && row < s_lastMatches.Count
            ? Start(context, s_lastMatches[row].Id, s_lastMatches[row].Name, seconds)
            : "There's no row " + row + " in the last result.";
    }

    private static string Start(CommandContext context, string id, string name, int seconds)
    {
        DrillStartResponse started = DrillDown.Start(new DrillStartRequest { Id = id, Seconds = seconds }, result => Report(context, result));

        return started.Started
            ? "Timing what " + name + " calls, for " + seconds + " s. It comes out by itself."
            : started.Message ?? "The drill-down didn't start.";
    }

    private static void Report(CommandContext context, DrillResultResponse result)
    {
        s_lastRows = result.Rows;
        s_lastMatches = new List<DrillRoot>();

        try
        {
            ChatReply.Send(context, LagReport.Drill(result));
        }
        catch (Exception exception)
        {
            MonitorLog.Error("Monitor could not report a drill-down.", exception);
        }
    }
}
