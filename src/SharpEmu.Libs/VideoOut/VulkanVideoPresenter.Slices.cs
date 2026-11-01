// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;
using VkBuffer = Silk.NET.Vulkan.Buffer;

// Thrown by a recording thread when an item needs a cache mutation only the render thread may make in
// stream order; the slice ends before the item and the render thread records it itself.
internal sealed class SerialRequiredException(string reason, bool resumable = false) : Exception(reason)
{
    public bool Resumable => resumable;
}

internal static unsafe partial class VulkanVideoPresenter
{
    private static readonly ThreadCounter _slicesRecorded = new();
    private static readonly ThreadCounter _segmentsRecorded = new();
    private static readonly ThreadCounter _usesShared = new();
    private static readonly ThreadCounter _nativeColorRechecksFailed = new();
    private static readonly ThreadCounter _segmentsEmpty = new();
    private static readonly ThreadCounter _slicesSerial = new();
    private static readonly ThreadCounter _slicesInvalid = new();
    private static readonly ThreadCounter _frontendTexturesStale = new();

    internal enum RenderReadSource
    {
        Wait,
        Operand,
        Memory,
        Word,
    }

    internal static class RoundTripTotals
    {
        public const int ReadKinds = 8;
        public static long FlipCount;
        public static long FlipTicks;
        public static long ReadCount;
        public static long ReadTicks;
        public static readonly long[] KindCounts = new long[ReadKinds];
        public static readonly long[] KindTicks = new long[ReadKinds];
        private static long _slowestTicks;
        private static long _slowestAddress;
        private static int _slowestKind;
        private static string _slowestSite = "";

        public static string KindName(int kind) => ((RenderReadSource)(kind / 2)).ToString().ToLowerInvariant() + (kind % 2 == 0 ? "/publication" : "/gpu_dirty");

        public static void NoteRead(int kind, long ticks, ulong address, string site)
        {
            Interlocked.Increment(ref KindCounts[kind]);
            Interlocked.Add(ref KindTicks[kind], ticks);
            var slowest = Volatile.Read(ref _slowestTicks);
            while (ticks > slowest)
            {
                var previous = Interlocked.CompareExchange(ref _slowestTicks, ticks, slowest);
                if (previous == slowest)
                {
                    Volatile.Write(ref _slowestAddress, (long)address);
                    Volatile.Write(ref _slowestKind, kind);
                    Volatile.Write(ref _slowestSite, site);
                    break;
                }

                slowest = previous;
            }
        }

        public static (long Ticks, ulong Address, int Kind, string Site) TakeSlowest()
        {
            var address = (ulong)Volatile.Read(ref _slowestAddress);
            var kind = Volatile.Read(ref _slowestKind);
            var site = Volatile.Read(ref _slowestSite);
            return (Interlocked.Exchange(ref _slowestTicks, 0), address, kind, site);
        }
    }
    private static readonly ThreadCounter _sliceItems = new();
    private static readonly ThreadCounter _serialItems = new();
    private static readonly ThreadCounter _sliceWaitTicks = new();
    private static readonly ThreadCounter _slicesStolen = new();
    private static readonly ThreadCounter _stolenItems = new();
    private static readonly ThreadCounter _restChunks = new();
    private static readonly ThreadCounter _restItems = new();
    private static readonly ThreadCounter _workerBusyTicks = new();
    private static readonly ThreadCounter _workerItems = new();
    private static readonly ThreadCounter _inlineTicks = new();
    private static readonly ThreadCounter _joinTicks = new();
    private static readonly ThreadCounter _validateTicks = new();
    private static readonly ThreadCounter _prologueTicks = new();
    private static readonly ThreadCounter[] _stolenByOrigin = NewThreadCounters(3);
    private static readonly ThreadCounter[] _waitByOrigin = NewThreadCounters(3);
    private static readonly ThreadCounter[] _stolenByAge = NewThreadCounters(3);
    private static readonly ThreadCounter[] _tailByProducer = NewThreadCounters(5);
    private static readonly ConcurrentDictionary<string, long[]> _roundTrips = new();
    private static readonly ThreadCounter _relayInWait = new();
    private static readonly ThreadCounter _relayInWaitTicks = new();
    private static readonly ThreadCounter _validateBuffersSkipped = new();
    private static readonly ThreadCounter _validateDeferredSkipped = new();
    private static readonly ThreadCounter _validateImageTicks = new();
    private static readonly ThreadCounter _validateBufferTicks = new();
    private static readonly ThreadCounter _validateDeferredTicks = new();
    private static readonly ThreadCounter _streamCopies = new();
    private static readonly ThreadCounter _streamFallbacks = new();
    private static readonly ConcurrentDictionary<string, StrongBox<long>> _serialReasons = new();
    private static int _gc0;
    private static int _gc1;
    private static int _gc2;
    private static long _allocated;
    private static TimeSpan _gcPaused;
    private static long _renderAllocated;
    private static readonly ThreadCounter _workerAllocated = new();

