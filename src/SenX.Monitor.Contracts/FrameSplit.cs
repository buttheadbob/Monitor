using System;

namespace SenX.Monitor.Contracts;

/// <summary>Splits a frame into parts that are each counted once. Quasar and the worker share this, so their answers match.</summary>
public static class FrameSplit
{
    /// <summary>A frame's budget. Past this, the server can't keep 60 frames a second, so sim speed falls.</summary>
    public const double BudgetMs = 1000d / 60d;

    public const string IdleName = "Idle";

    /// <summary>The parts, in the order <see cref="Of"/> gives them.</summary>
    public static readonly string[] Names =
    {
        "Physics", "Entities", "Session components", "Network", "Replication", "Callbacks", "GPS", "Plugins", "Monitor", "Other", IdleName,
    };

    /// <summary>A frame's time by part, from a way to read a scope. Children come out of their parents, so nothing counts twice.</summary>
    public static double[] Of(Func<int, double> scope)
    {
        double physics = scope(HistoryScopes.Physics);
        double entities = scope(HistoryScopes.EntitiesBefore) + scope(HistoryScopes.EntitiesAfter)
                          + scope(HistoryScopes.EntitiesSimulate) + scope(HistoryScopes.EntitiesOnce);
        double session = Math.Max(0d, scope(HistoryScopes.Components) - physics - entities);
        double network = scope(HistoryScopes.Network);
        double replication = scope(HistoryScopes.ReplicationBefore) + scope(HistoryScopes.ReplicationSend);
        double callbacks = scope(HistoryScopes.RunCallbacks);
        double gps = scope(HistoryScopes.Gps);
        double plugins = scope(HistoryScopes.Plugins);
        double monitor = scope(HistoryScopes.Monitor);

        double frame = scope(HistoryScopes.Frame);
        double known = physics + entities + session + network + replication + callbacks + gps + plugins + monitor;

        return new[] { physics, entities, session, network, replication, callbacks, gps, plugins, monitor, Math.Max(0d, frame - known), Math.Max(0d, BudgetMs - frame) };
    }
}
