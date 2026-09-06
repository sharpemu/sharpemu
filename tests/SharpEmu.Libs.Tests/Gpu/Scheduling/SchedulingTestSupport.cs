// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Scheduling;

// The fatal hook and the deferred-callback marker are static, so these suites run alone.
[CollectionDefinition(SchedulingStateCollection.Name, DisableParallelization = true)]
public sealed class SchedulingStateCollection
{
    public const string Name = "SchedulingState";
}

internal sealed class SchedulerFatalException(string message) : Exception(message);

internal sealed class FatalScope : IDisposable
{
    private readonly Action<string> _previous = SubmissionScheduler.OnFatal;

    public FatalScope()
    {
        SubmissionScheduler.OnFatal = message =>
        {
            lock (Messages)
            {
                Messages.Add(message);
            }

            throw new SchedulerFatalException(message);
        };
    }

    public List<string> Messages { get; } = new();

    public void Dispose() => SubmissionScheduler.OnFatal = _previous;
}

internal sealed class RecordingRenderingState : IRenderingState
{
    public List<string> Log { get; } = new();

    public bool IsRendering { get; set; }

    public void EndRendering()
    {
        Log.Add("end_rendering");
        IsRendering = false;
    }
}

// One timeline counter per device; the test retires ticks with Complete.
internal sealed class FakeTickDevice : IGpuTickDevice
{
    private static int _nextHandle;

    private readonly object _gate = new();
    private readonly List<string> _log = new();
    private readonly nint _firstBuffer;
    private ulong _counter;
    private nint _nextBuffer;

    public FakeTickDevice()
    {
        TimelineHandle = 0xA000 + (ulong)Interlocked.Increment(ref _nextHandle);
        _firstBuffer = (nint)(TimelineHandle << 8);
        _nextBuffer = _firstBuffer;
    }

    public object QueueGate { get; } = new();

    public ulong TimelineHandle { get; }

    public bool CompleteOnSubmit { get; set; }

    public bool FailSubmit { get; set; }

    public bool FailWait { get; set; }

    public List<(nint Buffer, ulong Tick, int Waits, int Signals)> Submits { get; } = new();

    public string[] Log
    {
        get
        {
            lock (_log)
            {
                return _log.ToArray();
            }
        }
    }

    public ulong ReadTimeline()
    {
        Note("read");
        lock (_gate)
        {
            return _counter;
        }
    }

    public bool TryWaitTimeline(ulong tick, out string failure)
    {
        Note($"wait {tick}");
        failure = "VK_ERROR_DEVICE_LOST";
        if (FailWait)
        {
            return false;
        }

        lock (_gate)
        {
            while (_counter < tick)
            {
                Monitor.Wait(_gate);
            }
        }

        return true;
    }

    public nint[] AllocateBuffers(int count)
    {
        Note($"allocate {count}");
        var buffers = new nint[count];
        for (var i = 0; i < count; i++)
        {
            buffers[i] = _nextBuffer++;
        }

        return buffers;
    }

    public void BeginBuffer(nint buffer) => Note($"begin {Name(buffer)}");

    public void EndBuffer(nint buffer) => Note($"end {Name(buffer)}");

    public bool TrySubmit(nint buffer, SubmitBundle bundle, out string failure)
    {
        failure = "VK_ERROR_DEVICE_LOST";
        if (FailSubmit)
        {
            return false;
        }

        var tick = 0UL;
        for (var i = 0; i < bundle.SignalCount; i++)
        {
            if (bundle.SignalSemaphores[i] == TimelineHandle)
            {
                tick = bundle.SignalTicks[i];
            }
        }

        Assert.NotEqual(0UL, tick);
        lock (Submits)
        {
            Submits.Add((buffer, tick, bundle.WaitCount, bundle.SignalCount));
        }

        Note($"submit {Name(buffer)} tick={tick}");
        if (CompleteOnSubmit)
        {
            Complete(tick);
        }

        return true;
    }

    public void Complete(ulong tick)
    {
        lock (_gate)
        {
            if (tick > _counter)
            {
                _counter = tick;
            }

            Monitor.PulseAll(_gate);
        }
    }

    public void Rewind(ulong tick)
    {
        lock (_gate)
        {
            _counter = tick;
        }
    }

    public string Name(nint buffer) => $"b{buffer - _firstBuffer}";

    public void Dispose() => Note("dispose");

    private void Note(string entry)
    {
        lock (_log)
        {
            _log.Add(entry);
        }
    }
}

internal static class SchedulingTestSupport
{
    public static SubmissionScheduler NewActiveScheduler(FakeTickDevice device, RecordingRenderingState? rendering = null)
    {
        var scheduler = new SubmissionScheduler(device, rendering ?? new RecordingRenderingState());
        scheduler.Begin(new SubmissionContext());
        return scheduler;
    }

    public static async Task<bool> CompletesWithin(Task task, int milliseconds) =>
        await Task.WhenAny(task, Task.Delay(milliseconds)) == task;

    public static bool WaitUntil(Func<bool> condition, int milliseconds = 5000)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > milliseconds)
            {
                return false;
            }

            Thread.Sleep(1);
        }

        return true;
    }
}
