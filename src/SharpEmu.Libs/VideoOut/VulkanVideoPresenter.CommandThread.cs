// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

internal static partial class VulkanVideoPresenter
{
    // The command stream is interpreted on its own thread, which hands its work to the render thread
    // in stream order. Draws, dispatches, barriers, completions and the interpreter's own stores are
    // queued; a callback that returns a result waits for the render thread to run everything before it.
    private sealed class CommandThread : AgcExports.IDeferredWorkSink
    {
        // The thread runs this far ahead of the render thread at most: 4,096 items, or a larger configured depth, so it keeps
        // interpreting while the render side records and joins slices. Operand reads only drain the queue when a queued write
        // targets them, so the depth mostly costs memory; too shallow and the two threads take turns waiting on each other
        // (128 did, with draws resolving on the pool).
        private static readonly int MaxOutstanding =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_COMMAND_QUEUE_DEPTH"), out var depth) && depth > 4096 ? depth : 4096;

        // A producer waiting for room resumes once the queue is an eighth below its cap rather than half empty, so it
        // keeps overlapping the render side instead of waiting for it to drain.
        private static readonly int ResumeBelow = MaxOutstanding - MaxOutstanding / 8;

        private static readonly int SlicesAheadPerWorker =
            int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_SLICES_AHEAD"), out var ahead) && ahead >= 1 ? ahead : 2;

        internal static string DescribeQueue() =>
            FormattableString.Invariant($"command queue depth={MaxOutstanding} slices ahead per worker={SlicesAheadPerWorker}");
        private const int ShadowSweepCount = 64;
        private const int StorePageShift = 12;

        internal enum Kind : byte
        {
            Deferred,
            Call,
            Write,
        }

        internal enum RangeKind : byte
        {
            None,
            Fill,
            Copy,
            Lod,
        }

        internal sealed class Item
        {
            public Kind Kind;
            public bool IsDraw;
            public AgcExports.DeferredWork? Work;
            public Action? Action;
            public ulong Address;
            public byte[]? Data;
            // The store's length; a pooled Data can be longer.
            public int Length;
            public bool Kernel;
            // A store over more than PendingStorePages.LargePages pages, kept as one range in the page index.
            public bool Large;
            // Data came from StoreBufferPool.Shared and returns there once the store lands.
            public bool Pooled;
            // The guest range a queued fill or copy writes; zero for other items.
            public ulong RangeSize;
            public RangeKind Range;
            public long Sequence;
            public bool Sync;
            public bool CarryableCall;
            public volatile bool Done;
            public Exception? Failure;
        }

        internal enum ShadowSource : byte
        {
            Dispatch,
            Fill,
            Copy,
        }

        private sealed class Shadow
        {
            public ulong Address;
            public int Length;
            public long Sequence;
            public KnownGpuWrite Known = null!;
            public ShadowSource Source;
        }

        private sealed class Unit
        {
            public Item? Item;
            public RenderSlice? Slice;
        }

        private const int SliceItems = 256;
        private const int MinimumSliceItems = 8;
        private const int RestInlineItems = 8;
        private const int RestChunkItems = 32;
        internal const ulong SmallTransferBytes = 64;

        private readonly Presenter _presenter;
        private readonly CommandStreamQueue _queue;
        private readonly ICpuMemory _memory;
        private readonly List<Unit> _units = new();
        private int _unitHead;
        private int _recentHeadMisses, _partnerSize;
        private long _headJoinCost;
        private RenderSlice? _cutting;
        private readonly Action<AgcExports.DeferredWork> _runDeferred;
        private readonly Func<SliceResult> _runSlice;
        private readonly Thread _thread;
        private readonly object _lock = new();
        private List<Item> _incoming = new();
        private List<Item> _draining = new();
        private int _drainIndex;
        private long _drainedSequence;
        private int _queuedSlices;
        private CommandHandoff<Item>? _handoff;
        private long _lastHandoffSequence = -1;
        private Item? _landingStore;
        private int _landingThread, _landingWaiters;
        private int _outstanding;
        private long _enqueued;
        private long _executed;
        private long _waitingForSequence = long.MaxValue;
        // What the producer sleeps on: room in the queue, or its synchronous item.
        private volatile bool _waitingForSpace;
        private volatile bool _waitingForSync;
        private volatile bool _consumerIdle;
        private volatile bool _stopping;
        private volatile Exception? _failure;

        // The interpreter's stores the render thread has not applied yet, oldest first; reads on
        // this thread see them over guest memory.
        private readonly object _writeLock = new();
        private readonly Queue<Item> _pendingWrites = new();
        private readonly PendingStorePages _storePages = new();
        private readonly Dictionary<ulong, Queue<Item>> _storesByPage = new();
        private readonly Stack<Queue<Item>> _spareStoreQueues = new();
        private volatile int _pendingWriteCount;

        // The destinations of queued fills and copies; a read on this thread that overlaps one waits
        // for the render thread to run it.
        private readonly List<Item> _pendingRanges = new();
        private volatile int _pendingRangeCount;

        private readonly List<Shadow> _shadows = new();
        private volatile Shadow[] _shadowSnapshot = [];
        // The newest sequence and the address bounds of the snapshot, published with it: a shadow test returns at once
        // when no shadow can match.
        private volatile ShadowBounds _shadowBounds = ShadowBounds.Empty;

        private sealed record ShadowBounds(long Newest, ulong Low, ulong High)
        {
            public static readonly ShadowBounds Empty = new(long.MinValue, ulong.MaxValue, 0);
        }

