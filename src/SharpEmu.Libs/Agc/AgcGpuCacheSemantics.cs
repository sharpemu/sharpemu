// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Agc;

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu;

[Flags]
internal enum AgcGpuCacheDomain
{
    None = 0,
    Instruction = 1 << 0,
    Scalar = 1 << 1,
    Vector = 1 << 2,
    ShaderL1 = 1 << 3,
    ShaderL2 = 1 << 4,
    Color = 1 << 5,
    Depth = 1 << 6,
    Metadata = 1 << 7,
}

[Flags]
internal enum AgcGpuCacheAction
{
    None = 0,
    MakeAvailable = 1 << 0,
    MakeVisible = 1 << 1,
    Invalidate = 1 << 2,
    WriteBack = 1 << 3,
    Discard = 1 << 4,
}

internal readonly record struct AgcGpuCacheSemantics(
    AgcGpuCacheDomain Domains,
    AgcGpuCacheAction Actions,
    bool CoversAllMemory,
    GuestGpuCacheScope Scope = GuestGpuCacheScope.Shared,
    GuestGpuCacheOrder Order = GuestGpuCacheOrder.Parallel)
{
    public GuestGpuCacheOperation ToGuestOperation(
        ulong baseAddress,
        ulong sizeBytes,
        uint rawCbDbControl,
        uint rawGcrControl) => new(
            (GuestGpuCacheDomain)(int)Domains,
            (GuestGpuCacheAction)(int)Actions,
            baseAddress,
            sizeBytes,
            CoversAllMemory,
            rawCbDbControl,
            rawGcrControl,
            Scope,
            Order);
}

/// <summary>
/// Decodes the GCR control used by ACQUIRE_MEM. This layout is not the same
/// as the GCR control used by RELEASE_MEM.
/// </summary>
internal readonly record struct AcquireMemGcrControl(uint Raw)
{
    public uint InstructionInvalidate => Raw & 0x3u;
    public uint TextureCacheRange => (Raw >> 2) & 0x3u;
    public bool Gl2MetadataInvalidate => (Raw & (1u << 5)) != 0;
    public bool Gl0ScalarInvalidate => (Raw & (1u << 7)) != 0;
    public bool Gl0VectorInvalidate => (Raw & (1u << 8)) != 0;
    public bool Gl1Invalidate => (Raw & (1u << 9)) != 0;
    public bool Gl2Unshared => (Raw & (1u << 10)) != 0;
    public uint Gl2Range => (Raw >> 11) & 0x3u;
    // GFX10 ACQUIRE_MEM uses bit 13 to discard GL2 lines. The public
    // AGC enum does not expose this hardware field.
    public bool Gl2Discard => (Raw & (1u << 13)) != 0;
    public bool Gl2Invalidate => (Raw & (1u << 14)) != 0;
    public bool Gl2Writeback => (Raw & (1u << 15)) != 0;
    public uint Order => (Raw >> 16) & 0x3u;

    public bool HasResourceOperation =>
        InstructionInvalidate != 0 ||
        Gl2MetadataInvalidate ||
        Gl0ScalarInvalidate ||
        Gl0VectorInvalidate ||
        Gl1Invalidate ||
        Gl2Discard ||
        Gl2Invalidate ||
        Gl2Writeback;

    public AgcGpuCacheSemantics ToSemantics(bool sizeIsAllMemory)
    {
        var domains = AgcGpuCacheDomain.None;
        var actions = AgcGpuCacheAction.MakeVisible;

        if (InstructionInvalidate != 0)
        {
            domains |= AgcGpuCacheDomain.Instruction;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl2MetadataInvalidate)
        {
            domains |= AgcGpuCacheDomain.Metadata;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl0ScalarInvalidate)
        {
            domains |= AgcGpuCacheDomain.Scalar;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl0VectorInvalidate)
        {
            domains |= AgcGpuCacheDomain.Vector;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl1Invalidate)
        {
            domains |= AgcGpuCacheDomain.ShaderL1;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl2Discard || Gl2Invalidate || Gl2Writeback)
        {
            domains |= AgcGpuCacheDomain.ShaderL2;
        }

        if (Gl2Discard)
        {
            actions |= AgcGpuCacheAction.Discard | AgcGpuCacheAction.Invalidate;
        }

        if (Gl2Writeback)
        {
            actions |= AgcGpuCacheAction.WriteBack;
        }

        if (Gl2Invalidate)
        {
            actions |= AgcGpuCacheAction.Invalidate;
        }

        var coversAllMemory = sizeIsAllMemory ||
            InstructionInvalidate == 1u ||
            ((Gl2MetadataInvalidate || Gl0ScalarInvalidate ||
              Gl0VectorInvalidate || Gl1Invalidate) && TextureCacheRange == 0) ||
            ((Gl2Discard || Gl2Invalidate || Gl2Writeback) && Gl2Range == 0);
        var scope = Gl2Unshared
            ? GuestGpuCacheScope.Unshared
            : GuestGpuCacheScope.Shared;
        var order = Order switch
        {
            1 => GuestGpuCacheOrder.LowToHigh,
            2 => GuestGpuCacheOrder.HighToLow,
            _ => GuestGpuCacheOrder.Parallel,
        };
        return new AgcGpuCacheSemantics(
            domains,
            actions,
            coversAllMemory,
            scope,
            order);
    }
}

