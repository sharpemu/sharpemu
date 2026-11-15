// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.GpuCommands;
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

    // A vertex program whose fetch adds s20 to its vertex index and s21 to its instance index.
    private static VertexInputInfo OffsetRegisterInput(uint vertexOffset, uint instanceOffset, bool fetchEmbedded)
    {
        var userData = new uint[14];
        userData[20 - UserDataBase] = vertexOffset;
        userData[21 - UserDataBase] = instanceOffset;
        var program = Program(ShaderStageKind.Vertex, userDataBase: UserDataBase, vertexOffsetScalar: 20, instanceOffsetScalar: 21);
        return new VertexInputInfo
        {
            Buffers = [new VertexInputBuffer(VertexBase, 16, 64)],
            Attributes = [Attribute(0, 0, 4)],
            FetchEmbedded = fetchEmbedded,
            Stage = new ShaderStageResources(program, new ResourceSnapshot { UserData = userData }, VertexShader),
        };
    }

    // The packet names 0xA9 for the base vertex and 0xA8 for the start instance (0x280 names none); the arguments hold 8
    // and 3. A named value reaches the program in its register, and a fetch the host replaced reads it from there; a program
    // that fetches itself adds it in its own code, so its draw counts from 0. Values the packet names no register for stay in
    // the draw's arguments.
    [Theory]
    [InlineData(true, true, true, "draw_indexed 3 1 0 8 3")]
    [InlineData(false, true, true, "draw 3 1 8 3")]
    [InlineData(true, false, true, "draw_indexed 3 1 0 0 0")]
    [InlineData(false, false, true, "draw 3 1 0 0")]
    [InlineData(true, true, false, "draw_indexed 3 1 0 8 3")]
    [InlineData(false, true, false, "draw 3 1 8 3")]
    public void IndirectDraw_OffsetsInNamedRegistersReachAReplacedFetch(bool indexed, bool fetchEmbedded, bool namesRegisters, string expected)
    {
        using var fatal = new FatalScope();
        var runner = new StreamRunner();
        runner.Host.WriteWords(StreamRunner.TableAddress, indexed ? new uint[] { 3, 1, 0, 8, 3 } : new uint[] { 3, 1, 8, 3 });
        var vertexRegister = namesRegisters ? 0xA9u : 0x280u;
        var instanceRegister = namesRegisters ? 0xA8u : 0x280u;
        var setBase = StreamRunner.Packet(PacketOpcode.SetBase, 1, StreamRunner.Low(StreamRunner.TableAddress), StreamRunner.High(StreamRunner.TableAddress));
        if (indexed)
        {
            runner.Run(
                setBase,
                StreamRunner.Packet(PacketOpcode.IndexBase, StreamRunner.Low(StreamRunner.DataAddress), StreamRunner.High(StreamRunner.DataAddress)),
                StreamRunner.Packet(PacketOpcode.IndexType, 0),
                StreamRunner.Packet(PacketOpcode.IndexBufferSize, 40),
                StreamRunner.Packet(PacketOpcode.DrawIndexIndirect, 0, vertexRegister, instanceRegister, 0x22));
        }
        else
        {
            runner.Run(setBase, StreamRunner.Packet(PacketOpcode.DrawIndirect, 0, vertexRegister, instanceRegister, 2));
        }

        // The program's s20 and s21 are the registers the packet named, or hold unrelated values when it named none.
        var input = namesRegisters
            ? OffsetRegisterInput(runner.Interpreter.Registers.Shader[0xA9], runner.Interpreter.Registers.Shader[0xA8], fetchEmbedded)
            : OffsetRegisterInput(5, 7, fetchEmbedded);
        var host = new RecordingRenderHost();
        var defaults = Programs();
        var provider = new FakePipelineProvider
        {
            Graphics = new GraphicsPrograms
            {
                Vertex = defaults.Vertex, Pixel = defaults.Pixel,
                VertexInput = input, PixelInput = defaults.PixelInput,
            },
        };
        var executor = new RenderExecutor(host, provider);
        if (indexed)
        {
            executor.DrawIndexed(1, Banks(), Assert.Single(runner.Host.IndexedDraws) with { IndexAddress = IndexBase });
        }
        else
        {
            executor.DrawAuto(1, Banks(), Assert.Single(runner.Host.AutoDraws));
        }

        Assert.Contains(expected, host.Calls);
    }
}
