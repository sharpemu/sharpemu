// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.GpuMemory;

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

    private readonly record struct TickWork(Action Callback, ulong Tick);
    private readonly record struct SubmissionTraceEntry(ulong Tick, uint Op, ulong SubmitId,
        uint Arg0, uint Arg1, uint Arg2, uint Arg3, ulong Arg4);
    private readonly SubmissionTraceEntry[] _submissionHistory = new SubmissionTraceEntry[32];
    private int _nextSubmissionHistoryIndex;
    private int _submissionHistoryCount;

    private readonly IGpuTickDevice _device;
    private readonly TickTimeline _timeline;
    private readonly TickedBufferRing _ring;
    private readonly RecordingBuffer _command;
    private readonly Action<SubmitBundle>? _prepareSubmit;
    private readonly Action<ulong>? _submitted;
    private readonly Queue<TickWork> _pending = new();
    private readonly Queue<TickWork> _priority = new();
    private readonly object _operationLock = new();
    private readonly Thread _priorityThread;
    private bool _priorityActive;
    private ulong _priorityActiveTick;
    private OperationState _state = OperationState.Open;
    private bool _stopRequested;
    private bool _disposeRequested;
    private bool _deviceDisposed;
    private SubmissionContext? _context;

    public SubmissionScheduler(
        IGpuTickDevice device,
        IRenderingState rendering,
        Action<SubmitBundle>? prepareSubmit = null,
        Action<ulong>? submitted = null)
    {
        _device = device;
        _prepareSubmit = prepareSubmit;
        _submitted = submitted;
        _timeline = new TickTimeline(device);
        _ring = new TickedBufferRing(device, _timeline);
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

    public TickTimeline Timeline => _timeline;

    internal bool PriorityWorkerAlive => _priorityThread.IsAlive;

    // Report the fatal error, then return an exception to stop the caller.
    internal static Exception Fatal(string message)
    {
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

    public ulong Flush() => Flush(new SubmitBundle());

    public ulong Flush(SubmitBundle bundle)
    {
        var tick = Submit(bundle);
        BeginNextCommandBuffer();
        return tick;
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

                operation = _pending.Dequeue();
            }

            WaitForPriorityOperations(operation.Tick);
            RunDeferredCallback(operation.Callback);
        }
    }

    public void QueueCompletionAction(Action operation)
    {
        RequireActiveScheduler();
        lock (_operationLock)
        {
            if (_state == OperationState.Open)
            {
                _pending.Enqueue(new TickWork(operation, CurrentTick));
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
        if (!_command.IsInvalid)
        {
            throw Fatal("Cannot start recording. The command buffer is already recording.");
        }

        _command.Buffer = _ring.AcquireBuffer();
        _command.Begin();
        return _command;
    }

    // Add the timeline signal to a copy. Keep the caller's submission bundle unchanged.
    public ulong Submit(SubmitBundle? bundle = null)
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

        _command.End();
        var buffer = _command.Buffer;
        bool submitted;
        string failure;
        ulong tick;
        lock (_device.QueueGate)
        {
            tick = _timeline.ReserveTick();
            submit.AddSignal(_timeline.Handle, tick);
            submitted = _device.TrySubmit(buffer, submit, out failure);
        }

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
        _command.Buffer = 0;
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