    internal static string TakeSliceReport() =>
        FormattableString.Invariant($"[PERF][SLICES] slices={_slicesRecorded.Take()} segments={_segmentsRecorded.Take()} empty={_segmentsEmpty.Take()} uses_shared={_usesShared.Take()} attachments_kept={RecordingHost.TakeAttachmentsKept()} worker_queries={RecordingHost.TakeWorkerQueries()} native_rechecks_failed={_nativeColorRechecksFailed.Take()} ") +
        SharpEmu.Libs.Gpu.Rendering.RenderRecorder.TakeInPlaceReport() + FormattableString.Invariant($" textures_kept={RecordingHost.TakeTexturesKept()} ") + RecordingHost.TakeTableReport() +
        FormattableString.Invariant($"bda_sweeps={Interlocked.Exchange(ref GuestBufferCache.BdaSweeps, 0)}/skipped:{Interlocked.Exchange(ref GuestBufferCache.BdaSweepsSkipped, 0)} ") +
        FormattableString.Invariant($"serial_cuts={_slicesSerial.Take()} invalid={_slicesInvalid.Take()} frontend_textures_stale={_frontendTexturesStale.Take()} ") +
        FormattableString.Invariant($"slice_items={_sliceItems.Take()} serial_items={_serialItems.Take()} join_wait_ms={_sliceWaitTicks.Take() * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ") +
        FormattableString.Invariant($"image_barriers={ImageAccessState.Barriers.Take()}/elided:{ImageAccessState.BarriersElided.Take()} ") +
        FormattableString.Invariant($"relay_in_wait={_relayInWait.Take()}/{_relayInWaitTicks.Take() * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms ") +
        FormattableString.Invariant($"stream_copies={_streamCopies.Take()} stream_fallbacks={_streamFallbacks.Take()} ") +
        FormattableString.Invariant($"worker_busy_ms={_workerBusyTicks.Take() * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} worker_items={_workerItems.Take()} inline_ms={_inlineTicks.Take() * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1} ") +
        FormattableString.Invariant($"join_ms={Ticks(_joinTicks):F1} validate_ms={Ticks(_validateTicks):F1} ") +
        FormattableString.Invariant($"validate_kind_ms=images:{Ticks(_validateImageTicks):F1},buffers:{Ticks(_validateBufferTicks):F1},deferred:{Ticks(_validateDeferredTicks):F1} validate_skipped=buffers:{_validateBuffersSkipped.Take()},deferred:{_validateDeferredSkipped.Take()} ") +
        FormattableString.Invariant($"prologue_ms={Ticks(_prologueTicks):F1} stolen_origin=ahead:{_stolenByOrigin[0].Take()},tail:{_stolenByOrigin[1].Take()},rest:{_stolenByOrigin[2].Take()} ") +
        FormattableString.Invariant($"stolen_age=lt100us:{_stolenByAge[0].Take()},lt1ms:{_stolenByAge[1].Take()},ge1ms:{_stolenByAge[2].Take()} ") +
        FormattableString.Invariant($"wait_origin_ms=ahead:{Ticks(_waitByOrigin[0]):F1},tail:{Ticks(_waitByOrigin[1]):F1},rest:{Ticks(_waitByOrigin[2]):F1} ") +
        FormattableString.Invariant($"stolen={_slicesStolen.Take()} stolen_items={_stolenItems.Take()} rest_chunks={_restChunks.Take()} rest_items={_restItems.Take()} ") +
        FormattableString.Invariant($"tail_producer=busy:{_tailByProducer[0].Take()},idle:{_tailByProducer[1].Take()},blocked:{_tailByProducer[2].Take()},space:{_tailByProducer[3].Take()},run:{_tailByProducer[4].Take()} ") +
        "round_trips=" + TakeRoundTrips() + " " + Presenter.TakeCommandReadReport() + " " + SharpEmu.ShaderCompiler.Resources.ResourceMaterializer.TakeProgramReport() + " " +
        SharpEmu.ShaderCompiler.Resources.ResourceMaterializer.TakeFastReport() + " " + SharpEmu.Libs.Gpu.Pipelines.ShaderPipelineCache.TakePairReport() + " " + SubmissionScheduler.TakeDeferredReport() + " " +
        "serial_reasons=" + TakeSerialReasons() + " " + GuestImageCache.TakeSliceLookupReport() + " " + TakeGcReport() + " " + Diagnostics.ItemCostTally.TakeReport() + " posts=" + TakePosts();

    private static double Ticks(ThreadCounter ticks) => ticks.Take() * 1000.0 / System.Diagnostics.Stopwatch.Frequency;

    private static ThreadCounter[] NewThreadCounters(int count)
    {
        var counters = new ThreadCounter[count];
        for (var index = 0; index < count; index++)
        {
            counters[index] = new ThreadCounter();
        }

        return counters;
    }

    private static string TakeGcReport()
    {
        var gc0 = GC.CollectionCount(0);
        var gc1 = GC.CollectionCount(1);
        var gc2 = GC.CollectionCount(2);
        var allocated = GC.GetTotalAllocatedBytes();
        var paused = GC.GetTotalPauseDuration();
        var renderAllocated = GC.GetAllocatedBytesForCurrentThread();
        var report = FormattableString.Invariant($"gc={gc0 - _gc0}/{gc1 - _gc1}/{gc2 - _gc2} alloc_mb={(allocated - _allocated) / 1048576.0:F0} gc_pause_ms={(paused - _gcPaused).TotalMilliseconds:F0} ") +
            FormattableString.Invariant($"render_alloc_mb={(renderAllocated - _renderAllocated) / 1048576.0:F0} worker_alloc_mb={_workerAllocated.Take() / 1048576.0:F0}");
        _renderAllocated = renderAllocated;
        _gc0 = gc0;
        _gc1 = gc1;
        _gc2 = gc2;
        _allocated = allocated;
        _gcPaused = paused;
        return report;
    }

    private static string TakeRoundTrips()
    {
        var parts = new List<string>();
        foreach (var (caller, totals) in _roundTrips)
        {
            var count = Interlocked.Exchange(ref totals[0], 0);
            var ticks = Interlocked.Exchange(ref totals[1], 0);
            if (count != 0)
            {
                parts.Add(caller + ":" + FormattableString.Invariant($"{count}/{ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency:F1}ms"));
            }
        }

        return string.Join(",", parts);
    }

    private static readonly ConcurrentDictionary<string, long> _posts = new();

