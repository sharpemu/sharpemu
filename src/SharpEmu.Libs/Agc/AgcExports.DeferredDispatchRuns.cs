// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;

namespace SharpEmu.Libs.Agc;

public static partial class AgcExports
{
    // A direct dispatch the command thread issued to the deferred dispatch workers. It is queued at its stream position at
    // issue; Done is set when the finalizer, in stream order, found it valid or cancelled it. A cancelled member records
    // nothing: the command thread runs it again after the rollback.
    internal sealed record DeferredDispatch : DeferredWork
    {
        public override bool IsDispatch => true;

        public readonly DeferredDispatchInputs Inputs = new();
        public ulong SubmitId;
        public long Sequence;
        public int Index;
        public int TableMark;
        public int Depth;
        public ulong CursorAddress;
        public uint PacketOffset;
        public int RawUndoMark;
        public uint UserDataMarker;
        public long FrontendImageVersion;
        public ResolvedDispatch? Record;
        // The worker could not resolve the member: the run rolls back to it and the command thread runs it.
        public bool Refused;
        public bool Cancelled;
        // 1 once the worker no longer touches the member.
        public int Resolved;
        public readonly List<PendingMark> Marks = new();
        public readonly ResolveReadLog Reads = new();
        public readonly ManualResetEventSlim Done = new(false);

        public void Reset()
        {
            Inputs.Reset();
            Record = null;
            Refused = false;
            Cancelled = false;
            Resolved = 0;
            FrontendImageVersion = 0;
            Marks.Clear();
            Reads.ClearForPool();
            Done.Reset();
        }
    }

    internal sealed partial class CommandStreamTranslation
    {
        // Six workers, but never more than half the logical processors.
        private static readonly int DeferredDispatchWorkers = Math.Clamp(Environment.ProcessorCount / 2, 1, 6);

        private DeferredDispatchRuns? _dispatchRuns;

        internal IDeferredDispatchRuns? DispatchRuns => _dispatchRuns;

        private void ReplaceDispatchRuns(bool create)
        {
            _dispatchRuns?.Dispose();
            _dispatchRuns = create && _executor is not null && _pipelines is { SupportsParallelResolution: true } && _host.RenderHost is { } renderHost
                ? new DeferredDispatchRuns(this, renderHost, DeferredDispatchWorkers)
                : null;
        }

        private static void WaitDone(DeferredDispatch deferred)
        {
            var spinner = new SpinWait();
            while (!deferred.Done.IsSet && spinner.Count < 200)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }

            deferred.Done.Wait();
        }

        private bool ValidateDispatchReads(DeferredDispatch deferred) =>
            deferred.FrontendImageVersion == _pipelines!.FrontendImageVersion && _host.ValidateWorkerReads(deferred.Reads);

        // On a render worker: a cancelled member records nothing; a valid one records the worker's record while its reads hold.
        private bool CanRecordDeferredDispatch(DeferredDispatch deferred)
        {
            WaitDone(deferred);
            return deferred.Cancelled || ValidateDispatchReads(deferred);
        }

        private void RecycleDispatch(DeferredDispatch deferred)
        {
            if (Volatile.Read(ref deferred.Resolved) != 1 || _dispatchRuns is not { } runs)
            {
                // The worker may still hold a cancelled member; the collector takes it.
                return;
            }

            deferred.Reset();
            runs.Return(deferred);
        }

        private void CompleteDeferredDispatch(DeferredDispatch deferred)
        {
            _executor!.PendingWrites.Release(deferred.Sequence);
            if (Volatile.Read(ref deferred.Resolved) == 1 && deferred.Record is { } record)
            {
                _pool!.Return(record);
                deferred.Record = null;
            }

            RecycleDispatch(deferred);
        }

        // On the render thread at the member's stream position.
        private void JoinDeferredDispatch(DeferredDispatch deferred)
        {
            WaitDone(deferred);
            if (deferred.Cancelled)
            {
                _executor!.PendingWrites.Release(deferred.Sequence);
                if (Volatile.Read(ref deferred.Resolved) == 1 && deferred.Record is { } dropped)
                {
                    _pool!.Return(dropped);
                    deferred.Record = null;
                }

                RecycleDispatch(deferred);
                return;
            }

            var record = deferred.Record!;
            if (!ValidateDispatchReads(deferred))
            {
                _pool!.Return(record);
                record = ResolveDispatchOnThisThread(deferred);
            }

            deferred.Record = null;
            _recorder!.Record(record);
            RecycleDispatch(deferred);
        }

