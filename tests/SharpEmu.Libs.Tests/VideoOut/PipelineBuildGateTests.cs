// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// One build of a pipeline key at a time.
public sealed class PipelineBuildGateTests
{
    [Fact]
    public void ASecondCreationOfAKeyWaitsForTheFirstBuild()
    {
        var gate = new PipelineBuildGate();
        var first = gate.Begin("key");
        Task<PipelineBuildGate.Build> second;
        try
        {
            second = Task.Factory.StartNew(() => gate.Begin("key"), TaskCreationOptions.LongRunning);
            Assert.False(second.Wait(200));
        }
        finally
        {
            gate.Finish(first);
        }

        Assert.True(second.Wait(5000));
        gate.Finish(second.Result);
    }

    [Fact]
    public void OtherKeysBuildSideBySide()
    {
        var gate = new PipelineBuildGate();
        var first = gate.Begin("first");
        var second = Task.Factory.StartNew(() => gate.Begin("second"), TaskCreationOptions.LongRunning);
        Assert.True(second.Wait(5000));
        gate.Finish(second.Result);
        gate.Finish(first);
    }

    [Fact]
    public void AWaiterLendsALowerPriorityBuilderItsPriorityUntilTheBuildEnds()
    {
        var gate = new PipelineBuildGate();
        using var registered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var after = ThreadPriority.Highest;
        var builder = new Thread(() =>
        {
            var build = gate.Begin("key");
            registered.Set();
            release.Wait();
            gate.Finish(build);
            after = Thread.CurrentThread.Priority;
        })
        {
            Priority = ThreadPriority.BelowNormal,
        };
        builder.Start();
        try
        {
            registered.Wait();
            var waiter = Task.Factory.StartNew(() => gate.Begin("key"), TaskCreationOptions.LongRunning);
            Assert.True(SpinWait.SpinUntil(() => builder.Priority == ThreadPriority.Normal, 5000));
            release.Set();
            builder.Join();
            Assert.Equal(ThreadPriority.BelowNormal, after);
            Assert.True(waiter.Wait(5000));
            gate.Finish(waiter.Result);
        }
        finally
        {
            release.Set();
            builder.Join();
        }
    }
}