        private static ShadowBounds BoundsOf(List<Shadow> shadows)
        {
            if (shadows.Count == 0)
            {
                return ShadowBounds.Empty;
            }

            var newest = long.MinValue;
            var low = ulong.MaxValue;
            var high = 0UL;
            foreach (var shadow in shadows)
            {
                newest = Math.Max(newest, shadow.Sequence);
                low = Math.Min(low, shadow.Address);
                high = Math.Max(high, shadow.Address + (ulong)shadow.Length);
            }

            return new ShadowBounds(newest, low, high);
        }
        private volatile Item[] _pendingRangeSnapshot = [];
        private long _shadowGeneration;

        private static readonly long RetryTicks = CommandStreamQueue.AllBlockedRetryMilliseconds * Stopwatch.Frequency / 1000L;

        public CommandThread(Presenter presenter, CommandStreamQueue queue, ICpuMemory memory, Action<AgcExports.DeferredWork> runDeferred, Func<SliceResult> runSlice)
        {
            _presenter = presenter;
            _queue = queue;
            _memory = memory;
            _runDeferred = runDeferred;
            _runSlice = runSlice;
            _thread = new Thread(Run) { IsBackground = true, Name = "SharpEmu command stream" };
        }

        public void Start() => _thread.Start();

        public bool IsProducerThread => Environment.CurrentManagedThreadId == _thread.ManagedThreadId;

        public bool HasWork => Volatile.Read(ref _outstanding) != 0 || _failure is not null;

        public long NextSequence => Volatile.Read(ref _enqueued) + 1;

        public long Executed => Volatile.Read(ref _executed);

        public long ShadowGeneration => Volatile.Read(ref _shadowGeneration);

        // Whether a queued store of the stream lands on a page of the range.
        public bool HasPendingStores(ulong address, ulong size) => _pendingWriteCount != 0 && _storePages.Overlaps(address, size);

        // A large store has no page queues: a single-page overlay read it overlaps scans the ordered store queue instead.
        private void NotePendingStorePages(Item item, int delta)
        {
            var address = item.Address;
            var length = (ulong)item.Length;
            if (delta > 0)
            {
                _storePages.Add(address, length, item.Large);
            }
            else
            {
                _storePages.Remove(address, length, item.Large);
            }

            if (item.Large)
            {
                return;
            }

            var last = (address + length - 1) >> StorePageShift;
            for (var page = address >> StorePageShift; page <= last; page++)
            {
                if (delta > 0)
                {
                    if (!_storesByPage.TryGetValue(page, out var queued))
                    {
                        queued = _spareStoreQueues.Count != 0 ? _spareStoreQueues.Pop() : new Queue<Item>();
                        _storesByPage.Add(page, queued);
                    }

                    queued.Enqueue(item);
                }
                else if (_storesByPage.TryGetValue(page, out var queued))
                {
                    if (!ReferenceEquals(queued.Dequeue(), item))
                    {
                        throw SubmissionScheduler.Fatal("The command stream store index ran out of order.");
                    }

                    if (queued.Count == 0)
                    {
                        _storesByPage.Remove(page);
                        if (_spareStoreQueues.Count < 4096)
                        {
                            _spareStoreQueues.Push(queued);
                        }
                    }
                }
            }
        }

        // Read by the render loop before it waits, so a queued item wakes it.
        public bool ConsumerIdle
        {
            set => _consumerIdle = value;
        }

        public bool IsAlive => _thread.IsAlive;

        // The producer side, on the command stream thread.

        public long EnqueueDeferred(AgcExports.DeferredWork work, bool isDraw)
        {
            var item = new Item { Kind = Kind.Deferred, Work = work, IsDraw = isDraw };
            Enqueue(item);
            return item.Sequence;
        }

        public void Post(Action action, [CallerMemberName] string caller = "")
        {
            Presenter.NotePost(caller);
            Enqueue(new Item { Kind = Kind.Call, Action = action });
        }

        // An asynchronous call eligible for storage beside the preceding slice. It still cuts recording.
        public void PostBetweenSegments(Action action, [CallerMemberName] string caller = "")
        {
            Presenter.NotePost(caller);
            Enqueue(new Item { Kind = Kind.Call, Action = action, CarryableCall = true });
        }

        public void Run(Action action, [CallerMemberName] string caller = "")
        {
            var started = Stopwatch.GetTimestamp();
            var item = new Item { Kind = Kind.Call, Action = action, Sync = true };
            Enqueue(item);
            var previous = _producerState;
            _producerState = ProducerRunWait;
            WaitFor(item);
            _producerState = previous;
            var elapsed = Stopwatch.GetTimestamp() - started;
            Diagnostics.PacketHandlerTally.WaitedTicks += elapsed;
            Presenter.NoteRoundTrip(caller, elapsed);
        }

        public T Run<T>(Func<T> function, [CallerMemberName] string caller = "")
        {
            T result = default!;
            Run(() => { result = function(); }, caller);
            return result;
        }

        internal const int ProducerBusy = 0;
        internal const int ProducerIdle = 1;
        internal const int ProducerBlocked = 2;
        internal const int ProducerSpaceWait = 3;
        internal const int ProducerRunWait = 4;
        private volatile int _producerState;

        public void PostWrite(ulong address, ReadOnlySpan<byte> data, bool kernel = false) => PostOwnedWrite(address, data.ToArray(), kernel);

        public void PostOwnedWrite(ulong address, byte[] data, bool kernel) => PostStore(address, data, data.Length, kernel, pooled: false);

        // The first length bytes of a buffer rented from StoreBufferPool.Shared, which takes it back once the store lands.
        public void PostPooledWrite(ulong address, byte[] data, int length, bool kernel) => PostStore(address, data, length, kernel, pooled: true);