        // The member resolved on the render thread from its inputs, as the command thread would have resolved it.
        private ResolvedDispatch ResolveDispatchOnThisThread(DeferredDispatch deferred)
        {
            var executor = _joinExecutor ??= new RenderExecutor(_host.RenderHost!, _pipelines!, _joinSink ??= new CapturingSink(), _executor!.PendingWrites, _pool);
            var inputs = deferred.Inputs;
            if (!_pipelines!.RunDeferredRebind(inputs, out var program))
            {
                program = new ComputeProgram { Available = false };
            }

            var record = executor.ResolveDeferredDispatch(deferred.SubmitId, inputs.Compute, inputs.GroupsX, inputs.GroupsY, inputs.GroupsZ, inputs.Initiator,
                program, serialOnly: true) ?? throw _host.Fatal("The serial resolve of a deferred dispatch produced no record.");
            record.Sequence = deferred.Sequence;
            return record;
        }

        // Issues, resolves and finalizes deferred dispatch runs. From the third direct dispatch of one kept compute program
        // in a row, the command thread copies each dispatch's compute registers and queues it at its stream position; an open
        // run then takes every deferrable dispatch, whatever its program. Workers rebind and decode the members in any order.
        // Whichever thread finishes a member finalizes every member that is ready, in stream order, under the finalize lock:
        // it checks the member's reads against the marks of the members before it and publishes the member's own write
        // marks. A shader table the command thread applied during the run is logged; a member found to write one, or a
        // member a worker could not resolve, rolls the run back to that point. The command thread waits for the run at the
        // barrier before any packet a run cannot hold.
        private sealed class DeferredDispatchRuns : IDeferredDispatchRuns, IDisposable
        {
            private const int Capacity = 1024;
            private const int TableCapacity = 4096;
            private const uint DispatchPacketDwords = 5;

            private readonly CommandStreamTranslation _owner;
            private readonly Thread[] _threads;
            private readonly ConcurrentQueue<DeferredDispatch> _queue = new();
            private readonly SemaphoreSlim _wake = new(0);
            private readonly ConcurrentQueue<DeferredDispatch> _spare = new();
            private int _parked;
            private volatile bool _stopping;

            // The open run. The command thread writes _items, _issued and the table log; the finalizer reads them and writes
            // _cursor, _poison and _rewind under _finalize.
            private readonly DeferredDispatch?[] _items = new DeferredDispatch?[Capacity];
            private int _issued;
            private int _cursor;
            private int _poison = int.MaxValue;
            private DeferredRewind? _rewind;
            private readonly (ulong Address, ulong Size, int Member)[] _tables = new (ulong, ulong, int)[TableCapacity];
            private int _tableCount;
            private readonly object _finalize = new();
            private readonly ManualResetEventSlim _drained = new(true);

            // Command thread only.
            private RenderExecutor? _helper;
            private bool _suppressed;
            private KeptComputeProgram? _lastKept;
            private int _sameCount;

            public DeferredDispatchRuns(CommandStreamTranslation owner, IRenderHost host, int workers)
            {
                _owner = owner;
                _threads = new Thread[workers];
                for (var index = 0; index < workers; index++)
                {
                    var executor = new RenderExecutor(host, owner._pipelines!, new CapturingSink(), owner._executor!.PendingWrites, owner._pool);
                    _threads[index] = new Thread(() => Run(executor)) { IsBackground = true, Name = $"SharpEmu deferred dispatch worker {index}" };
                    _threads[index].Start();
                }
            }

            public bool IsOpen => _issued != 0;

            public bool Active => _issued != 0 || _suppressed;

            public bool StopsTicketScan => !_suppressed;

            public bool WouldDefer(ComputeStageRegisters compute, uint initiator) =>
                compute.Address != 0 && _owner._pipelines!.FindDeferrableKept(compute, initiator) is not null;

