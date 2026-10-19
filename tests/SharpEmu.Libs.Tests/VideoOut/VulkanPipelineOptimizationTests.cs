// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPipelineOptimizationTests
{
    [Fact]
    public async Task PendingAndFailedBuildsKeepTheInitialPipelineWhileCompletedBuildsArePromoted()
    {
        var type = PresenterUnderTest.PresenterType;
        var presenter = RuntimeHelpers.GetUninitializedObject(type);
        foreach (var name in new[] { "_pipelineOptimizationGate", "_pipelineOptimizationQueue", "_pipelineOptimizations" })
        {
            var field = type.GetField(name, PresenterUnderTest.InstanceMembers)!;
            field.SetValue(presenter, Activator.CreateInstance(field.FieldType));
        }
        var tasks = (ConcurrentDictionary<ulong, Task<Pipeline>>)type.GetField("_pipelineOptimizations", PresenterUnderTest.InstanceMembers)!.GetValue(presenter)!;
        object? Invoke(string name, params object[] arguments) => type.GetMethod(name, PresenterUnderTest.InstanceMembers)!.Invoke(presenter, arguments);

        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var initial = new Pipeline(11);
        var optimized = new Pipeline(22);
        try
        {
            Invoke("QueuePipelineOptimization", initial, (Func<Pipeline>)(() =>
            {
                entered.SetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                return optimized;
            }));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(initial.Handle, ((Pipeline)Invoke("ResolveOptimizedPipeline", initial)!).Handle);

            release.Set();
            Assert.Equal(optimized.Handle, (await tasks[initial.Handle].WaitAsync(TimeSpan.FromSeconds(10))).Handle);
            Assert.Equal(optimized.Handle, ((Pipeline)Invoke("ResolveOptimizedPipeline", initial)!).Handle);

            var failed = new Pipeline(33);
            Invoke("QueuePipelineOptimization", failed, (Func<Pipeline>)(() => throw new InvalidOperationException("test compile failure")));
            await tasks[failed.Handle].WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(failed.Handle, ((Pipeline)Invoke("ResolveOptimizedPipeline", failed)!).Handle);
        }
        finally
        {
            release.Set();
            if (tasks.TryGetValue(initial.Handle, out var pending)) await pending.WaitAsync(TimeSpan.FromSeconds(10));
            tasks.TryRemove(initial.Handle, out _);
            Invoke("StopPipelineOptimization");
        }
    }
}
