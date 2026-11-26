// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class VulkanPipelineLibraryTests
{
    // Pixel modules move their own bindings from set 0 to set 2 when pipelines link from libraries; the bindless heap
    // (set 1) and every other instruction stay as they were.
    [Fact]
    public void WithDescriptorSet_MovesOnlyTheNamedSet()
    {
        uint[] words =
        [
            0x07230203, 0x00010500, 0, 10, 0,
            (4u << 16) | 71, 5, 34, 0,     // OpDecorate %5 DescriptorSet 0
            (4u << 16) | 71, 5, 33, 58,    // OpDecorate %5 Binding 58
            (4u << 16) | 71, 6, 34, 1,     // OpDecorate %6 DescriptorSet 1
            (4u << 16) | 71, 7, 34, 0,     // OpDecorate %7 DescriptorSet 0
            (5u << 16) | 43, 1, 8, 34, 0,  // OpConstant %1 %8 = 34 0 (looks like a decoration, is not one)
        ];
        var code = new byte[words.Length * 4];
        Buffer.BlockCopy(words, 0, code, 0, code.Length);
        var method = PresenterUnderTest.PresenterType.GetMethod("WithDescriptorSet", BindingFlags.NonPublic | BindingFlags.Static)!;

        var moved = (byte[])method.Invoke(null, [code, 0u, 2u])!;

        var result = new uint[words.Length];
        Buffer.BlockCopy(moved, 0, result, 0, moved.Length);
        var expected = (uint[])words.Clone();
        expected[8] = 2;
        expected[20] = 2;
        Assert.Equal(expected, result);
        Assert.Equal(0u, BitConverter.ToUInt32(code, 8 * 4));
    }
}
