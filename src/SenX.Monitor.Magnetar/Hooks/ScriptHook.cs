using System;
using System.Runtime.CompilerServices;
using Sandbox.Game.Entities.Blocks;
using Sandbox.ModAPI;
using SenX.Monitor.Core;

namespace SenX.Monitor.Magnetar.Hooks;

/// <summary>Programmable block runs, one row per block. Few calls and often expensive, so only the group's guard ever samples these.</summary>
public static unsafe class ScriptHook
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static MyProgrammableBlock.ScriptTerminationReason Run(
        MyProgrammableBlock block,
        Action<IMyGridProgram> action,
        out string response)
    {
        byte gate = Gates.Read(Site.Script);
        return gate == Gates.Closed
            ? block.RunSandboxedProgramAction(action, out response)
            : RunOpen(block, action, out response, gate);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static MyProgrammableBlock.ScriptTerminationReason RunOpen(
        MyProgrammableBlock block,
        Action<IMyGridProgram> action,
        out string response,
        int shift)
    {
        if (!Sampler.Hit(block.EntityId, Site.Script, shift))
            return block.RunSandboxedProgramAction(action, out response);

        long started = Clock.Now();
        MyProgrammableBlock.ScriptTerminationReason result = block.RunSandboxedProgramAction(action, out response);
        long elapsed = Clock.Now() - started;

        EntitySlot* created = Recorder.AddEntity(Site.Script, Phase.Other, block.EntityId, elapsed, shift);
        if (created != null)
            SlotMeta.Fill(created, block);

        return result;
    }
}
