// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Rendering;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Scheduling;

public sealed class SubmissionScheduler : IGpuTickScheduler, IDisposable
{
    internal static Action<string> OnFatal = message => Environment.FailFast(message);

    [ThreadStatic]
    private static SubmissionScheduler? _callbackOwner;

    private enum OperationState
    {
        Open,
        Draining,
        Closed,
    }

    private readonly record struct TickWork(Action Callback, ulong Tick, bool MemoryPublication = false);
    private readonly record struct ParkedWork(Action Callback, long Generation, ulong Tick = 0, bool MemoryPublication = false);
    private readonly record struct SubmissionTraceEntry(ulong Tick, uint Op, ulong SubmitId,
        uint Arg0, uint Arg1, uint Arg2, uint Arg3, ulong Arg4);
    private readonly SubmissionTraceEntry[] _submissionHistory = new SubmissionTraceEntry[32];
    private int _nextSubmissionHistoryIndex;
    private int _submissionHistoryCount;

    private readonly IGpuTickDevice _device;
    private readonly TickTimeline _timeline;
    private readonly RecordingBuffer _command;
    private readonly Action<SubmitBundle>? _prepareSubmit;
    private readonly Action<ulong>? _submitted;
    private readonly Action<ulong>? _completed;
    private readonly Queue<TickWork> _pending = new();
    private readonly Queue<TickWork> _priority = new();
    private readonly Queue<ParkedWork> _parked = new();
    private long _cutGeneration;
    private readonly SortedSet<long> _unjoinedCuts = new();
    private readonly object _operationLock = new();
    private readonly Thread _priorityThread;
    private bool _priorityActive;
    private ulong _priorityActiveTick;
    private OperationState _state = OperationState.Open;
    private bool _stopRequested;
    private bool _disposeRequested;
    private bool _deviceDisposed;
    private SubmissionContext? _context;
    private readonly IRenderingState _rendering;
    private ulong _lastMemoryDrainTick;
    private bool _publishingMemory;
    private ulong _pendingTailTick;
    private ulong _enqueueSequence;
    private const int MaxDeferredSubmissions = 8;
    private readonly List<nint> _deferredBuffers = new();
    private readonly List<ulong> _deferredTicks = new();
    private static long _deferredSubmissions;
    private static long _batchedSubmissions;
    private const int TickLedgerSize = 4096;
    private readonly ulong[] _ledgerTicks = new ulong[TickLedgerSize];
    private readonly long[] _reservedAt = new long[TickLedgerSize];
    private readonly long[] _submittedAt = new long[TickLedgerSize];
    private static readonly HLE.Diagnostics.ThreadCounter _heldTicks = new();
    private static readonly HLE.Diagnostics.ThreadCounter _heldTime = new();
    private static readonly HLE.Diagnostics.ThreadCounter _submitsAtLimit = new();
    private static readonly HLE.Diagnostics.ThreadCounter _submitsEnsured = new();
    private static readonly HLE.Diagnostics.ThreadCounter _submitsFolded = new();
    private static readonly HLE.Diagnostics.ThreadCounter _completions = new();
    private static readonly HLE.Diagnostics.ThreadCounter _completionAfterSubmit = new();

    public static string TakeTickLedgerReport()
    {
        var held = _heldTicks.Take();
        var completions = _completions.Take();
        return FormattableString.Invariant($"tick_hold=ticks:{held},avg_us:{Microseconds(_heldTime.Take(), held):F1} submits=limit:{_submitsAtLimit.Take()},ensure:{_submitsEnsured.Take()},fold:{_submitsFolded.Take()} ") +
            FormattableString.Invariant($"completion_callbacks={completions}/after_submit_us:{Microseconds(_completionAfterSubmit.Take(), completions):F1}");
    }

    private static double Microseconds(long ticks, long samples) =>
        samples == 0 ? 0 : ticks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency / samples;

    public long ReservedAt(ulong tick) => Volatile.Read(ref _ledgerTicks[tick % TickLedgerSize]) == tick ? Volatile.Read(ref _reservedAt[tick % TickLedgerSize]) : 0;

    public long SubmittedAt(ulong tick) => Volatile.Read(ref _ledgerTicks[tick % TickLedgerSize]) == tick ? Volatile.Read(ref _submittedAt[tick % TickLedgerSize]) : 0;

