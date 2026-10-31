// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class TilerShaderTests
{
    public static IEnumerable<object[]> BlockCopies()
    {
        foreach (var shape in Enum.GetValues<TilerBlockShape>())
            foreach (var bytes in new[] { 1u, 2u, 4u, 8u, 16u })
                foreach (var toTiled in new[] { false, true })
                {
                    yield return [shape, bytes, toTiled, false];
                    if (TilerShaders.SupportsPackedWords(shape, bytes))
                        yield return [shape, bytes, toTiled, true];
                }
    }

    [Theory]
    [MemberData(nameof(BlockCopies))]
    public void BlockCopyPreservesBindingsAndUsesStoresOnlyForWholeWords(TilerBlockShape shape, uint bytes, bool toTiled, bool packed)
    {
        var spirv = TilerShaders.CreateBlockCopy(shape, bytes, toTiled, packed);
        var module = new SpirvModuleInspector(spirv);
        Assert.True(module.DescriptorBindings.SetEquals([(0u, 0u), (0u, 1u), (0u, 2u)]));
        Assert.True(module.HasVariableInStorageClass(SpirvStorageClass.Uniform));
        var atomic = bytes < 4 && !packed;
        Assert.Equal(atomic, module.Opcodes.Contains((ushort)SpirvOp.AtomicAnd));
        Assert.Equal(atomic, module.Opcodes.Contains((ushort)SpirvOp.AtomicOr));
        Assert.Equal(!atomic, module.Opcodes.Contains((ushort)SpirvOp.Store));

        var local = TilerShaders.BlockCopyWorkgroupSize(shape, bytes, toTiled, packed);
        var words = MemoryMarshal.Cast<byte, uint>(spirv);
        var localSizes = 0;
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            if ((SpirvOp)(words[index] & 0xffff) == SpirvOp.ExecutionMode &&
                words[index + 2] == (uint)SpirvExecutionMode.LocalSize)
            {
                Assert.Equal((local.X, local.Y, local.Z), (words[index + 3], words[index + 4], words[index + 5]));
                localSizes++;
            }
        }
        Assert.Equal(1, localSizes);
        Assert.Equal(packed ? 32u / bytes : 64u, local.X * local.Y * local.Z);
    }

    [Theory]
    [InlineData(TilerBlockShape.GcnThin1D, 1u)]
    [InlineData(TilerBlockShape.Depth64KB, 1u)]
    [InlineData(TilerBlockShape.Standard4KB, 4u)]
    [InlineData(TilerBlockShape.Standard4KB, 8u)]
    [InlineData(TilerBlockShape.Standard4KB, 16u)]
    public void UnsupportedPackedLayoutsAreRejected(TilerBlockShape shape, uint bytes)
    {
        Assert.False(TilerShaders.SupportsPackedWords(shape, bytes));
        foreach (var toTiled in new[] { false, true })
        {
            Assert.Throws<ArgumentException>(() => TilerShaders.CreateBlockCopy(shape, bytes, toTiled, true));
            Assert.Throws<ArgumentException>(() => TilerShaders.BlockCopyWorkgroupSize(shape, bytes, toTiled, true));
            Assert.Equal((8u, 8u, 1u), TilerShaders.BlockCopyWorkgroupSize(shape, bytes, toTiled, false));
        }
    }
}