            public void Return(DeferredDispatch deferred) => _spare.Enqueue(deferred);

            public void BreakChain()
            {
                _lastKept = null;
                _sameCount = 0;
            }

            public DeferredIssue TryIssue(in DeferredIssueSite site, uint groupsX, uint groupsY, uint groupsZ, uint initiator)
            {
                if (Volatile.Read(ref _poison) != int.MaxValue)
                {
                    return DeferredIssue.Rewind;
                }

                if (_suppressed)
                {
                    return DeferredIssue.NotEligible;
                }

                var state = _owner.RequireCurrent();
                var compute = _owner.RequireTypedRegisters(state).Shader.Compute;
                if (compute.Address == 0 || RenderExecutor.IsEmptyDirectDispatch(compute, initiator, groupsX, groupsY, groupsZ))
                {
                    BreakChain();
                    return DeferredIssue.NotEligible;
                }

                var kept = _owner._pipelines!.FindDeferrableKept(compute, initiator);
                if (kept is null)
                {
                    BreakChain();
                    return DeferredIssue.NotEligible;
                }

                // An open run takes any deferrable dispatch; only a run's start needs three of one program in a row.
                var joins = _issued != 0;
                if (!ReferenceEquals(kept, _lastKept))
                {
                    _lastKept = kept;
                    _sameCount = 1;
                    if (!joins)
                    {
                        return DeferredIssue.NotEligible;
                    }
                }
                else if (++_sameCount < 3 && !joins)
                {
                    return DeferredIssue.NotEligible;
                }

                if (_issued == Capacity || _tableCount >= TableCapacity - 1)
                {
                    return DeferredIssue.NotEligible;
                }

                var item = _spare.TryDequeue(out var spare) ? spare : new DeferredDispatch();
                if (!_owner._pipelines.ClaimDeferral(kept, compute, groupsX, groupsY, groupsZ, initiator, item.Inputs))
                {
                    item.Reset();
                    _spare.Enqueue(item);
                    return DeferredIssue.NotEligible;
                }

                var index = _issued;
                item.SubmitId = site.SubmitId;
                item.Index = index;
                item.TableMark = _tableCount;
                item.Depth = site.Depth;
                item.CursorAddress = site.CursorAddress;
                item.PacketOffset = site.PacketOffset;
                item.RawUndoMark = site.RawUndoMark;
                item.UserDataMarker = site.UserDataMarker;
                state.FrameDispatchCount++;
                item.Sequence = _owner.Sink!.EnqueueDeferred(item, isDraw: false);
                if (index == 0)
                {
                    // No release may pass the run's first member until the run closes (PendingGpuWrites.HoldFrom).
                    _owner._executor!.PendingWrites.HoldFrom(item.Sequence);
                }

                _items[index] = item;
                Volatile.Write(ref _issued, index + 1);
                _queue.Enqueue(item);
                Interlocked.MemoryBarrier();
                if (Volatile.Read(ref _parked) > 0)
                {
                    _wake.Release();
                }

                return DeferredIssue.Issued;
            }

            public bool NoteTable(ulong address, ulong size)
            {
                var member = _issued;
                if (member == 0)
                {
                    return true;
                }

                var count = _tableCount;
                if (count == TableCapacity)
                {
                    return false;
                }

                _tables[count] = (address, size, member);
                Volatile.Write(ref _tableCount, count + 1);
                // The finalizer publishes marks and then reads the log; this logs and then reads the marks: one of the two sees
                // the other.
                Interlocked.MemoryBarrier();
                return true;
            }