    private void NoteReserved(ulong tick, long now, bool submitted)
    {
        var slot = tick % TickLedgerSize;
        Volatile.Write(ref _ledgerTicks[slot], 0);
        Volatile.Write(ref _reservedAt[slot], now);
        Volatile.Write(ref _submittedAt[slot], submitted ? now : 0);
        Volatile.Write(ref _ledgerTicks[slot], tick);
    }

    private void NoteDeferredSubmitted(long now)
    {
        foreach (var tick in _deferredTicks)
        {
            var slot = tick % TickLedgerSize;
            if (Volatile.Read(ref _ledgerTicks[slot]) != tick)
            {
                continue;
            }

            Volatile.Write(ref _submittedAt[slot], now);
            _heldTicks.Increment();
            _heldTime.Add(now - Volatile.Read(ref _reservedAt[slot]));
        }
    }

    public static string TakeDeferredReport() =>
        FormattableString.Invariant($"deferred_submits={Interlocked.Exchange(ref _deferredSubmissions, 0)} batched_submits={Interlocked.Exchange(ref _batchedSubmissions, 0)}");

    public SubmissionScheduler(
        IGpuTickDevice device,
        IRenderingState rendering,
        Action<SubmitBundle>? prepareSubmit = null,
        Action<ulong>? submitted = null,
        Action<ulong>? completed = null)
    {
        _device = device;
        _rendering = rendering;
        _prepareSubmit = prepareSubmit;
        _submitted = submitted;
        _completed = completed;
        _timeline = new TickTimeline(device);
        _command = new RecordingBuffer(this, device, rendering);
        _priorityThread = new Thread(RunPriorityWorker) { IsBackground = true, Name = "SharpEmu GPU priority" };
        _priorityThread.Start();
    }

    public static bool InDeferredOperation => _callbackOwner != null;

    public bool InsideTickCallback => InDeferredOperation;

    public bool Active => _context != null;

    public RecordingBuffer Current
    {
        get
        {
            RequireActiveScheduler();
            return _command;
        }
    }

    public ulong CurrentTick => _timeline.CurrentTick;

    public ulong CompletedTick => _timeline.CompletedTick;

    public Action<nint, MemoryBarrierRequest>? MemoryBarrierRecorder { get; set; }

    public TickTimeline Timeline => _timeline;

    internal bool PriorityWorkerAlive => _priorityThread.IsAlive;

    // Set on a thread that does work ahead of the render thread: there a fatal condition only
    // abandons that work, which the render thread then does itself and fails on if it is real.
    [ThreadStatic]
    internal static bool ThrowOnFatal;

    public static bool ThrowsOnFatal => ThrowOnFatal;
    [ThreadStatic]
    private static Func<string, Exception>? s_readOnlyViolation;

    public static bool ReadOnlyThread => s_readOnlyViolation is not null;

    public static void MarkReadOnlyThread(Func<string, Exception> violation) => s_readOnlyViolation = violation;

    public static Exception ReadOnlyViolation(string operation) => s_readOnlyViolation!(operation);

    public sealed class AbandonedWorkException(string message) : Exception(message);

    // Report the fatal error, then return an exception to stop the caller.
    internal static Exception Fatal(string message)
    {
        if (ThrowOnFatal)
        {
            throw new AbandonedWorkException(message);
        }

        Console.Error.WriteLine($"[GPU][FATAL] {message}");
        OnFatal(message);
        return new InvalidOperationException(message);
    }

    // Release the device once after shutdown. A callback leaves final disposal to the shutdown owner.
    public void Dispose()
    {
        Volatile.Write(ref _disposeRequested, true);
        Shutdown();
        DisposeDeviceOnceClosed();
    }

    private void DisposeDeviceOnceClosed()
    {
        lock (_operationLock)
        {
            if (_state != OperationState.Closed || _deviceDisposed)
            {
                return;
            }

            _deviceDisposed = true;
        }

        _device.Dispose();
    }

