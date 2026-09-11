// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class PresenterStartupTests
{
    private sealed class StartupState : IDisposable
    {
        private static readonly Type PresenterType = typeof(VulkanVideoPresenter);
        private readonly Dictionary<FieldInfo, object?> _saved = new();
        private readonly object _gate = Read("_gate")!;
        private readonly Func<SharpEmu.HLE.ICpuMemory, SharpEmu.Libs.Agc.AgcExports.HeadlessCommandStream>? _factory =
            VulkanVideoPresenter.TestCommandStreamFactory;

        public StartupState()
        {
            VulkanVideoPresenter.TestCommandStreamFactory = null;
            Set("_closed", false);
            Set("_presenterCloseRequested", false);
            Set("_presenterStartupFailure", null);
            Set("_activePresenter", null);
            Set("_thread", Thread.CurrentThread);
        }

        private static FieldInfo Field(string name) => PresenterType.GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
        private static object? Read(string name) => Field(name).GetValue(null);

        public void Set(string name, object? value)
        {
            lock (_gate)
            {
                var field = Field(name);
                _saved.TryAdd(field, field.GetValue(null));
                field.SetValue(null, value);
                Monitor.PulseAll(_gate);
            }
        }

        public void Publish(CommandStreamQueue queue)
        {
            var type = PresenterType.GetNestedType("Presenter", BindingFlags.NonPublic)!;
            var presenter = RuntimeHelpers.GetUninitializedObject(type);
            type.GetField("_commandStream", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(presenter, queue);
            Set("_activePresenter", presenter);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                foreach (var pair in _saved)
                    pair.Key.SetValue(null, pair.Value);
                VulkanVideoPresenter.TestCommandStreamFactory = _factory;
                Monitor.PulseAll(_gate);
            }
        }
    }

    [Fact]
    public void EarlySubmissionWaitsAndUsesThePublishedQueue()
    {
        var runner = new StreamRunner();
        var queue = new CommandStreamQueue(runner.Host);
        Assert.True(runner.Host.Memory.TryWrite(StreamRunner.CommandAddress, new byte[] { 0, 16, 0, 192, 0, 0, 0, 0 }));
        Assert.True(runner.Host.Memory.TryWrite(StreamRunner.CommandAddress + 8, new byte[] { 0, 16, 0, 192, 0, 0, 0, 0 }));
        using var state = new StartupState();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VulkanVideoPresenter.SubmitCommandStream(runner.Host.Memory, 0, StreamRunner.CommandAddress, 2, 1, null); }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, 5000));
            Assert.Equal(0, queue.PendingSubmissionCount);
            state.Publish(queue);
            Assert.True(thread.Join(5000));
            Assert.Null(failure);
            Assert.Equal(1, queue.PendingSubmissionCount);
            VulkanVideoPresenter.SubmitCommandStream(runner.Host.Memory, 0, StreamRunner.CommandAddress + 8, 2, 2, null);
            Assert.Equal(2, queue.PendingSubmissionCount);
            Assert.Equal(0UL, queue.SubmissionsStarted);
            Assert.Equal(SliceResult.Completed, queue.ProcessOne());
            Assert.Equal(SliceResult.Completed, queue.ProcessOne());
            Assert.Equal(new[] { "begin 0 1", "begin 0 2" }, runner.Host.Calls.Where(call => call.StartsWith("begin ", StringComparison.Ordinal)));
        }
        finally
        {
            if (thread.IsAlive)
            {
                VulkanVideoPresenter.RequestClose();
                Assert.True(thread.Join(5000));
            }
        }
    }

    [Fact]
    public void StartupFailureRejectsTheSubmission()
    {
        var memory = new FakeCpuMemory(0x1000, 0x100);
        using var state = new StartupState();
        var failure = new InvalidOperationException("Device initialization failed.");
        state.Set("_presenterStartupFailure", failure);
        state.Set("_closed", true);
        var exception = Assert.Throws<InvalidOperationException>(() =>
            VulkanVideoPresenter.SubmitCommandStream(memory, 0, 0x1000, 2, 1, null));
        Assert.Same(failure, exception.InnerException);
        Assert.Equal(IdleOutcome.Failed, VulkanVideoPresenter.SubmitDone(memory));
    }

    [Fact]
    public void CloseRejectsAnEarlySubmission()
    {
        var memory = new FakeCpuMemory(0x1000, 0x100);
        using var state = new StartupState();
        VulkanVideoPresenter.RequestClose();
        Assert.Throws<OperationCanceledException>(() =>
            VulkanVideoPresenter.SubmitCommandStream(memory, 0, 0x1000, 2, 1, null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StartupTerminationReleasesAWaitingSubmission(bool failed)
    {
        var memory = new FakeCpuMemory(0x1000, 0x100);
        using var state = new StartupState();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { VulkanVideoPresenter.SubmitCommandStream(memory, 0, 0x1000, 2, 1, null); }
            catch (Exception exception) { failure = exception; }
        });
        thread.Start();
        try
        {
            Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, 5000));
            if (failed)
            {
                state.Set("_presenterStartupFailure", new InvalidOperationException("Device initialization failed."));
                state.Set("_closed", true);
            }
            else
            {
                VulkanVideoPresenter.RequestClose();
            }
            Assert.True(thread.Join(5000));
            if (failed) Assert.IsType<InvalidOperationException>(failure);
            else Assert.IsType<OperationCanceledException>(failure);
        }
        finally
        {
            if (thread.IsAlive)
            {
                VulkanVideoPresenter.RequestClose();
                Assert.True(thread.Join(5000));
            }
        }
    }
}
