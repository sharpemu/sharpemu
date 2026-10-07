// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// The embedded vertex and instance offsets reach the draw from the user data of the registers the detector named.
[Collection(SchedulingStateCollection.Name)]
public sealed class EmbeddedVertexOffsetTests
{
    private const uint UserDataBase = 8;
    private const int VertexOffsetRegister = 18;
    private const int InstanceOffsetRegister = 19;
    private const uint Format32x4Float = 77;

    private static VertexAttributeResource Attribute(int attributeId, int bufferIndex, uint offsetBytes) =>
        new(new BufferDescriptorWords(unchecked((uint)VertexBase), (uint)(VertexBase >> 32) | (16u << 16), 64, (Format32x4Float << 12) | 0xFAC), 0, 4, attributeId, 0, bufferIndex, offsetBytes);

    private static VertexInputInfo VertexInput(int vertexOffset, int vertexRegister = VertexOffsetRegister, int instanceRegister = ShaderProgramInfo.NoScalarRegister, uint instanceOffset = 0, bool fetchEmbedded = true)
    {
        var userData = new uint[12];
        userData[VertexOffsetRegister - UserDataBase] = unchecked((uint)vertexOffset);
        userData[InstanceOffsetRegister - UserDataBase] = instanceOffset;
        var program = Program(ShaderStageKind.Vertex, userDataBase: UserDataBase, vertexOffsetScalar: vertexRegister, instanceOffsetScalar: instanceRegister);
        return new VertexInputInfo
        {
            Buffers = [new VertexInputBuffer(VertexBase, 16, 64)],
            Attributes = [Attribute(0, 0, 4)],
            FetchEmbedded = fetchEmbedded,
            Stage = new ShaderStageResources(program, new ResourceSnapshot { UserData = userData }, VertexShader),
        };
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(-8)]
    public void VertexOffset_ComesFromTheUserDataOfTheDetectedRegister(int offset)
    {
        var input = VertexInput(offset);

        Assert.Equal(VertexOffsetRegister, input.Stage.Program!.VertexOffsetScalarRegister);
        Assert.Equal(unchecked((uint)offset), input.Stage.Resources.UserData[VertexOffsetRegister - UserDataBase]);
        Assert.Equal(offset, RenderExecutor.ResolveVertexOffset(0, input));
        Assert.Equal(new VertexInputBuffer(VertexBase, 16, 64), Assert.Single(input.Buffers));
        Assert.Equal(4u, Assert.Single(input.Attributes).OffsetBytes);
    }

    [Fact]
    public void FixedFunctionFetch_LeavesTheDrawArgumentAlone()
    {
        var input = VertexInput(8, fetchEmbedded: false);

        Assert.Equal(5, RenderExecutor.ResolveVertexOffset(5, input));
        Assert.Equal(0, RenderExecutor.ResolveVertexOffset(0, input));
        Assert.Equal(0u, RenderExecutor.ResolveInstanceOffset(input));
    }

    [Fact]
    public void NonZeroIndexOffsetRegister_WinsOverTheEmbeddedOffset()
    {
        var input = VertexInput(8);

        Assert.Equal(5, RenderExecutor.ResolveVertexOffset(5, input));
    }

    [Theory]
    [InlineData(false, false, 8, 0, 2, 10)]
    [InlineData(true, false, 8, 0, -3, 5)]
    [InlineData(true, false, -8, 0, 10, 2)]
    [InlineData(false, false, 8, 7, 2, 9)]
    [InlineData(false, true, 8, 7, 2, 2)]
    [InlineData(true, true, 8, 7, -3, -3)]
    public void Offset_ReachesTheDrawWithoutChangingBufferOrAttributeOffsets(
        bool indexed, bool indirect, int scalarOffset, uint registerOffset, int argumentOffset, int expectedOffset)
    {
        using var fatal = new FatalScope();
        var host = new RecordingRenderHost();
        var defaults = Programs();
        var provider = new FakePipelineProvider
        {
            Graphics = new GraphicsPrograms
            {
                Vertex = defaults.Vertex, Pixel = defaults.Pixel,
                VertexInput = VertexInput(scalarOffset), PixelInput = defaults.PixelInput,
            },
        };
        var banks = Banks();
        banks.UserConfig.IndexOffset = registerOffset;
        var source = indirect ? DrawOffsetSource.IndirectArguments : DrawOffsetSource.Packet;
        var executor = new RenderExecutor(host, provider);
        if (indexed)
        {
            executor.DrawIndexed(1, banks, Indexed(3, baseVertex: argumentOffset, source: source));
            Assert.Contains($"draw_indexed 3 1 0 {expectedOffset} 0", host.Calls);
        }
        else
        {
            executor.DrawAuto(1, banks, Auto(3, firstVertex: (uint)argumentOffset, source: source));
            Assert.Contains($"draw 3 1 {expectedOffset} 0", host.Calls);
        }

        Assert.Contains(host.Calls, call => call.StartsWith("obtain 100400000 400 written=False", StringComparison.Ordinal));
        Assert.Contains(host.Calls, call => call.StartsWith("bind_vertex ", StringComparison.Ordinal) && call.EndsWith(":0", StringComparison.Ordinal));
    }

    [Fact]
    public void InstanceOffset_ComesFromItsOwnRegisterAndKeepsTheVertexOffset()
    {
        var input = VertexInput(8, instanceRegister: InstanceOffsetRegister, instanceOffset: 3);

        Assert.Equal(8, RenderExecutor.ResolveVertexOffset(0, input));
        Assert.Equal(3u, RenderExecutor.ResolveInstanceOffset(input));
    }
}