    public void Shutdown()
    {
        lock (_operationLock)
        {
            if (_state == OperationState.Closed)
            {
                return;
            }

            if (_callbackOwner == this)
            {
                if (_state == OperationState.Open)
                {
                    throw Fatal("Cannot stop the scheduler from its own deferred callback.");
                }

                // Return to the shutdown owner so it can finish running the remaining callbacks.
                return;
            }

            if (_state == OperationState.Draining)
            {
                while (_state != OperationState.Closed)
                {
                    Monitor.Wait(_operationLock);
                }

                return;
            }

            _state = OperationState.Draining;
            while (_parked.Count != 0)
            {
                Unpark(_parked.Dequeue());
            }
        }

        if (!_command.IsInvalid)
        {
            Submit();
        }

        _timeline.Wait(CurrentTick - 1);
        RunCompletedOperations();
        WaitForAllPriorityOperations();
        lock (_operationLock)
        {
            _stopRequested = true;
            Monitor.PulseAll(_operationLock);
        }

        _priorityThread.Join();
        lock (_operationLock)
        {
            if (_pending.Count != 0 || _priority.Count != 0 || _priorityActive)
            {
                throw Fatal("The scheduler closed before all queued operations completed.");
            }

            _state = OperationState.Closed;
            Monitor.PulseAll(_operationLock);
        }

        if (Volatile.Read(ref _disposeRequested))
        {
            DisposeDeviceOnceClosed();
        }
    }

    public void Begin(SubmissionContext context)
    {
        lock (_operationLock)
        {
            if (_state != OperationState.Open)
            {
                throw Fatal("Cannot start the scheduler after shutdown.");
            }
        }

        _context = context;
        if (_command.IsInvalid)
        {
            BeginNextCommandBuffer();
        }
        else
        {
            BindCurrentContext();
        }
    }

    public void EndRendering()
    {
        if (Active && !_command.IsInvalid)
        {
            Current.EndRendering();
        }
    }

    public void EndRendering(Vulkan.ScopeBreak reason)
    {
        if (Active && !_command.IsInvalid && _rendering.IsRendering)
        {
            Vulkan.BarrierTally.CountScopeBreak(reason);
        }

        EndRendering();
    }

    public bool TryDeferUntilRenderingEnds(PipelineStageFlags sourceStages, PipelineStageFlags destinationStages, List<ImageMemoryBarrier2> barriers) =>
        Active && !_command.IsInvalid && _rendering.IsRendering &&
        _rendering.TryDeferUntilRenderingEnds(sourceStages, destinationStages, barriers);

    public ulong Flush() => Flush(new SubmitBundle());

    public ulong Flush(SubmitBundle bundle) => Flush(bundle, default);

    public ulong Flush(SubmitBundle bundle, ReadOnlySpan<RecordingBuffer> trailing)
    {
        var tick = Submit(bundle, trailing);
        BeginNextCommandBuffer();
        return tick;
    }

    public ulong FlushDeferred(ReadOnlySpan<RecordingBuffer> trailing)
    {
        if (_command.IsInvalid)
        {
            throw Fatal("Cannot submit without a command buffer that is recording.");
        }

        var bundle = new SubmitBundle();
        _prepareSubmit?.Invoke(bundle);
        if (bundle.WaitCount != 0 || bundle.SignalCount != 0)
        {
            return Flush(bundle, trailing);
        }

        foreach (var recording in trailing)
        {
            if (recording.IsInvalid || !recording.Ended)
            {
                throw Fatal("A trailing command buffer must be recorded and ended before it is submitted.");
            }
        }

        _command.End();
        ulong tick;
        lock (_operationLock)
        {
            lock (_device.QueueGate)
            {
                tick = _timeline.ReserveTick();
            }
        }

        _deferredBuffers.Add(_command.Buffer);
        foreach (var recording in trailing)
        {
            _deferredBuffers.Add(recording.Buffer);
        }

        _deferredTicks.Add(tick);
        NoteReserved(tick, System.Diagnostics.Stopwatch.GetTimestamp(), submitted: false);
        _command.Submitted(tick);
        foreach (var recording in trailing)
        {
            recording.Submitted(tick);
        }

        _submitted?.Invoke(tick);
        Interlocked.Increment(ref _deferredSubmissions);
        if (_deferredTicks.Count >= MaxDeferredSubmissions)
        {
            _submitsAtLimit.Increment();
            SubmitDeferred();
        }

        BeginNextCommandBuffer();
        return tick;
    }

