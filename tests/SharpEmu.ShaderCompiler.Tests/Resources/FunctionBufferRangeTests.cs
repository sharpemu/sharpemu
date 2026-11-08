// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class FunctionBufferRangeTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FunctionBuffer_ResolvesTheDescriptorExtentAndWriteAccess(bool written)
    {
        var program = Program(written ? BufferStore(0, 0) : BufferLoad(0, 0), EndProgram(8)) with
        {
            FunctionBufferAccesses = new HashSet<uint> { 0 },
        };
        var plan = Extract(program);
        Assert.True(plan.Info.UsesDeviceAddresses);
        Assert.Empty(plan.Info.Buffers);
        var range = Assert.Single(DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x2000, 16u << 16, 10, 0])));
        Assert.True(range.Planned);
        Assert.Equal(0x2000ul, range.Base);
        Assert.Equal(160ul, range.Size);
        Assert.Equal(written, range.Written);
        Assert.Throws<ResourcePlanException>(() =>
            DeviceAddressRangePlanner.Evaluate(plan, Inputs([0x2000, 0x80000000, 10, 0])));
    }
}