        private void PostStore(ulong address, byte[] data, int length, bool kernel, bool pooled)
        {
            var item = new Item
            {
                Kind = Kind.Write, Address = address, Data = data, Length = length, Kernel = kernel, Pooled = pooled,
                Large = PendingStorePages.IsLarge(address, (ulong)length),
            };
            lock (_writeLock)
            {
                _pendingWrites.Enqueue(item);
                NotePendingStorePages(item, 1);
                _pendingWriteCount = _pendingWrites.Count;
            }

            Enqueue(item);
        }

        // A fill or copy the render thread runs in order; reads of its destination wait for it.
        public long PostRange(Action action, ulong address, ulong size, RangeKind kind)
        {
            var item = new Item { Kind = Kind.Call, Action = action, Address = address, RangeSize = size, Range = kind };
            if (size != 0)
            {
                lock (_writeLock)
                {
                    _pendingRanges.Add(item);
                    _pendingRangeCount = _pendingRanges.Count;
                    _pendingRangeSnapshot = _pendingRanges.ToArray();
                }
            }

            Enqueue(item);
            return item.Sequence;
        }

        // Runs every queued item when one of them is a fill or copy into this range.
        public void WaitForPendingRange(ulong address, ulong size)
        {
            if (_pendingRangeCount == 0 || !TryFindPendingRange(address, size, out var pending))
            {
                return;
            }

            var started = Diagnostics.CommandThreadTally.Enabled ? Stopwatch.GetTimestamp() : 0;
            Run(static () => { });
            if (started != 0)
            {
                Diagnostics.CommandThreadTally.NoteRangeWait(Stopwatch.GetTimestamp() - started, RangeBucket(pending));
            }
        }

        public bool HasPendingRange(ulong address, ulong size) => _pendingRangeCount != 0 && TryFindPendingRange(address, size, out _);

        public bool HasPendingRangeAfter(ulong address, ulong size, long afterSequence) => HasPendingRangeBetween(address, size, afterSequence, long.MaxValue);

