// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using SharpEmu.ShaderCompiler.Tests.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5SpecializedBufferFormatTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(14u)]
    [InlineData(77u)]
    public void SpecializedFormatXDoesNotCreateRuntimeFormatTable(uint format)
    {
        var program = Program(BufferLoad(0, 0, formatted: true), EndProgram(8));
        var (plan, resources, layout) = Prepare(program);
        Assert.Single(resources.Info.Buffers);
        resources.Info.Buffers[0].DescriptorFormat = format;
        resources.Info.Buffers[0].DescriptorSwizzle = 0x204;
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(new ShaderCompileRequest(plan, resources, layout), out var shader, out var error), error);
        var module = new SpirvModuleInspector(shader.Spirv);
        Assert.DoesNotContain("gfx10BufferFormats", module.Names.Values);
    }
}
