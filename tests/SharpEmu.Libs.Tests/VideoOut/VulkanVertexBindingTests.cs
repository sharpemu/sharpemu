// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanVertexBindingTests
{
    [Fact]
    public void SharedBufferUsesOneBinding()
    {
        ulong[] handles = [10, 10];
        bool[] perInstance = [false, false];
        Span<int> sources = stackalloc int[handles.Length];

        var count = VulkanVertexBindingPlanner.BuildUniqueSourceIndices(
            handles,
            perInstance,
            sources);

        Assert.Equal(1, count);
        Assert.Equal(0, sources[0]);
    }

    [Fact]
    public void LaterUniqueBufferKeepsItsBindingPosition()
    {
        ulong[] handles = [10, 10, 20];
        bool[] perInstance = [false, false, false];
        Span<int> sources = stackalloc int[handles.Length];

        var count = VulkanVertexBindingPlanner.BuildUniqueSourceIndices(
            handles,
            perInstance,
            sources);

        Assert.Equal(2, count);
        Assert.Equal([0, 2], sources[..count].ToArray());
    }

    [Fact]
    public void DifferentInputRatesUseDifferentBindings()
    {
        ulong[] handles = [10, 10];
        bool[] perInstance = [false, true];
        Span<int> sources = stackalloc int[handles.Length];

        var count = VulkanVertexBindingPlanner.BuildUniqueSourceIndices(
            handles,
            perInstance,
            sources);

        Assert.Equal(2, count);
        Assert.Equal([0, 1], sources[..count].ToArray());
    }
}