        public bool HasPendingRangeBetween(ulong address, ulong size, long afterSequence, long beforeSequence)
        {
            if (_pendingRangeCount == 0)
            {
                return false;
            }

            lock (_writeLock)
            {
                foreach (var pending in _pendingRanges)
                {
                    if (pending.Sequence > afterSequence && pending.Sequence < beforeSequence &&
                        address < pending.Address + pending.RangeSize && pending.Address < address + size)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        private static int RangeBucket(Item pending) => pending.Range switch
        {
            RangeKind.Fill => pending.RangeSize <= SmallTransferBytes ? 0 : 1,
            RangeKind.Copy => pending.RangeSize <= SmallTransferBytes ? 2 : 3,
            _ => 4,
        };

        private bool TryFindPendingRange(ulong address, ulong size, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Item? found)
        {
            foreach (var pending in _pendingRangeSnapshot)
            {
                if (!pending.Done && address < pending.Address + pending.RangeSize && pending.Address < address + size)
                {
                    found = pending;
                    return true;
                }
            }

            found = null;
            return false;
        }

        public void NoteKnownWrite(KnownGpuWrite known, long sequence) => NoteKnownWrite(known, sequence, ShadowSource.Dispatch);

        public void NoteKnownWrite(KnownGpuWrite known, long sequence, ShadowSource source)
        {
            lock (_writeLock)
            {
                if (_shadows.Count >= ShadowSweepCount)
                {
                    SweepShadows();
                }

                _shadows.Add(new Shadow { Address = known.Address, Length = known.ByteCount, Sequence = sequence, Known = known, Source = source });
                _shadowSnapshot = _shadows.ToArray();
                _shadowBounds = BoundsOf(_shadows);
            }

            Interlocked.Increment(ref _shadowGeneration);
        }

        private void SweepShadows()
        {
            var executed = Volatile.Read(ref _executed);
            var kept = 0;
            for (var index = 0; index < _shadows.Count; index++)
            {
                var shadow = _shadows[index];
                if (shadow.Sequence <= executed)
                {
                    continue;
                }

                _shadows[kept++] = shadow;
            }

            _shadows.RemoveRange(kept, _shadows.Count - kept);
            _shadowSnapshot = _shadows.ToArray();
            _shadowBounds = BoundsOf(_shadows);
        }

        public bool HasShadow(ulong address, ulong size) => HasShadowBefore(address, size, long.MaxValue);

        // A known write of a dispatch between the executed position and this stream position.
        public bool HasShadowBefore(ulong address, ulong size, long sequence)
        {
            var executed = Volatile.Read(ref _executed);
            var end = address + size;
            var bounds = _shadowBounds;
            if (bounds.Newest <= executed || end <= bounds.Low || bounds.High <= address)
            {
                return false;
            }

            foreach (var shadow in _shadowSnapshot)
            {
                if (shadow.Sequence > executed && shadow.Sequence < sequence && address < shadow.Address + (ulong)shadow.Length && shadow.Address < end)
                {
                    return true;
                }
            }

            return false;
        }

        public bool TryReadShadow(ulong address, Span<byte> destination, out long sequence, out ShadowSource source) =>
            TryReadShadowBefore(address, destination, long.MaxValue, out sequence, out source);

        // The newest known write before the stream position that covers the range, with the stores
        // between the two applied.
        public bool TryReadShadowBefore(ulong address, Span<byte> destination, long beforeSequence, out long sequence, out ShadowSource source)
        {
            sequence = 0;
            source = ShadowSource.Dispatch;
            if (!HasShadowBefore(address, (ulong)destination.Length, beforeSequence))
            {
                return false;
            }

            var end = address + (ulong)destination.Length;
            lock (_writeLock)
            {
                var executed = Volatile.Read(ref _executed);
                for (var index = _shadows.Count - 1; index >= 0; index--)
                {
                    var shadow = _shadows[index];
                    var shadowEnd = shadow.Address + (ulong)shadow.Length;
                    if (shadow.Sequence <= executed || shadow.Sequence >= beforeSequence || address >= shadowEnd || end <= shadow.Address)
                    {
                        continue;
                    }

                    if (address < shadow.Address || end > shadowEnd || !shadow.Known.TryCopyWords((int)(address - shadow.Address), destination))
                    {
                        return false;
                    }

                    shadow.Known.NoteRead();
                    sequence = shadow.Sequence;
                    source = shadow.Source;
                    ApplyPendingStoresLocked(address, destination, shadow.Sequence, beforeSequence);
                    return true;
                }
            }

            return false;
        }

        // Guest memory as the stream sees it: with its own stores applied, queued or not.
        public bool ReadOverlay(ulong address, Span<byte> destination)
        {
            WaitForPendingRange(address, (ulong)destination.Length);
            if (!HasPendingStores(address, (ulong)destination.Length))
            {
                return _memory.TryRead(address, destination);
            }

            lock (_writeLock)
            {
                AwaitLanding(address, destination.Length);
                if (!_memory.TryRead(address, destination))
                {
                    return false;
                }

                ApplyPendingStoresLocked(address, destination, 0);
                return true;
            }
        }

        // Tests: ReadOverlay into a new array.
        internal byte[] ReadOverlayForTest(ulong address, int length)
        {
            var bytes = new byte[length];
            return ReadOverlay(address, bytes) ? bytes : throw new InvalidOperationException("The overlay read failed.");
        }

        // Guest memory as a draw at this stream position sees it: the queued stores before it applied,
        // none of the later ones. A queued fill or copy before it into the range cannot be read ahead.
        public bool ReadOverlayBefore(ulong address, Span<byte> destination, long sequence)
        {
            var size = (ulong)destination.Length;
            if (!HasPendingStores(address, size))
            {
                if (_pendingRangeCount != 0)
                {
                    foreach (var pending in _pendingRangeSnapshot)
                    {
                        if (pending.Sequence != 0 && pending.Sequence < sequence && address < pending.Address + pending.RangeSize && pending.Address < address + size)
                        {
                            throw new SerialResolveRequiredException("pending range read");
                        }
                    }
                }

                return _memory.TryRead(address, destination);
            }
            lock (_writeLock)
            {
                AwaitLanding(address, destination.Length);
                foreach (var pending in _pendingRanges)
                {
                    if (pending.Sequence != 0 && pending.Sequence < sequence && address < pending.Address + pending.RangeSize && pending.Address < address + size)
                    {
                        throw new SerialResolveRequiredException("pending range read");
                    }
                }

                if (!_memory.TryRead(address, destination))
                {
                    return false;
                }

                if (_pendingWriteCount != 0)
                {
                    ApplyPendingStoresLocked(address, destination, 0, sequence);
                }

                return true;
            }
        }

        private void ApplyPendingStoresLocked(ulong address, Span<byte> destination, long afterSequence, long beforeSequence = long.MaxValue)
        {
            var end = address + (ulong)destination.Length;
            var page = address >> StorePageShift;
            if (page == (end - 1) >> StorePageShift && (!_storePages.HasLarge || !_storePages.OverlapsLarge(address, end)))
            {
                if (_storesByPage.TryGetValue(page, out var queued))
                {
                    foreach (var write in queued)
                    {
                        ApplyPendingStore(write, address, end, destination, afterSequence, beforeSequence);
                    }
                }

                return;
            }

            foreach (var write in _pendingWrites)
            {
                ApplyPendingStore(write, address, end, destination, afterSequence, beforeSequence);
            }
        }

        private static void ApplyPendingStore(Item write, ulong address, ulong end, Span<byte> destination, long afterSequence, long beforeSequence)
        {
            var writeEnd = write.Address + (ulong)write.Length;
            if (write.Sequence <= afterSequence || write.Address >= end || writeEnd <= address ||
                (beforeSequence != long.MaxValue && (write.Sequence == 0 || write.Sequence >= beforeSequence)))
            {
                return;
            }

            var start = Math.Max(address, write.Address);
            var stop = Math.Min(end, writeEnd);
            write.Data.AsSpan((int)(start - write.Address), (int)(stop - start))
                .CopyTo(destination[(int)(start - address)..]);
        }

        private void Enqueue(Item item)
        {
            ThrowIfFailed();
            if (Volatile.Read(ref _outstanding) >= MaxOutstanding)
            {
                WaitForSpace();
            }

            lock (_lock)
            {
                item.Sequence = ++_enqueued;
                if (item.Kind != Kind.Deferred || _incoming.Count != 0 ||
                    !(_handoff ??= new CommandHandoff<Item>()).TryPublish(item))
                    _incoming.Add(item);
            }

            Interlocked.Increment(ref _outstanding);
            if (_consumerIdle || item.Sync)
            {
                Presenter.WakeRenderThread();
            }
        }

        private void WaitForSpace()
        {
            var previous = _producerState;
            _producerState = ProducerSpaceWait;
            var started = Stopwatch.GetTimestamp();
            try
            {
                WaitForSpaceTimed();
            }
            finally
            {
                _producerState = previous;
                Diagnostics.PacketHandlerTally.WaitedTicks += Stopwatch.GetTimestamp() - started;
            }
        }

        private void WaitForSpaceTimed()
        {
            var started = Diagnostics.CommandThreadTally.Enabled ? Stopwatch.GetTimestamp() : 0;
            try
            {
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.CommandSpaceWait))
                {
                    WaitForSpaceCore();
                }
            }
            finally
            {
                if (started != 0)
                {
                    Diagnostics.CommandThreadTally.NoteSpaceWait(Stopwatch.GetTimestamp() - started);
                }
            }
        }

        private void WaitForSpaceCore()
        {
            lock (_lock)
            {
                // The flag is set before the check: the render thread reads it after it changes what is checked.
                for (;;)
                {
                    _waitingForSpace = true;
                    Interlocked.MemoryBarrier();
                    if (Volatile.Read(ref _outstanding) < ResumeBelow || _failure is not null)
                    {
                        break;
                    }

                    Monitor.Wait(_lock, 10);
                }

                _waitingForSpace = false;
            }

            ThrowIfFailed();
        }

        public void WaitPending(long sequence)
        {
            if (Volatile.Read(ref _executed) >= sequence)
            {
                return;
            }

            var started = Diagnostics.CommandThreadTally.Enabled ? Stopwatch.GetTimestamp() : 0;
            var spinner = new SpinWait();
            for (var spin = 0; spin < 100 && Volatile.Read(ref _executed) < sequence; spin++)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }

            if (Volatile.Read(ref _executed) < sequence)
            {
                lock (_lock)
                {
                    for (;;)
                    {
                        Volatile.Write(ref _waitingForSequence, sequence);
                        Interlocked.MemoryBarrier();
                        if (Volatile.Read(ref _executed) >= sequence || _failure is not null)
                        {
                            break;
                        }

                        Monitor.Wait(_lock, 10);
                    }

                    Volatile.Write(ref _waitingForSequence, long.MaxValue);
                }
            }

            if (started != 0)
            {
                Diagnostics.CommandThreadTally.NotePendingWait(Stopwatch.GetTimestamp() - started);
            }

            ThrowIfFailed();
        }

