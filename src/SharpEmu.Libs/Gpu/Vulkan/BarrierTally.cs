// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Diagnostics;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Vulkan;

internal enum BarrierSource
{
    Other,
    Requested,
    Global,
    ScopeEnd,
    Image,
    SliceImage,
    Copy,
    CopyRun,
    Fill,
    Upload,
    Sweep,
    IndirectDispatch,
    HostRead,
    GlobalDataShare,
    Download,
    Tiler,
    Count,
}

public enum ScopeBreak
{
    Image,
    SliceImage,
    StoringDraw,
    GuestFlush,
    DeferredFlush,
    GlobalDataShare,
    Count,
}

internal static class BarrierTally
{
    private const PipelineStageFlags FullSourceGraphics = PipelineStageFlags.AllGraphicsBit | PipelineStageFlags.ComputeShaderBit;
    private const PipelineStageFlags DrainingDestination = PipelineStageFlags.TopOfPipeBit | PipelineStageFlags.AllCommandsBit |
        PipelineStageFlags.TransferBit | PipelineStageFlags.HostBit | PipelineStageFlags.DrawIndirectBit | PipelineStageFlags.AllGraphicsBit;

    private static readonly ThreadCounter[] _calls = NewCounters((int)BarrierSource.Count);
    private static readonly ThreadCounter[] _fullSource = NewCounters((int)BarrierSource.Count);
    private static readonly ThreadCounter[] _drains = NewCounters((int)BarrierSource.Count);
    private static readonly ThreadCounter[] _scopeBreaks = NewCounters((int)ScopeBreak.Count);

    public static readonly ThreadCounter ScopeEnds = new();
    public static readonly ThreadCounter ScopeEndCalls = new();
    public static readonly ThreadCounter ScopeEndImages = new();
    public static readonly ThreadCounter DiscardedBuffers = new();
    public static readonly ThreadCounter DiscardedBarriers = new();
    public static readonly ThreadCounter DiscardAvoided = new();
    public static readonly ThreadCounter IdleRequests = new();
    public static readonly ThreadCounter PrologueUploads = new();
    public static readonly ThreadCounter PrologueUploadPairs = new();
    public static readonly ThreadCounter HostReadRecorded = new();
    public static readonly ThreadCounter HostReadSkipped = new();
    public static readonly ThreadCounter ReadReadBarriers = new();
    public static readonly ThreadCounter AttachmentRepeats = new();

    private static ThreadCounter[] NewCounters(int count)
    {
        var counters = new ThreadCounter[count];
        for (var index = 0; index < count; index++)
        {
            counters[index] = new ThreadCounter();
        }

        return counters;
    }

    public static bool IsFullSource(PipelineStageFlags source) =>
        (source & PipelineStageFlags.AllCommandsBit) != 0 || (source & FullSourceGraphics) == FullSourceGraphics;

    public static void Count(BarrierSource source, PipelineStageFlags sourceStages, PipelineStageFlags destinationStages)
    {
        _calls[(int)source].Increment();
        if (!IsFullSource(sourceStages))
        {
            return;
        }

        _fullSource[(int)source].Increment();
        if ((destinationStages & DrainingDestination) != 0)
        {
            _drains[(int)source].Increment();
        }
    }

    public static void CountAvoidableImageBarrier(in Images.ImageAccessState state, AccessFlags access)
    {
        if (state.RepeatsAttachmentAccess(access))
        {
            AttachmentRepeats.Increment();
        }
        else if (state.ReadsAfterRead(access))
        {
            ReadReadBarriers.Increment();
        }
    }

    public static void CountScopeBreak(ScopeBreak reason)
    {
        if (GpuSyncProfile.Enabled)
        {
            _scopeBreaks[(int)reason].Increment();
        }
    }

    public static string TakeReport()
    {
        var calls = 0L;
        var fullSource = 0L;
        var drains = 0L;
        var sources = new List<string>();
        for (var index = 0; index < (int)BarrierSource.Count; index++)
        {
            var sourceCalls = _calls[index].Take();
            var sourceFull = _fullSource[index].Take();
            var sourceDrains = _drains[index].Take();
            calls += sourceCalls;
            fullSource += sourceFull;
            drains += sourceDrains;
            if (sourceCalls != 0)
            {
                sources.Add(FormattableString.Invariant($"{(BarrierSource)index}:{sourceCalls}/{sourceFull}/{sourceDrains}"));
            }
        }

        var breaks = new string[(int)ScopeBreak.Count];
        for (var index = 0; index < breaks.Length; index++)
        {
            breaks[index] = FormattableString.Invariant($"{(ScopeBreak)index}:{_scopeBreaks[index].Take()}");
        }

        return FormattableString.Invariant($"[PERF][BARRIERS] calls={calls} full_source={fullSource} drains={drains} ") +
            "by_source=" + string.Join(",", sources) + " scope_break_by_barrier=" + string.Join(",", breaks) + " " +
            FormattableString.Invariant($"scope_end=scopes:{ScopeEnds.Take()},calls:{ScopeEndCalls.Take()},images:{ScopeEndImages.Take()} ") +
            FormattableString.Invariant($"discarded=buffers:{DiscardedBuffers.Take()},barriers:{DiscardedBarriers.Take()},avoided:{DiscardAvoided.Take()} ") +
            FormattableString.Invariant($"idle_requests={IdleRequests.Take()} prologue_uploads={PrologueUploads.Take()}/pairs:{PrologueUploadPairs.Take()} ") +
            FormattableString.Invariant($"host_read=recorded:{HostReadRecorded.Take()},skipped:{HostReadSkipped.Take()} ") +
            FormattableString.Invariant($"read_read={ReadReadBarriers.Take()} attachment_repeats={AttachmentRepeats.Take()}");
    }
}
