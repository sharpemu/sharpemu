// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.GpuCommands;

// The dedicated thread form of the submission loop for hosts without their own render loop.
public sealed class CommandStreamWorker
{
    private readonly CommandStreamQueue _queue;
    private readonly ICommandStreamHost _host;
    private readonly Func<bool> _hasPendingCommands;
    private readonly bool _cancelBlockedAtStop;
    private readonly string _threadName;
    private SharpEmu.HLE.Host.HostQosThread? _thread;
    // Set by the thread itself, which may run before Start has stored _thread.
    private volatile Thread? _workerThread;
    private Exception? _failure;

    // A natively created thread would otherwise get Darwin's 512 KiB default
    // secondary stack, smaller than a managed thread's; the reservation is virtual.
    private const int WorkerStackBytes = 16 * 1024 * 1024;

    public CommandStreamWorker(CommandStreamQueue queue, ICommandStreamHost host, Func<bool> hasPendingCommands, bool cancelBlockedAtStop, string threadName)
    {
        _queue = queue;
        _host = host;
        _hasPendingCommands = hasPendingCommands;
        _cancelBlockedAtStop = cancelBlockedAtStop;
        _threadName = threadName;
    }

    public bool IsWorkerThread => ReferenceEquals(Thread.CurrentThread, _workerThread);

    // The GPU submission thread: every guest draw and every flip passes through
    // here on its way to the backend, so it sits on the frame deadline just as
    // much as the presenter does. HostQosThread creates it at UserInteractive -
    // natively on macOS, where CoreCLR threads refuse a QoS class (EPERM).
    public void Start() => _thread = SharpEmu.HLE.Host.HostQosThread.Start(
        _threadName, SharpEmu.HLE.Host.HostThreadQos.UserInteractive, WorkerStackBytes, Run);

    // Stops accepting work, drains the queue and joins; a worker failure is rethrown here.
    public IdleOutcome Stop()
    {
        _queue.StopAccepting();
        _queue.Wake();
        if (_thread is { IsAlive: true } && !IsWorkerThread)
        {
            _thread.Join();
        }

        if (_failure is not null)
        {
            throw _failure;
        }

        return _queue.Outcome;
    }

    private void Run()
    {
        _workerThread = Thread.CurrentThread;
        try
        {
            for (;;)
            {
                _host.RunPendingCommands();
                if (_queue.IsStopping)
                {
                    _queue.DrainForShutdown(_cancelBlockedAtStop);
                    _host.RunPendingCommands();
                    _host.SynchronizeGpu();
                    return;
                }

                switch (_queue.ProcessOne())
                {
                    case SliceResult.NoWork:
                        if (!_hasPendingCommands())
                        {
                            _ = _queue.WaitForWork(AllBlockedWait);
                        }

                        break;
                    case SliceResult.AllBlocked:
                        if (!_queue.WaitForRetryInterval(CommandStreamQueue.AllBlockedRetryMilliseconds))
                        {
                            _queue.RetryBlocked();
                        }

                        break;
                }
            }
        }
        catch (Exception exception)
        {
            _failure = exception;
            _queue.Fail();
        }
    }

    private const int AllBlockedWait = 1000;
}
