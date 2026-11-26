// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.Libs.Gpu.GpuCommands;

// One ordinary end-of-pipe label write (Write32 or Write64 of a fixed value). The command stream thread writes the label
// into guest memory when it interprets the release, before the render thread records the work in front of it; the render
// thread names the scheduler tick of the recording that holds the release. This is a scheduling hint only: it never
// satisfies a wait and never shows that guest bytes are current.
public sealed class EndOfPipeLanding
{
    private long _tick;
    private int _consumed;

    internal EndOfPipeLanding(int queue, ulong destination, int width, ulong value, long sequence)
    {
        Queue = queue;
        Destination = destination;
        Width = width;
        Value = value;
        Sequence = sequence;
    }

    internal int Queue { get; }

    internal ulong Destination { get; }

    // 4 or 8 bytes.
    internal int Width { get; }

    internal ulong Value { get; }

    internal long Sequence { get; }

    // 0 until the render thread recorded the release.
    internal ulong Tick => (ulong)Volatile.Read(ref _tick);

    // A soft wait used this release once; it is never armed again.
    internal bool Consumed => Volatile.Read(ref _consumed) != 0;

    // Render thread: the release is in the recording that Submit gives this tick (SubmissionScheduler reserves timeline
    // ticks only there, in order, so the current recording's tick is CurrentTick; QueueCompletionAction relies on the same).
    internal void Recorded(ulong tick) => Volatile.Write(ref _tick, (long)tick);

    internal void Consume() => Volatile.Write(ref _consumed, 1);

    // Why the soft wait that used this release passed (tests and traces); null while unused or still waiting.
    internal EndOfPipeLandings.Release? ReleasedBy { get; set; }

    // Recorded, and the scheduler timeline reached its tick. Unrecorded, or a recording that never submitted, is not.
    internal bool IsComplete(ulong completedTick)
    {
        var tick = Tick;
        return tick != 0 && completedTick >= tick;
    }
}

// Another queue's wait on an ordinary end-of-pipe label is satisfied as soon as the command stream thread interpreted the
// release, long before the work in front of the release completes on the GPU; on the console it would not be. While that
// work has not completed and another queue's head may run, the satisfied wait suspends like an unmet one, so the render
// thread translates that queue's work instead of waiting for GPU results the resumed queue reads. The wait then passes
// unchanged: once the release completes on the GPU, once no other head may run, at shutdown, or at CapTicks after the first
// suspension. Only the order of the queues' slices changes, to one the command stream could already choose; the label, the
// wait's comparison and every packet stay as they are. SHARPEMU_END_OF_PIPE_SOFT_WAITS=0 turns these soft waits off.
internal sealed class EndOfPipeLandings
{
    internal static readonly bool Configured =
        Environment.GetEnvironmentVariable("SHARPEMU_END_OF_PIPE_SOFT_WAITS") != "0";

    internal static readonly long DefaultCapTicks = Stopwatch.Frequency / 125;

    private const int MaxTracked = 4096;

    // One monotonic deadline per soft wait, from its first suspension; never re-armed (8 ms, the busy batch age).
    internal long CapTicks { get; init; } = DefaultCapTicks;

    // Command stream thread only: the newest admitted release of each label address.
    private readonly Dictionary<ulong, EndOfPipeLanding> _latest = new();
    private long _sequence;

    // Command stream thread: an ordinary label write of this queue is in guest memory now. Other kinds take no record (an
    // earlier record of the address is dropped, so a later wait cannot match it).
    internal EndOfPipeLanding? Publish(int queue, in EndOfPipeWrite write)
    {
        var width = write.Kind switch
        {
            EndOfPipeWriteKind.Write32 => 4,
            EndOfPipeWriteKind.Write64 => 8,
            _ => 0,
        };
        if (width == 0 || write.Destination == 0)
        {
            _latest.Remove(write.Destination);
            return null;
        }

        if (_latest.Count >= MaxTracked && !_latest.ContainsKey(write.Destination))
        {
            _latest.Clear();
        }

        var value = width == 4 ? (uint)write.Value : write.Value;
        var landing = new EndOfPipeLanding(queue, write.Destination, width, value, ++_sequence);
        _latest[write.Destination] = landing;
        return landing;
    }

    // Command stream thread: this queue's wait read observed at address (not a deferred label, not its own write) and found
    // the condition met. Returns the release to wait for softly, or null. Admitted: an equality compare of the record's
    // width under a full mask, whose reference and observed value both equal the value another queue's newest release of
    // that address wrote, while that release is unconsumed and has not completed on the GPU.
    internal EndOfPipeLanding? Admit(int queue, ulong address, int width, uint compareFunction, ulong reference, ulong mask,
        ulong observed, ulong completedTick)
    {
        if (!_latest.TryGetValue(address, out var landing))
        {
            return null;
        }

        if (landing.Consumed || landing.IsComplete(completedTick))
        {
            _latest.Remove(address);
            return null;
        }

        var fullMask = width == 4 ? 0xFFFF_FFFFUL : ulong.MaxValue;
        if (landing.Queue == queue || landing.Width != width || compareFunction != 3 || (mask & fullMask) != fullMask ||
            reference != landing.Value || observed != landing.Value)
        {
            return null;
        }

        return landing;
    }

    // Command stream thread: another write to this address (a deferred label) replaces its hint.
    internal void Forget(ulong destination) => _latest.Remove(destination);

    internal enum Release
    {
        Complete,
        Idle,
        Cap,
        Off,
    }
}
