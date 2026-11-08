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
        // The thread runs this far ahead of the render thread at most. Operand reads only drain the
        // queue when a queued write targets them, so the depth mostly costs memory; too shallow and the
        // two threads take turns waiting on each other (128 did, with draws resolving on the pool).
        private const int MaxOutstanding = 1024;
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
            public bool Kernel;
            // The guest range a queued fill or copy writes; zero for other items.
            public ulong RangeSize;
            public RangeKind Range;
            public long Sequence;
            public bool Sync;
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
        private RenderSlice? _cutting;
        private readonly Action<AgcExports.DeferredWork> _runDeferred;
        private readonly Func<SliceResult> _runSlice;
        private readonly Thread _thread;
        private readonly object _lock = new();
        private List<Item> _incoming = new();
        private List<Item> _draining = new();
        private int _drainIndex;
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
        private readonly ConcurrentDictionary<ulong, int> _pendingStorePages = new();
        private volatile int _pendingWriteCount;

        // The destinations of queued fills and copies; a read on this thread that overlaps one waits
        // for the render thread to run it.
        private readonly List<Item> _pendingRanges = new();
        private volatile int _pendingRangeCount;

        private readonly List<Shadow> _shadows = new();
        private volatile Shadow[] _shadowSnapshot = [];
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
        public bool HasPendingStores(ulong address, ulong size)
        {
            if (_pendingWriteCount == 0)
            {
                return false;
            }

            var last = (address + size - 1) >> StorePageShift;
            for (var page = address >> StorePageShift; page <= last; page++)
            {
                if (_pendingStorePages.ContainsKey(page))
                {
                    return true;
                }
            }

            return false;
        }

        private void NotePendingStorePages(ulong address, int length, int delta)
        {
            var last = (address + (ulong)length - 1) >> StorePageShift;
            for (var page = address >> StorePageShift; page <= last; page++)
            {
                if (_pendingStorePages.AddOrUpdate(page, delta, (_, count) => count + delta) == 0)
                {
                    _pendingStorePages.TryRemove(new KeyValuePair<ulong, int>(page, 0));
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

        public void PostWrite(ulong address, ReadOnlySpan<byte> data, bool kernel = false)
        {
            var item = new Item { Kind = Kind.Write, Address = address, Data = data.ToArray(), Kernel = kernel };
            lock (_writeLock)
            {
                _pendingWrites.Enqueue(item);
                NotePendingStorePages(address, item.Data.Length, 1);
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
        }

        public bool HasShadow(ulong address, ulong size) => HasShadowBefore(address, size, long.MaxValue);

        // A known write of a dispatch between the executed position and this stream position.
        public bool HasShadowBefore(ulong address, ulong size, long sequence)
        {
            var executed = Volatile.Read(ref _executed);
            var end = address + size;
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
                if (!_memory.TryRead(address, destination))
                {
                    return false;
                }

                ApplyPendingStoresLocked(address, destination, 0);
                return true;
            }
        }

        // Guest memory as a draw at this stream position sees it: the queued stores before it applied,
        // none of the later ones. A queued fill or copy before it into the range cannot be read ahead.
        public bool ReadOverlayBefore(ulong address, Span<byte> destination, long sequence)
        {
            var size = (ulong)destination.Length;
            if (_pendingRangeCount == 0 && !HasPendingStores(address, size))
            {
                return _memory.TryRead(address, destination);
            }
            lock (_writeLock)
            {
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
            foreach (var write in _pendingWrites)
            {
                var writeEnd = write.Address + (ulong)write.Data!.Length;
                if (write.Sequence <= afterSequence || write.Address >= end || writeEnd <= address ||
                    (beforeSequence != long.MaxValue && (write.Sequence == 0 || write.Sequence >= beforeSequence)))
                {
                    continue;
                }

                var start = Math.Max(address, write.Address);
                var stop = Math.Min(end, writeEnd);
                write.Data.AsSpan((int)(start - write.Address), (int)(stop - start))
                    .CopyTo(destination[(int)(start - address)..]);
            }
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
                    if (Volatile.Read(ref _outstanding) < MaxOutstanding / 2 || _failure is not null)
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
                FillUnits(maxItems);
                if (_unitHead == _units.Count)
                {
                    _units.Clear();
                    _unitHead = 0;
                    if (_cutting is null)
                    {
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

                var unit = _units[_unitHead++];
                if (unit.Slice is { } slice)
                {
                    ran += JoinSlice(slice, betweenItems);
                }
                else
                {
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

        private void FillUnits(int maxItems)
        {
            var slicing = _presenter.SlicesEnabled;
            var slicesAhead = 0;
            for (var index = _unitHead; index < _units.Count; index++)
            {
                if (_units[index].Slice is not null)
                {
                    slicesAhead++;
                }
            }

            var limit = slicing ? 2 * _presenter.WorkerCount : 0;
            var pulled = 0;
            while (slicesAhead <= limit && pulled < maxItems)
            {
                if (_drainIndex == _draining.Count && !RefillDraining())
                {
                    break;
                }

                var item = _draining[_drainIndex];
                _draining[_drainIndex++] = null!;
                pulled++;
                if (slicing && item.Kind == Kind.Deferred)
                {
                    (_cutting ??= new RenderSlice()).Items.Add(item);
                    if (_cutting.Items.Count >= SliceItems)
                    {
                        CloseSlice();
                        slicesAhead++;
                    }

                    continue;
                }

                if (_cutting is not null && FlushCutting())
                {
                    slicesAhead++;
                }

                _units.Add(new Unit { Item = item });
            }
        }

        private bool RefillDraining()
        {
            _draining.Clear();
            _drainIndex = 0;
            lock (_lock)
            {
                (_incoming, _draining) = (_draining, _incoming);
            }

            return _draining.Count != 0;
        }

        private bool FlushCutting()
        {
            var slice = _cutting!;
            if (slice.Items.Count >= MinimumSliceItems)
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

        private void SplitTail()
        {
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
                _presenter.DispatchSlice(part, urgent: true);
                start = end;
            }
        }

        private void CloseSlice()
        {
            var slice = _cutting!;
            _cutting = null;
            _units.Add(new Unit { Slice = slice });
            _presenter.DispatchSlice(slice);
        }

        private int JoinSlice(RenderSlice slice, Action betweenItems)
        {
            var ran = 0;
            if (!slice.Dispatched)
            {
                _presenter.DispatchSlice(slice);
            }

            if (slice.TryClaim())
            {
                Presenter.NoteStolen(slice);
                slice.Done.Set();
                try
                {
                    foreach (var item in slice.Items)
                    {
                        RunInline(item, betweenItems);
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
                while (_presenter.NextSegment(slice, betweenItems) is { } segment)
                {
                    var end = segment.First + segment.Count;
                    var joinStarted = Stopwatch.GetTimestamp();
                    var joined = _presenter.JoinSegment(slice, segment);
                    Presenter.NoteJoin(joinStarted);
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
            if (remaining == 0)
            {
                return ran;
            }

            var inline = slice.Items.Count;
            if (_presenter.SlicesEnabled && remaining >= RestInlineItems + MinimumSliceItems)
            {
                inline = slice.Resume + RestInlineItems;
                var chunks = (slice.Items.Count - inline + RestChunkItems - 1) / RestChunkItems;
                var start = inline;
                for (var chunk = 0; chunk < chunks; chunk++)
                {
                    var end = inline + (int)((long)(slice.Items.Count - inline) * (chunk + 1) / chunks);
                    var rest = new RenderSlice { Origin = 2 };
                    rest.Items.AddRange(slice.Items.GetRange(start, end - start));
                    _units.Insert(_unitHead + chunk, new Unit { Slice = rest });
                    _presenter.DispatchSlice(rest, urgent: true);
                    Presenter.NoteRestChunk(rest.Items.Count);
                    start = end;
                }
            }

            for (var position = slice.Resume; position < inline; position++)
            {
                RunInline(slice.Items[position], betweenItems);
                ran++;
            }

            return ran;
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
            var wakeForSpace = _waitingForSpace && outstanding < MaxOutstanding / 2;
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
                var written = (item.Kernel && _presenter.TryApplyKernelWriteOverGpuBytes(item.Address, item.Data!)) || _memory.TryWrite(item.Address, item.Data);
                if (!ReferenceEquals(_pendingWrites.Peek(), item))
                {
                    throw SubmissionScheduler.Fatal("The command stream stores ran out of order.");
                }

                _pendingWrites.Dequeue();
                NotePendingStorePages(item.Address, item.Data!.Length, -1);
                _pendingWriteCount = _pendingWrites.Count;
                if (!written)
                {
                    throw SubmissionScheduler.Fatal(
                        $"The command stream cannot write guest memory: address=0x{item.Address:X16} size={item.Data!.Length}.");
                }
            }
        }

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
