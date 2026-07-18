// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanGuestImageWriterTests
{
    [Fact]
    public void RecordingSingleRenderTargetDoesNotAllocate()
    {
        const ulong address = 0x1000;
        var draw = new VulkanOffscreenGuestDraw(
            new VulkanTranslatedGuestDraw(
                [], [], [], [], [], 0, 0, 0, 0, null, GuestRenderState.Default),
            [new GuestRenderTarget(address, 1, 1, 0, 0)],
            null,
            PublishTarget: true,
            ShaderAddress: 0);
        var writers = Assert.IsType<Dictionary<ulong, long>>(
            typeof(VulkanVideoPresenter)
                .GetField("_guestImageWorkSequences", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetValue(null));
        var record = (Action<object, long>)typeof(VulkanVideoPresenter)
            .GetMethod("RecordGuestImageWritersLocked", BindingFlags.Static | BindingFlags.NonPublic)!
            .CreateDelegate(typeof(Action<object, long>));

        writers.Clear();
        writers[address] = 0;
        record(draw, 1);
        writers.Clear();
        writers[address] = 0;

        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        record(draw, 2);

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore);
        Assert.Equal(2, writers[address]);
    }
}