            public DeferredRewind? Barrier()
            {
                var issued = _issued;
                if (issued == 0)
                {
                    _suppressed = false;
                    return null;
                }

                if (Volatile.Read(ref _cursor) != issued)
                {
                    // The members no worker took yet are resolved here instead of waiting for them.
                    var helper = _helper ??= new RenderExecutor(_owner._host.RenderHost!, _owner._pipelines!, new CapturingSink(),
                        _owner._executor!.PendingWrites, _owner._pool);
                    while (Volatile.Read(ref _cursor) != issued && _queue.TryDequeue(out var queued))
                    {
                        Process(helper, queued);
                    }

                    var spinner = new SpinWait();
                    while (Volatile.Read(ref _cursor) != issued && spinner.Count < 100)
                    {
                        spinner.SpinOnce(sleep1Threshold: -1);
                    }

                    while (Volatile.Read(ref _cursor) != issued)
                    {
                        _drained.Reset();
                        Interlocked.MemoryBarrier();
                        if (Volatile.Read(ref _cursor) != issued)
                        {
                            // The barrier finalizes what is ready itself, so a member no worker finalized cannot stall the run.
                            lock (_finalize)
                            {
                                AdvanceLocked();
                            }

                            if (Volatile.Read(ref _cursor) != issued)
                            {
                                _drained.Wait(1);
                            }
                        }
                    }
                }

                DeferredRewind? rewind;
                lock (_finalize)
                {
                    rewind = _poison != int.MaxValue ? _rewind : null;
                    Array.Clear(_items, 0, issued);
                    _cursor = 0;
                    _tableCount = 0;
                    _poison = int.MaxValue;
                    _rewind = null;
                    Volatile.Write(ref _issued, 0);
                }

                _owner._executor!.PendingWrites.EndHold();
                if (rewind is not null)
                {
                    _suppressed = true;
                    BreakChain();
                }

                return rewind;
            }

            private void Run(RenderExecutor executor)
            {
                SharpEmu.HLE.Host.HostLaneReservation.ApplyToHostWorkerThread();
                VideoOut.RenderPhaseProfile.AttachResolveWorker();
                while (!_stopping)
                {
                    if (!_queue.TryDequeue(out var item))
                    {
                        var spinner = new SpinWait();
                        while (spinner.Count < 50 && _queue.IsEmpty)
                        {
                            spinner.SpinOnce(sleep1Threshold: -1);
                        }

                        if (!_queue.TryDequeue(out item))
                        {
                            Interlocked.Increment(ref _parked);
                            if (!_queue.TryDequeue(out item))
                            {
                                _wake.Wait();
                                Interlocked.Decrement(ref _parked);
                                continue;
                            }

                            Interlocked.Decrement(ref _parked);
                        }
                    }

                    Process(executor, item);
                }
            }

            // On a worker, or on the command thread helping at a barrier.
            private void Process(RenderExecutor executor, DeferredDispatch item)
            {
                if (!Volatile.Read(ref item.Cancelled))
                {
                    Resolve(executor, item);
                }

                // A full fence: a failed TryEnter below takes no lock, and the holder's last look at the head must see this
                // store.
                Interlocked.Exchange(ref item.Resolved, 1);
                // A worker that finds the finalizer busy goes on resolving; the holder looks at the head once more after it
                // leaves, so it finalizes this member too.
                while (Monitor.TryEnter(_finalize))
                {
                    try
                    {
                        AdvanceLocked();
                    }
                    finally
                    {
                        Monitor.Exit(_finalize);
                    }

                    Interlocked.MemoryBarrier();
                    if (!HeadReady())
                    {
                        return;
                    }
                }
            }

            // A member at the cursor the finalizer can take now.
            private bool HeadReady()
            {
                var cursor = Volatile.Read(ref _cursor);
                if (cursor >= Volatile.Read(ref _issued))
                {
                    return false;
                }

                return cursor >= Volatile.Read(ref _poison) || (_items[cursor] is { } head && Volatile.Read(ref head.Resolved) != 0);
            }

            private void Resolve(RenderExecutor executor, DeferredDispatch item)
            {
                var host = _owner._host;
                var pipelines = _owner._pipelines!;
                item.FrontendImageVersion = pipelines.FrontendImageVersion;
                host.BeginStrictWorkerResolve(item.Sequence, item.Reads);
                ShaderProgramCache.SpeculativeResolve = true;
                try
                {
                    var inputs = item.Inputs;
                    if (!pipelines.RunDeferredRebind(inputs, out var program) ||
                        executor.ResolveDeferredDispatch(item.SubmitId, inputs.Compute, inputs.GroupsX, inputs.GroupsY, inputs.GroupsZ, inputs.Initiator,
                            program, serialOnly: false) is not { } record)
                    {
                        item.Refused = true;
                        return;
                    }

                    record.Sequence = item.Sequence;
                    executor.CollectDispatchWrites(record, item.Marks);
                    item.Record = record;
                }
                catch (Exception)
                {
                    // SerialResolveRequiredException where the command thread would compile, raise a fatal or read what the
                    // worker cannot log: the command thread runs the member again and reports any real failure itself.
                    item.Refused = true;
                }
                finally
                {
                    ShaderProgramCache.SpeculativeResolve = false;
                    host.EndWorkerResolve();
                }
            }