        private void WaitFor(Item item)
        {
            var spinner = new SpinWait();
            for (var spin = 0; spin < 100 && !item.Done; spin++)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }

            if (!item.Done)
            {
                lock (_lock)
                {
                    for (;;)
                    {
                        _waitingForSync = true;
                        Interlocked.MemoryBarrier();
                        if (item.Done || _failure is not null)
                        {
                            break;
                        }

                        Monitor.Wait(_lock, 10);
                    }

                    _waitingForSync = false;
                }
            }

            if (item.Failure is { } failure)
            {
                throw new InvalidOperationException("A command stream callback failed on the render thread.", failure);
            }

            if (!item.Done)
            {
                ThrowIfFailed();
            }
        }

        // The consumer side, on the render thread. Draws and dispatches are cut into slices that the
        // render workers record ahead; everything is completed in stream order, slices by joining
        // them, other items by running them here. Runs until the deadline or until nothing is left;
        // returns how many items completed.
        public int RunBatch(long deadline, int maxItems, Action betweenItems)
        {
            var ran = 0;
            for (;;)
            {
                FillUnits();
                if (_unitHead == _units.Count)
                {
                    _units.Clear();
                    _unitHead = 0;
                    if (_cutting is null)
                    {
                        _partnerSize = 0;
                        ThrowIfFailed();
                        return ran;
                    }

                    if (_presenter.SlicesEnabled && _cutting.Items.Count >= 2 * RestChunkItems)
                    {
                        SplitTail();
                        Presenter.NoteTail(_producerState);
                    }
                    else if (FlushCutting())
                    {
                        _units[^1].Slice!.Origin = 1;
                        Presenter.NoteTail(_producerState);
                    }

                    continue;
                }

                var unit = _units[_unitHead];
                if (unit.Slice is { } slice)
                {
                    _queuedSlices--;
                    // Leave the trailer at the head. Stolen or refused suffixes insert before
                    // it, and the ordinary budget still applies between the slice and its trailer.
                    if (unit.Item is null) _unitHead++;
                    else unit.Slice = null;
                    ran += JoinSlice(slice, betweenItems);
                }
                else
                {
                    _unitHead++;
                    var item = unit.Item!;
                    var started = Stopwatch.GetTimestamp();
                    Execute(item);
                    CompleteItem(item);
                    NoteItemCost(item, Diagnostics.ItemCostTally.Inline, started);
                    betweenItems();
                    ran++;
                }

                if (ran >= maxItems || ((ran & 31) == 0 && ran != 0 && Stopwatch.GetTimestamp() >= deadline))
                {
                    return ran;
                }
            }
        }

        // A fill cuts slices up to the look-ahead limit rather than a fixed number of items, so the render workers keep
        // recording while the render thread joins and the command thread runs ahead.
        private void FillUnits()
        {
            // The last swap took every item through this sequence. A new post will be seen by the next fill.
            if (_drainIndex == _draining.Count && Volatile.Read(ref _enqueued) == _drainedSequence)
            {
                return;
            }

            var slicing = _presenter.SlicesEnabled;
            var slicesAhead = _queuedSlices;

            var workers = slicing ? _presenter.WorkerCount : 0;
            var limit = SlicesAheadPerWorker * workers;
            var waveSize = workers > 1 ? Math.Max(RestChunkItems, SliceItems / workers) : SliceItems;
            while (slicesAhead <= limit)
            {
                if (_drainIndex == _draining.Count &&
                    (Volatile.Read(ref _enqueued) == _drainedSequence || !RefillDraining()))
                {
                    break;
                }

                var item = _draining[_drainIndex];
                _draining[_drainIndex++] = null!;
                if (slicing && item.Kind == Kind.Deferred)
                {
                    (_cutting ??= new RenderSlice()).Items.Add(item);
                    // Give the first wave to separate workers. Once each lane has a slice, use full
                    // slices again to amortize validation, prologue recording and submission.
                    var target = _partnerSize != 0 ? _partnerSize : slicesAhead < workers ? waveSize : SliceItems;
                    // With no older worker slice, a long run close to the execution cursor can
                    // occupy one lane while the renderer waits for its entire first segment.
                    // Observe comparable heads, then give the two halves to independent lanes.
                    var half = target / 2;
                    var split = false;
                    if (_cutting.Items.Count == half && slicesAhead == 0 && workers > 1 &&
                        _partnerSize == 0 &&
                        _cutting.Items[0].Sequence - _executed - 1 < half)
                    {
                        _cutting.WatchHead = true;
                        split = _recentHeadMisses != 0;
                    }
                    if (split || _cutting.Items.Count >= target)
                    {
                        CloseSlice();
                        if (split)
                        {
                            _partnerSize = target - half;
                        }
                        slicesAhead++;
                    }

                    continue;
                }

                _partnerSize = 0;
                if (_cutting is not null && FlushCutting(slicesAhead, workers))
                {
                    slicesAhead++;
                    // Decide admission from the pure deferred prefix, exactly as for an ordinary
                    // cut. The observation never reaches a worker or promotes a short run.
                    if (item.Kind == Kind.Write || item.CarryableCall)
                    {
                        _units[^1].Item = item;
                        continue;
                    }
                }

                _units.Add(new Unit { Item = item });
            }

            // The producer has temporarily run dry. Give an already substantial tail to
            // a free lane while the render thread consumes older units. Keep larger tails
            // eligible for the balanced split in RunBatch.
            if (slicesAhead < workers && _unitHead < _units.Count &&
                _cutting?.Items.Count is >= RestChunkItems and < 2 * RestChunkItems)
            {
                CloseSlice();
            }
        }

