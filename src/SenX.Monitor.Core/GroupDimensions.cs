using System;

namespace SenX.Monitor.Core;

/// <summary>Reads a list's name as a person would type it.</summary>
public static class GroupDimensions
{
    /// <summary>The list with this name, using the same words as the commands. None if there isn't one.</summary>
    public static GroupDimension Named(string name)
    {
        switch (name.ToLowerInvariant())
        {
            case "grids":
                return GroupDimension.Grids;
            case "blocktypes":
                return GroupDimension.BlockTypes;
            case "blocks":
            case "definitions":
                return GroupDimension.Definitions;
            case "players":
            case "owners":
                return GroupDimension.Owners;
            case "factions":
                return GroupDimension.Factions;
            case "entities":
            case "entitytypes":
                return GroupDimension.EntityTypes;
            case "session":
                return GroupDimension.Session;
            case "logic":
                return GroupDimension.Logic;
            case "callbacks":
                return GroupDimension.Callbacks;
            case "scripts":
                return GroupDimension.Scripts;
            case "mods":
                return GroupDimension.Mods;
            case "rpcs":
                return GroupDimension.Rpcs;
            case "rpcssent":
                return GroupDimension.RpcsSent;
            default:
                return GroupDimension.None;
        }
    }
}