            // Under _finalize: finalizes the members that are ready, in stream order.
            private void AdvanceLocked()
            {
                var issued = Volatile.Read(ref _issued);
                var pending = _owner._executor!.PendingWrites;
                while (_cursor < issued)
                {
                    var item = _items[_cursor]!;
                    if (_cursor >= _poison)
                    {
                        Cancel(item);
                        Volatile.Write(ref _cursor, _cursor + 1);
                        continue;
                    }

                    if (Volatile.Read(ref item.Resolved) == 0)
                    {
                        break;
                    }

                    if (item.Refused || !ReadsHold(item, pending))
                    {
                        PoisonAt(_cursor, table: false);
                        Cancel(item);
                        Volatile.Write(ref _cursor, _cursor + 1);
                        continue;
                    }

                    foreach (var mark in item.Marks)
                    {
                        pending.Mark(mark.Address, mark.Size, item.Sequence, mark.Kind);
                    }

                    Interlocked.MemoryBarrier();
                    var tables = Volatile.Read(ref _tableCount);
                    for (var index = item.TableMark; index < tables; index++)
                    {
                        var (address, size, member) = _tables[index];
                        if (member > item.Index && Overlaps(item.Marks, address, size))
                        {
                            PoisonAt(member, table: true);
                            break;
                        }
                    }

                    item.Done.Set();
                    Volatile.Write(ref _cursor, _cursor + 1);
                }

                if (_cursor == issued)
                {
                    _drained.Set();
                }
            }

            // The member's reads against the marks before it: the marks of the run's earlier members stay pending until the
            // run closes, so every write they make before this member is seen here.
            private static bool ReadsHold(DeferredDispatch item, PendingGpuWrites pending)
            {
                var released = pending.Released;
                var reads = item.Reads;
                for (var index = 0; index < reads.Count; index++)
                {
                    var bytes = reads.Bytes(index, out var address);
                    var size = (ulong)bytes.Length;
                    if (pending.MayHavePending(address, size) && pending.HasPendingBetween(address, size, released, item.Sequence))
                    {
                        return false;
                    }
                }

                return true;
            }

            private static bool Overlaps(List<PendingMark> marks, ulong address, ulong size)
            {
                foreach (var mark in marks)
                {
                    if (mark.Address < address + size && address < mark.Address + mark.Size)
                    {
                        return true;
                    }
                }

                return false;
            }

            // Members from member on are cancelled. A table a member writes rolls back to the state at the issue of the member
            // before it, and the packets run again from just after that member's dispatch; a member a worker could not resolve
            // rolls back to its own issue, and the packets run again from its dispatch.
            private void PoisonAt(int member, bool table)
            {
                if (member >= _poison)
                {
                    return;
                }

                var source = table ? _items[member - 1]! : _items[member]!;
                _rewind = new DeferredRewind
                {
                    Depth = source.Depth,
                    CursorAddress = source.CursorAddress,
                    Offset = table ? source.PacketOffset + DispatchPacketDwords : source.PacketOffset,
                    Compute = source.Inputs.Compute.Copy(),
                    RawUndoMark = source.RawUndoMark,
                    UserDataMarker = source.UserDataMarker,
                };
                Volatile.Write(ref _poison, member);
            }

            private static void Cancel(DeferredDispatch item)
            {
                item.Cancelled = true;
                item.Done.Set();
            }

            public void Dispose()
            {
                _stopping = true;
                _wake.Release(_threads.Length);
                foreach (var thread in _threads)
                {
                    thread.Join();
                }
            }
        }
    }
}