    private static string TakePosts()
    {
        var posts = _posts.ToArray();
        foreach (var post in posts)
        {
            _posts.TryRemove(post.Key, out _);
        }

        return string.Join(",", posts.OrderByDescending(post => post.Value).Select(post => post.Key + ":" + post.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static string TakeSerialReasons()
    {
        var reasons = _serialReasons.Select(reason => (reason.Key, Count: Interlocked.Exchange(ref reason.Value.Value, 0))).Where(reason => reason.Count != 0).ToArray();
        return string.Join(",", reasons.OrderByDescending(reason => reason.Count).Select(reason => reason.Key.Replace(' ', '_') + ":" + reason.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)));
    }

    private sealed class SliceRequests
    {
        public readonly List<BufferSliceUse> Buffers = new();
        public readonly List<(ulong Address, ulong Size)> SyncRanges = new();
        public readonly List<ImageSliceUse> Images = new();
        private HashSet<BufferSliceUse>? _bufferSet;
        private Dictionary<(ResourceSlotIdentifier Image, ImageRole Role, ImageViewDescription View), int>? _imageIndex;

        public void AddBuffer(in BufferSliceUse use)
        {
            if (Buffers.Count != 0)
            {
                if (_bufferSet is null)
                {
                    _bufferSet = [.. Buffers];
                }

                if (!_bufferSet.Add(use))
                {
                    _usesShared.Increment();
                    return;
                }
            }

            Buffers.Add(use);
        }

        public void AddImage(in ImageSliceUse use)
        {
            var key = (use.Image, use.Request.Role, use.Request.View);
            _imageIndex ??= new();
            if (_imageIndex.TryGetValue(key, out var index))
            {
                var known = Images[index];
                if (ReferenceEquals(known.Expected, use.Expected) && SameRequest(known.Request, use.Request))
                {
                    _usesShared.Increment();
                    return;
                }
            }
            else
            {
                _imageIndex.Add(key, Images.Count);
            }

            Images.Add(use);
        }

        internal static bool SameRequest(in ImageRequest left, in ImageRequest right) =>
            SameBytes(left.Description, right.Description) && left.View == right.View && left.Role == right.Role &&
            left.TraceTextureMetadataAddress == right.TraceTextureMetadataAddress &&
            string.Equals(left.TraceTextureDescriptor, right.TraceTextureDescriptor, StringComparison.Ordinal) &&
            left.TraceMetadataCompress == right.TraceMetadataCompress && left.TraceWriteCompress == right.TraceWriteCompress;

        private static bool SameBytes<T>(in T left, in T right) where T : unmanaged =>
            MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in left)).SequenceEqual(MemoryMarshal.AsBytes(new ReadOnlySpan<T>(in right)));

        public readonly List<(ulong Address, uint[] Words)> KnownWrites = new();
        public readonly List<SubmissionUploadResources> Resources = new();
        public bool NeedsBdaSweep;
        public long BufferSerial;
        public long? ReadsVersion;
        public bool MixedReadsVersions;
        public readonly List<(ResourceSlotIdentifier Image, ImageRequest Request)> NativeColorUses = new();

        public bool WritesRange(GuestSpan range)
        {
            foreach (var use in Buffers)
            {
                if (use.Written && use.Address < range.Address + range.Size && range.Address < use.Address + use.Size)
                {
                    return true;
                }
            }

            return false;
        }

        public readonly List<(IShaderFrontend Frontend, uint[] Words)> FrontendTextures = new();

        public bool WritesImageRange(GuestSpan range)
        {
            if (WritesRange(range))
            {
                return true;
            }

            foreach (var use in Images)
            {
                var data = use.Request.Description.Data;
                if (use.Request.Role is ImageRole.ColorTarget or ImageRole.DepthTarget or ImageRole.StorageImage &&
                    data.Address < range.Address + range.Size && range.Address < data.Address + data.Size)
                {
                    return true;
                }
            }

            return false;
        }
        public ulong LookupGeneration;
        public long MetadataGeneration;
        public IReadOnlyList<GuestSpan>? BdaSpans;
        public ulong BdaMapping;
    }

    // Items recorded into one command buffer; the item after them, if any, needs the render thread.
    private sealed class SliceSegment
    {
        public readonly SliceRequests Requests = new();
        public int First;
        public int Count;
        public string? SerialReason;
        public RecordingBuffer? Buffer;
        public List<VulkanOcclusionQueries.Block>? QueryBlocks;
        public int DrawCount;
    }

    private sealed class RenderSlice
    {
        public readonly List<CommandThread.Item> Items = new();
        public readonly ConcurrentQueue<SliceSegment> Segments = new();
        public readonly ManualResetEventSlim Progress = new(false);
        public readonly ManualResetEventSlim Done = new(false);
        public long Generation;
        public int Resume;
        public RecordingHost? Host;
        public List<SliceArenaChunk> Chunks = [];
        public bool Submitted;
        public bool Dispatched;
        public long DispatchedAt;
        public int Origin;
        public Exception? Failure;
        private int _claimed;

        public bool TryClaim() => Interlocked.CompareExchange(ref _claimed, 1, 0) == 0;
    }


    private sealed class RenderWorkerPool : IDisposable
    {
        private readonly ConcurrentQueue<RenderSlice> _queue = new();
        private readonly ConcurrentQueue<RenderSlice> _urgent = new();
        private readonly SemaphoreSlim _available = new(0);
        private volatile bool _stopping;
        private readonly Thread[] _threads;
        private readonly RecordingHost[] _hosts;

        public RenderWorkerPool(Presenter presenter, int count)
        {
            _hosts = new RecordingHost[count];
            _threads = new Thread[count];
            for (var index = 0; index < count; index++)
            {
                var host = new RecordingHost(presenter, worker: true);
                _hosts[index] = host;
                _threads[index] = new Thread(() => Run(host)) { IsBackground = true, Name = $"SharpEmu render worker {index}" };
                _threads[index].Start();
            }
        }

        public int Count => _threads.Length;

        public void Dispatch(RenderSlice slice, bool urgent)
        {
            (urgent ? _urgent : _queue).Enqueue(slice);
            _available.Release();
        }

        private void Run(RecordingHost host)
        {
            SharpEmu.HLE.Host.HostLaneReservation.ApplyToHostWorkerThread();
            RenderPhaseProfile.AttachResolveWorker();
            SubmissionScheduler.MarkReadOnlyThread(static operation => new SerialRequiredException("unguarded " + operation));
            while (true)
            {
                _available.Wait();
                if (!_urgent.TryDequeue(out var slice) && !_queue.TryDequeue(out slice))
                {
                    if (_stopping)
                    {
                        break;
                    }

                    continue;
                }

                if (!slice.TryClaim())
                {
                    continue;
                }

                var allocated = RenderPhaseProfile.Enabled ? GC.GetAllocatedBytesForCurrentThread() : 0;
                var busyStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                try
                {
                    host.RecordSlice(slice);
                    _workerItems.Add(slice.Resume);
                }
                catch (Exception exception)
                {
                    slice.Failure = exception;
                }
                finally
                {
                    if (allocated != 0)
                    {
                        _workerAllocated.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                    }

                    _workerBusyTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - busyStarted);
                    slice.Done.Set();
                    slice.Progress.Set();
                }
            }
        }

