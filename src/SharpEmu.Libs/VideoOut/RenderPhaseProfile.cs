// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Self-time accounting for the render thread, enabled with
/// SHARPEMU_PROFILE_RENDER=1 or the unified performance profile. The existing
/// videoout counters report how much work was done (draws, pipelines, SPIR-V)
/// but not where the render thread's second went, which is the number that
/// decides whether a low frame rate is the emulator recording commands, the
/// GPU executing them, or neither.
///
/// Scopes nest: entering a phase suspends the enclosing one and resumes it on
/// dispose, so a <see cref="Phase.QueueSubmit"/> inside
/// <see cref="Phase.Flush"/> is never counted twice.
/// </summary>
internal static class RenderPhaseProfile
{
    internal enum Phase
    {
        /// <summary>Outside any measured phase — loop overhead.</summary>
        Unattributed = 0,
        /// <summary>Parked because no command stream or newer flip exists.</summary>
        Idle,
        /// <summary>Blocked on the frame slot's fence: the GPU is behind.</summary>
        FrameSlotWait,
        /// <summary>Reaping completed guest submissions (fence polls).</summary>
        Collect,
        /// <summary>Selecting the presentation to show this iteration.</summary>
        TakePresentation,
        /// <summary>Running one slice of the guest command stream.</summary>
        CommandStream,
        CommandMemorySync,
        CommandMemoryRead,
        CommandGpuWait,
        CommandMemoryTransfer,
        CommandEndOfPipe,
        CommandDrawTranslation,
        CommandDispatchTranslation,
        GeometrySnapshotValidation,
        CommandDrawStateCreation,
        DrawVertexShaderSetup,
        DrawVertexEvaluation,
        DrawBindingAssembly,
        Draw,
        /// <summary>Closing and submitting the batched guest command buffer.</summary>
        Flush,
        /// <summary>vkQueueSubmit itself.</summary>
        QueueSubmit,
        /// <summary>vkAcquireNextImageKHR.</summary>
        Acquire,
        /// <summary>Recording + submitting the presentation command buffer.</summary>
        Present,
        /// <summary>vkQueuePresentKHR.</summary>
        QueuePresent,
        BufferFaults,
        ImageReadback,
        ImageCollect,
        BufferCollect,
        ImageLookup,
        ImageAcquire,
        ImageCreate,
        ImageDelete,
        ImageRefresh,
        ImageUpload,
        ImageDownload,
        ImageOverlap,
        ImageTracking,
        ImageTiling,
        ImageTransitions,
        DrawResources,
        BufferResources,
        DescriptorSetup,
        PipelineSetup,
        DrawRecording,
        ResourceDestroy,
        ImageVersions,
        QueueRelay,
        WindowLoop,
        WindowEvents,
        CursorUpdate,
        GamepadPoll,
        WindowState,
        WindowDelay,
        QueueContext,
        PresentationPreparation,
        Count,
    }

