// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DeviceBufferFormatSizeTests
{
    [Theory]
    [InlineData(1u, false)]
    [InlineData(2u, false)]
    [InlineData(3u, false)]
    [InlineData(4u, false)]
    [InlineData(4u, true)]
    public void RepeatedDynamicFormattedLoads_HaveBoundedModuleSize(uint components, bool traceFaults)
    {
        var instructions = new List<Gen5ShaderInstruction>
        {
            ReadFirstLane(0, 12, 0), MoveScalar(4, 13, 0),
            MoveScalar(8, 14, 16), MoveScalar(12, 15, 0),
            ScalarBufferLoad(16, 12, destination: 20, count: 4),
        };
        uint pc = 24;
        for (var access = 0; access < 64; access++, pc += 8)
        {
            instructions.Add(BufferLoad(pc, 20, offset: access * 16, dwords: components, formatted: true));
        }

        instructions.Add(EndProgram(pc));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout) { TraceDeviceAddressFaults = traceFaults };
        Assert.All(request.Memory.Entries.Where(entry => entry.Pc >= 24), entry => Assert.True(entry.DeviceDescriptor));
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        // Repeating the complete dynamic converter at each site exceeds 1 MiB.
        Assert.True(shader.Spirv.Length < 256 * 1024, $"module contains {shader.Spirv.Length} bytes");
    }
}
