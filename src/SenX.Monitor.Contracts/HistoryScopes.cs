namespace SenX.Monitor.Contracts;

/// <summary>The parts of the frame the dashboards chart. Ids match the worker's scope table, and a test checks that.</summary>
public static class HistoryScopes
{
    public const int Frame = 1;
    public const int Update = 2;
    public const int Network = 4;
    public const int Plugins = 10;
    public const int Session = 11;
    public const int RunCallbacks = 14;
    public const int ReplicationBefore = 15;
    public const int Components = 16;
    public const int Gps = 17;
    public const int ReplicationSend = 19;
    public const int Physics = 21;
    public const int EntitiesBefore = 27;
    public const int EntitiesAfter = 33;
    public const int EntitiesSimulate = 40;
    public const int EntitiesOnce = 41;
    public const int Monitor = 42;
}
