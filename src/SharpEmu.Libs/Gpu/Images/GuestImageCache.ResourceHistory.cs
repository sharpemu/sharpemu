// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public sealed partial class GuestImageCache
{
    private const int LookupReportMatchLimit = 256;
    private const int RangeReportMatchLimit = 16384;

    private ResourceAccessHistory? _resourceHistory;
    private long _resourceHistorySequence;
    private readonly HashSet<ulong> _reportedResourceHistories = new();

    // Writers can call while the image-cache lock is held, so this history has its own lock.
    private readonly object _writerHistoryGate = new();
    private ResourceAccessHistory? _writerHistory;

    // Callers hold the image-cache lock. The trace retains all image types before a volume is known.
    private void RecordResourceHistory(string operation, ulong address, ulong size,
        Format format = Format.Undefined, Extent3D extent = default, ImageRole role = ImageRole.Texture)
    {
        if (!ImageClearTrace.Enabled || size == 0 || size > ulong.MaxValue - address) return;
        _resourceHistory ??= new ResourceAccessHistory(static () => ImageTraceRange.ActiveFilter);
        _resourceHistory.Record(CreateHistoryEntry(operation, address, size, format, extent, role));
    }

    // Command-stream and shader buffer writes that can bypass image invalidation.
    public void TraceGuestWrite(string operation, ulong address, ulong size, ulong source = 0, ulong shaderHash = 0)
    {
        if (!ImageClearTrace.Enabled || size == 0 || size > ulong.MaxValue - address) return;
        var entry = CreateHistoryEntry(operation, address, size, Format.Undefined, default, ImageRole.Texture) with
        {
            Source = source, ShaderHash = shaderHash,
        };
        lock (_writerHistoryGate)
        {
            _writerHistory ??= new ResourceAccessHistory(static () => ImageTraceRange.ActiveFilter);
            _writerHistory.Record(entry);
        }
    }

    private ResourceHistoryEntry CreateHistoryEntry(string operation, ulong address, ulong size,
        Format format, Extent3D extent, ImageRole role) =>
        new((ulong)(Interlocked.Increment(ref _resourceHistorySequence) - 1), _scheduler.CurrentTick, _metadataQueueId,
            _metadataSubmissionId, operation, address, size, format, extent, role);

    // Volumes and images inside a traced range report once, at their first lookup.
    private static bool IsTracedImage(in ImageDescription description) =>
        description.IsVolume || ImageTraceRange.Overlaps(description.Data.Address, description.Data.Size);

    private void PrintResourceHistory(in ImageRequest request)
    {
        if (!ImageClearTrace.Enabled || !IsTracedImage(request.Description) || _resourceHistory == null ||
            _reportedResourceHistories.Count >= 128 || !_reportedResourceHistories.Add(request.Description.Data.Address)) return;
        var range = request.Description.Data;
        PrintResourceHistoryReport($"image=0x{range.Address:X16}", range, LookupReportMatchLimit);
        // Volumes are rare and their producers are hard to find, so they report context too.
        if (_submissionContextReports < MaximumSubmissionContextReports &&
            (request.Description.IsVolume || ImageTraceRange.Overlaps(range.Address, range.Size)))
        {
            _submissionContextReports++;
            PrintSubmissionContext($"image=0x{range.Address:X16}");
        }
    }

    private const int MaximumSubmissionContextReports = 32;
    private int _submissionContextReports;

    // Work in this and the previous submission at any address; a producer can target another range.
    private void PrintSubmissionContext(string subject)
    {
        var queue = _metadataQueueId;
        var submission = _metadataSubmissionId;
        var first = submission == 0 ? 0 : submission - 1;
        Console.Error.WriteLine($"[GPU][TRACE] ResourceContext {subject} queue={queue} submissions={first}-{submission}");
        var printed = 0;
        void Print(string channel, ResourceAccessHistory.Ring ring)
        {
            foreach (var entry in ring.Read())
            {
                if (entry.Queue != queue || entry.Submission < first || entry.Submission > submission) continue;
                if (++printed > RangeReportMatchLimit) continue;
                Console.Error.WriteLine($"[GPU][TRACE] ResourceContext {subject} channel={channel} sequence={entry.Sequence} tick={entry.Tick} " +
                    $"submission={entry.Submission} operation={entry.Operation} address=0x{entry.Address:X16} size=0x{entry.Size:X} " +
                    $"format={entry.Format} extent={entry.Extent.Width}x{entry.Extent.Height}x{entry.Extent.Depth} role={entry.Role} " +
                    $"source=0x{entry.Source:X16} shader=0x{entry.ShaderHash:X16}");
            }
        }
        if (_resourceHistory is { } history) Print("bindings", history.Bindings);
        lock (_writerHistoryGate)
        {
            if (_writerHistory is { } writers)
            {
                Print("draw-targets", writers.DrawTargets);
                Print("shader-writes", writers.ShaderWrites);
                Print("command-writes", writers.CommandWrites);
            }
        }
        Console.Error.WriteLine($"[GPU][TRACE] ResourceContext {subject} entries={printed} omitted={Math.Max(0, printed - RangeReportMatchLimit)}");
    }

    // Recorded writers of any range, such as a predicate or label word.
    public void TraceWritersOf(string subject, ulong address, ulong size)
    {
        if (!ImageClearTrace.Enabled || size == 0 || size > ulong.MaxValue - address) return;
        using var held = _lock.Hold();
        PrintResourceHistoryReport(subject, new GuestSpan(address, size), LookupReportMatchLimit);
    }

    // A traced range also reports at shutdown, so producers after the first lookup are visible.
    private void PrintRangeResourceHistory()
    {
        if (!ImageClearTrace.Enabled || (_resourceHistory == null && _writerHistory == null)) return;
        foreach (var range in ImageTraceRange.Ranges)
            PrintResourceHistoryReport($"range=0x{range.Address:X16}+0x{range.Size:X}", range, RangeReportMatchLimit);
    }

    private void PrintResourceHistoryReport(string subject, GuestSpan range, int matchLimit)
    {
        if (_resourceHistory is { } history)
        {
            PrintResourceHistoryChannel(subject, range, "lifecycle", history.Lifecycle, matchLimit);
            PrintResourceHistoryChannel(subject, range, "bindings", history.Bindings, matchLimit);
            PrintResourceHistoryChannel(subject, range, "writes", history.Writes, matchLimit);
            PrintResourceHistoryChannel(subject, range, "gpu-writes", history.GpuWrites, matchLimit);
        }
        lock (_writerHistoryGate)
        {
            if (_writerHistory is not { } writers) return;
            PrintResourceHistoryChannel(subject, range, "command-writes", writers.CommandWrites, matchLimit);
            PrintResourceHistoryChannel(subject, range, "shader-writes", writers.ShaderWrites, matchLimit);
            PrintResourceHistoryChannel(subject, range, "draw-targets", writers.DrawTargets, matchLimit);
        }
    }

    private static void PrintResourceHistoryChannel(string subject, GuestSpan range, string channel,
        ResourceAccessHistory.Ring history, int matchLimit)
    {
        Console.Error.WriteLine($"[GPU][TRACE] ResourceHistory {subject} channel={channel} retained={history.Count} droppedNotifications={history.Dropped} coalesced={history.Coalesced}");
        var matched = 0;
        foreach (var entry in history.Read())
        {
            if (!ImageTraceRange.Overlaps([range], entry.Address, entry.Size)) continue;
            matched++;
            if (matched > matchLimit) continue;
            Console.Error.WriteLine($"[GPU][TRACE] ResourceHistory {subject} channel={channel} sequence={entry.Sequence} lastSequence={entry.LastSequence} notifications={entry.Notifications} tick={entry.Tick} " +
                $"queue={entry.Queue} submission={entry.Submission} operation={entry.Operation} address=0x{entry.Address:X16} size=0x{entry.Size:X} " +
                $"format={entry.Format} extent={entry.Extent.Width}x{entry.Extent.Height}x{entry.Extent.Depth} role={entry.Role} rangeEnvelope={entry.Notifications > 1} " +
                $"source=0x{entry.Source:X16} shader=0x{entry.ShaderHash:X16}");
        }
        Console.Error.WriteLine($"[GPU][TRACE] ResourceHistory {subject} channel={channel} matches={matched} omitted={Math.Max(0, matched - matchLimit)}");
    }
}
