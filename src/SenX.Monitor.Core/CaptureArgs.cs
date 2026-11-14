using System;
using System.Collections.Generic;

namespace SenX.Monitor.Core;

/// <summary>Which options make sense for a command, beyond how long and how many.</summary>
[System.Flags]
public enum CaptureOptions
{
    None = 0,

    /// <summary>Its rows belong to a grid or an owner, so it can be limited to one.</summary>
    Filters = 1,

    Gps = 2,
    Details = 4,
}

/// <summary>Options for a lag-finding command, such as <c>--secs=30 --top=5 --this</c>.</summary>
public sealed class CaptureArgs
{
    public const int DefaultSeconds = 10;
    public const int MaxSeconds = 600;

    /// <summary>The most a read from history can cover, because rollups last about an hour.</summary>
    public const int MaxWindowSeconds = 3600;

    public const int DefaultTop = 10;
    public const int MaxTop = 50;

    public int Seconds { get; private set; } = DefaultSeconds;

    public int Top { get; private set; } = DefaultTop;

    /// <summary>Only this grid. Zero means every grid.</summary>
    public long GridId { get; private set; }

    /// <summary>Only the grid the caller is controlling.</summary>
    public bool This { get; private set; }

    public string? Player { get; private set; }

    public string? Faction { get; private set; }

    /// <summary>Only blocks whose class name contains this.</summary>
    public string? BlockType { get; private set; }

    /// <summary>Only the grids' own updates, leaving out their blocks.</summary>
    public bool NoBlocks { get; private set; }

    /// <summary>Adds owner and faction to each grid.</summary>
    public bool Details { get; private set; }

    /// <summary>Sends the caller a GPS marker for each grid.</summary>
    public bool Gps { get; private set; }

    /// <summary>A cluster from the last physics result to list the grids of.</summary>
    public int? Inspect { get; private set; }

    /// <summary>A cluster from the last physics result to go to.</summary>
    public int? TakeMe { get; private set; }

    /// <summary>Finishes going to clusters, clearing their GPS markers.</summary>
    public bool TakeMeDone { get; private set; }

    /// <summary>True when this limits what a run counts.</summary>
    public bool Narrows => GridId != 0L || This || Player != null || Faction != null || NoBlocks || BlockType != null;

    /// <summary>Says why these options don't suit a command. A filter that can't apply must not be quietly ignored.</summary>
    public string? ProblemWith(CaptureOptions allowed)
    {
        if (Narrows && (allowed & CaptureOptions.Filters) == 0)
            return "This list can't be limited to a grid, player, faction or block type, because its rows aren't tied to one.";

        if (Gps && (allowed & CaptureOptions.Gps) == 0)
            return "--gps only works with grids.";

        return Details && (allowed & CaptureOptions.Details) == 0 ? "--details only works with grids and diagnose." : null;
    }

    /// <summary>True when an option only makes sense for the physics command.</summary>
    public bool ForPhysicsOnly => Inspect.HasValue || TakeMe.HasValue || TakeMeDone;

    /// <summary>Reads options for a command that reads history, which only takes how far back to look.</summary>
    public static CaptureArgs? ParseWindow(IReadOnlyList<string> words, out string? error)
    {
        var args = new CaptureArgs();
        error = null;

        foreach (string word in words)
        {
            if (!word.StartsWith("--secs=", System.StringComparison.OrdinalIgnoreCase))
            {
                error = "This only takes --secs=N, because it reads history for the whole server.";
                return null;
            }

            if (!int.TryParse(word.Substring("--secs=".Length), out int seconds) || seconds < 1 || seconds > MaxWindowSeconds)
            {
                error = "--secs must be from 1 to " + MaxWindowSeconds.ToString() + ".";
                return null;
            }

            args.Seconds = seconds;
        }

        return args;
    }

    /// <summary>Reads options. An option that isn't known is an error, so a typo can't quietly measure the wrong thing.</summary>
    public static CaptureArgs? Parse(IReadOnlyList<string> words, out string? error)
    {
        var args = new CaptureArgs();
        error = null;

        foreach (string word in words)
        {
            int equals = word.IndexOf('=');
            string name = equals < 0 ? word : word.Substring(0, equals);
            string value = equals < 0 ? string.Empty : word.Substring(equals + 1);

            switch (name.ToLowerInvariant())
            {
                case "--secs":
                    if (!int.TryParse(value, out int seconds) || seconds < 1 || seconds > MaxSeconds)
                        error = "--secs must be from 1 to " + MaxSeconds.ToString() + ".";
                    else
                        args.Seconds = seconds;
                    break;

                case "--top":
                    if (!int.TryParse(value, out int top) || top < 1 || top > MaxTop)
                        error = "--top must be from 1 to " + MaxTop.ToString() + ".";
                    else
                        args.Top = top;
                    break;

                case "--grid":
                case "--entity":
                    if (!long.TryParse(value, out long grid) || grid == 0L)
                        error = name + " needs a grid's entity id.";
                    else
                        args.GridId = grid;
                    break;

                case "--player":
                    if (value.Length == 0)
                        error = "--player needs a name.";
                    else
                        args.Player = value;
                    break;

                case "--faction":
                    if (value.Length == 0)
                        error = "--faction needs a tag.";
                    else
                        args.Faction = value;
                    break;

                case "--block":
                    if (value.Length == 0)
                        error = "--block needs part of a block class name, such as --block=Drill.";
                    else
                        args.BlockType = value;
                    break;

                case "--this":
                    args.This = true;
                    break;

                case "--noblocks":
                    args.NoBlocks = true;
                    break;

                case "--details":
                case "--detail":
                    args.Details = true;
                    break;

                case "--gps":
                    args.Gps = true;
                    break;

                case "--inspect":
                    if (!int.TryParse(value, out int inspect) || inspect < 0)
                        error = "--inspect needs a cluster number from the last result.";
                    else
                        args.Inspect = inspect;
                    break;

                case "--takeme":
                    if (value.Equals("done", System.StringComparison.OrdinalIgnoreCase))
                        args.TakeMeDone = true;
                    else if (!int.TryParse(value, out int takeMe) || takeMe < 0)
                        error = "--takeme needs a cluster number from the last result, or done.";
                    else
                        args.TakeMe = takeMe;
                    break;

                default:
                    error = "Unknown option '" + word + "'.";
                    break;
            }

            if (error != null)
                return null;
        }

        if (args.NoBlocks && args.BlockType != null)
        {
            error = "--block and --noblocks can't be used together.";
            return null;
        }

        return args;
    }

    /// <summary>
    /// The words typed after a command on Torch. Torch finds where they start by searching the whole line for the first one,
    /// so the "on" of "monitor everything on" is found in "monitor", and the rest of the command arrives as words too.
    /// </summary>
    public static string[] WordsAfter(string command, IReadOnlyList<string> words)
    {
        for (int start = 0; start < command.Length; start++)
        {
            string[] tail = command.Substring(start).Split(' ');

            if (StartsWith(words, tail))
                return From(words, tail.Length);
        }

        return From(words, 0);
    }

    private static bool StartsWith(IReadOnlyList<string> words, string[] start)
    {
        if (start.Length > words.Count)
            return false;

        for (int at = 0; at < start.Length; at++)
        {
            if (!string.Equals(words[at], start[at], StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static string[] From(IReadOnlyList<string> words, int first)
    {
        var rest = new string[words.Count - first];

        for (int at = 0; at < rest.Length; at++)
            rest[at] = words[first + at];

        return rest;
    }
}
