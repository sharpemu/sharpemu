// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Gpu.Buffers;

// Copies guest bytes into mapped staging memory with a few helper threads; the calling thread copies
// too. Pieces are taken one at a time and the caller waits only for pieces a helper has taken, so a
// helper that the guest's threads keep from running costs no more than its share. One thread runs it.
internal sealed unsafe class ParallelGuestCopy(ICpuMemory guest, int helperCount) : IDisposable
{
    internal readonly record struct Piece(ulong Address, nint Destination, int Length);

    // One run. A helper that wakes after it ended finds every piece taken and never reads them.
    private sealed class Job(Piece[] pieces, int count)
    {
        public readonly Piece[] Pieces = pieces;
        public readonly int Count = count;
        public int Next;
        public int Done;
        public volatile bool Failed;
    }

    // Wakes the helpers for a job and the caller when a helper completes the last piece.
    private readonly object _gate = new();
    private Thread[]? _helpers;
    private Job? _job;
    private long _jobs;
    private bool _stopping;

    // Copies the first count pieces, with the helpers when parallel is set. False when a read failed:
    // the caller stages those bytes again where the failure is reported.
    public bool Run(Piece[] pieces, int count, bool parallel)
    {
        var job = new Job(pieces, count);
        if (parallel && helperCount > 0 && count > 1)
        {
            lock (_gate)
            {
                StartHelpers();
                _job = job;
                _jobs++;
                Monitor.PulseAll(_gate);
            }
        }

        Copy(job, helper: false);
        if (Volatile.Read(ref job.Done) != count)
        {
            var spinner = new SpinWait();
            while (Volatile.Read(ref job.Done) != count && !spinner.NextSpinWillYield)
            {
                spinner.SpinOnce(sleep1Threshold: -1);
            }

            lock (_gate)
            {
                while (Volatile.Read(ref job.Done) != count)
                {
                    Monitor.Wait(_gate);
                }
            }
        }

        return !job.Failed;
    }

    private void StartHelpers()
    {
        if (_helpers is not null)
        {
            return;
        }

        _helpers = new Thread[helperCount];
        for (var index = 0; index < helperCount; index++)
        {
            _helpers[index] = new Thread(HelperLoop) { IsBackground = true, Name = $"SharpEmu staging copy {index}" };
            _helpers[index].Start();
        }
    }

    private void Copy(Job job, bool helper)
    {
        int index;
        while ((index = Interlocked.Increment(ref job.Next) - 1) < job.Count)
        {
            var piece = job.Pieces[index];
            try
            {
                if (!guest.TryRead(piece.Address, new Span<byte>((void*)piece.Destination, piece.Length)))
                {
                    job.Failed = true;
                }
            }
            catch (Exception)
            {
                job.Failed = true;
            }

            // A locked increment: the piece's stores into the staging memory are visible before the
            // caller, seeing the count, records the copies that read them.
            if (Interlocked.Increment(ref job.Done) == job.Count && helper)
            {
                lock (_gate)
                {
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    private void HelperLoop()
    {
        SharpEmu.HLE.Host.HostLaneReservation.ApplyToHostWorkerThread();
        var seen = 0L;
        while (true)
        {
            Job job;
            lock (_gate)
            {
                while (!_stopping && _jobs == seen)
                {
                    Monitor.Wait(_gate);
                }

                if (_stopping)
                {
                    return;
                }

                seen = _jobs;
                job = _job!;
            }

            Copy(job, helper: true);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            Monitor.PulseAll(_gate);
        }
    }
}