    public void SubmitDeferred()
    {
        if (_deferredTicks.Count == 0)
        {
            return;
        }

        var submit = new SubmitBundle();
        var last = _deferredTicks[^1];
        Span<nint> buffers = stackalloc nint[_deferredBuffers.Count];
        CollectionsMarshal.AsSpan(_deferredBuffers).CopyTo(buffers);
        bool submitted;
        string failure;
        lock (_operationLock)
        {
            lock (_device.QueueGate)
            {
                submit.AddSignal(_timeline.Handle, last);
                submitted = _device.TrySubmit(buffers, submit, out failure);
            }

            if (submitted)
            {
                NoteDeferredSubmitted(System.Diagnostics.Stopwatch.GetTimestamp());
                EnqueueDeferredCompletions();
            }
        }

        if (!submitted)
        {
            throw Fatal($"vkQueueSubmit failed for the deferred buffers: {failure}, tick={last}");
        }

        _deferredBuffers.Clear();
        _deferredTicks.Clear();
    }

    private void EnqueueDeferredCompletions()
    {
        if (_completed is not { } completed)
        {
            return;
        }

        foreach (var tick in _deferredTicks)
        {
            _priority.Enqueue(new TickWork(() => completed(tick), tick));
        }

        Monitor.Pulse(_operationLock);
    }

    public void EnsureSubmitted(ulong tick)
    {
        if (_deferredTicks.Count != 0 && tick >= _deferredTicks[0])
        {
            _submitsEnsured.Increment();
            SubmitDeferred();
        }
    }

    public void FlushAndWait()
    {
        var tick = Submit();
        _timeline.Wait(tick);
        BeginNextCommandBuffer();
    }

    public void Finish()
    {
        RequireActiveScheduler();
        if (!_command.IsInvalid)
        {
            Submit();
        }

        _timeline.Wait(CurrentTick - 1);
        BeginNextCommandBuffer();
        RunCompletedOperations();
    }

    public void FinishMemoryAccess()
    {
        RequireActiveScheduler();
        if (InsideTickCallback)
        {
            throw Fatal("Cannot finish memory access from a GPU completion callback.");
        }

        lock (_operationLock)
        {
            // Reuse a completed drain only while its next command buffer and callback queues stay unused.
            if (_lastMemoryDrainTick == CurrentTick && !_command.IsInvalid && !_command.HasPendingCommands &&
                _pending.Count == 0 && _parked.Count == 0 && _priority.Count == 0 && !_priorityActive)
            {
                return;
            }
        }

        var tick = CurrentTick;
        var nextTick = tick + (_command.IsInvalid ? 0UL : 1UL);
        Finish();
        WaitForPriorityOperations(tick);
        // A callback can record or submit more work. It must not count as part of this drain.
        _lastMemoryDrainTick = nextTick;
    }

    public void Wait(ulong tick)
    {
        if (tick > CurrentTick)
        {
            throw Fatal($"Cannot wait for a tick that has not been issued: {tick} > {CurrentTick}.");
        }

        if (tick == CurrentTick)
        {
            RequireActiveScheduler();
            var submitted = Submit();
            if (submitted != tick)
            {
                throw Fatal($"The submitted tick {submitted} does not match the requested tick {tick}.");
            }

            _timeline.Wait(tick);
            BeginNextCommandBuffer();
        }
        else
        {
            EnsureSubmitted(tick);
            _timeline.Wait(tick);
        }
    }

    public void RunCompletedOperations()
    {
        _timeline.RefreshCompletedTick();
        for (;;)
        {
            TickWork operation;
            lock (_operationLock)
            {
                if (_pending.Count == 0 || !_timeline.IsTickComplete(_pending.Peek().Tick))
                {
                    return;
                }

                if (_publishingMemory && _pending.Peek().MemoryPublication) return;

                operation = _pending.Dequeue();
            }

            WaitForPriorityOperations(operation.Tick);
            if (operation.MemoryPublication)
            {
                if (InsideTickCallback)
                    throw Fatal("Cannot publish guest memory inside a completion callback.");
                _publishingMemory = true;
                try { operation.Callback(); }
                finally { _publishingMemory = false; }
            }
            else
                RunDeferredCallback(operation.Callback);
        }
    }

