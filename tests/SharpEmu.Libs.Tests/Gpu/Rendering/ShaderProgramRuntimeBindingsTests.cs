// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

public sealed class ShaderProgramRuntimeBindingsTests
{
    [Theory]
    [InlineData(0, 4, ShaderStageKind.Pixel)]
    [InlineData(4, 3, ShaderStageKind.Vertex)]
    public void StageRuntimeBlock_UsesCombinedDescriptorIndices(int firstBinding, int bindingCount, ShaderStageKind stage)
    {
        Gen5GlobalMemoryBinding[] combined = Enumerable.Range(0, 7)
            .Select(index => new Gen5GlobalMemoryBinding(0, 0x10004UL + (ulong)index * 4, [], [], 0, false, 64))
            .ToArray();
        var local = combined.Skip(firstBinding).Take(bindingCount).ToArray();
        var evaluation = new Gen5ShaderEvaluation([0x12345678], [], [], local);
        var state = new Gen5ShaderState(new Gen5ShaderProgram(0, []), [], null);
        var provider = typeof(AgcExports).GetNestedType("ShaderProgramProvider", BindingFlags.NonPublic)!;
        var create = provider.GetMethod("CreateStageProgram", BindingFlags.Static | BindingFlags.NonPublic)!;
        var program = (AgcExports.CompiledStageProgram)create.Invoke(null,
            [stage, 0UL, new VulkanCompiledGuestShader([]), state, evaluation,
             Array.Empty<GuestDrawTexture>(), Array.Empty<Gen5VertexInputBinding>(), firstBinding, 0, 9,
             stage == ShaderStageKind.Pixel ? 7 : 8, false, combined])!;
        var scalarBuffer = Assert.IsType<GuestMemoryBuffer>(program.ScalarBuffer);
        try
        {
            Assert.Equal((256 + combined.Length) * sizeof(uint), scalarBuffer.Length);
            Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(scalarBuffer.Data));
            var alignment = (ulong)typeof(AgcExports).GetField("_storageBufferOffsetAlignment", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
            for (var index = 0; index < combined.Length; index++)
            {
                Assert.Equal((uint)(combined[index].BaseAddress & (alignment - 1)),
                    BinaryPrimitives.ReadUInt32LittleEndian(scalarBuffer.Data.AsSpan((256 + index) * sizeof(uint))));
            }

            Assert.Equal(bindingCount, program.GlobalBuffers.Count);
            Assert.Equal(firstBinding, program.GlobalBufferBase);
        }
        finally
        {
            GuestDataPool.Shared.Return(scalarBuffer.Data);
        }
    }
}