        public void Dispose()
        {
            _stopping = true;
            _available.Release(_threads.Length);
            foreach (var thread in _threads)
            {
                thread.Join();
            }

            foreach (var host in _hosts)
            {
                host.DisposeWorker();
            }
        }
    }

    private sealed partial class Presenter
    {
        internal static readonly int RenderWorkerCount =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_RENDER_WORKERS"), out var workers) && workers >= 0
                ? workers
                : Math.Clamp(Environment.ProcessorCount / 4, 1, 6);

        private RenderWorkerPool? _workers;
        internal SliceArenaPool? _arenaPool;

        private void CreateRenderWorkers()
        {
            if (RenderWorkerCount == 0)
            {
                return;
            }

            _scheduler.MemoryBarrierRecorder ??= RecordRequestedMemoryBarrier;
            _arenaPool = new SliceArenaPool(_deviceInfo, _scheduler);
            _workers = new RenderWorkerPool(this, RenderWorkerCount);
            Console.Error.WriteLine($"[LOADER][INFO] Render workers: {RenderWorkerCount}");
        }

        private void StopRenderWorkers()
        {
            _workers?.Dispose();
            _workers = null;
            _arenaPool?.Dispose();
            _arenaPool = null;
        }

        internal bool SlicesEnabled => _workers is not null && !_deviceLost;

        internal int WorkerCount => _workers?.Count ?? 0;

        internal void DispatchSlice(RenderSlice slice, bool urgent = false)
        {
            slice.Dispatched = true;
            slice.DispatchedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            slice.Generation = _scheduler.BeginSliceCut();
            _bufferCache.RefreshSnapshot();
            _workers!.Dispatch(slice, urgent);
        }

        internal static void NoteStolen(RenderSlice slice)
        {
            _slicesStolen.Increment();
            _stolenItems.Add(slice.Items.Count);
            _stolenByOrigin[slice.Origin].Increment();
            var age = (System.Diagnostics.Stopwatch.GetTimestamp() - slice.DispatchedAt) * 1_000_000 / System.Diagnostics.Stopwatch.Frequency;
            _stolenByAge[age < 100 ? 0 : age < 1000 ? 1 : 2].Increment();
        }

        internal static void NoteTail(int producerState) => _tailByProducer[producerState].Increment();

        internal static void NotePost(string caller) => _posts.AddOrUpdate(caller, 1, static (_, count) => count + 1);

        internal static void NoteRoundTrip(string caller, long ticks)
        {
            var totals = _roundTrips.GetOrAdd(caller, static _ => new long[2]);
            Interlocked.Increment(ref totals[0]);
            Interlocked.Add(ref totals[1], ticks);
            var flip = caller == "PrepareCpuFlip";
            Interlocked.Increment(ref flip ? ref RoundTripTotals.FlipCount : ref RoundTripTotals.ReadCount);
            Interlocked.Add(ref flip ? ref RoundTripTotals.FlipTicks : ref RoundTripTotals.ReadTicks, ticks);
        }

        internal static void NoteJoin(long started) => _joinTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - started);

        internal static void NoteRestChunk(int items)
        {
            _restChunks.Increment();
            _restItems.Add(items);
        }

        // The next finished segment of the slice in order, or null once the recording thread is done with it.
        internal SliceSegment? NextSegment(RenderSlice slice, Action whileWaiting)
        {
            var waitStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            var relayTicks = 0L;
            var spinner = new SpinWait();
            SliceSegment? segment;
            while (!slice.Segments.TryDequeue(out segment))
            {
                if (slice.Done.IsSet)
                {
                    if (slice.Segments.TryDequeue(out segment))
                    {
                        break;
                    }

                    var waitedForDone = System.Diagnostics.Stopwatch.GetTimestamp() - waitStarted - relayTicks;
                    _sliceWaitTicks.Add(waitedForDone);
                    _waitByOrigin[slice.Origin].Add(waitedForDone);
                    if (slice.Failure is { } failure)
                    {
                        throw new InvalidOperationException("A render worker failed.", failure);
                    }

                    return null;
                }

                if (_relay.HasPendingCommands)
                {
                    relayTicks += RunRelayInSegmentWait(whileWaiting);
                    continue;
                }

                if (spinner.Count < 200)
                {
                    spinner.SpinOnce(sleep1Threshold: -1);
                    continue;
                }

                slice.Progress.Reset();
                Interlocked.Exchange(ref _segmentWaitSlice, slice);
                try
                {
                    if (slice.Segments.TryDequeue(out segment))
                    {
                        break;
                    }

                    if (!slice.Done.IsSet && !_relay.HasPendingCommands)
                    {
                        slice.Progress.Wait();
                    }
                }
                finally
                {
                    Volatile.Write(ref _segmentWaitSlice, null);
                }
            }

            var waited = System.Diagnostics.Stopwatch.GetTimestamp() - waitStarted - relayTicks;
            _sliceWaitTicks.Add(waited);
            _waitByOrigin[slice.Origin].Add(waited);
            return segment;
        }

        private RenderSlice? _segmentWaitSlice;

        private void WakeRenderThreadForRelay()
        {
            WakeRenderThread();
            Volatile.Read(ref _segmentWaitSlice)?.Progress.Set();
        }

        private static long RunRelayInSegmentWait(Action whileWaiting)
        {
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            whileWaiting();
            var ticks = System.Diagnostics.Stopwatch.GetTimestamp() - started;
            _relayInWait.Increment();
            _relayInWaitTicks.Add(ticks);
            return ticks;
        }

        // Joins one segment in stream order: validates what the recording thread resolved, records the
        // prologue, applies the mutations it skipped and submits the prologue and the segment together.
        // False when the segment's items must run on this thread instead.
        internal bool JoinSegment(RenderSlice slice, SliceSegment segment)
        {
            try
            {
                if (segment.Count == 0)
                {
                    _segmentsEmpty.Increment();
                    if (segment.Buffer is { } emptyBuffer)
                    {
                        _scheduler.DiscardRecording(emptyBuffer);
                    }
                    return false;
                }

                var validateStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                var valid = ValidateSegment(slice, segment);
                _validateTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - validateStarted);
                if (!valid)
                {
                    _slicesInvalid.Increment();
                    _scheduler.DiscardRecording(segment.Buffer!);
                    return false;
                }

                var prologueStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                RecordSlicePrologue(segment.Requests);
                _prologueTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - prologueStarted);
                var uploads = _spareSubmissionResources is { Count: not 0 } spare ? spare.Pop() : new SubmissionUploadResources();
                uploads.DebugName = "SharpEmu slice";
                _batchResources.Add(uploads);
                _batchResources.AddRange(segment.Requests.Resources);
                if (segment.DrawCount != 0)
                {
                    if (_batchDrawCount == 0)
                    {
                        _batchOpenedAt = System.Diagnostics.Stopwatch.GetTimestamp();
                    }

                    _batchDrawCount += segment.DrawCount;
                }

                using (RenderPhaseProfile.Measure(RenderPhaseProfile.Phase.QueueSubmit))
                {
                    _ = _scheduler.FlushDeferred([segment.Buffer!]);
                }

                if (segment.QueryBlocks is { } blocks)
                {
                    segment.QueryBlocks = null;
                    foreach (var block in blocks)
                    {
                        _occlusionQueries!.Adopt(block, _occlusionQueueId);
                    }
                }

                EnsureBatchedGuestCommands();
                RequestGuestMemoryBarrier(RecordingHost.GlobalMemoryBarrier);

                slice.Submitted = true;
                _bufferCache.OnBatchSubmitted();
                _segmentsRecorded.Increment();
                _sliceItems.Add(segment.Count);
                if (segment.SerialReason is not null)
                {
                    _slicesSerial.Increment();
                }

                return true;
            }
            finally
            {
                if (segment.QueryBlocks is { } unused)
                {
                    segment.QueryBlocks = null;
                    unused.ForEach(_occlusionQueries!.ReturnBlock);
                }

                if (segment.Buffer is { } buffer)
                {
                    segment.Buffer = null;
                    slice.Host!.ReturnWorkerBuffer(buffer);
                }
            }
        }

        private bool ValidateSegment(RenderSlice slice, SliceSegment segment)
        {
            var requests = segment.Requests;
            foreach (var (frontend, words) in requests.FrontendTextures)
            {
                if (!frontend.IsTexturePrepared(words, ordered: true, out _, out _))
                {
                    _frontendTexturesStale.Increment();
                    return false;
                }
            }

            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var valid = _imageCache.ValidateSliceImages(requests.Images, requests.LookupGeneration, requests.MetadataGeneration);
            var imagesDone = System.Diagnostics.Stopwatch.GetTimestamp();
            _validateImageTicks.Add(imagesDone - started);
            if (!valid)
            {
                return false;
            }

            if (requests.BufferSerial == _bufferCache.RegistrationSerial)
            {
                _validateBuffersSkipped.Increment();
            }
            else
            {
                valid = _bufferCache.ValidateSliceBuffers(requests.Buffers);
            }

            var buffersDone = System.Diagnostics.Stopwatch.GetTimestamp();
            _validateBufferTicks.Add(buffersDone - imagesDone);
            if (!valid)
            {
                return false;
            }

            foreach (var (image, request) in requests.NativeColorUses)
            {
                var range = request.Description.Metadata.Range;
                if (_bufferCache.HasGpuDirtyBytes(range.Address, range.Size) || !_imageCache.NativeColorClearSettled(image, request))
                {
                    _nativeColorRechecksFailed.Increment();
                    return false;
                }
            }

            if (!requests.MixedReadsVersions && (requests.ReadsVersion is not { } version || version == _bufferCache.GpuModifiedVersion))
            {
                _validateDeferredSkipped.Increment();
            }
            else
            {
                valid = ValidateDeferredItems(slice, segment);
            }

            _validateDeferredTicks.Add(System.Diagnostics.Stopwatch.GetTimestamp() - buffersDone);
            return valid;
        }

        private bool ValidateDeferredItems(RenderSlice slice, SliceSegment segment)
        {
            var end = segment.First + segment.Count;
            for (var position = segment.First; position < end; position++)
            {
                if (!_translation.ValidateDeferred(slice.Items[position].Work!))
                {
                    return false;
                }
            }

            return true;
        }

        internal void FinishSlice(RenderSlice slice)
        {
            slice.Done.Wait();
            _slicesRecorded.Increment();
            _scheduler.NoteSliceJoined(slice.Generation);
            if (slice.Chunks.Count == 0)
            {
                return;
            }

            var chunks = slice.Chunks;
            slice.Chunks = [];
            if (slice.Submitted)
            {
                _scheduler.QueueCompletionAction(() => chunks.ForEach(_arenaPool!.Return));
            }
            else
            {
                chunks.ForEach(_arenaPool!.Return);
            }
        }

        private void RecordSlicePrologue(SliceRequests requests)
        {
            Recording.EndRendering();
            EnsureBatchedGuestCommands();
            RequestGuestMemoryBarrier(RecordingHost.GlobalMemoryBarrier);
            var uploadsBefore = _bufferCache.UploadsRecorded;
            var pairsBefore = _bufferCache.UploadBarrierPairs;
            _bufferCache.BeginUploadBatch();
            try
            {
                if (requests.NeedsBdaSweep && requests.BdaSpans is { } spans)
                {
                    _bufferCache.PrepareBda(spans, requests.BdaMapping);
                }

                foreach (var (address, size) in requests.SyncRanges)
                {
                    _bufferCache.SynchronizeBuffersInRange(address, size);
                }

                foreach (var use in requests.Buffers)
                {
                    _bufferCache.ApplySliceUse(in use);
                }
            }
            finally
            {
                _bufferCache.EndUploadBatch();
            }

            if (GpuSyncProfile.Enabled)
            {
                BarrierTally.PrologueUploads.Add(_bufferCache.UploadsRecorded - uploadsBefore);
                BarrierTally.PrologueUploadPairs.Add(_bufferCache.UploadBarrierPairs - pairsBefore);
            }

            foreach (var use in requests.Images)
            {
                var request = use.Request;
                _imageCache.ApplySliceUse(use.Image, in request);
            }

            foreach (var (address, words) in requests.KnownWrites)
            {
                _bufferCache.NoteKnownGpuContents(address, words);
            }
        }
    }

    private sealed partial class RecordingHost : IRenderingState
    {
        private readonly bool _worker;
        private RecordingBuffer? _workerBuffer;
        private long _itemSequence;
        private readonly ConcurrentBag<RecordingBuffer>? _spareBuffers;
        private readonly SliceArena? _arena;
        private readonly SliceHazards? _hazards;
        private readonly RenderRecorder? _workerRecorder;
        private readonly Dictionary<CachedImage, BindingBox> _workerBindings = new(ReferenceEqualityComparer.Instance);

        private sealed class BindingBox
        {
            public ImageBindingState Value;
        }
        private SliceSegment? _segment;
        private BufferSnapshot _buffers = BufferSnapshot.Empty;
        private int _sliceDrawCount;

        bool IRenderingState.IsRendering => _renderingActive;

        void IRenderingState.EndRendering() => EndRendering();

        bool IRenderingState.TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers) =>
            TryDeferUntilRenderingEnds(sourceStages, destinationStages, barriers);

        private static SerialRequiredException Serial(string reason, bool resumable = false) => new(reason, resumable);

        private SliceRequests Requests => _segment?.Requests ?? throw SubmissionScheduler.Fatal("The recording host is not recording a slice.");

        internal void RecordSlice(RenderSlice slice)
        {
            slice.Host = this;
            _buffers = _bufferCache.Snapshot;
            SliceSegment? segment = null;
            slice.Resume = slice.Items.Count;
            try
            {
                for (var index = 0; index < slice.Items.Count; index++)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    try
                    {
                        if (!_presenter._translation.CanRecordDeferred(slice.Items[index].Work!))
                        {
                            const string reason = "unresolved draw";
                            Interlocked.Increment(ref _serialReasons.GetOrAdd(reason, static _ => new StrongBox<long>()).Value);
                            if (segment is null)
                            {
                                slice.Segments.Enqueue(new SliceSegment { First = index, SerialReason = reason });
                                slice.Progress.Set();
                            }
                            else
                            {
                                segment.SerialReason = reason;
                                EndSegment(slice, segment);
                                segment = null;
                            }

                            continue;
                        }

                        segment ??= BeginSegment(index);
                        _itemSequence = slice.Items[index].Sequence;
                        if (slice.Items[index].Work is SharpEmu.Libs.Agc.AgcExports.GlobalBarrierWork)
                        {
                            RecordGuestCacheFlush();
                        }
                        else
                        {
                            _presenter._translation.RunDeferred(slice.Items[index].Work!, _workerRecorder!);
                        }

                        segment!.Count++;
                        NoteReadsVersion(segment.Requests, slice.Items[index].Work!);
                    }
                    catch (SerialRequiredException serial)
                    {
                        segment!.SerialReason = serial.Message;
                        Interlocked.Increment(ref _serialReasons.GetOrAdd(serial.Message, static _ => new StrongBox<long>()).Value);
                        EndSegment(slice, segment);
                        segment = null;
                        if (!serial.Resumable || index + 1 == slice.Items.Count)
                        {
                            slice.Resume = index + 1;
                            break;
                        }
                    }
                    finally
                    {
                        Diagnostics.ItemCostTally.Note(slice.Items[index].Work!.CostKind, Diagnostics.ItemCostTally.Record,
                            System.Diagnostics.Stopwatch.GetTimestamp() - started);
                    }
                }

                if (segment is not null)
                {
                    EndSegment(slice, segment);
                }
            }
            finally
            {
                slice.Chunks = _arena!.TakeChunks();
                _segment = null;
            }
        }

        private void RecordGuestCacheFlush()
        {
            if (DeferGlobalBarriers && _renderingActive && !_renderingWritesMemory)
            {
                _globalBarrierAfterRendering = true;
                return;
            }

            EndRendering(ScopeBreak.GuestFlush);
            if (BatchBarriers)
            {
                RequestGuestMemoryBarrier(GlobalMemoryBarrier);
                return;
            }

            _presenter.RecordGlobalBarrier(BeginBatchedGuestCommands());
        }

        private static void NoteReadsVersion(SliceRequests requests, SharpEmu.Libs.Agc.AgcExports.DeferredWork work)
        {
            if (work is not SharpEmu.Libs.Agc.AgcExports.DeferredDraw draw)
            {
                return;
            }

            if (requests.ReadsVersion is not { } version)
            {
                requests.ReadsVersion = draw.Reads.Version;
            }
            else if (version != draw.Reads.Version)
            {
                requests.MixedReadsVersions = true;
            }
        }

        private SliceSegment BeginSegment(int first)
        {
            var segment = new SliceSegment { First = first };
            segment.Requests.BufferSerial = _buffers.Serial;
            segment.Requests.LookupGeneration = _imageCache.LookupGeneration;
            segment.Requests.MetadataGeneration = _imageCache.MetadataGeneration;
            _segment = segment;
            _workerBuffer = _spareBuffers!.TryTake(out var buffer) ? buffer : _scheduler.CreateRecordingBuffer(this);
            _scheduler.BeginRecording(_workerBuffer);
            _workerBuffer.RequestMemoryBarrier(GlobalMemoryBarrier);
            _hazards!.Reset();
            _workerBindings.Clear();
            _hasBoundDepth = false;
            _attachments.Clear();
            _workerQueryBlock = null;
            _depthClearRecordedSeparately = false;
            _boundGraphicsPipeline = null;
            _sliceDrawCount = 0;
            return segment;
        }

        private void EndSegment(RenderSlice slice, SliceSegment segment)
        {
            EndRendering();
            if (segment.Count == 0)
            {
                _scheduler.EndDiscardedRecording(_workerBuffer!);
            }
            else
            {
                _scheduler.EndRecording(_workerBuffer!);
            }

            segment.Buffer = _workerBuffer;
            segment.DrawCount = _sliceDrawCount;
            slice.Segments.Enqueue(segment);
            slice.Progress.Set();
        }

        internal void ReturnWorkerBuffer(RecordingBuffer buffer)
        {
            if (!buffer.IsInvalid)
            {
                _scheduler.DiscardRecording(buffer);
            }

            _spareBuffers!.Add(buffer);
        }

        internal void DisposeWorker()
        {
            while (_spareBuffers!.TryTake(out var buffer))
            {
                if (!buffer.IsInvalid)
                {
                    _scheduler.DiscardRecording(buffer);
                }
            }
        }

        private CachedImage Image(ResourceSlotIdentifier imageIdentifier) =>
            _worker ? _imageCache.PeekImage(imageIdentifier) ?? throw Serial("image released", resumable: true) : _imageCache.GetImage(imageIdentifier);

        private ref ImageBindingState BindingOf(CachedImage image)
        {
            if (!_worker)
            {
                return ref image.Binding;
            }

            if (!_workerBindings.TryGetValue(image, out var box))
            {
                box = new BindingBox();
                _workerBindings.Add(image, box);
            }

            return ref box.Value;
        }

        private ResourceSlotIdentifier FindBuffer(ulong address, ulong size)
        {
            if (!_worker)
            {
                return _bufferCache.FindBuffer(address, size);
            }

            if (address == 0)
            {
                return GuestBufferCache.NullBufferId;
            }

            return _buffers.TryFind(address, size, out _, out var identifier) ? identifier : throw Serial("buffer lookup", resumable: true);
        }

        private (GpuBuffer Buffer, ulong Offset) ObtainBufferForSlice(ulong address, ulong size, bool isWritten, bool isTexelBuffer, bool invalidateImages = false)
        {
            var (buffer, offset, _) = ObtainBufferRangeForSlice(address, size, isWritten, isTexelBuffer, invalidateImages);
            return (buffer, offset);
        }

        private (GpuBuffer Buffer, ulong Offset, ulong Available) ObtainBufferRangeForSlice(ulong address, ulong size, bool isWritten, bool isTexelBuffer,
            bool invalidateImages = false)
        {
            if (address == 0 || size == 0)
            {
                throw Serial("buffer range", resumable: true);
            }

            if (_presenter.HasOverlappingPublications(address, size, includeLabels: true))
            {
                throw Serial("pending publication", resumable: true);
            }

            var cache = _sliceUses ??= new SliceUseEntry[SliceUseSlots];
            ref var entry = ref cache[(int)(((address >> 4) ^ (address >> 17) ^ (size * 0x9E3779B1UL)) & (SliceUseSlots - 1))];
            if (entry.Buffer is { } cached && entry.Address == address && entry.Size == size && entry.Written == isWritten &&
                entry.Texel == isTexelBuffer && entry.InvalidateImages == invalidateImages)
            {
                if (ReferenceEquals(entry.Snapshot, _buffers) && (isWritten || !_bufferCache.MayHaveCpuDirtyPages(address, size)))
                {
                    _sliceUseHits.Increment();
                    NoteSliceWrite(cached, isWritten);
                    Requests.AddBuffer(new BufferSliceUse(address, size, isWritten, isTexelBuffer, invalidateImages, cached));
                    return (cached, cached.Offset(address), cached.Size - cached.Offset(address));
                }

                _sliceUseStale.Increment();
            }
            else
            {
                _sliceUseMisses.Increment();
            }

            var imageBytes = isTexelBuffer && _bufferCache.MayHaveImageBytes(address, size);
            if (!isWritten && !imageBytes && _bufferCache.PrefersStreamCopy(address, size, isTexelBuffer) &&
                _presenter._translation.PendingWrites is { } pending && !pending.MayHavePending(address, size))
            {
                var thread = _presenter._commandThread!;
                if (thread.HasPendingRange(address, size))
                {
                    _streamFallbacks.Increment();
                }
                else if (_arena!.TryReserve(size, _bufferCache.StreamOffsetAlignment, out var reservation))
                {
                    if (thread.ReadOverlayBefore(address, reservation.Destination, _itemSequence))
                    {
                        _arena.Commit(in reservation);
                        _streamCopies.Increment();
                        return (reservation.Chunk.Buffer, reservation.Offset, (size + 3) & ~3UL);
                    }
                }
            }

            if (!_buffers.TryFind(address, size, out var buffer, out _))
            {
                throw Serial("buffer lookup", resumable: true);
            }

            entry = new SliceUseEntry(address, size, isWritten, isTexelBuffer, invalidateImages, _buffers, buffer);
            NoteSliceWrite(buffer, isWritten);
            Requests.AddBuffer(new BufferSliceUse(address, size, isWritten, isTexelBuffer, invalidateImages, buffer));
            return (buffer, buffer.Offset(address), buffer.Size - buffer.Offset(address));
        }

        private void CompleteOverlappingPublications(ulong address, ulong size, bool includeLabels = true)
        {
            if (!_worker)
            {
                _presenter.CompleteOverlappingPublications(address, size, includeLabels);
            }
            else if (_presenter.HasOverlappingPublications(address, size, includeLabels))
            {
                throw Serial("pending publication", resumable: true);
            }
        }

        private void SynchronizeColorMetadata(in ImageRequest request)
        {
            if (!_worker)
            {
                _imageCache.SynchronizeColorMetadata(request);
            }
            else if (request.Description.Metadata.NativeColorClear &&
                     (_bufferCache.HasGpuDirtyBytes(request.Description.Metadata.Range.Address, request.Description.Metadata.Range.Size) ||
                      Requests.WritesRange(request.Description.Metadata.Range)))
            {
                throw Serial("native color metadata", resumable: true);
            }
        }

        private void ApplyNativeColorClear(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
        {
            if (!_worker)
            {
                _imageCache.ApplyNativeColorClear(imageIdentifier, request);
            }
            else if (request.Description.Metadata.NativeColorClear)
            {
                if (!_imageCache.NativeColorClearSettled(imageIdentifier, request))
                {
                    throw Serial("native color clear", resumable: true);
                }

                Requests.NativeColorUses.Add((imageIdentifier, request));
            }
        }

        private void NoteSliceWrite(GpuBuffer buffer, bool isWritten)
        {
            if (isWritten && buffer.IsMapped)
            {
                _workerBuffer?.NoteMappedMemoryWrite();
            }
        }

        private const int SliceUseSlots = 4096;
        private SliceUseEntry[]? _sliceUses;

        private readonly record struct SliceUseEntry(ulong Address, ulong Size, bool Written, bool Texel, bool InvalidateImages, BufferSnapshot Snapshot, GpuBuffer Buffer);

        private void TransitionImage(CachedImage image, AccessFlags access, SubresourceRange? range, CommandBuffer command, bool guest = false)
        {
            if (!_worker)
            {
                image.Transition(access, range, command, guest);
                return;
            }

            var stage = SliceHazards.StagesOf(access);
            var barriers = _hazards!.Transition(image, access, stage, out var sourceStages, guest);
            if (barriers.Count == 0)
            {
                return;
            }

            var source = sourceStages == 0 ? PipelineStageFlags.TopOfPipeBit : sourceStages;
            if (CachedImage.OnlyRepeatsAttachmentAccess(barriers) && TryDeferUntilRenderingEnds(source, stage, barriers))
            {
                return;
            }

            EndRendering(ScopeBreak.SliceImage);
            fixed (ImageMemoryBarrier2* pointer = CollectionsMarshal.AsSpan(barriers))
            {
                VulkanSynchronization.PipelineBarrier(_vk, command, source, stage, DependencyFlags.ByRegionBit, 0, null, 0, null, (uint)barriers.Count, pointer, BarrierSource.SliceImage);
            }
        }

        // Targets this segment already acquired: the join validates the first use for all of them.
        private readonly List<(ResourceSlotIdentifier Image, ImageRequest Request, ImageView View, CachedImage Cached)> _attachments = new();
        private static long _attachmentsKept;

        internal static long TakeAttachmentsKept() => Interlocked.Exchange(ref _attachmentsKept, 0);

        private bool TryKeptAttachment(ResourceSlotIdentifier imageIdentifier, in ImageRequest request, out ImageView view, out CachedImage image)
        {
            foreach (var kept in _attachments)
            {
                if (kept.Image == imageIdentifier && SliceRequests.SameRequest(kept.Request, request))
                {
                    Interlocked.Increment(ref _attachmentsKept);
                    view = kept.View;
                    image = kept.Cached;
                    return true;
                }
            }

            view = default;
            image = null!;
            return false;
        }

        private void AcquireColorAttachmentForSlice(ColorAttachment target)
        {
            if (TryKeptAttachment(target.ImageIdentifier, target.Request, out var keptView, out var keptImage))
            {
                target.View = keptView;
                target.Image = keptImage;
                var keptRange = target.Request.View;
                TransitionImage(keptImage, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                    new SubresourceRange(keptRange.BaseLevel, keptRange.LevelCount, keptRange.BaseLayer, keptRange.LayerCount), BeginBatchedGuestCommands());
                target.Clear = false;
                target.ClearValue = default;
                return;
            }

            if (_imageCache.IsStaleForSlice(target.ImageIdentifier))
            {
                throw Serial("stale color target");
            }

            if (!_imageCache.TryPrepareColorTargetViewForSlice(target.ImageIdentifier, target.Request, out var view))
            {
                throw Serial("color target acquire", resumable: true);
            }

            var image = Image(target.ImageIdentifier);
            if (image.Backing.Samples != target.Resolution.Samples || view.Handle == 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The color target backing does not match its request: address=0x{target.Address:X16} backingSamples={image.Backing.Samples} samples={target.Resolution.Samples}.");
            }

            target.View = view;
            target.Image = image;
            Requests.AddImage(new ImageSliceUse(target.ImageIdentifier, image, target.Request));
            var range = target.Request.View;
            TransitionImage(image, AccessFlags.ColorAttachmentReadBit | AccessFlags.ColorAttachmentWriteBit,
                new SubresourceRange(range.BaseLevel, range.LevelCount, range.BaseLayer, range.LayerCount), BeginBatchedGuestCommands());
            target.Clear = false;
            target.ClearValue = default;
            var description = target.Request.Description;
            if (description.Metadata.Kind == MetadataKind.Dcc && description.Metadata.Range.Size == 0)
            {
                var metadataAddress = description.Metadata.Range.Address;
                for (uint layer = 0; layer < range.LayerCount; layer++)
                {
                    if (_imageCache.IsMetadataCleared(metadataAddress, range.BaseLayer + layer))
                    {
                        throw Serial("pending dcc clear");
                    }

                    if (description.DccSliceSize != 0 &&
                        _imageCache.TryReadGuestDccClear(metadataAddress, description.DccSliceSize, range.BaseLayer + layer, out _, out _))
                    {
                        throw Serial("guest dcc clear");
                    }
                }
            }

            _attachments.Add((target.ImageIdentifier, target.Request, view, image));
        }

        private DepthAttachmentAcquisition AcquireDepthAttachmentForSlice(in DepthAttachmentState depth)
        {
            var resolution = depth.Target.Target;
            if (_imageCache.IsStaleForSlice(depth.Image))
            {
                throw Serial("stale depth target");
            }

            var layer = resolution.Request.View.BaseLayer;
            if (resolution.HasHtile && (resolution.DepthClearEnabled || _imageCache.IsMetadataCleared(resolution.HtileAddress, layer)))
            {
                throw Serial("depth clear");
            }

            if (TryKeptAttachment(depth.Image, resolution.Request, out var keptView, out var keptImage))
            {
                return new DepthAttachmentAcquisition(keptView, keptImage.Backing.Samples, false);
            }

            if (!_imageCache.TryPrepareDepthTargetViewForSlice(depth.Image, resolution.Request, out var view))
            {
                throw Serial("depth target acquire", resumable: true);
            }

            var image = Image(depth.Image);
            Requests.AddImage(new ImageSliceUse(depth.Image, image, resolution.Request));
            _attachments.Add((depth.Image, resolution.Request, view, image));
            return new DepthAttachmentAcquisition(view, image.Backing.Samples, false);
        }

        private ImageView AcquireTextureViewForSlice(ResourceSlotIdentifier imageIdentifier, in ImageRequest request)
        {
            if (!_imageCache.TryPrepareTextureViewForSlice(imageIdentifier, in request, out var view))
            {
                throw Serial("texture acquire", resumable: true);
            }

            Requests.AddImage(new ImageSliceUse(imageIdentifier, Image(imageIdentifier), request));
            return view;
        }
    }
}