        private bool RefillDraining()
        {
            _draining.Clear();
            _drainIndex = 0;
            if (Volatile.Read(ref _enqueued) == _lastHandoffSequence) return false;
            if (DrainHandoff()) return true;
            lock (_lock)
            {
                // A producer may have published between the empty check and this lock.
                if (DrainHandoff()) return true;
                (_incoming, _draining) = (_draining, _incoming);
                _drainedSequence = _enqueued;
            }

            return _draining.Count != 0;
        }

        private bool FlushCutting(int slicesAhead = 0, int workers = 0)
        {
            var slice = _cutting!;
            if (slice.Items.Count >= MinimumSliceItems)
            {
                CloseSlice();
                return true;
            }

            // Each unjoined slice occupies at most one lane. Only start a short one when
            // there is spare capacity and earlier work to overlap. FIFO keeps older slices first.
            if (slicesAhead < workers && _unitHead < _units.Count && WorthRecordingShortGroup(slice))
            {
                CloseSlice();
                return true;
            }

            _cutting = null;
            foreach (var item in slice.Items)
            {
                _units.Add(new Unit { Item = item });
            }

            _serialItems.Add(slice.Items.Count);
            return false;
        }

        private static bool WorthRecordingShortGroup(RenderSlice slice)
        {
            // Amortize the extra cut, validation, prologue and submit. Recording a draw inline
            // costs at least two dispatches; a barrier alone costs very little.
            var weight = 0;
            foreach (var item in slice.Items)
            {
                if (item.Work is AgcExports.GlobalBarrierWork) continue;
                weight += item.Work!.IsDispatch ? 1 : 2;
                if (weight >= 4) return true;
            }
            return false;
        }