    public static readonly bool Enabled =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"),
            "1",
            StringComparison.Ordinal);

    private static readonly double _reportSeconds =
        double.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_RENDER_REPORT_S"),
            System.Globalization.CultureInfo.InvariantCulture,
            out var seconds) && seconds > 0
            ? seconds
            : 5.0;

    private static readonly long[] _ticks = new long[(int)Phase.Count];
    private static readonly long[] _entries = new long[(int)Phase.Count];
    private static long _frames;
    internal enum CommandReadKind { Header, Payload, RegisterTable, Operand32, Operand64, Other, Count }
    private static readonly long[] _commandReadCalls = new long[(int)CommandReadKind.Count];
    private static readonly long[] _commandReadBytes = new long[(int)CommandReadKind.Count];

    internal static void RecordCommandRead(CommandReadKind kind, int bytes)
    {
        if (!Enabled || _scopeDepth == 0)
        {
            return;
        }

        _commandReadCalls[(int)kind]++;
        _commandReadBytes[(int)kind] += bytes;
    }
    internal static bool ImageUploadDetailsEnabled => Enabled && _scopeDepth > 0;

    private readonly record struct ImageUploadKey(ulong Address, uint Width, uint Height, uint Depth,
        uint Layers, uint Levels, uint Format, uint TileMode, string Reason,
        string UploadPath, ulong WriteAddress, ulong WriteSize);

    internal sealed class ImageUploadStatistics
    {
        public long Count;
        public ulong SourceBytes;
        public long WatchTicks;
        public long SourceTicks;
        public long RecordTicks;

        public void Add(ulong sourceBytes, long watchTicks, long sourceTicks, long recordTicks)
        {
            Count++;
            SourceBytes += sourceBytes;
            WatchTicks += watchTicks;
            SourceTicks += sourceTicks;
            RecordTicks += recordTicks;
        }
    }

    private static readonly Dictionary<ImageUploadKey, ImageUploadStatistics> _imageUploads = new();
    private static ImageUploadStatistics _otherImageUploads = new();

    internal static void RecordImageUpload(in SharpEmu.Libs.Gpu.Images.ImageDescription description,
        string reason, long watchTicks, long sourceTicks, long recordTicks,
        string uploadPath = "unknown", ulong writeAddress = 0, ulong writeSize = 0)
    {
        if (!ImageUploadDetailsEnabled)
        {
            return;
        }

        var key = new ImageUploadKey(description.Data.Address, description.Extent.Width,
            description.Extent.Height, description.Extent.Depth, description.Resources.Layers,
            description.Resources.Levels, (uint)description.PixelFormat, (uint)description.TileMode, reason,
            uploadPath, writeAddress, writeSize);
        if (!_imageUploads.TryGetValue(key, out var statistics))
        {
            // Bound diagnostic memory when a frame creates many distinct images.
            if (_imageUploads.Count >= 256)
            {
                _otherImageUploads.Add(description.Data.Size, watchTicks, sourceTicks, recordTicks);
                return;
            }

            statistics = new ImageUploadStatistics();
            _imageUploads.Add(key, statistics);
        }

        statistics.Add(description.Data.Size, watchTicks, sourceTicks, recordTicks);
    }

    private static string FormatImageUploadStatistics(ImageUploadStatistics statistics) =>
        $"uploads={statistics.Count} source_bytes={statistics.SourceBytes} " +
        $"watch_ms={statistics.WatchTicks * 1000.0 / Stopwatch.Frequency:F2} " +
        $"source_ms={statistics.SourceTicks * 1000.0 / Stopwatch.Frequency:F2} " +
        $"record_ms={statistics.RecordTicks * 1000.0 / Stopwatch.Frequency:F2}";

    private static void ReportImageUploads()
    {
        var ranked = _imageUploads.OrderByDescending(static pair =>
            pair.Value.WatchTicks + pair.Value.SourceTicks + pair.Value.RecordTicks).ToArray();
        foreach (var (key, statistics) in ranked.Take(8))
        {
            Console.Error.WriteLine($"[PERF][IMAGE_UPLOAD] address=0x{key.Address:X16} " +
                $"size={key.Width}x{key.Height}x{key.Depth} layers={key.Layers} levels={key.Levels} " +
                $"format={key.Format} tile={key.TileMode} reason={key.Reason} path={key.UploadPath} " +
                $"write_address=0x{key.WriteAddress:X16} write_bytes={key.WriteSize} {FormatImageUploadStatistics(statistics)}");
        }

        foreach (var (_, statistics) in ranked.Skip(8))
        {
            _otherImageUploads.Count += statistics.Count;
            _otherImageUploads.SourceBytes += statistics.SourceBytes;
            _otherImageUploads.WatchTicks += statistics.WatchTicks;
            _otherImageUploads.SourceTicks += statistics.SourceTicks;
            _otherImageUploads.RecordTicks += statistics.RecordTicks;
        }

        if (_otherImageUploads.Count > 0)
        {
            Console.Error.WriteLine($"[PERF][IMAGE_UPLOAD] other=1 {FormatImageUploadStatistics(_otherImageUploads)}");
        }

        _imageUploads.Clear();
        _otherImageUploads = new ImageUploadStatistics();
    }
    private static long _windowStart = Stopwatch.GetTimestamp();
    // The render loop is single-threaded, so plain fields are enough and keep
    // the per-scope cost to two timestamp reads.
    [ThreadStatic] private static Phase _current;
    [ThreadStatic] private static long _lastTimestamp;
    [ThreadStatic] private static int _scopeDepth;

    internal readonly ref struct Scope
    {
        private readonly Phase _previous;
        private readonly bool _active;

        internal Scope(Phase previous)
        {
            _previous = previous;
            _active = true;
        }

        public void Dispose()
        {
            if (!_active)
            {
                return;
            }

            Charge(_previous);
            _scopeDepth--;
        }

        // Switch sequential work in this scope. Nested scopes must finish first.
        public void SwitchPhase(Phase phase)
        {
            if (!_active)
            {
                return;
            }

            Charge(phase);
            _entries[(int)phase]++;
        }
    }

    public static Scope Measure(Phase phase)
    {
        if (!Enabled)
        {
            return default;
        }

        var previous = Charge(phase);
        _scopeDepth++;
        _entries[(int)phase]++;
        return new Scope(previous);
    }

    // Cache calls on other threads must not enter the render-thread counters.
    internal static Scope MeasureDetail(Phase phase) =>
        _scopeDepth > 0 ? Measure(phase) : default;

    /// <summary>
    /// Closes out the running phase and switches to <paramref name="next"/>,
    /// returning the phase that was running.
    /// </summary>
    private static Phase Charge(Phase next)
    {
        var now = Stopwatch.GetTimestamp();
        var previous = _current;
        if (_lastTimestamp != 0)
        {
            _ticks[(int)previous] += now - _lastTimestamp;
        }

        _lastTimestamp = now;
        _current = next;
        return previous;
    }

    /// <summary>Called once per presented frame; also drives the report.</summary>
    public static void RecordFrame()
    {
        if (!Enabled)
        {
            return;
        }

        _frames++;
        var now = Stopwatch.GetTimestamp();
        var elapsedTicks = now - _windowStart;
        if (elapsedTicks < _reportSeconds * Stopwatch.Frequency)
        {
            return;
        }

        _windowStart = now;
        var seconds = elapsedTicks / (double)Stopwatch.Frequency;
        var frames = _frames;
        _frames = 0;

        var parts = new List<(Phase Phase, double Percent, long Entries, double Milliseconds)>((int)Phase.Count);
        var accounted = 0L;
        for (var index = 0; index < (int)Phase.Count; index++)
        {
            var phaseTicks = _ticks[index];
            _ticks[index] = 0;
            var entries = _entries[index];
            _entries[index] = 0;
            accounted += phaseTicks;
            if (phaseTicks <= 0)
            {
                continue;
            }

            parts.Add(((Phase)index, phaseTicks * 100.0 / elapsedTicks, entries,
                phaseTicks * 1000.0 / Stopwatch.Frequency));
        }

        parts.Sort(static (left, right) => right.Percent.CompareTo(left.Percent));
        Console.Error.WriteLine(
            $"[PERF][RENDER] {seconds:F1}s fps={frames / seconds:F1} " +
            $"covered={accounted * 100.0 / elapsedTicks:F0}% " +
            string.Join(
                " ",
                parts.Select(part =>
                    $"{part.Phase}={part.Percent:F1}%" +
                    (part.Entries > 0 ? $"/n{part.Entries}" : string.Empty))));

        Console.Error.WriteLine(
            $"[PERF][RENDER_MS] window_s={seconds:F1} frames={frames} " +
            string.Join(" ", parts.Select(part => $"{part.Phase}={part.Milliseconds:F2}ms/n{part.Entries}")));

        ReportImageUploads();
        var commandReads = new List<string>();
        for (var index = 0; index < (int)CommandReadKind.Count; index++)
        {
            if (_commandReadCalls[index] != 0)
            {
                commandReads.Add($"{(CommandReadKind)index}={_commandReadCalls[index]}/bytes{_commandReadBytes[index]}");
            }
            _commandReadCalls[index] = 0;
            _commandReadBytes[index] = 0;
        }
        if (commandReads.Count != 0)
        {
            Console.Error.WriteLine($"[PERF][COMMAND_READS] window_s={seconds:F1} {string.Join(" ", commandReads)}");
        }
    }

}