/// <summary>
/// Decodes the GCR control used by RELEASE_MEM. Its bit positions differ from
/// the GCR control used by ACQUIRE_MEM.
/// </summary>
internal readonly record struct ReleaseMemGcrControl(uint Raw)
{
    private const uint KnownMask = 0xF1Eu;

    public bool Gl2MetadataInvalidate => (Raw & (1u << 1)) != 0;
    public bool Gl0VectorInvalidate => (Raw & (1u << 2)) != 0;
    public bool Gl1Invalidate => (Raw & (1u << 3)) != 0;
    public bool Gl2Unshared => (Raw & (1u << 4)) != 0;
    public bool Gl2Invalidate => (Raw & (1u << 8)) != 0;
    public bool Gl2Writeback => (Raw & (1u << 9)) != 0;
    public uint Order => (Raw >> 10) & 0x3u;
    public bool IsKnownEncoding => (Raw & ~KnownMask) == 0;

    public bool HasResourceOperation =>
        Gl2MetadataInvalidate ||
        Gl0VectorInvalidate ||
        Gl1Invalidate ||
        Gl2Invalidate ||
        Gl2Writeback;

    public AgcGpuCacheSemantics ToSemantics()
    {
        var domains = AgcGpuCacheDomain.None;
        var actions = AgcGpuCacheAction.MakeAvailable;

        if (Gl2MetadataInvalidate)
        {
            domains |= AgcGpuCacheDomain.Metadata;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl0VectorInvalidate)
        {
            domains |= AgcGpuCacheDomain.Vector;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl1Invalidate)
        {
            domains |= AgcGpuCacheDomain.ShaderL1;
            actions |= AgcGpuCacheAction.Invalidate;
        }

        if (Gl2Invalidate || Gl2Writeback)
        {
            domains |= AgcGpuCacheDomain.ShaderL2;
        }

        if (Gl2Writeback)
        {
            actions |= AgcGpuCacheAction.WriteBack;
        }

        if (Gl2Invalidate)
        {
            actions |= AgcGpuCacheAction.Invalidate;
        }

        return new AgcGpuCacheSemantics(
            domains,
            actions,
            CoversAllMemory: HasResourceOperation);
    }
}

/// <summary>
/// Keeps the end-of-pipe CB/DB action separate from both GCR layouts. Unknown
/// values remain visible to diagnostics and use the conservative fallback.
/// </summary>
internal readonly record struct EndOfPipeCbDbAction(uint Raw)
{
    public const uint WritebackColorDepth = 0x04;
    public const uint WritebackInvalidateColorDepth = 0x14;
    public const uint None = 0x28;
    public const uint WritebackInvalidateDepth = 0x2B;
    public const uint WritebackInvalidateColor = 0x2D;

    public bool IsNone => Raw == None;
    public bool IsKnown => Raw is
        WritebackColorDepth or
        WritebackInvalidateColorDepth or
        None or
        WritebackInvalidateDepth or
        WritebackInvalidateColor;

    public AgcGpuCacheSemantics ToSemantics() => Raw switch
    {
        None => new AgcGpuCacheSemantics(
            AgcGpuCacheDomain.Color | AgcGpuCacheDomain.Depth,
            AgcGpuCacheAction.MakeAvailable,
            CoversAllMemory: true),
        WritebackColorDepth => new AgcGpuCacheSemantics(
            AgcGpuCacheDomain.Color | AgcGpuCacheDomain.Depth,
            AgcGpuCacheAction.MakeAvailable | AgcGpuCacheAction.WriteBack,
            CoversAllMemory: true),
        WritebackInvalidateColorDepth => new AgcGpuCacheSemantics(
            AgcGpuCacheDomain.Color | AgcGpuCacheDomain.Depth,
            AgcGpuCacheAction.MakeAvailable |
            AgcGpuCacheAction.WriteBack |
            AgcGpuCacheAction.Invalidate,
            CoversAllMemory: true),
        WritebackInvalidateDepth => new AgcGpuCacheSemantics(
            AgcGpuCacheDomain.Depth,
            AgcGpuCacheAction.MakeAvailable |
            AgcGpuCacheAction.WriteBack |
            AgcGpuCacheAction.Invalidate,
            CoversAllMemory: true),
        WritebackInvalidateColor => new AgcGpuCacheSemantics(
            AgcGpuCacheDomain.Color | AgcGpuCacheDomain.Metadata,
            AgcGpuCacheAction.MakeAvailable |
            AgcGpuCacheAction.WriteBack |
            AgcGpuCacheAction.Invalidate,
            CoversAllMemory: true),
        _ => new AgcGpuCacheSemantics(
            AgcGpuCacheDomain.Color |
            AgcGpuCacheDomain.Depth |
            AgcGpuCacheDomain.Metadata,
            AgcGpuCacheAction.MakeAvailable |
            AgcGpuCacheAction.WriteBack |
            AgcGpuCacheAction.Invalidate,
            CoversAllMemory: true),
    };
}

