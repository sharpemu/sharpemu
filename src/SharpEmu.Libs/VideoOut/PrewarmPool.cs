// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu.Scheduling;

// The boot prewarm's threads, shared by its lanes (the compute records and the graphics parts). Each turn a worker runs
// the next item of the lane whose next item must start first: the lowest start, the time by which the item must start
// to be ready at its first use. At boot the compute records need far more threads than the graphics parts, and a few
// long compute compiles can hold every compute thread while short records due in the first seconds wait, so the threads
// follow the deadlines instead of half going to each lane.
internal sealed class PrewarmPool(Func<bool> stopping)
{
    private readonly object _gate = new();
    private readonly List<Thread> _threads = [];
    private Lane[] _lanes = [];
    private bool _started;

    // Adds a lane and threads; every thread serves every lane. Threads added before Start begin there, so their first
    // turns already see every lane; threads added after it begin at once. A lane without items is finished at once.
    public void Add(Lane lane, int threads, string name)
    {
        lock (_gate)
        {
            Volatile.Write(ref _lanes, [.. _lanes, lane]);
            for (var index = 0; index < threads; index++)
            {
                var thread = new Thread(Work) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = $"{name} {index}" };
                _threads.Add(thread);
                if (_started)
                {
                    thread.Start();
                }
            }
        }

        if (lane.Done)
        {
            lane.Finish();
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            foreach (var thread in _threads)
            {
                thread.Start();
            }
        }
    }

    // Waits for every thread, then finishes the lanes the pool stopped before their last item.
    public void Stop()
    {
        Thread[] threads;
        lock (_gate)
        {
            threads = _started ? [.. _threads] : [];
        }

        foreach (var thread in threads)
        {
            thread.Join();
        }

        foreach (var lane in Volatile.Read(ref _lanes))
        {
            lane.Finish();
        }
    }

    private void Work()
    {
        // A fatal condition in an item abandons the item, whose failure path notes it, instead of ending the process: the
        // session compiles the same work itself and fails there if the condition is real.
        SubmissionScheduler.ThrowOnFatal = true;
        while (!stopping())
        {
            var lanes = Volatile.Read(ref _lanes);
            Lane? next = null;
            var nextStart = float.PositiveInfinity;
            foreach (var lane in lanes)
            {
                if (lane.TryPeek(out var start) && (next is null || start < nextStart))
                {
                    (next, nextStart) = (lane, start);
                }
            }

            // No lane has an item to take: the items left, if any, are with other workers, which run them (a worker that
            // puts a busy caller's record back takes it again itself).
            if (next is null)
            {
                return;
            }

            if (next.Run(mayWait: false) || lanes.Any(lane => lane != next && lane.TryPeek(out _) && lane.Run(mayWait: false)))
            {
                continue;
            }

            // Each item left waits on a busy linked caller and no other lane has one: wait for a caller.
            next.Run(mayWait: true);
        }
    }

    // One kind of work: items, how many items run takes in all. peek: the next item's start, false when none is left to take.
    // run: takes and runs the next item, false when it took none (none was left, or each one left waits on a busy linked
    // caller and mayWait is false). finished: once, after the last item ran or when the pool stopped.
    internal sealed class Lane(int items, Lane.Peek peek, Func<bool, bool> run, Action finished)
    {
        internal delegate bool Peek(out float start);

        // The items not run yet. A queue that looks empty does not end a lane: a worker that puts a busy caller's
        // record back holds it outside the queue for a moment.
        private int _left = items;
        private int _finished;

        internal bool Done => Volatile.Read(ref _left) == 0;

        internal bool TryPeek(out float start) => peek(out start);

        internal bool Run(bool mayWait)
        {
            if (!run(mayWait))
            {
                return false;
            }

            if (Interlocked.Decrement(ref _left) == 0)
            {
                Finish();
            }

            return true;
        }

        internal void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 0)
            {
                finished();
            }
        }
    }
}
