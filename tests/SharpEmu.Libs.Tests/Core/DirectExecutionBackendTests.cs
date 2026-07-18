// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Core;

public sealed class DirectExecutionBackendTests
{
    [Fact]
    public async Task BlockedGuestCallback_WaitsForWakeBeforeResuming()
    {
        var waiter = new ControlledBlockWaiter(0x1234);

        var completion = Task.Run(() =>
        {
            var success = DirectExecutionBackend.WaitForBlockedGuestCallbackCore(
                waiter,
                deadlineTimestamp: 0,
                stopRequested: static () => false,
                tryWake: static candidate => candidate.TryWake(),
                out var resumeResult,
                out var error);
            return (Success: success, ResumeResult: resumeResult, Error: error);
        });

        await waiter.WakeAttempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, waiter.ResumeCount);
        Assert.False(completion.IsCompleted);

        waiter.Ready.Set();

        var result = await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Success);
        Assert.Equal(0x1234, result.ResumeResult);
        Assert.Null(result.Error);
        Assert.Equal(1, waiter.ResumeCount);
    }

    private sealed class ControlledBlockWaiter(int resumeResult) : IGuestThreadBlockWaiter
    {
        private int _resumeCount;

        public TaskCompletionSource WakeAttempted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public ManualResetEventSlim Ready { get; } = new(false);

        public int ResumeCount => Volatile.Read(ref _resumeCount);

        public int Resume()
        {
            Interlocked.Increment(ref _resumeCount);
            return resumeResult;
        }

        public bool TryWake()
        {
            WakeAttempted.TrySetResult();
            return Ready.IsSet;
        }
    }
}