        private void SplitTail()
        {
            _partnerSize = 0;
            var tail = _cutting!;
            _cutting = null;
            var chunks = (tail.Items.Count + RestChunkItems - 1) / RestChunkItems;
            var start = 0;
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var end = (int)((long)tail.Items.Count * (chunk + 1) / chunks);
                var part = new RenderSlice { Origin = 1 };
                part.Items.AddRange(tail.Items.GetRange(start, end - start));
                _units.Add(new Unit { Slice = part });
                _queuedSlices++;
                _presenter.DispatchSlice(part, urgent: true);
                start = end;
            }
        }

        private void CloseSlice()
        {
            _partnerSize = 0;
            var slice = _cutting!;
            _cutting = null;
            _units.Add(new Unit { Slice = slice });
            _queuedSlices++;
            _presenter.DispatchSlice(slice);
        }

        private int JoinSlice(RenderSlice slice, Action betweenItems)
        {
            var ran = 0;
            var suffixQueued = false;
            if (!slice.Dispatched)
            {
                _presenter.DispatchSlice(slice);
            }

            if (slice.TryClaim())
            {
                if (slice.WatchHead && slice.Origin == 0) NoteHeadWait(false);
                var stolenInline = slice.Items.Count;
                if (SplitStolenSlices && _presenter.SlicesEnabled && stolenInline >= RestInlineItems + MinimumSliceItems)
                {
                    stolenInline = RestInlineItems;
                    QueueRestChunks(slice, stolenInline);
                }

                Presenter.NoteStolen(slice, stolenInline);
                slice.Done.Set();
                try
                {
                    for (var position = 0; position < stolenInline; position++)
                    {
                        RunInline(slice.Items[position], betweenItems);
                        ran++;
                    }
                }
                finally
                {
                    _presenter.FinishSlice(slice);
                }

                return ran;
            }

            try
            {
                var watchHead = slice.WatchHead && slice.Origin == 0;
                while (true)
                {
                    if (_presenter.NextSegment(slice, betweenItems) is not { } segment)
                    {
                        if (watchHead) NoteHeadWait(false);
                        break;
                    }
                    var end = segment.First + segment.Count;
                    var joinStarted = Stopwatch.GetTimestamp();
                    var joined = _presenter.JoinSegment(slice, segment);
                    var joinTicks = Presenter.NoteJoin(joinStarted);
                    if (watchHead)
                    {
                        NoteHeadWait(joined && slice.HeadWaitTicks > _headJoinCost);
                        if (joined) _headJoinCost = joinTicks;
                        slice.WatchHead = watchHead = false;
                    }
                    if (joined)
                    {
                        for (var position = segment.First; position < end; position++)
                        {
                            var item = slice.Items[position];
                            _presenter._translation.CompleteDeferred(item.Work!);
                            CompleteItem(item);
                            Diagnostics.ItemCostTally.Note(item.Work!.CostKind, Diagnostics.ItemCostTally.Joined, 0);
                            ran++;
                        }

                        betweenItems();
                    }
                    else
                    {
                        for (var position = segment.First; position < end; position++)
                        {
                            RunInline(slice.Items[position], betweenItems);
                            ran++;
                        }
                    }

                    if (segment.SerialReason is not null)
                    {
                        // The worker published its final segment and will never touch the suffix.
                        // Queue it before the serial item runs; its commands still join after that item.
                        var suffix = end + 1;
                        if (segment.StopsRecording && _presenter.SlicesEnabled &&
                            slice.Items.Count - suffix >= MinimumSliceItems)
                        {
                            QueueRestChunks(slice, suffix);
                            suffixQueued = true;
                        }
                        RunInline(slice.Items[end], betweenItems);
                        ran++;
                    }
                }
            }
            finally
            {
                _presenter.FinishSlice(slice);
            }

            var remaining = slice.Items.Count - slice.Resume;
            if (remaining == 0 || suffixQueued)
            {
                return ran;
            }

            var inline = slice.Items.Count;
            if (_presenter.SlicesEnabled && remaining >= RestInlineItems + MinimumSliceItems)
            {
                inline = slice.Resume + RestInlineItems;
                QueueRestChunks(slice, inline);
            }

            for (var position = slice.Resume; position < inline; position++)
            {
                RunInline(slice.Items[position], betweenItems);
                ran++;
            }

            return ran;
        }

        private static readonly bool SplitStolenSlices = Environment.GetEnvironmentVariable("SHARPEMU_SPLIT_STOLEN_SLICES") != "0";

        // Two ready heads age out a miss, restoring the normal wave size.
        private void NoteHeadWait(bool missed) =>
            _recentHeadMisses = ((_recentHeadMisses << 1) | (missed ? 1 : 0)) & 3;

        private void QueueRestChunks(RenderSlice slice, int inline)
        {
            var chunks = (slice.Items.Count - inline + RestChunkItems - 1) / RestChunkItems;
            var start = inline;
            for (var chunk = 0; chunk < chunks; chunk++)
            {
                var end = inline + (int)((long)(slice.Items.Count - inline) * (chunk + 1) / chunks);
                var rest = new RenderSlice { Origin = 2 };
                rest.Items.AddRange(slice.Items.GetRange(start, end - start));
                _units.Insert(_unitHead + chunk, new Unit { Slice = rest });
                _queuedSlices++;
                _presenter.DispatchSlice(rest, urgent: true);
                Presenter.NoteRestChunk(rest.Items.Count);
                start = end;
            }
        }

        private static void NoteItemCost(Item item, int stage, long started) =>
            Diagnostics.ItemCostTally.Note(item.Work?.CostKind ?? Diagnostics.ItemCostTally.Other, stage, Stopwatch.GetTimestamp() - started);

        private void RunInline(Item item, Action betweenItems)
        {
            var started = Stopwatch.GetTimestamp();
            Execute(item);
            CompleteItem(item);
            NoteItemCost(item, Diagnostics.ItemCostTally.Inline, started);
            betweenItems();
            _serialItems.Increment();
            _inlineTicks.Add(Stopwatch.GetTimestamp() - started);
        }

        private void CompleteItem(Item item)
        {
            Volatile.Write(ref _executed, item.Sequence);
            var outstanding = Interlocked.Decrement(ref _outstanding);
            // One pulse per wait: the producer sets its flag again if it keeps waiting.
            var wakeForSync = item.Sync && _waitingForSync;
            var wakeForSpace = _waitingForSpace && outstanding < ResumeBelow;
            var wakeForSequence = item.Sequence >= Volatile.Read(ref _waitingForSequence);
            if (wakeForSync || wakeForSpace || wakeForSequence)
            {
                if (wakeForSync)
                {
                    _waitingForSync = false;
                }

                if (wakeForSpace)
                {
                    _waitingForSpace = false;
                }

                if (wakeForSequence)
                {
                    Volatile.Write(ref _waitingForSequence, long.MaxValue);
                }

                lock (_lock)
                {
                    Monitor.PulseAll(_lock);
                }
            }
        }

        private void Execute(Item item)
        {
            if (item.IsDraw)
            {
                _runDeferred(item.Work!);
                return;
            }

            try
            {
                switch (item.Kind)
                {
                    case Kind.Deferred:
                        _runDeferred(item.Work!);
                        break;
                    case Kind.Call:
                        _presenter._bufferCache.NoteObservation();
                        item.Action!();
                        break;
                    case Kind.Write:
                        _presenter._bufferCache.NoteObservation();
                        // The store can be an end-of-pipe label that tells the guest earlier GPU writes are done. Their
                        // pages must block CPU reads before it lands, not only at the next submission.
                        _presenter._bufferCache.ApplyPendingGpuProtections();
                        ApplyWrite(item);
                        break;
                }
            }
            catch (Exception exception) when (item.Sync)
            {
                item.Failure = exception;
            }
            catch (Exception exception)
            {
                _failure ??= exception;
                throw;
            }
            finally
            {
                if (item.RangeSize != 0)
                {
                    lock (_writeLock)
                    {
                        _pendingRanges.Remove(item);
                        _pendingRangeCount = _pendingRanges.Count;
                        _pendingRangeSnapshot = _pendingRanges.ToArray();
                    }
                }

                if (item.Sync)
                {
                    item.Done = true;
                }
            }
        }

        private void ApplyWrite(Item item)
        {
            lock (_writeLock)
            {
                var data = item.Data.AsSpan(0, item.Length);
                if (item.Kernel && _presenter.TryApplyKernelWriteOverGpuBytes(item.Address, data))
                {
                    RetireStoreLocked(item, true);
                    return;
                }
                if (_memory is not IGuestBackedSpace { SupportsDisjointPageOffsetAccess: true })
                {
                    RetireStoreLocked(item, _memory.TryWrite(item.Address, data));
                    return;
                }
                _landingStore = item;
                _landingThread = Environment.CurrentManagedThreadId;
            }

            bool? written = null;
            try
            {
                written = _memory.TryWrite(item.Address, item.Data.AsSpan(0, item.Length));
            }
            finally
            {
                lock (_writeLock)
                {
                    _landingStore = null;
                    if (_landingWaiters != 0) Monitor.PulseAll(_writeLock);
                    if (written.HasValue) RetireStoreLocked(item, written.Value);
                }
            }
        }

        private void AwaitLanding(ulong address, int length)
        {
            while (_landingStore is { } store && _landingThread != Environment.CurrentManagedThreadId)
            {
                if (CommandStoreLanding.DisjointOffsets(address, length, store.Address, store.Length))
                {
                    break;
                }
                _landingWaiters++;
                try { Monitor.Wait(_writeLock); }
                finally { _landingWaiters--; }
            }
        }

        private void RetireStoreLocked(Item item, bool written)
        {
            if (!ReferenceEquals(_pendingWrites.Peek(), item))
            {
                throw SubmissionScheduler.Fatal("The command stream stores ran out of order.");
            }

            _pendingWrites.Dequeue();
            NotePendingStorePages(item, -1);
            _pendingWriteCount = _pendingWrites.Count;
            if (!written)
            {
                throw SubmissionScheduler.Fatal(
                    $"The command stream cannot write guest memory: address=0x{item.Address:X16} size={item.Length}.");
            }

            // No reader reaches the store once it left the queue and the index under this lock.
            if (item.Pooled)
            {
                StoreBufferPool.Shared.Return(item.Data!);
                item.Data = null;
            }
        }

        private bool DrainHandoff()
        {
            if (Volatile.Read(ref _handoff)?.Drain(_draining) != true) return false;
            _lastHandoffSequence = _draining[^1].Sequence;
            return true;
        }

        public void ReportFailure(Exception exception) => Interlocked.CompareExchange(ref _failure, exception, null);

        public void ThrowIfFailed()
        {
            if (_failure is { } failure)
            {
                throw new InvalidOperationException("The command stream thread failed.", failure);
            }
        }

        // Ends the thread after its current slice; the render thread keeps running its items
        // meanwhile, since the slice may wait for one.
        public void Stop(Action runItems)
        {
            _stopping = true;
            _queue.Wake();
            var deadline = Stopwatch.GetTimestamp() + 5 * Stopwatch.Frequency;
            while (_thread.IsAlive && Stopwatch.GetTimestamp() < deadline)
            {
                runItems();
                _thread.Join(1);
            }

            runItems();
        }

        private void Run()
        {
            SharpEmu.HLE.Host.HostLaneReservation.ApplyToFrontEndThread();
            RenderPhaseProfile.AttachResolveWorker();
            try
            {
                var lastProgress = Stopwatch.GetTimestamp();
                var tally = Diagnostics.CommandThreadTally.Enabled;
                while (!_stopping)
                {
                    var started = tally ? Stopwatch.GetTimestamp() : 0;
                    var allocated = tally ? GC.GetAllocatedBytesForCurrentThread() : 0;
                    _producerState = ProducerBusy;
                    var result = _runSlice();
                    if (tally)
                    {
                        var now = Stopwatch.GetTimestamp();
                        Diagnostics.CommandThreadTally.NoteSlice(now - started);
                        Diagnostics.CommandThreadTally.NoteAllocated(GC.GetAllocatedBytesForCurrentThread() - allocated);
                        DcbParseProfile.RecordParse(0, now - started, false);
                        started = now;
                    }

                    switch (result)
                    {
                        case SliceResult.NoWork:
                            _producerState = ProducerIdle;
                            _ = _queue.WaitForWork(8);
                            if (tally)
                            {
                                Diagnostics.CommandThreadTally.NoteIdle(Stopwatch.GetTimestamp() - started);
                            }

                            break;
                        case SliceResult.AllBlocked:
                            _producerState = ProducerBlocked;
                            WaitBlocked(ref lastProgress);
                            if (tally)
                            {
                                Diagnostics.CommandThreadTally.NoteBlocked(Stopwatch.GetTimestamp() - started);
                            }

                            break;
                        case SliceResult.BlockedWithoutProgress:
                            if (!_queue.HasUnblockedPending)
                            {
                                _producerState = ProducerBlocked;
                                WaitBlocked(ref lastProgress);
                                if (tally)
                                {
                                    Diagnostics.CommandThreadTally.NoteBlocked(Stopwatch.GetTimestamp() - started);
                                }
                            }

                            break;
                        default:
                            lastProgress = Stopwatch.GetTimestamp();
                            break;
                    }
                }
            }
            catch (Exception exception)
            {
                _failure ??= exception;
                Presenter.WakeRenderThread();
            }
        }

        // Blocked heads retry when a GPU completion or a presentation unblocks them, or every
        // retry interval without progress.
        private void WaitBlocked(ref long lastProgress)
        {
            var remaining = RetryTicks - (Stopwatch.GetTimestamp() - lastProgress);
            if (remaining <= 0)
            {
                _queue.RetryBlocked();
                lastProgress = Stopwatch.GetTimestamp();
                return;
            }

            _ = _queue.WaitForRetryInterval((int)Math.Max(1, remaining * 1000 / Stopwatch.Frequency));
        }
    }
}
