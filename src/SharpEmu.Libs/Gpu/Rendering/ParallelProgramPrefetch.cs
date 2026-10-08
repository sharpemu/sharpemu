// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.Gpu.Rendering;

// Resolves the programs of deferred draws on a worker thread, in issue order, while the render
// thread interprets and records. The render thread takes a result for the draw it runs only when
// the resolution completed, used the inputs the draw derives and still matches the host's
// GPU-ownership state; otherwise it resolves the draw itself.
public sealed class ParallelProgramPrefetch : IProgramPrefetch, IDisposable
{
    public sealed class Job(RegisterBanks banks, long gate)
    {
        internal readonly RegisterBanks Banks = banks;
        // The count of stream items other than draws that must have run before the job resolves.
        internal readonly long Gate = gate;
        internal GraphicsProgramInputs Inputs;
        internal GraphicsPrograms? Programs;
        internal object? Assumptions;
        // 0 queued, 1 resolved, 2 abandoned.
        internal volatile int State;
    }

    private readonly RenderExecutor _executor;
    private readonly IRenderHost _host;
    private readonly IConcurrentResolveHost _concurrent;
    private readonly object _lock = new();
    private readonly Queue<Job> _queue = new();
    private readonly Thread _thread;
    private int _outstanding;
    private long _finished;
    private long _executedGate;
    // The gate the worker sleeps on; long.MaxValue while it does not.
    private long _sleepingOnGate = long.MaxValue;
    private bool _paused;
    private bool _running;
    private bool _stopping;

    public ParallelProgramPrefetch(RenderExecutor executor, IRenderHost host, IConcurrentResolveHost concurrent)
    {
        _executor = executor;
        _host = host;
        _concurrent = concurrent;
        _thread = new Thread(Run) { IsBackground = true, Name = "SharpEmu program resolve" };
        _thread.Start();
    }

    // The job of the draw the render thread runs now; set around each deferred draw.
    public Job? Current { get; set; }

    public long Submitted { get; private set; }

    // A gate above zero holds the job until AdvanceGate reaches it: with the command stream on its own
    // thread, draws are submitted ahead of the work before them, which may change what they read.
    public Job Submit(RegisterBanks banks, long gate = 0)
    {
        var job = new Job(banks, gate);
        lock (_lock)
        {
            _queue.Enqueue(job);
            Submitted++;
            _outstanding++;
            Monitor.PulseAll(_lock);
        }

        return job;
    }

    // Returns once the worker has finished every submitted job: it reads guest memory and cache
    // state, which the render thread changes only with the worker idle.
    public void WaitIdle()
    {
        if (Volatile.Read(ref _outstanding) == 0)
        {
            return;
        }

        lock (_lock)
        {
            while (_outstanding != 0)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    // The render thread ran this many non-draw items: jobs gated on them may resolve.
    public void AdvanceGate(long executed)
    {
        Interlocked.Exchange(ref _executedGate, executed);
        // Only a worker asleep on a gate this reaches needs a wake: one per item costs the render
        // thread a kernel call each.
        if (Volatile.Read(ref _sleepingOnGate) <= executed)
        {
            lock (_lock)
            {
                Monitor.PulseAll(_lock);
            }
        }
    }

    // Returns once the worker finished the first count jobs it was given, so a non-draw item runs
    // with no resolution of the draws before it still reading.
    public void WaitFinished(long count)
    {
        if (Volatile.Read(ref _finished) >= count)
        {
            return;
        }

        lock (_lock)
        {
            while (_finished < count)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    // Holds the worker between jobs, for host work that changes what a resolution reads.
    public void Pause()
    {
        lock (_lock)
        {
            _paused = true;
            while (_running)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            Monitor.PulseAll(_lock);
        }
    }

    public bool TryTakeGraphics(RegisterBanks banks, in GraphicsProgramInputs inputs, out GraphicsPrograms programs)
    {
        programs = null!;
        var job = Current;
        if (job is null || !ReferenceEquals(job.Banks, banks))
        {
            return false;
        }

        Current = null;
        WaitFor(job);
        if (job.State != 1 || !job.Inputs.Equals(inputs) || !_concurrent.IsStillCurrent(job.Assumptions!))
        {
            return false;
        }

        programs = job.Programs!;
        return true;
    }

    private void WaitFor(Job job)
    {
        var spinner = new SpinWait();
        for (var spin = 0; spin < 64 && job.State == 0; spin++)
        {
            spinner.SpinOnce(sleep1Threshold: -1);
        }

        if (job.State != 0)
        {
            return;
        }

        lock (_lock)
        {
            while (job.State == 0)
            {
                Monitor.Wait(_lock);
            }
        }
    }

    private void Run()
    {
        SubmissionScheduler.ThrowOnFatal = true;
        while (true)
        {
            Job job;
            lock (_lock)
            {
                while (_queue.Count == 0 && !_stopping)
                {
                    Monitor.Wait(_lock);
                }

                if (_stopping)
                {
                    return;
                }

                job = _queue.Peek();
                if (job.Gate > Volatile.Read(ref _executedGate))
                {
                    // The gate usually opens within a few items: spin before sleeping.
                    Monitor.Exit(_lock);
                    try
                    {
                        var spinner = new SpinWait();
                        for (var spin = 0; spin < 50 && job.Gate > Volatile.Read(ref _executedGate); spin++)
                        {
                            spinner.SpinOnce(sleep1Threshold: -1);
                        }
                    }
                    finally
                    {
                        Monitor.Enter(_lock);
                    }
                }

                while ((_paused || job.Gate > Volatile.Read(ref _executedGate)) && !_stopping)
                {
                    Interlocked.Exchange(ref _sleepingOnGate, _paused ? long.MaxValue : job.Gate);
                    if (!_paused && job.Gate <= Volatile.Read(ref _executedGate))
                    {
                        break;
                    }

                    Monitor.Wait(_lock, 50);
                }

                Volatile.Write(ref _sleepingOnGate, long.MaxValue);
                if (_stopping)
                {
                    return;
                }

                _queue.Dequeue();
                _running = true;
            }

            Resolve(job);
            lock (_lock)
            {
                _outstanding--;
                _finished++;
                _running = false;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void Resolve(Job job)
    {
        _concurrent.BeginConcurrentResolve();
        try
        {
            job.Inputs = RenderExecutor.ProgramInputsOf(job.Banks, _host.FormatSupport, _host.Fatal);
            job.Programs = _executor.ResolveGraphicsPrograms(job.Banks, in job.Inputs);
            job.Assumptions = _concurrent.EndConcurrentResolve();
            job.State = 1;
        }
        catch (Exception)
        {
            // The render thread resolves the draw itself, and fails there if the error is real.
            job.Assumptions = _concurrent.EndConcurrentResolve();
            job.State = 2;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _stopping = true;
            Monitor.PulseAll(_lock);
        }
    }
}