    // Slices recorded ahead of the stream may still reference what a completion action destroys;
    // while any is unjoined the action waits until the slices cut so far have been submitted.
    public long BeginSliceCut()
    {
        lock (_operationLock)
        {
            var generation = ++_cutGeneration;
            _unjoinedCuts.Add(generation);
            return generation;
        }
    }

    public void NoteSliceJoined(long generation)
    {
        lock (_operationLock)
        {
            _unjoinedCuts.Remove(generation);
            var joined = _unjoinedCuts.Count == 0 ? _cutGeneration : _unjoinedCuts.Min - 1;
            while (_parked.Count != 0 && _parked.Peek().Generation <= joined)
            {
                Unpark(_parked.Dequeue());
            }
        }
    }

    private void Unpark(ParkedWork parked)
    {
        var tick = parked.MemoryPublication ? parked.Tick : CurrentTick;
        _pendingTailTick = _pending.Count == 0 ? tick : Math.Max(_pendingTailTick, tick);
        _pending.Enqueue(new TickWork(parked.Callback, tick, parked.MemoryPublication));
    }

    public void QueueCompletionAction(Action operation)
    {
        RequireActiveScheduler();
        lock (_operationLock)
        {
            if (_state == OperationState.Open)
            {
                if (_unjoinedCuts.Count != 0)
                {
                    _parked.Enqueue(new ParkedWork(operation, _cutGeneration));
                }
                else
                {
                    _pending.Enqueue(new TickWork(operation, CurrentTick));
                    _pendingTailTick = CurrentTick;
                }

                _enqueueSequence++;
                return;
            }

            if (_callbackOwner != this)
            {
                while (_state != OperationState.Closed)
                {
                    Monitor.Wait(_operationLock);
                }
            }
        }

        operation();
    }

    // Publication runs on the queue owner, outside callback scope, so memory ownership can drain.
    // Dependent guest signals must not become visible before publication succeeds.
    internal void QueueMemoryPublication(Action operation) => QueueMemoryPublication(operation, CurrentTick);

    internal ulong QueueMemoryPublication(Action operation, ulong dependencyTick) =>
        QueueMemoryPublication(operation, dependencyTick, out _);

    // True while the publication with this sequence is still queued, nothing was queued after it,
    // and a new publication with this dependency would take the same tick, so it may join it.
    internal bool IsLastQueuedPublication(ulong sequence, ulong dependencyTick, ulong tick)
    {
        lock (_operationLock)
            return _enqueueSequence == sequence && _parked.Count == 0 && _pending.Count != 0 && Math.Max(dependencyTick, _pendingTailTick) == tick;
    }

    internal ulong QueueMemoryPublication(Action operation, ulong dependencyTick, out ulong sequence)
    {
        RequireActiveScheduler();
        if (InsideTickCallback)
            throw Fatal("Cannot queue a memory publication from a completion callback.");
        lock (_operationLock)
        {
            if (_state != OperationState.Open)
                throw Fatal("Cannot queue a memory publication after shutdown starts.");
            if (dependencyTick > CurrentTick)
                throw Fatal("Cannot publish memory for an unknown submission tick.");
            // Keep ready results behind earlier work without attaching them to an unused future tick.
            var tick = _pending.Count == 0 && _parked.Count == 0 ? dependencyTick : Math.Max(dependencyTick, _pendingTailTick);
            if (_parked.Count != 0)
            {
                _parked.Enqueue(new ParkedWork(operation, _cutGeneration, tick, MemoryPublication: true));
            }
            else
            {
                _pending.Enqueue(new TickWork(operation, tick, MemoryPublication: true));
                _pendingTailTick = tick;
            }

            sequence = ++_enqueueSequence;
            return tick;
        }
    }

    public void QueuePriorityCompletionAction(Action operation)
    {
        RequireActiveScheduler();
        lock (_operationLock)
        {
            if (_state == OperationState.Open)
            {
                _priority.Enqueue(new TickWork(operation, CurrentTick));
                Monitor.Pulse(_operationLock);
                return;
            }

            if (_callbackOwner != this)
            {
                while (_state != OperationState.Closed)
                {
                    Monitor.Wait(_operationLock);
                }
            }
        }

        operation();
    }

