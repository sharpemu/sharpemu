// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using System.Buffers.Binary;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class DeviceAddressCodeSizeTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void PartialDeviceFormatLoad_ConvertsOnlyItsRequestedComponents(uint count)
    {
        var request = Request(Program(ReadFirstLane(0, 12, 0), MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16), MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, 12, destination: 20, count: 4),
            BufferLoad(24, 20, dwords: count, formatted: true), EndProgram(32)));
        Assert.Contains(request.Memory.Entries, memory => memory.Pc == 24 && memory.DeviceDescriptor);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
        var divisions = 0;
        for (var offset = 20; offset < shader.Spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(shader.Spirv.AsSpan(offset));
            if ((header & 0xFFFF) == (uint)SpirvOp.FDiv) divisions++;
            offset += checked((int)(header >> 16) * 4);
        }
        Assert.Equal((int)count * 2, divisions);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void RepeatedDeviceFormatLoads_ShareFormatDecoding(uint components)
    {
        byte[] Compile(int count)
        {
            var instructions = new List<Gen5ShaderInstruction>
            {
                ReadFirstLane(0, 12, 0), MoveScalar(4, 13, 0),
                MoveScalar(8, 14, 16), MoveScalar(12, 15, 0),
                ScalarBufferLoad(16, 12, destination: 20, count: 4),
            };
            for (uint index = 0; index < count; index++)
                instructions.Add(BufferLoad(24 + index * 8, 20,
                    offset: (int)index * 16, dwords: components, formatted: true));
            instructions.Add(EndProgram(24 + (uint)count * 8));
            var request = Request(Program(instructions.ToArray()));
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
            return shader.Spirv;
        }

        var single = Compile(1);
        var repeated = Compile(16);
        // Addresses and destination writes vary at every site. The runtime format
        // catalogue, conversions, swizzles and page walks must not be duplicated.
        Assert.True(repeated.Length - single.Length < 15 * 1500,
            $"Additional device formatted loads grew SPIR-V by {repeated.Length - single.Length} bytes.");
    }

    [Fact]
    public void RepeatedRuntimeFormats_DoNotDuplicateTheLayoutCatalogue()
    {
        byte[] Compile(int count)
        {
            var instructions = new List<Gen5ShaderInstruction>();
            for (uint index = 0; index < count; index++)
                instructions.Add(BufferAccess(index * 8, "BufferLoadFormatXyzw", 8,
                    (int)index * 16, 4, vectorData: 4));
            instructions.Add(EndProgram((uint)count * 8));
            var (plan, resources, layout) = Prepare(Program(instructions.ToArray()));
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                LocalSizeX = 1, ThreadCountX = 1, ForceGenericBufferFormats = true,
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            Gen5LargeDispatcherValidationTests.ValidateWithSpirvToolsWhenAvailable(shader.Spirv);
            return shader.Spirv;
        }
        var single = Compile(1);
        var repeated = Compile(16);
        Assert.True(repeated.Length - single.Length < 15 * 18000,
            $"Additional formatted loads grew SPIR-V by {repeated.Length - single.Length} bytes.");
    }
}
