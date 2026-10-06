// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// Draw entry points over a recording host: skips, topology, indices, offsets, vertex ranges and emission.
[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorDrawTests : IDisposable
{
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorDrawTests() => _executor = new RenderExecutor(_host, _pipelines);

    public void Dispose() => _fatal.Dispose();

    private void AssertOrder(params string[] prefixes)
    {
        var last = -1;
        foreach (var prefix in prefixes)
        {
            var index = _host.Calls.FindIndex(last + 1, c => c.StartsWith(prefix, StringComparison.Ordinal));
            Assert.True(index >= 0, $"missing '{prefix}' after index {last} in: {string.Join(" | ", _host.Calls)}");
            last = index;
        }
    }

    [Fact]
    public void IndexedDraw_RecordsTheFullSequenceInOrder()
    {
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)]);
        _executor.DrawIndexed(7, Banks(), Indexed(6, instances: 2, baseVertex: 3));

        AssertOrder(
            "pending",
            "debug DrawIndex 7 6 0 1 2",
            "find_image 100000000 ColorTarget",
            "bind_target 1",
            "prepare_bindings Vertex -> 1",
            "prepare_bindings Pixel -> 2",
            "bind_resources 1",
            "bind_resources 2",
            "obtain 100400000 40 written=False -> 100:0",
            "obtain 100500000 C written=False",
            "acquire_color 0 100000000 image=1",
            "debug DrawIndex 7 100 6 0 2 0",
            "bind_vertex 100:0",
            "commit Graphics A1 [1,2]",
            "bind_index",
            "dynamic_state",
            "begin_rendering 64x64x1 colors=1 samples=1",
            "bind_pipeline Graphics A1",
            "draw_indexed 6 2 0 3 0",
            "reset_bindings");
        Assert.Single(_pipelines.PipelineRequests);
        Assert.Equal((PrimitiveTopology.TriangleList, false, true), _pipelines.PipelineRequests[0]);
        Assert.DoesNotContain("end_rendering", _host.Calls);
        Assert.DoesNotContain("prepare_device_addresses", _host.Calls);
        Assert.Contains("create_graphics_pipeline colors=1 depth=False topology=TriangleList restart=False", _pipelines.Calls);
    }

    private const ulong IndirectArguments = RecordingRenderHost.MemoryBase + 0x60_0000;

    private void WriteIndirectArguments(params uint[] words)
    {
        var bytes = new byte[words.Length * 4];
        for (var i = 0; i < words.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), words[i]);
        }

        _host.WriteGuest(IndirectArguments, bytes);
    }

    [Fact]
    public void IndirectIndexedDraw_LetsTheGpuReadTheArguments()
    {
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4)]);
        var arguments = Indexed(40, source: DrawOffsetSource.IndirectArguments) with { IndirectArgumentsAddress = IndirectArguments };
        _executor.DrawIndexed(7, Banks(), arguments);

        AssertOrder(
            "obtain 100500000 50 written=False",
            "obtain 100600000 14 written=False",
            "begin_rendering",
            "bind_pipeline Graphics A1",
            "draw_indexed_indirect",
            "reset_bindings");
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("draw_indexed ", StringComparison.Ordinal));
    }

    [Fact]
    public void IndirectIndexedStrip_ReadsTheArgumentsOnTheCpu()
    {
        WriteIndirectArguments(6, 2, 4, 3, 1);
        var arguments = Indexed(40, source: DrawOffsetSource.IndirectArguments) with { IndirectArgumentsAddress = IndirectArguments };
        _executor.DrawIndexed(7, Banks(PrimitiveTriangleStrip), arguments);

        AssertOrder("obtain 100500008 C written=False", "draw_indexed 6 2 0 3 1", "reset_bindings");
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("draw_indexed_indirect", StringComparison.Ordinal));
    }

    [Fact]
    public void AutoDraw_RecordsThePhasesAndTheVertexOffsets()
    {
        _executor.DrawAuto(3, Banks(), Auto(3, instances: 1, firstVertex: 5));

        AssertOrder(
            "debug DrawIndexAuto 3 3 0 5 1 0",
            "debug DrawIndexAuto 3 200 3 0 1 0",
            "debug DrawIndexAuto 3 300 3 0 1 0",
            "debug DrawIndexAuto 3 400 3 0 1 0",
            "begin_rendering",
            "debug DrawIndexAuto 3 500 3 0 1 0",
            "draw 3 1 5 0",
            "debug DrawIndexAuto 3 600 3 0 1 0",
            "debug DrawIndexAuto 3 700 3 0 1 0",
            "reset_bindings");
        Assert.Contains("bind_vertex ", _host.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("bind_index", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0ul, false)]
    [InlineData(0x3000ul, false)]
    [InlineData(0ul, true)]
    [InlineData(0ul, true, 7u)]
    [InlineData(0ul, true, 8u)]
    [InlineData(0ul, false, 65535u, 0x2000u)]
    [InlineData(0ul, true, 65535u, 0x2000u)]
    public void MeshAutoDraw_RecordsMeshGroupsAndDrawParameters(ulong geometryAddress, bool triangleStrip,
        uint totalLimit = 65535, uint stages = 0x20)
    {
        var meshStage = Stage(Program(ShaderStageKind.Mesh));
        _pipelines.MeshShadersSupported = true;
        _pipelines.Graphics = new GraphicsPrograms
        {
            Vertex = new ShaderProgram(0x11),
            Pixel = new ShaderProgram(0x22),
            VertexInput = new VertexInputInfo { Stage = meshStage },
            MeshInput = new MeshDrawConfiguration
            {
                Geometry = new GuestGeometryConfiguration
                {
                    InputPrimitiveCountPerWorkgroup = 8,
                    InputTriangleStrip = triangleStrip,
                },
                Execution = new MeshExecutionLimits
                {
                    MaxGroupCountX = 65535,
                    MaxGroupCountY = 65535,
                    MaxGroupTotalCount = totalLimit,
                },
            },
            PixelInput = new PixelInputInfo { Stage = Stage(Program(ShaderStageKind.Pixel)) },
        };
        MeshDrawParameters? parameters = null;
        _host.PreparationFailure = stage =>
        {
            if (stage.Program?.Stage == ShaderStageKind.Mesh) parameters = stage.MeshDraw;
            return null;
        };
        var banks = Banks();
        banks.UserConfig.PrimitiveType = triangleStrip ? 6u : 4u;
        banks.Context.ShaderStages = stages;
        banks.Shader.Vertex.GeometryAddress = geometryAddress;

        if (totalLimit < (triangleStrip ? 8u : 4u))
        {
            var error = Assert.Throws<RenderExecutorFatalException>(() =>
                _executor.DrawAuto(1, banks, Auto(30, instances: 2, firstVertex: 5)));
            Assert.Contains("total=8/7", error.Message);
            Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw_mesh", StringComparison.Ordinal));
            return;
        }
        _executor.DrawAuto(1, banks, Auto(30, instances: 2, firstVertex: 5));

        Assert.Contains("get_mesh_programs pixelActive=True", _pipelines.Calls);
        Assert.Contains(triangleStrip ? "draw_mesh 4 2 1" : "draw_mesh 2 2 1", _host.Calls);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("bind_vertex", StringComparison.Ordinal));
        Assert.Equal(new MeshDrawParameters(30, 5, 0, 0, 0), parameters);
        Assert.Equal(triangleStrip ? 6u : 4u, _pipelines.MeshPrimitiveType);
    }

    [Theory]
    [InlineData(0x20u, false)]
    [InlineData(0x2030u, false)]
    [InlineData(0x2030u, true)]
    [InlineData(0x2000u, false)]
    [InlineData(0x2000u, true)]
    [InlineData(0x00402000u, false)]
    [InlineData(0x2030u, false, true)]
    [InlineData(0x2030u, true, true)]
    public void IndexedMeshDrawUploadsIndicesWithinPreparation(uint stages, bool strip, bool indirect = false)
    {
        _pipelines.MeshShadersSupported = true;
        _pipelines.Graphics = new GraphicsPrograms
        {
            Vertex = new ShaderProgram(0x11), Pixel = new ShaderProgram(0x22),
            VertexInput = new VertexInputInfo { Stage = Stage(Program(ShaderStageKind.Mesh)) },
            PixelInput = new PixelInputInfo { Stage = Stage(Program(ShaderStageKind.Pixel)) },
            MeshInput = new MeshDrawConfiguration
            {
                Geometry = new GuestGeometryConfiguration { InputPrimitiveCountPerWorkgroup = 1 },
                Execution = new MeshExecutionLimits
                {
                    MaxGroupCountX = 65535, MaxGroupCountY = 65535, MaxGroupTotalCount = 65535,
                },
            },
        };
        MeshDrawParameters? parameters = null;
        _host.PreparationFailure = stage =>
        {
            if (stage.Program?.Stage == ShaderStageKind.Mesh) parameters = stage.MeshDraw;
            return null;
        };
        var banks = Banks(withDepth: indirect);
        banks.Context.ShaderStages = stages;
        banks.UserConfig.PrimitiveType = strip ? 6u : 4u;
        _host.WriteGuest(IndexBase + (indirect ? 8ul : 0), [7, 0, 2, 0, 7, 0, 9, 0, 4, 0, 2, 0]);
        var arguments = Indexed(6, instances: 2, baseVertex: -1);
        if (indirect)
        {
            WriteIndirectArguments(6, 2, 4, unchecked((uint)-1), 5);
            arguments = Indexed(40, source: DrawOffsetSource.IndirectArguments) with
            {
                IndirectArgumentsAddress = IndirectArguments,
            };
        }
        _executor.DrawIndexed(1, banks, arguments);
        Assert.Equal(strip ? new uint[] { 6, 1, 6, 1, 8, 6, 6, 8, 3, 8, 1, 3 }
            : [6, 1, 6, 8, 3, 1], _host.MeshIndices);
        Assert.Equal(new MeshDrawParameters(strip ? 12u : 6u, 0, indirect ? 5u : 0u, 4, 0x8000), parameters);
        AssertOrder("preparation_begin", "upload_mesh_indices", "prepare_bindings",
            strip ? "draw_mesh 4 2 1" : "draw_mesh 2 2 1", "preparation_end");
        Assert.Equal(4u, _pipelines.MeshPrimitiveType);
        Assert.Equal(indirect, _pipelines.MeshDepthBound);
        Assert.Contains("get_mesh_programs pixelActive=True", _pipelines.Calls);
        Assert.Equal(strip ? 6u : 4u, banks.UserConfig.PrimitiveType);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("bind_index", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw_indexed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrimitiveGenerationWithHullStageRemainsRejectedWithMeshSupport(bool indexed)
    {
        _pipelines.MeshShadersSupported = true;
        var banks = Banks();
        banks.Context.ShaderStages = 0x2004;
        if (indexed) _executor.DrawIndexed(1, banks, Indexed(6));
        else _executor.DrawAuto(1, banks, Auto(6));
        Assert.Empty(_pipelines.Calls);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw", StringComparison.Ordinal));
        Assert.Null(_host.MeshIndices);
    }

    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(6u, 0u)]
    [InlineData(0u, 1u)]
    public void ZeroCountOrInstances_ReturnBeforeAnyResolution(uint count, uint instances)
    {
        _executor.DrawIndexed(1, Banks(), Indexed(count, instances));
        _executor.DrawAuto(1, Banks(), Auto(count, instances));

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("find_image", StringComparison.Ordinal));
        Assert.DoesNotContain("reset_bindings", _host.Calls);
        Assert.Empty(_pipelines.Calls);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    public void ColorMetadataModes_ConsumeTheDrawAndResetBindings(byte mode)
    {
        var banks = Banks();
        banks.Context.ColorControl.Mode = mode;
        _executor.DrawIndexed(1, banks, Indexed(3));
        _executor.DrawAuto(1, banks, Auto(3));

        Assert.Equal(2, _host.Calls.Count(c => c == "reset_bindings"));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("find_image", StringComparison.Ordinal));
        Assert.True(RenderExecutor.ConsumesColorMetadataOperation(banks.Context));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void OtherColorModes_AreNormalDraws(byte mode)
    {
        var context = new ContextRegisters { ColorControl = new ColorControlRegisters { Mode = mode } };
        Assert.False(RenderExecutor.ConsumesColorMetadataOperation(context));
    }

    [Fact]
    public void MissingVertexShader_SkipsWithoutResettingBindings()
    {
        var banks = Banks();
        banks.Shader.Vertex.ExportAddress = 0;
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.DoesNotContain("reset_bindings", _host.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("find_image", StringComparison.Ordinal));
    }

    public static TheoryData<string, Action<RegisterBanks>> UnsupportedGeometryStages => new()
    {
        { "stage mask", banks => banks.Context.ShaderStages = 0x1 },
        { "geometry shader outside the primitive path", banks => banks.Shader.Vertex.GeometryAddress = 0x3000 },
        { "subgroup control", banks => banks.Context.ShaderInterface.PrimitiveShaderSubgroupControl = 2 },
        { "max vertices out", banks => banks.Context.ShaderInterface.GeometryMaxVerticesOut = 4 },
        { "output primitive", banks => banks.Context.ShaderInterface.GeometryOutputPrimitiveType = 5 },
        { "max output per subgroup", banks => banks.Context.ShaderInterface.MaxOutputPerSubgroup = 0x41 },
    };

    [Theory]
    [MemberData(nameof(UnsupportedGeometryStages))]
    public void UnsupportedGeometryStage_SkipsTheDraw(string reason, Action<RegisterBanks> mutate)
    {
        var banks = Banks();
        mutate(banks);
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.True(!_host.Calls.Exists(c => c.StartsWith("find_image", StringComparison.Ordinal)), reason);
        Assert.Empty(_pipelines.Calls);
    }

    [Theory]
    [InlineData(0x02002000u, 0u)]
    [InlineData(0x02002000u, 1u)]
    [InlineData(0x00002000u, 0u)]
    [InlineData(0x00002000u, 1u)]
    public void PrimitiveShaderVertexPath_WithDefaultGeometryStateDraws(uint stageMask, uint subgroupControl)
    {
        var banks = Banks();
        banks.Context.ShaderStages = stageMask;
        banks.Shader.Vertex.GeometryAddress = 0x3000;
        banks.Context.ShaderInterface.PrimitiveShaderSubgroupControl = subgroupControl;
        banks.Context.ShaderInterface.MaxOutputPerSubgroup = 0x40;
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Contains(_host.Calls, c => c.StartsWith("draw_indexed", StringComparison.Ordinal));
    }

    [Fact]
    public void PrimitiveShaderVertexPath_WithFixedNggStateAndNoGeometryShaderDraws()
    {
        var banks = Banks();
        banks.Context.ShaderStages = 0x00002030;
        banks.Context.ShaderInterface.PrimitiveShaderSubgroupControl = 1;
        banks.Context.ShaderInterface.MaxOutputPerSubgroup = 0xC0;
        banks.Context.ShaderInterface.GeometryMaxVerticesOut = 3;
        banks.Context.ShaderInterface.GeometryOutputPrimitiveType = 2;

        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Contains(_host.Calls, c => c.StartsWith("draw_indexed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x2000u, false)]
    [InlineData(0x2000u, true)]
    [InlineData(0x2030u, false)]
    [InlineData(0x2030u, true)]
    [InlineData(0x00402000u, false)]
    [InlineData(0x00402000u, true)]
    [InlineData(0x00C02030u, false)]
    [InlineData(0x00C02030u, true)]
    public void PrimitiveShaderWithoutMeshSupportUsesVertexPrograms(uint stages, bool indexed)
    {
        _pipelines.MeshShadersSupported = false;
        var banks = Banks();
        banks.Context.ShaderStages = stages;
        banks.UserConfig.PrimitiveType = (uint)GuestPrimitiveType.TriangleStrip;

        if (indexed) _executor.DrawIndexed(1, banks, Indexed(6));
        else _executor.DrawAuto(1, banks, Auto(6));

        Assert.Contains("get_graphics_programs pixelActive=True", _pipelines.Calls);
        Assert.DoesNotContain(_pipelines.Calls, call => call.StartsWith("get_mesh_programs", StringComparison.Ordinal));
        Assert.Null(_host.MeshIndices);
        Assert.NotEmpty(_pipelines.PipelineRequests);
        Assert.All(_pipelines.PipelineRequests, request => Assert.Equal(PrimitiveTopology.TriangleStrip, request.Topology));
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("draw_mesh", StringComparison.Ordinal));
        Assert.Contains(_host.Calls, call => call.StartsWith(indexed ? "draw_indexed " : "draw ", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1u, PrimitiveTopology.PointList)]
    [InlineData(2u, PrimitiveTopology.LineList)]
    [InlineData(3u, PrimitiveTopology.LineStrip)]
    [InlineData(4u, PrimitiveTopology.TriangleList)]
    [InlineData(5u, PrimitiveTopology.TriangleFan)]
    [InlineData(6u, PrimitiveTopology.TriangleStrip)]
    [InlineData(7u, PrimitiveTopology.PatchList)]
    [InlineData(19u, PrimitiveTopology.TriangleFan)]
    public void ResolveTopology_MapsEveryPrimitiveType(uint primitiveType, PrimitiveTopology expected)
    {
        var userConfig = new UserConfigRegisters { PrimitiveType = primitiveType };
        Assert.True(_executor.ResolveTopology(userConfig, autoDraw: false, out var topology));
        Assert.Equal(expected, topology);
    }

    [Fact]
    public void ResolveTopology_HandlesNoneAndTheLegacyRectangleList()
    {
        Assert.False(_executor.ResolveTopology(new UserConfigRegisters { PrimitiveType = 0 }, autoDraw: false, out _));
        Assert.True(_executor.ResolveTopology(new UserConfigRegisters { PrimitiveType = 17 }, autoDraw: true, out var topology));
        Assert.Equal(PrimitiveTopology.TriangleStrip, topology);
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.ResolveTopology(new UserConfigRegisters { PrimitiveType = 17 }, autoDraw: false, out _));
        Assert.Contains("primitiveType=17", fatal.Message);
        fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.ResolveTopology(new UserConfigRegisters { PrimitiveType = 9 }, autoDraw: true, out _));
        Assert.Contains("primitiveType=9", fatal.Message);
    }

    [Fact]
    public void PrimitiveTypeNone_SkipsTheDrawAfterTheValidation()
    {
        _executor.DrawIndexed(1, Banks(primitiveType: 0), Indexed(3));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("find_image", StringComparison.Ordinal));

        _executor.DrawAuto(1, Banks(primitiveType: 0), Auto(3));
        Assert.Contains(_host.Calls, c => c.StartsWith("find_image", StringComparison.Ordinal));
        Assert.Contains("reset_bindings", _host.Calls);
        Assert.Empty(_pipelines.Calls);
    }

    [Fact]
    public void PrimitiveRestart_FollowsTheControlBitsThePrimitiveTypeAndTheIndexMask()
    {
        var banks = Banks(primitiveType: PrimitiveTriangleStrip);
        banks.UserConfig.PrimitiveResetControl = 0;
        Assert.False(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 0));

        banks.UserConfig.PrimitiveResetControl = 1;
        banks.Context.PrimitiveResetIndex = 0xFFFF;
        Assert.True(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 0));
        Assert.False(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleList, 0));

        banks.UserConfig.PrimitiveType = PrimitiveTriangleList;
        Assert.False(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 0));

        banks.UserConfig.PrimitiveType = PrimitiveTriangleStrip;
        banks.Context.PrimitiveResetIndex = 0xFFFF_FFFF;
        banks.UserConfig.PrimitiveResetControl = 3;
        Assert.False(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 0));
        Assert.True(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 1));

        banks.UserConfig.PrimitiveResetControl = 1;
        banks.Context.PrimitiveResetIndex = 0xFF;
        Assert.True(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 2));
        Assert.True(_executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 0));

        banks.UserConfig.PrimitiveResetControl = 4;
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 0));
        Assert.Contains("control=0x00000004", fatal.Message);

        banks.UserConfig.PrimitiveResetControl = 1;
        fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.ResolvePrimitiveRestart(banks, PrimitiveTopology.TriangleStrip, 3));
        Assert.Contains("indexTypeAndSize=3", fatal.Message);
    }

    [Theory]
    [InlineData(0u, 6u, "C", "Uint16")]
    [InlineData(1u, 6u, "18", "Uint32")]
    public void IndexTypes_ObtainTheGuestRangeWithTheirStride(uint indexType, uint count, string size, string type)
    {
        _executor.DrawIndexed(1, Banks(), Indexed(count, indexType: indexType));

        Assert.Contains($"obtain 100500000 {size} written=False -> 100:0", _host.Calls);
        Assert.Contains(_host.Calls, c => c.StartsWith($"bind_index 100:0 {type}", StringComparison.Ordinal));
    }

    [Fact]
    public void Index8_ExpandsToTransient16BitIndicesAndMapsTheRestartValue()
    {
        _host.WriteGuest(IndexBase, [1, 2, 0xFF, 3]);
        var banks = Banks(primitiveType: PrimitiveTriangleStrip);
        banks.UserConfig.PrimitiveResetControl = 1;
        banks.Context.PrimitiveResetIndex = 0xFF;
        _executor.DrawIndexed(1, banks, Indexed(4, indexType: 2));

        Assert.Equal(new byte[] { 1, 0, 2, 0, 0xFF, 0xFF, 3, 0 }, _host.LastTransient);
        Assert.Contains("upload_transient 8 align=16 -> 50:0", _host.Calls);
        Assert.Contains("bind_index 50:0 Uint16", _host.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("obtain 100500000", StringComparison.Ordinal));
        Assert.True(_pipelines.PipelineRequests[0].Restart);
    }

    [Fact]
    public void Index8_WithoutRestartKeepsTheRestartValueAsAnIndex()
    {
        _host.WriteGuest(IndexBase, [0xFF]);
        _executor.DrawIndexed(1, Banks(), Indexed(1, indexType: 2));

        Assert.Equal(new byte[] { 0xFF, 0 }, _host.LastTransient);
    }

    [Theory]
    [InlineData(0u, 7u, 0xFFFFu)]
    [InlineData(1u, 0xFFFFu, 0x10000u)]
    [InlineData(2u, 7u, 0xFFu)]
    public void CustomRestart_ConvertsOnlyTheMarkerAndPreservesDrawArguments(uint indexType, uint restartIndex, uint ordinaryIndex)
    {
        var sourceStride = indexType == 0 ? 2 : indexType == 1 ? 4 : 1;
        uint[] values = [1, restartIndex, ordinaryIndex, restartIndex, 2];
        var source = new byte[values.Length * sourceStride];
        for (var index = 0; index < values.Length; index++)
        {
            if (sourceStride == 4)
                BinaryPrimitives.WriteUInt32LittleEndian(source.AsSpan(index * sourceStride), values[index]);
            else if (sourceStride == 2)
                BinaryPrimitives.WriteUInt16LittleEndian(source.AsSpan(index * sourceStride), (ushort)values[index]);
            else
                source[index] = (byte)values[index];
        }

        _host.WriteGuest(IndexBase, source);
        var banks = Banks(primitiveType: PrimitiveTriangleStrip);
        banks.UserConfig.PrimitiveResetControl = 1;
        banks.Context.PrimitiveResetIndex = restartIndex;
        _executor.DrawIndexed(1, banks, Indexed(5, instances: 3, indexType: indexType, baseVertex: 4));

        var resultStride = indexType == 2 ? 2 : 4;
        var expected = new byte[values.Length * resultStride];
        for (var index = 0; index < values.Length; index++)
        {
            var value = values[index] == restartIndex ? uint.MaxValue : values[index];
            if (resultStride == 4)
                BinaryPrimitives.WriteUInt32LittleEndian(expected.AsSpan(index * resultStride), value);
            else
                BinaryPrimitives.WriteUInt16LittleEndian(expected.AsSpan(index * resultStride), (ushort)value);
        }

        Assert.Equal(expected, _host.LastTransient);
        Assert.Contains($"bind_index 50:0 {(indexType == 2 ? "Uint16" : "Uint32")}", _host.Calls);
        Assert.Contains("draw_indexed 5 3 0 4 0", _host.Calls);
        Assert.True(_pipelines.PipelineRequests[0].Restart);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("obtain 100500000", StringComparison.Ordinal));
    }

    [Fact]
    public void CustomRestart_RejectsA32BitMarkerCollisionBeforeRecording()
    {
        _host.WriteGuest(IndexBase, [0xFF, 0xFF, 0xFF, 0xFF]);
        var banks = Banks(primitiveType: PrimitiveTriangleStrip);
        banks.UserConfig.PrimitiveResetControl = 1;
        banks.Context.PrimitiveResetIndex = 0xFFFF;
        var failure = Assert.Throws<RenderExecutorFatalException>(() =>
            _executor.DrawIndexed(1, banks, Indexed(1, indexType: 1)));
        Assert.Contains("conflicts", failure.Message);
        Assert.Empty(_pipelines.PipelineRequests);
    }

    [Fact]
    public void CustomRestart_RejectsUnreadableIndices()
    {
        var banks = Banks(primitiveType: PrimitiveTriangleStrip);
        banks.UserConfig.PrimitiveResetControl = 1;
        banks.Context.PrimitiveResetIndex = 0xFFFF;
        var failure = Assert.Throws<RenderExecutorFatalException>(() =>
            _executor.DrawIndexed(1, banks, Indexed(1, indexType: 1, indexAddress: 0)));
        Assert.Contains("unreadable", failure.Message);
    }

    [Fact]
    public void Index8_WithoutAnAddressIsFatal()
    {
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, Banks(), Indexed(4, indexType: 2, indexAddress: 0)));
        Assert.Contains("count=4", fatal.Message);
    }

    [Fact]
    public void UnknownIndexType_IsFatal()
    {
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, Banks(), Indexed(3, indexType: 7)));
        Assert.Contains("indexTypeAndSize=7", fatal.Message);
    }

    [Fact]
    public void VertexOffset_UsesTheIndexOffsetRegisterThenTheEmbeddedFetchScalar()
    {
        var plain = Programs().VertexInput;
        Assert.Equal(5, RenderExecutor.ResolveVertexOffset(5, plain));
        Assert.Equal(0, RenderExecutor.ResolveVertexOffset(0, plain));
        Assert.Equal(0u, RenderExecutor.ResolveInstanceOffset(plain));

        var program = Program(ShaderStageKind.Vertex, userDataBase: 4, vertexOffsetScalar: 6, instanceOffsetScalar: 7);
        var embedded = Programs(fetchEmbedded: true, vertexStage: Stage(program, userData: [10, 11, 12, 13])).VertexInput;
        Assert.Equal(9, RenderExecutor.ResolveVertexOffset(9, embedded));
        Assert.Equal(12, RenderExecutor.ResolveVertexOffset(0, embedded));
        Assert.Equal(13u, RenderExecutor.ResolveInstanceOffset(embedded));

        var outOfRange = Programs(fetchEmbedded: true, vertexStage: Stage(program, userData: [10, 11])).VertexInput;
        Assert.Equal(0, RenderExecutor.ResolveVertexOffset(0, outOfRange));
        Assert.Equal(0u, RenderExecutor.ResolveInstanceOffset(outOfRange));

        var belowBase = Programs(fetchEmbedded: true, vertexStage: Stage(Program(ShaderStageKind.Vertex, userDataBase: 8, vertexOffsetScalar: 6), userData: [1, 2, 3])).VertexInput;
        Assert.Equal(0, RenderExecutor.ResolveVertexOffset(0, belowBase));
    }

    [Fact]
    public void PacketOffsets_AddTheBaseVertexToTheRegisterAndIndirectOnesUseThePacketAlone()
    {
        var program = Program(ShaderStageKind.Vertex, userDataBase: 0, vertexOffsetScalar: 1, instanceOffsetScalar: 2);
        _pipelines.Graphics = Programs(fetchEmbedded: true, vertexStage: Stage(program, userData: [0, 20, 30]));
        var banks = Banks();
        banks.UserConfig.IndexOffset = 0;
        _executor.DrawIndexed(1, banks, Indexed(3, baseVertex: 4));
        Assert.Contains("draw_indexed 3 1 0 24 30", _host.Calls);

        banks.UserConfig.IndexOffset = 7;
        _executor.DrawAuto(1, banks, Auto(3, firstVertex: 2));
        Assert.Contains("draw 3 1 9 30", _host.Calls);

        _executor.DrawIndexed(1, banks, Indexed(3, baseVertex: 4, firstInstance: 9, source: DrawOffsetSource.IndirectArguments));
        Assert.Contains("draw_indexed 3 1 0 4 9", _host.Calls);

        _executor.DrawAuto(1, banks, Auto(3, firstVertex: 2, firstInstance: 5, source: DrawOffsetSource.IndirectArguments));
        Assert.Contains("draw 3 1 2 5", _host.Calls);
    }

    [Fact]
    public void PacketDrawWithFirstInstance_IsFatal()
    {
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, Banks(), Indexed(3, firstInstance: 2)));
        Assert.Contains("firstInstance=2", fatal.Message);
        fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawAuto(1, Banks(), Auto(3, firstInstance: 2)));
        Assert.Contains("firstInstance=2", fatal.Message);
    }

    [Fact]
    public void NoRecordingCommandBuffer_IsFatal()
    {
        _host.Recording = false;
        Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, Banks(), Indexed(3)));
        Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawAuto(1, Banks(), Auto(3)));
        Assert.Throws<RenderExecutorFatalException>(() => _executor.Dispatch(1, Banks(), 1, 1, 1, 0x41));
    }

    [Fact]
    public void VertexRanges_MergeOverlappingAndTouchingRangesAndOffsetEverySlot()
    {
        _pipelines.Graphics = Programs(vertexBuffers:
        [
            new VertexInputBuffer(VertexBase + 0x100, 16, 8),
            new VertexInputBuffer(VertexBase, 0, 0x100),
            new VertexInputBuffer(VertexBase + 0x1000, 32, 2),
            new VertexInputBuffer(VertexBase + 0x40, 0, 0),
            new VertexInputBuffer(VertexBase + 0x180, 8, 4),
        ]);
        _executor.DrawAuto(1, Banks(), Auto(3));

        var obtains = _host.Calls.Where(c => c.StartsWith("obtain", StringComparison.Ordinal)).ToList();
        Assert.Equal(["obtain 100400000 1A0 written=False -> 100:0", "obtain 100401000 40 written=False -> 101:0"], obtains);
        Assert.Contains("bind_vertex 100:100,100:0,101:0,1:0,100:180", _host.Calls);
    }

    [Theory]
    [InlineData(0u, 1u, 2u, 74u, 0u, 12ul)]
    [InlineData(0u, 1u, 2u, 74u, 4u, 16ul)]
    [InlineData(0u, 1u, 2u, 29u, 0u, 4ul)]
    [InlineData(0u, 1u, 2u, 50u, 0u, 4ul)]
    [InlineData(0u, 0u, 2u, 74u, 0u, 0ul)]
    [InlineData(0u, 1u, 0u, 74u, 0u, 1ul)]
    [InlineData(0u, 1u, 1u, 74u, 0u, 1ul)]
    [InlineData(0u, 1u, 3u, 74u, 0u, 1ul)]
    [InlineData(16u, 2u, 2u, 74u, 0u, 32ul)]
    public void ConstantVertexRange_CoversTheFormatOnlyWhenBoundsPermitIt(
        uint stride, uint records, uint bounds, uint format, uint offset, ulong expected)
    {
        var defaults = Programs();
        var descriptor = new BufferDescriptorWords((uint)(VertexBase + offset),
            (uint)(VertexBase >> 32) | (stride << 16), records, (bounds << 28) | (format << 12) | 0x3AC);
        _pipelines.Graphics = new GraphicsPrograms
        {
            Vertex = defaults.Vertex, Pixel = defaults.Pixel, PixelInput = defaults.PixelInput,
            VertexInput = new VertexInputInfo
            {
                Stage = defaults.VertexInput.Stage,
                Buffers = [new VertexInputBuffer(VertexBase, stride, records)],
                Attributes = [new VertexAttributeResource(descriptor, 0, 3, 11, 0, 0, offset)],
            },
        };
        _executor.DrawAuto(1, Banks(), Auto(3));
        var obtains = _host.Calls.Where(call => call.StartsWith("obtain", StringComparison.Ordinal)).ToArray();
        if (expected == 0) Assert.Empty(obtains);
        else Assert.Equal($"obtain {VertexBase:X} {expected:X} written=False -> 100:0", Assert.Single(obtains));
    }

    [Fact]
    public void VertexRanges_ClampToTheMappedSizeAndFailForAnAddressOutsideTheAcquiredRange()
    {
        _host.ClampOverride = (address, size) => Math.Min(size, 0x20);
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 4), new VertexInputBuffer(VertexBase + 0x30, 16, 1)]);
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawAuto(1, Banks(), Auto(3)));

        Assert.Contains("obtain 100400000 20 written=False -> 100:0", _host.Calls);
        Assert.Contains("address=0x0000000100400030", fatal.Message);
    }

    [Fact]
    public void VertexRange_WithoutAnAddressIsFatal()
    {
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(0, 16, 4)]);
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawAuto(1, Banks(), Auto(3)));
        Assert.Contains("address=0x0000000000000000 size=0x0000000000000040", fatal.Message);
    }

    [Fact]
    public void LegacyRectangleList_DrawsFourVerticesAndRejectsOtherShapes()
    {
        _executor.DrawAuto(1, Banks(primitiveType: 17), Auto(3, instances: 2));
        Assert.Contains("draw 4 2 0 0", _host.Calls);

        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawAuto(1, Banks(primitiveType: 17), Auto(6)));
        Assert.Contains("count=6 buffers=0", fatal.Message);

        fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, Banks(primitiveType: 17), Indexed(3)));
        Assert.Contains("primitiveType=17", fatal.Message);
    }

    [Theory]
    [InlineData(3u)]
    [InlineData(6u)]
    [InlineData(63u)]
    public void LegacyRectangleList_NativeFillPreservesGuestDrawArguments(uint count)
    {
        _host.SupportsNativeRectangles = true;
        Assert.True(_executor.ResolveTopology(new UserConfigRegisters { PrimitiveType = 17 }, autoDraw: true, out var topology));
        Assert.Equal(PrimitiveTopology.PatchList, topology);
        _executor.DrawAuto(1, Banks(primitiveType: 17), Auto(count, instances: 2, firstVertex: 9, firstInstance: 4, source: DrawOffsetSource.IndirectArguments));
        Assert.Contains($"draw {count} 2 9 4", _host.Calls);
    }

    [Fact]
    public void LegacyRectangleList_NativeFillRejectsIncompleteGroups()
    {
        _host.SupportsNativeRectangles = true;
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawAuto(1, Banks(primitiveType: 17), Auto(4)));
        Assert.Contains("count=4", fatal.Message);
    }

    [Fact]
    public void LegacyQuadList_EmitsOneDrawPerQuad()
    {
        _executor.DrawIndexed(1, Banks(primitiveType: 19), Indexed(8, baseVertex: 1));
        Assert.Contains("draw_indexed 4 1 0 1 0", _host.Calls);
        Assert.Contains("draw_indexed 4 1 4 1 0", _host.Calls);

        _executor.DrawAuto(1, Banks(primitiveType: 19), Auto(8, firstVertex: 2));
        Assert.Contains("draw 4 1 2 0", _host.Calls);
        Assert.Contains("draw 4 1 6 0", _host.Calls);

        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawAuto(1, Banks(primitiveType: 19), Auto(6)));
        Assert.Contains("count=6", fatal.Message);
    }

    [Fact]
    public void RectangleListWithoutParameterExportsAndPixelInputs_IsSkipped()
    {
        _pipelines.Graphics = Programs(vertexStage: Stage(Program(ShaderStageKind.Vertex, parameterExportMask: 0)), pixelInputs: 2);
        _executor.DrawAuto(1, Banks(primitiveType: 7), Auto(3));

        Assert.Contains("reset_bindings", _host.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("draw", StringComparison.Ordinal));
        Assert.Empty(_pipelines.PipelineRequests);

        _pipelines.Graphics = Programs(vertexStage: Stage(Program(ShaderStageKind.Vertex, parameterExportMask: 0)), pixelInputs: 0);
        _executor.DrawAuto(1, Banks(primitiveType: 7), Auto(3));
        Assert.Contains("draw 3 1 0 0", _host.Calls);
        Assert.Equal(PrimitiveTopology.PatchList, _pipelines.PipelineRequests[0].Topology);
    }

    [Fact]
    public void DrawWithoutTargetsOrAnActivePixelShader_IsSkipped()
    {
        var banks = Banks(withPixel: false);
        banks.Context.RenderTargetMask = 0;
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Contains("reset_bindings", _host.Calls);
        // The vertex stage is resolved to see whether it stores to memory, but nothing is drawn.
        Assert.Empty(_pipelines.PipelineRequests);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("begin_rendering", StringComparison.Ordinal));
    }

    [Fact]
    public void PixelShaderWithDepthSideEffects_KeepsADrawWithoutColorOutput()
    {
        var banks = Banks();
        banks.Context.RenderTargetMask = 0;
        banks.Context.ShaderInterface.DepthShaderControl = new DepthShaderControlRegisters { KillEnable = true };
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Contains("begin_rendering 16384x8192x1 colors=0 samples=1", _host.Calls);
        Assert.True(_pipelines.PipelineRequests[0].PixelActive);
    }

    [Fact]
    public void InactivePixelShader_PreparesOnlyTheVertexStage()
    {
        var banks = Banks();
        banks.Context.ShaderInterface.ColorShaderMask = 0;
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Contains("commit Graphics A1 [1]", _host.Calls);
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("prepare_bindings Pixel", StringComparison.Ordinal));
        Assert.False(_pipelines.PipelineRequests[0].PixelActive);
        Assert.Contains("get_graphics_programs pixelActive=False", _pipelines.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DepthOnlyDraw_WithRetainedExportFormat_DoesNotActivatePixelShader(bool indexed)
    {
        var banks = Banks(withDepth: true);
        banks.Context.RenderTargetMask = 0;
        banks.Context.ShaderInterface.DepthExportFormat = 4;
        banks.Context.ShaderInterface.DepthShaderControl = default;

        if (indexed)
        {
            _executor.DrawIndexed(1, banks, Indexed(3));
        }
        else
        {
            _executor.DrawAuto(1, banks, Auto(3));
        }

        Assert.Single(_host.BegunRenderings);
        Assert.False(Assert.Single(_pipelines.PipelineRequests).PixelActive);
        Assert.DoesNotContain(_host.Calls, call => call.StartsWith("prepare_bindings Pixel", StringComparison.Ordinal));
    }

    [Fact]
    public void MultisampleResolveMode_ResolvesSlotZeroIntoSlotOneInsteadOfDrawing()
    {
        var banks = Banks();
        banks.Context.ColorControl.Mode = 3;
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 64, 64);
        _executor.DrawIndexed(1, banks, Indexed(3));

        AssertOrder("find_image 100000000 ColorTarget exact=True -> 1", "bind_target 1", "find_image 100100000 ColorTarget exact=True -> 2", "bind_target 2", "mark_written 2", "resolve 1:0:0 -> 2:0:0", "reset_bindings");
        Assert.Empty(_pipelines.Calls);
    }

    [Fact]
    public void MultisampleResolveMode_WithTheSameTargetConsumesTheDraw()
    {
        var banks = Banks();
        banks.Context.ColorControl.Mode = 3;
        banks.Context.ColorTargets[1] = RegisterWords.Color(ColorBase, 64, 64);
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("resolve", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("draw", StringComparison.Ordinal));
        Assert.Contains("reset_bindings", _host.Calls);
    }

    [Fact]
    public void MultisampleResolveMode_WithoutASecondTargetDrawsNormally()
    {
        var banks = Banks();
        banks.Context.ColorControl.Mode = 3;
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Contains(_host.Calls, c => c.StartsWith("draw_indexed", StringComparison.Ordinal));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("resolve", StringComparison.Ordinal));
    }

    [Fact]
    public void SecondColorSlot_NeedsItsMaskNibbleAndAddress()
    {
        var banks = Banks();
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 32, 32);
        _executor.DrawIndexed(1, banks, Indexed(3));
        Assert.Contains("begin_rendering 64x64x1 colors=1 samples=1", _host.Calls);

        banks.Context.RenderTargetMask = 0xFF;
        _executor.DrawIndexed(1, banks, Indexed(3));
        Assert.Contains("begin_rendering 32x32x1 colors=2 samples=1", _host.Calls);
        Assert.Contains("create_graphics_pipeline colors=2 depth=False topology=TriangleList restart=False", _pipelines.Calls);
    }

    [Fact]
    public void ShaderBufferWrites_EndRenderingAndBarrierAfterTheDraw()
    {
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        var descriptor = BufferDescriptor(VertexBase, 4, 16);
        _pipelines.Graphics = Programs(
            vertexStage: Stage(Program(ShaderStageKind.Vertex, buffers: [written]), buffers: [descriptor]),
            pixelStage: Stage(Program(ShaderStageKind.Pixel, buffers: [written]), buffers: [descriptor]));
        _executor.DrawIndexed(1, Banks(), Indexed(3));

        AssertOrder("draw_indexed 3 1 0 0 0", "end_rendering", $"write_barrier {PipelineStageFlags.VertexShaderBit | PipelineStageFlags.FragmentShaderBit}", "reset_bindings");
    }

    [Fact]
    public void ShaderBufferWrites_WithoutAnAddressOrFootprintNeedNoBarrier()
    {
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        _pipelines.Graphics = Programs(vertexStage: Stage(Program(ShaderStageKind.Vertex, buffers: [written]), buffers: [BufferDescriptor(0, 4, 16)]));
        _executor.DrawIndexed(1, Banks(), Indexed(3));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("write_barrier", StringComparison.Ordinal));

        _pipelines.Graphics = Programs(vertexStage: Stage(Program(ShaderStageKind.Vertex, buffers: [written]), buffers: [BufferDescriptor(VertexBase, 4, 0)]));
        _executor.DrawIndexed(1, Banks(), Indexed(3));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("write_barrier", StringComparison.Ordinal));
    }

    [Fact]
    public void DeviceAddresses_ArePreparedAfterBindingPreparationAndBeforeResources()
    {
        _pipelines.Graphics = Programs(pixelStage: Stage(Program(ShaderStageKind.Pixel, usesDeviceAddresses: true)));
        _executor.DrawIndexed(1, Banks(), Indexed(3));

        AssertOrder("prepare_bindings Vertex", "prepare_bindings Pixel", "prepare_device_addresses", "bind_resources 1", "bind_resources 2", "obtain");
    }
}