    public void WaitForAllPriorityOperations()
    {
        if (_callbackOwner == this)
        {
            throw Fatal("Cannot drain priority operations from the scheduler's own deferred callback.");
        }

        lock (_operationLock)
        {
            while (_priority.Count != 0 || _priorityActive)
            {
                Monitor.Wait(_operationLock);
            }
        }
    }

    public void WaitForPriorityOperations(ulong tick)
    {
        if (_callbackOwner == this)
        {
            throw Fatal("Cannot wait for priority operations from the scheduler's own deferred callback.");
        }

        lock (_operationLock)
        {
            while ((_priorityActive && _priorityActiveTick <= tick) ||
                   (_priority.Count != 0 && _priority.Peek().Tick <= tick))
            {
                Monitor.Wait(_operationLock);
            }
        }
    }

    // Uses the last completed tick read from the device; it does not query the device.
    internal bool IsTickKnownComplete(ulong tick) => _timeline.IsTickComplete(tick);

    public bool IsTickComplete(ulong tick)
    {
        if (_timeline.IsTickComplete(tick))
        {
            return true;
        }

        _timeline.RefreshCompletedTick();
        return _timeline.IsTickComplete(tick);
    }

    public void RequireActiveScheduler()
    {
        if (!Active)
        {
            throw Fatal("The scheduler must be active for this operation.");
        }
    }

    public RecordingBuffer BeginCommand()
    {
        _command.Acquire();
        return _command;
    }

    public RecordingBuffer CreateRecordingBuffer(IRenderingState rendering) => new(this, _device.CreateBufferSource(), rendering);

    public void BeginRecording(RecordingBuffer buffer)
    {
        buffer.Bind(_context ?? throw Fatal("The scheduler requires a submission context."));
        buffer.Acquire();
    }

    public void EndRecording(RecordingBuffer buffer) => buffer.End();

    public void EndDiscardedRecording(RecordingBuffer buffer) => buffer.EndDiscarded();

    public void NoteMappedMemoryWrite()
    {
        if (Active && !_command.IsInvalid)
        {
            _command.NoteMappedMemoryWrite();
        }
    }

    public void DiscardRecording(RecordingBuffer buffer) => buffer.Discard();

    // Add the timeline signal to a copy. Keep the caller's submission bundle unchanged. Trailing
    // buffers were ended by their recording threads and execute after the current buffer.
    public ulong Submit(SubmitBundle? bundle = null, ReadOnlySpan<RecordingBuffer> trailing = default)
    {
        var submit = bundle == null ? new SubmitBundle() : new SubmitBundle(bundle);
        if (_command.IsInvalid)
        {
            throw Fatal("Cannot submit without a command buffer that is recording.");
        }

        _prepareSubmit?.Invoke(submit);
        if (submit.WaitCount > SubmitBundle.MaxSemaphores || submit.SignalCount >= SubmitBundle.MaxSemaphores)
        {
            throw Fatal($"The submission exceeds its capacity: waits={submit.WaitCount} signals={submit.SignalCount}.");
        }

        var deferred = _deferredBuffers.Count;
        Span<nint> buffers = stackalloc nint[deferred + 1 + trailing.Length];
        CollectionsMarshal.AsSpan(_deferredBuffers).CopyTo(buffers);
        for (var index = 0; index < trailing.Length; index++)
        {
            if (trailing[index].IsInvalid || !trailing[index].Ended)
            {
                throw Fatal("A trailing command buffer must be recorded and ended before it is submitted.");
            }

            buffers[deferred + index + 1] = trailing[index].Buffer;
        }

        _command.End();
        buffers[deferred] = _command.Buffer;
        bool submitted;
        string failure;
        ulong tick;
        // Keep completion notifications before callbacks for the next recording tick.
        lock (_operationLock)
        {
            lock (_device.QueueGate)
            {
                tick = _timeline.ReserveTick();
                submit.AddSignal(_timeline.Handle, tick);
                submitted = _device.TrySubmit(buffers, submit, out failure);
            }

            if (submitted)
            {
                var now = System.Diagnostics.Stopwatch.GetTimestamp();
                NoteReserved(tick, now, submitted: true);
                if (deferred != 0)
                {
                    _submitsFolded.Increment();
                    NoteDeferredSubmitted(now);
                }

                EnqueueDeferredCompletions();
            }

            if (submitted && _completed is { } completed)
            {
                _priority.Enqueue(new TickWork(() => completed(tick), tick));
                Monitor.Pulse(_operationLock);
            }
        }

        if (submitted && deferred != 0)
        {
            Interlocked.Increment(ref _batchedSubmissions);
            _deferredBuffers.Clear();
            _deferredTicks.Clear();
        }

        if (MeshDrawTrace.Enabled) MeshDrawTrace.Submitted((ulong)buffers[deferred], tick, submitted, failure);
        if (!submitted)
        {
            Console.Error.WriteLine($"[GPU][ERROR] submission history known_completed_tick={_timeline.CompletedTick}");
            foreach (var entry in FormatRecentSubmissions())
                Console.Error.WriteLine($"[GPU][ERROR] {entry}");
            throw Fatal(
                $"vkQueueSubmit failed: {failure}, tick={tick} debug_op={_command.DebugOp} debug_submit={_command.DebugSubmitId} " +
                $"args={_command.DebugArg0},{_command.DebugArg1},{_command.DebugArg2},{_command.DebugArg3},0x{_command.DebugArg4:X16}");
        }

        _submissionHistory[_nextSubmissionHistoryIndex] = new SubmissionTraceEntry(tick, _command.DebugOp, _command.DebugSubmitId,
            _command.DebugArg0, _command.DebugArg1, _command.DebugArg2, _command.DebugArg3, _command.DebugArg4);
        _nextSubmissionHistoryIndex = (_nextSubmissionHistoryIndex + 1) % _submissionHistory.Length;
        _submissionHistoryCount = Math.Min(_submissionHistoryCount + 1, _submissionHistory.Length);
        _command.Submitted(tick);
        foreach (var recording in trailing)
        {
            recording.Submitted(tick);
        }

        _submitted?.Invoke(tick);
        return tick;
    }