/// <summary>
/// Decodes the shader-completion action that can occupy the action byte in an
/// AGC release packet. It is not a CB/DB cache action.
/// </summary>
internal readonly record struct EndOfShaderAction(uint Raw)
{
    public const uint ComputeDone = 0x2F;
    public const uint PixelDone = 0x30;

    public bool IsKnown => Raw is ComputeDone or PixelDone;

    public string Name => Raw switch
    {
        ComputeDone => "cs_done",
        PixelDone => "ps_done",
        _ => "unknown",
    };

    public AgcGpuCacheSemantics ToSemantics() => new(
        AgcGpuCacheDomain.None,
        AgcGpuCacheAction.MakeAvailable,
        CoversAllMemory: true);
}

internal readonly record struct AgcReleaseMemCacheControl(
    uint RawAction,
    uint CachePolicy,
    ReleaseMemGcrControl GcrControl)
{
    public EndOfPipeCbDbAction CbDbAction => new(RawAction);
    public EndOfShaderAction ShaderAction => new(RawAction);
    public bool IsEndOfPipeAction => CbDbAction.IsKnown;
    public bool IsEndOfShaderAction => ShaderAction.IsKnown;
    public string ActionName => IsEndOfPipeAction
        ? "end_of_pipe"
        : IsEndOfShaderAction
            ? ShaderAction.Name
            : "unknown";

    public AgcGpuCacheSemantics ActionSemantics => IsEndOfPipeAction
        ? CbDbAction.ToSemantics()
        : IsEndOfShaderAction
            ? ShaderAction.ToSemantics()
            : CbDbAction.ToSemantics();
}

internal readonly record struct StandardReleaseMemCacheControl(
    uint EventType,
    uint EventIndex,
    uint CachePolicy,
    ReleaseMemGcrControl GcrControl);

public static partial class AgcExports
{
    private static readonly bool _logGpuCacheOperations = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_LOG_GPU_CACHE_OPERATIONS"),
        "1",
        StringComparison.Ordinal);
    // Set this option to 0 to skip host cache work during an A/B test.
    // Packet decode and cache-operation logging stay active.
    private static readonly bool _gpuCacheHostEffectsEnabled = !string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_GPU_CACHE_HOST_EFFECTS"),
        "0",
        StringComparison.Ordinal);
    private static long _gpuCacheHostEffectsDisabledReportCount;
    private static readonly ConcurrentDictionary<string, byte> _seenGpuCacheOperationTuples = new();

    internal static StandardReleaseMemCacheControl DecodeStandardReleaseMemCacheControl(
        uint control) => new(
            EventType: control & 0x3Fu,
            EventIndex: (control >> 8) & 0xFu,
            CachePolicy: (control >> 25) & 0x3u,
            GcrControl: new ReleaseMemGcrControl((control >> 12) & 0x1FFFu));

    internal static AgcReleaseMemCacheControl DecodeAgcReleaseMemCacheControl(
            uint actionControl,
            uint gcrAndDataControl) => new(
                RawAction: actionControl & 0xFFu,
                CachePolicy: (actionControl >> 8) & 0xFFu,
                GcrControl: new ReleaseMemGcrControl(gcrAndDataControl & 0xFFFFu));

    private static void TraceUniqueGpuCacheOperation(
        string packet,
        SubmittedDcbState state,
        uint cbDbAction,
        uint gcrControl,
        ulong baseAddress,
        ulong sizeBytes,
        AgcGpuCacheSemantics semantics,
        string nextConsumer = "pending",
        string completionAction = "none")
    {
        var key =
            $"{packet}|{state.QueueName}|{cbDbAction:X}|{gcrControl:X}|" +
            $"{baseAddress:X}|{sizeBytes:X}|{semantics.Domains}|{semantics.Actions}|" +
            $"{semantics.CoversAllMemory}|{nextConsumer}|{completionAction}";
        if (!_seenGpuCacheOperationTuples.TryAdd(key, 0))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] agc.cache_operation packet={packet} queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} completion={completionAction} " +
            $"cbdb=0x{cbDbAction:X8} " +
            $"gcr=0x{gcrControl:X8} base=0x{baseAddress:X16} " +
            $"size=0x{sizeBytes:X16} scope={(semantics.CoversAllMemory ? "all" : "range")} " +
            $"domains={semantics.Domains} actions={semantics.Actions} " +
            $"next={nextConsumer}");
    }
}
