// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5DeviceBufferFormatStoreTests
{
    // A V# read from a lane-chosen address is a runtime descriptor; its format is known only when the shader runs.
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    public void AFormattedStoreThroughARuntimeDescriptorCompiles(uint components)
    {
        var program = Program(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            ScalarLoad(8, 12, 16, count: 4),
            BufferStore(16, 16, dwords: components, formatted: true),
            EndProgram(24));
        Assert.True(Extract(program).Memory.Find(16)!.DeviceDescriptor);
        var request = Request(program);

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.Contains((ushort)SpirvOp.AtomicCompareExchange, module.Opcodes);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
    }

    // A typed load through a runtime descriptor converts with the instruction format, not the descriptor's.
    [Theory]
    [InlineData(1u)]
    [InlineData(4u)]
    public void ATypedLoadThroughARuntimeDescriptorCompiles(uint components)
    {
        const uint Format32Float = 22;
        var load = BufferLoad(16, 16, dwords: components, formatted: true);
        load = load with
        {
            Opcode = "T" + load.Opcode,
            Control = ((Gen5BufferMemoryControl)load.Control!) with { Typed = true, TypedFormat = Format32Float },
        };
        var program = Program(
            ReadFirstLane(0, 12, 0),
            MoveScalar(4, 13, 0),
            ScalarLoad(8, 12, 16, count: 4),
            load,
            EndProgram(24));
        Assert.True(Extract(program).Memory.Find(16)!.DeviceDescriptor);
        var request = Request(program);

        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
    }
}