    // Report the last operation in each saved submission. Earlier GPU work can cause device loss.
    internal IEnumerable<string> FormatRecentSubmissions()
    {
        for (var index = 0; index < _submissionHistoryCount; index++)
        {
            var slot = (_nextSubmissionHistoryIndex - _submissionHistoryCount + index + _submissionHistory.Length) % _submissionHistory.Length;
            var entry = _submissionHistory[slot];
            yield return $"tick={entry.Tick} op={(RecordedOperation)entry.Op} submit={entry.SubmitId} " +
                $"args={entry.Arg0},{entry.Arg1},{entry.Arg2},{entry.Arg3},0x{entry.Arg4:X16}";
        }
    }

    private void RunPriorityWorker()
    {
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHostWorkerThread();
        for (;;)
        {
            TickWork operation;
            lock (_operationLock)
            {
                while (!_stopRequested && _priority.Count == 0)
                {
                    Monitor.Wait(_operationLock);
                }

                if (_stopRequested)
                {
                    return;
                }

                operation = _priority.Dequeue();
                _priorityActive = true;
                _priorityActiveTick = operation.Tick;
            }

            _timeline.Wait(operation.Tick);
            if (SubmittedAt(operation.Tick) is var submittedAt and not 0)
            {
                _completions.Increment();
                _completionAfterSubmit.Add(System.Diagnostics.Stopwatch.GetTimestamp() - submittedAt);
            }

            if (!Volatile.Read(ref _stopRequested))
            {
                RunDeferredCallback(operation.Callback);
            }

            lock (_operationLock)
            {
                _priorityActive = false;
                _priorityActiveTick = 0;
                Monitor.PulseAll(_operationLock);
            }
        }
    }

    private void RunDeferredCallback(Action operation)
    {
        var previous = _callbackOwner;
        _callbackOwner = this;
        try
        {
            operation();
        }
        finally
        {
            _callbackOwner = previous;
        }
    }

    private void BindCurrentContext() => _command.Bind(_context ?? throw Fatal("The scheduler requires a submission context."));

    private void BeginNextCommandBuffer()
    {
        if (!_command.IsInvalid)
        {
            throw Fatal("Cannot start the next command buffer while the current buffer is recording.");
        }

        BindCurrentContext();
        BeginCommand();
    }
}
