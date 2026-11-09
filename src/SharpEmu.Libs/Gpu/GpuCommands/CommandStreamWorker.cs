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
    private readonly Thread _thread;
    private Exception? _failure;

    public CommandStreamWorker(CommandStreamQueue queue, ICommandStreamHost host, Func<bool> hasPendingCommands, bool cancelBlockedAtStop, string threadName)
    {
        _queue = queue;
        _host = host;
        _hasPendingCommands = hasPendingCommands;
        _cancelBlockedAtStop = cancelBlockedAtStop;
        _thread = new Thread(Run) { IsBackground = true, Name = threadName };
    }

    public bool IsWorkerThread => Thread.CurrentThread == _thread;

    public void Start() => _thread.Start();

    // Stops accepting work, drains the queue and joins; a worker failure is rethrown here.
    public IdleOutcome Stop()
    {
        _queue.StopAccepting();
        _queue.Wake();
        if (_thread.IsAlive && !IsWorkerThread)
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
