// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Ampr;

public static partial class AmprExports
{
    internal enum AmprEngine
    {
        Apr,
        Amm,
    }

    private static readonly ConcurrentDictionary<(AmprEngine Engine, uint Priority), CommandQueue> _commandQueues = new();

    // Submits a buffer to the queue of its engine and priority. The submission completes when the buffer has run.
    // onQueuedCompletion runs only for a buffer that finished on the queue's thread, with that thread's context.
    internal static int SubmitCommandBuffer(
        CpuContext ctx,
        ulong commandBuffer,
        AmprEngine engine,
        uint priority,
        Action<CpuContext, int, uint>? onQueuedCompletion,
        out AmprSubmission? submission)
    {
        submission = null;
        var status = TryCaptureCommands(ctx, commandBuffer, out var captured);
        if (status != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return status;
        }

        var queue = _commandQueues.GetOrAdd(
            (engine, priority),
            static key => new CommandQueue($"SharpEmu-AMPR {key.Engine} {key.Priority}"));
        submission = queue.Submit(ctx, captured!, onQueuedCompletion);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal sealed class AmprSubmission
    {
        private readonly ManualResetEventSlim _completed = new();

        public int ExecutionResult { get; private set; }

        public uint ErrorOffset { get; private set; }

        // The buffer ran on the thread that submitted it, before the submission returned.
        public bool CompletedInline { get; private set; }

        public bool IsCompleted => _completed.IsSet;

        public void Wait() => _completed.Wait();

        public bool Wait(TimeSpan timeout) => _completed.Wait(timeout);

        internal void Complete(int executionResult, uint errorOffset, bool inline)
        {
            ExecutionResult = executionResult;
            ErrorOffset = errorOffset;
            CompletedInline = inline;
            _completed.Set();
        }
    }

    // The AMPR engine runs the buffers of one queue in order, and a buffer that waits for an address holds the queue,
    // not the thread that submitted it: titles submit reads that wait for work they submit later. A buffer runs on the
    // submitting thread while the queue is idle and every wait it reaches is already met. From the first wait that is
    // not, the rest of it and every later buffer of the queue run in order on the queue's own thread.
    private sealed class CommandQueue(string name)
    {
        private readonly object _gate = new();
        private readonly Queue<QueuedRun> _pending = new();
        private Thread? _worker;

        public AmprSubmission Submit(CpuContext ctx, CapturedCommands captured, Action<CpuContext, int, uint>? onQueuedCompletion)
        {
            var submission = new AmprSubmission();
            lock (_gate)
            {
                var next = 0;
                if (_pending.Count == 0 &&
                    RunCommands(ctx, captured, ref next, stopAtUnmetWait: true, out var executionResult, out var errorOffset))
                {
                    submission.Complete(executionResult, errorOffset, inline: true);
                    return submission;
                }

                // The submitting thread goes on using its context, so the queue's thread gets its own.
                var context = new CpuContext(ctx.Memory, ctx.TargetGeneration)
                {
                    FsBase = ctx.FsBase,
                    GsBase = ctx.GsBase,
                };
                _pending.Enqueue(new QueuedRun(context, captured, next, onQueuedCompletion, submission));
                if (_worker is null)
                {
                    _worker = new Thread(Drain) { IsBackground = true, Name = name };
                    _worker.Start();
                }
                else
                {
                    Monitor.Pulse(_gate);
                }
            }

            return submission;
        }

        private void Drain()
        {
            while (true)
            {
                QueuedRun run;
                lock (_gate)
                {
                    while (_pending.Count == 0)
                    {
                        Monitor.Wait(_gate);
                    }

                    run = _pending.Peek();
                }

                // The run stays queued until it ends, so a buffer submitted meanwhile queues behind it.
                var next = run.Next;
                RunCommands(run.Context, run.Commands, ref next, stopAtUnmetWait: false, out var executionResult, out var errorOffset);
                run.OnQueuedCompletion?.Invoke(run.Context, executionResult, errorOffset);
                run.Submission.Complete(executionResult, errorOffset, inline: false);
                lock (_gate)
                {
                    _pending.Dequeue();
                }
            }
        }
    }

    private sealed record QueuedRun(
        CpuContext Context,
        CapturedCommands Commands,
        int Next,
        Action<CpuContext, int, uint>? OnQueuedCompletion,
        AmprSubmission Submission);
}
