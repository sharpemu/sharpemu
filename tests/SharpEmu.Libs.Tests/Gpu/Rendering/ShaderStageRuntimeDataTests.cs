// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderStageRuntimeDataTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallAndTessellationData_PreserveEachOtherAndTheBufferPrefix(bool callsFirst)
    {
        using var fatal = new FatalScope();
        var info = new ShaderResourceInfo
        {
            Buffers = Enumerable.Range(0, 5).Select(_ => new BufferResource { Read = true }).ToList(),
        };
        var layout = BindingLayout.Allocate(info, [0u, 1u], false, false, true,
            usesDispatchThreadLimits: true, callArgumentCount: 1, callTargetCount: 2,
            usesBufferLengths: true, usesTessellationData: true);
        BindingLayoutValidator.Validate(layout, info, [0u, 1u], false, false, true, 0x474E4D, ShaderStage.Compute);
        Assert.False(layout.UsesPushData);
        Assert.NotNull(layout.Find(DescriptorBindingKind.ShaderData));
        Assert.Equal(42u, layout.ShaderDataDwordCount);
        var tessellation = Enumerable.Range(0, (int)Gen5TessellationData.DwordCount).Select(i => 0x100u + (uint)i).ToArray();
        var stage = new ShaderStageResources(new ShaderProgramInfo { Stage = ShaderStageKind.Compute, Bindings = layout },
            new ResourceSnapshot())
        {
            ThreadLimits = new DispatchThreadLimits(17, 2, 1),
            TessellationData = tessellation,
            CallArguments = [0x123456789ABCDEF0ul],
            CallTargets = [0x1122334455667788ul, 0x8877665544332211ul],
        };
        var data = Enumerable.Repeat(0xA5A5A5A5u, (int)layout.ShaderDataDwordCount).ToArray();
        stage.WriteDispatchThreadLimits(data);
        if (callsFirst) stage.WriteCallData(data);
        stage.WriteTessellationData(data);
        if (!callsFirst) stage.WriteCallData(data);
        Assert.All(data.Take(11), word => Assert.Equal(0xA5A5A5A5u, word));
        Assert.Equal(new uint[] { 17, 2, 1 }, data[11..14]);
        Assert.Equal(tessellation, data[14..36]);
        Assert.Equal(new uint[] { 0x9ABCDEF0, 0x12345678, 0x55667788, 0x11223344, 0x44332211, 0x88776655 }, data[36..]);
    }
}
