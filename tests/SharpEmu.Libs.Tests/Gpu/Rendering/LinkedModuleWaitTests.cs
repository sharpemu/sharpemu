// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Rendering;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class LinkedModuleWaitTests
{
    [Fact]
    public void A_module_that_compiles_within_the_bound_is_waited_for()
    {
        var compile = Task.Delay(50);
        Assert.True(RenderExecutor.WaitForLinkedModule(() => compile.IsCompleted, () => compile, Stopwatch.Frequency * 5));
    }

    [Fact]
    public void A_module_still_compiling_at_the_bound_is_left_to_the_skip()
    {
        var compile = new TaskCompletionSource().Task;
        var started = Stopwatch.GetTimestamp();
        Assert.False(RenderExecutor.WaitForLinkedModule(() => false, () => compile, Stopwatch.Frequency / 20));
        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Without_a_completion_the_wait_polls_until_ready()
    {
        var polls = 0;
        Assert.True(RenderExecutor.WaitForLinkedModule(() => ++polls >= 3, () => null, Stopwatch.Frequency * 5));
        Assert.Equal(3, polls);
    }

    [Fact]
    public void A_completed_compile_whose_pipeline_is_not_ready_polls_with_sleeps()
    {
        var polls = 0;
        Assert.False(RenderExecutor.WaitForLinkedModule(() => { polls++; return false; }, () => Task.CompletedTask, Stopwatch.Frequency / 20));
        // About one poll per millisecond of the 50 ms bound, not a spin.
        Assert.InRange(polls, 1, 200);
    }

    [Fact]
    public void A_failed_compile_ends_the_wait_without_throwing()
    {
        var compile = Task.FromException(new InvalidOperationException("compile failed"));
        Assert.False(RenderExecutor.WaitForLinkedModule(() => false, () => compile, Stopwatch.Frequency / 20));
    }
}
