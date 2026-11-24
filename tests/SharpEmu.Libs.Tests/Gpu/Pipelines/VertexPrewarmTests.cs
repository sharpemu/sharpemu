// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode.Shaders;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Vertex and mesh programs recorded in the prewarm side file with the pre-rasterization states their libraries were
// compiled with, and translated again from the record.
public sealed class VertexPrewarmTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong DataBase = PipelineTestGuest.MemoryBase + 0x4_0000;
    private const uint Format32x4Float = 77;

    // The attribute table load and the formatted fetch of attribute 0 that the embedded fetch replaces, then s_endpgm.
    private static readonly uint[] EmbeddedFetchProgram = [0xF4080303, 0xFA000000, 0x02020B08, 0xE00C2000, 0x80030401, 0xBF810000];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    internal static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static ShaderCodeCapture Code(ulong hash, ulong continuationOffset = 0) => new()
    {
        Hash = hash, CodeSize = 12, Address = CodeAddress, Generation = Generation.Gen5,
        Fused = continuationOffset == 0
            ? null
            : new FusedCodeParts(HeaderAddress, CodeAddress + continuationOffset, HeaderAddress + 0x100),
        Ranges = [new CodeRange(CodeAddress, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])],
    };

    private static VertexInputInfo EmbeddedFetchInfo(ulong buffer, uint stride, uint records)
    {
        var descriptor = PipelineTestGuest.BufferDescriptor(buffer, stride, records, Format32x4Float);
        return new VertexInputInfo
        {
            FetchEmbedded = true,
            FetchAttributeRegister = 4,
            FetchBufferRegister = 6,
            Attributes = [new VertexAttributeResource(new(descriptor[0], descriptor[1], descriptor[2], descriptor[3]), 4, 4, 0, 0, 0, 0)],
            Buffers = [new VertexInputBuffer(buffer, stride, records)],
        };
    }

    private static VertexPrewarmRecord VertexRecord(ShaderCodeCapture code, VertexInputInfo info) => new()
    {
        Hash = code.Hash,
        CodeSize = code.CodeSize,
        Address = code.Address,
        UserDataBase = 0,
        UserDataCount = 8,
        PushDataCursor = 0,
        VertexInfo = info,
        RequiredVertexOutputCount = 2,
        Specialization = new ResourceSpecialization(),
    };

    private static VertexPrewarmRecord MeshRecord(ShaderCodeCapture code) => new()
    {
        Mesh = true,
        Hash = code.Hash,
        CodeSize = code.CodeSize,
        Address = code.Address,
        ContinuationAddressOffset = 0x40,
        UserDataBase = 2,
        UserDataCount = 12,
        PushDataCursor = 5,
        MeshInfo = new MeshDrawConfiguration
        {
            Geometry = new GuestGeometryConfiguration
            {
                ThreadsPerGroup = 128, InputTriangleStrip = true, InputPrimitiveCountPerWorkgroup = 64,
                InputVertexCountPerWorkgroup = 192, OutputVertexCapacity = 256, OutputPrimitiveCapacity = 128, ProvokingVertex = 2,
                WaveSize = 32, IeeeMode = true, ScratchDwords = 7, LocalDataShareDwords = 1024, PositionExportControl = 3,
                ClipSpace = new ClipSpaceTransform(true, 0.5f, -0.5f, 0.25f, 0.75f, 960, 540),
            },
            Execution = new MeshExecutionLimits
            {
                DeviceSubgroupLaneCount = 32, MaxGroupCountX = 65535, MaxGroupCountY = 65535, MaxGroupTotalCount = 4194304,
            },
        },
        RequiredVertexOutputCount = 4,
        MeshOutputLocationMask = 0x13,
        Specialization = new ResourceSpecialization { BaseBufferCount = 1, Buffers = [new BufferSpecialization(4, 0xFAC)] },
        DeviceRecordCounts = true,
    };

    private static PixelPrewarmRecord PixelRecord(ShaderCodeCapture code, uint userDataBase) => new()
    {
        Hash = code.Hash,
        CodeSize = code.CodeSize,
        Address = code.Address,
        UserDataBase = userDataBase,
        UserDataCount = 4,
        PushDataCursor = 0,
        Info = new PixelInputInfo { InputCount = 1 },
        Outputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        InputEnable = 0x2,
        InputAddress = 0x2,
        Specialization = new ResourceSpecialization(),
    };

    [Fact]
    public void AVertexRecordLeavesOutBufferAddressesAndRecordCounts()
    {
        var code = Code(0x1111);
        var identity = ShaderPrewarmList.Identity(VertexRecord(code, EmbeddedFetchInfo(DataBase, 16, 4)));
        Assert.Equal(identity, ShaderPrewarmList.Identity(VertexRecord(code, EmbeddedFetchInfo(DataBase + 0x1_0001_2340, 16, 900))));
        Assert.NotEqual(identity, ShaderPrewarmList.Identity(VertexRecord(code, EmbeddedFetchInfo(DataBase, 32, 4))));
    }

    // The same program met at another code address is one record.
    [Fact]
    public void ProgramsMetAtAnotherCodeAddressAreOneRecord()
    {
        var code = Code(0x1111);
        var moved = new ShaderCodeCapture
        {
            Hash = code.Hash, CodeSize = code.CodeSize, Address = CodeAddress + 0x2000, Generation = Generation.Gen5,
            Ranges = [new CodeRange(CodeAddress + 0x2000, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])],
        };
        var pixel = PixelRecord(code, 1);
        var movedPixel = PixelRecord(moved, 1);
        var vertex = VertexRecord(code, EmbeddedFetchInfo(DataBase, 16, 4));
        var movedVertex = VertexRecord(moved, EmbeddedFetchInfo(DataBase, 16, 4));
        Assert.Equal(ShaderPrewarmList.Identity(pixel), ShaderPrewarmList.Identity(movedPixel));
        Assert.Equal(ShaderPrewarmList.Identity(vertex), ShaderPrewarmList.Identity(movedVertex));
        var state = new PreRasterizationState(0, 2, 1, false, false, true, false);
        using (var list = Open())
        {
            list.RecordPixel(code, pixel);
            list.RecordPixel(moved, movedPixel);
            list.RecordPreRasterization(list.RecordVertex(code, vertex), state);
            Assert.Equal(list.RecordVertex(code, vertex), list.RecordVertex(moved, movedVertex));
        }

        using var reloaded = Open();
        Assert.Equal(1, reloaded.LoadedPixelCount);
        var items = reloaded.LoadedGraphicsPrewarm();
        Assert.Equal(2, items.Count);
        Assert.Equal(CodeAddress, Assert.IsType<PixelPrewarmItem>(items[0]).Record.Address);
        Assert.Equal(CodeAddress, Assert.IsType<PreRasterizationPrewarmItem>(items[1]).Program.Address);
    }

    // Items run by the time into their sessions at which they were first met, so a state a later session adds runs
    // before the earlier session's later records instead of after all of them.
    [Fact]
    public void ALaterSessionsRecordsRunWhereThatSessionFirstMetThem()
    {
        var pixelCode = Code(0x1111);
        var vertexCode = Code(0x2222);
        var vertex = VertexRecord(vertexCode, EmbeddedFetchInfo(DataBase, 16, 4));
        var state = new PreRasterizationState(0, 2, 1, false, false, true, false);
        using (var list = Open())
        {
            list.SessionClock = () => 10;
            list.RecordPixel(pixelCode, PixelRecord(pixelCode, 1));
            list.SessionClock = () => 100;
            list.RecordPixel(pixelCode, PixelRecord(pixelCode, 2));
        }

        using (var list = Open())
        {
            list.SessionClock = () => 50;
            list.RecordPreRasterization(list.RecordVertex(vertexCode, vertex), state);
        }

        using var reloaded = Open();
        Assert.Collection(reloaded.LoadedGraphicsPrewarm(),
            item => Assert.Equal(1u, Assert.IsType<PixelPrewarmItem>(item).Record.UserDataBase),
            item => Assert.Equal(state, Assert.IsType<PreRasterizationPrewarmItem>(item).State),
            item => Assert.Equal(2u, Assert.IsType<PixelPrewarmItem>(item).Record.UserDataBase));
        // The prewarm pool schedules the items by their first uses.
        Assert.Equal(new[] { 10f, 50f, 100f }, reloaded.LoadedGraphicsPrewarm().Select(static item => item.FirstUse));
    }

    [Fact]
    public void PixelProgramsAndPreRasterizationStatesLoadInTheOrderTheyWereRecorded()
    {
        var pixelCode = Code(0x1111);
        var vertexCode = Code(0x2222);
        var meshCode = Code(0x3333, continuationOffset: 0x40);
        var vertex = VertexRecord(vertexCode, EmbeddedFetchInfo(DataBase, 16, 4));
        var mesh = MeshRecord(meshCode);
        var first = new PreRasterizationState(0, 2, 1, true, false, true, false);
        var second = new PreRasterizationState(2, 0, 0, false, true, false, true);
        using (var list = Open())
        {
            list.RecordPixel(pixelCode, PixelRecord(pixelCode, 1));
            var vertexIdentity = list.RecordVertex(vertexCode, vertex);
            list.RecordPreRasterization(vertexIdentity, first);
            list.RecordPixel(pixelCode, PixelRecord(pixelCode, 2));
            var meshIdentity = list.RecordVertex(meshCode, mesh);
            list.RecordPreRasterization(meshIdentity, second);
            list.RecordPreRasterization(vertexIdentity, first);
            // A state of a program the file does not hold is kept but left out of the prewarm.
            list.RecordPreRasterization(0xDEAD, first);
            Assert.Equal(vertexIdentity, list.RecordVertex(vertexCode, vertex));
            Assert.Equal(ShaderPrewarmList.Identity(mesh), meshIdentity);
        }

        using var reloaded = Open();
        Assert.Equal(3, reloaded.LoadedPreRasterizationCount);
        Assert.Collection(reloaded.LoadedGraphicsPrewarm(),
            item => Assert.Equal(1u, Assert.IsType<PixelPrewarmItem>(item).Record.UserDataBase),
            item =>
            {
                var part = Assert.IsType<PreRasterizationPrewarmItem>(item);
                Assert.Equal(first, part.State);
                Assert.Equal(ShaderPrewarmList.Identity(vertex), ShaderPrewarmList.Identity(part.Program));
                var attribute = Assert.Single(part.Program.VertexInfo!.Attributes);
                Assert.Equal(0u, attribute.Descriptor.Word0);
                Assert.Equal(16u, attribute.Descriptor.Stride);
                Assert.Equal(Format32x4Float, attribute.Descriptor.Format);
                Assert.Equal(vertexCode.Ranges[0].Bytes, Assert.Single(part.Code.Ranges).Bytes);
            },
            item => Assert.Equal(2u, Assert.IsType<PixelPrewarmItem>(item).Record.UserDataBase),
            item =>
            {
                var part = Assert.IsType<PreRasterizationPrewarmItem>(item);
                Assert.Equal(second, part.State);
                Assert.True(part.Program.Mesh);
                Assert.Equal(mesh.MeshInfo, part.Program.MeshInfo);
                Assert.Equal(mesh.MeshOutputLocationMask, part.Program.MeshOutputLocationMask);
                Assert.Equal(0x40ul, part.Program.ContinuationAddressOffset);
                Assert.True(part.Program.DeviceRecordCounts);
                Assert.Equal(new BufferSpecialization(4, 0xFAC), Assert.Single(part.Program.Specialization.Buffers));
                Assert.Equal(ShaderPrewarmList.Identity(mesh), ShaderPrewarmList.Identity(part.Program));
            });
    }

    // The embedded fetch vertex program compiled by a program cache that records into the list, as a session's does: its
    // prewarm identity, the session's SPIR-V and the host it compiled for.
    internal static (ulong Identity, byte[] Spirv, IShaderPipelineHost Host) RecordEmbeddedFetchProgram(ShaderPrewarmList list)
    {
        var guest = new PipelineTestGuest(Compile);
        guest.Host.ShaderPrewarm = list;
        guest.RegisterProgram(CodeAddress, HeaderAddress, EmbeddedFetchProgram);
        var cursor = 0u;
        guest.Programs.GetOrCompile(guest.Source(CodeAddress, ShaderStage.Vertex, new uint[8]),
            new StageCompileOptions { VertexInfo = EmbeddedFetchInfo(DataBase, 16, 4) }, ref cursor, out _);
        var (_, identity) = Assert.Single(guest.Host.RecordedVertexPrograms);
        return (identity, Assert.Single(guest.Compiler.Shaders).Spirv, guest.Host);
    }

    [Fact]
    public void ARecordedVertexProgramTranslatesToTheSameSpirvAfterReload()
    {
        byte[] runtime;
        IShaderPipelineHost host;
        var state = new PreRasterizationState(0, 0, 0, false, false, true, false);
        using (var list = Open())
        {
            (var identity, runtime, host) = RecordEmbeddedFetchProgram(list);
            list.RecordPreRasterization(identity, state);
        }

        using var reloaded = Open();
        var part = Assert.IsType<PreRasterizationPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.Equal(state, part.State);
        var compiler = new FakeShaderCompiler(Compile);
        Assert.True(ShaderProgramCache.TryCompileVertexPrewarm(part.Program, part.Code, compiler, host, out var compiled, out var layout,
            out var task, out var error), error);
        Assert.Null(task);
        Assert.NotNull(layout);
        Assert.Single(Assert.IsType<FakeCompiledShader>(compiled).Request.VertexInputs);
        Assert.Equal(runtime, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
    }

    [Fact]
    public void GcnVertexPrewarmReplaysTheRecordedFetchShader()
    {
        var frontend = new GcnShaderFrontend();
        var guest = new PipelineTestGuest(Compile);
        const ulong fetchAddress = CodeAddress - 0x100;
        guest.RegisterProgram(CodeAddress, HeaderAddress, [0xBE802100, 0xBF810000]);
        guest.RegisterProgram(fetchAddress, HeaderAddress - 0x100, [0x7E0802F2, 0xBE802000]);
        uint[] userData = [unchecked((uint)fetchAddress), (uint)(fetchAddress >> 32)];
        var registered = new RegisteredShader(CodeAddress, 0, 8, 0, 0, 0, 0, fetchAddress, 8)
        {
            Frontend = frontend,
            FrontendUserData = userData,
        };
        var context = new CpuContext(guest.Memory, Generation.Gen4);
        using (var list = Open())
        {
            guest.Host.ShaderPrewarm = list;
            var programs = new ShaderProgramCache(context, guest.Compiler, guest.Host);
            var cursor = 0u;
            programs.GetOrCompile(new ShaderSource(registered, 0x1234, userData, 0, ShaderStage.Vertex),
                new StageCompileOptions { VertexInfo = new VertexInputInfo() }, ref cursor, out _);
            var (_, identity) = Assert.Single(guest.Host.RecordedVertexPrograms);
            list.RecordPreRasterization(identity, new PreRasterizationState(0, 0, 0, false, false, true, false));
        }

        using var reloaded = Open();
        var part = Assert.IsType<PreRasterizationPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.Equal(registered.CodeSizeBytes, part.Code.FrontendEntryCodeSize);
        var compiler = new FakeShaderCompiler(Compile);
        Assert.True(ShaderProgramCache.TryCompileVertexPrewarm(part.Program, part.Code, compiler, guest.Host,
            out var compiled, out _, out _, out var error, domain => domain == frontend.CacheDomain ? frontend : null), error);
        Assert.Equal(Assert.Single(guest.Compiler.Shaders).Spirv, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
    }

    // A vertex program compiled after pixel stages that end at different push cursors is one translation, whose push
    // data takes the top of the range above every one of them; after a pixel stage its data does not fit beside, it
    // reads the shader data block. The program reads a V# from user data, so it has shader data to place.
    [Fact]
    public void AVertexProgramIsOneTranslationForEveryPixelStageBeforeIt()
    {
        var guest = new PipelineTestGuest(Compile);
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var userData = PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Float);
        var options = new StageCompileOptions { VertexInfo = new VertexInputInfo() };
        var ids = new List<ulong>();
        BindingLayout? first = null;
        var pixelEnds = new List<uint> { 0 };
        for (var index = 0; index < pixelEnds.Count; index++)
        {
            var pixelEnd = pixelEnds[index];
            var cursor = pixelEnd;
            var handle = guest.Programs.GetOrCompile(guest.Source(CodeAddress, ShaderStage.Vertex, userData), options, ref cursor,
                out var stage);
            ids.Add(handle.Id);
            var layout = stage.Program!.Bindings!;
            if (first is null)
            {
                // The last cursors the data fits above: the middle of the room and its end.
                first = layout;
                Assert.True(layout.ShaderDataDwordCount is > 0 and < PushData.DwordCount, $"data={layout.ShaderDataDwordCount}");
                pixelEnds.Add((PushData.DwordCount - layout.ShaderDataDwordCount) / 2);
                pixelEnds.Add(PushData.DwordCount - layout.ShaderDataDwordCount);
            }

            Assert.True(layout.UsesPushData, $"cursor={pixelEnd} data={layout.ShaderDataDwordCount} start={layout.PushDataStartDword}");
            Assert.Equal(PushData.DwordCount - layout.ShaderDataDwordCount, layout.PushDataStartDword);
            Assert.True(layout.PushDataStartDword >= pixelEnd);
            Assert.Equal(PushData.DwordCount, cursor);
        }

        Assert.Equal(3, ids.Count);
        Assert.All(ids, id => Assert.Equal(ids[0], id));
        Assert.Single(guest.Compiler.Shaders);

        var late = PushData.DwordCount - first!.ShaderDataDwordCount + 1;
        var lateHandle = guest.Programs.GetOrCompile(guest.Source(CodeAddress, ShaderStage.Vertex, userData), options, ref late,
            out var lateStage);
        Assert.NotEqual(ids[0], lateHandle.Id);
        Assert.False(lateStage.Program!.Bindings!.UsesPushData);
        Assert.Equal(2, guest.Compiler.Shaders.Count);
    }

    internal static readonly MeshShaderLimits TaskMeshLimits = new(128, 256, 256, 28672, 65535, 65535, 1u << 22, 128, 32768, 49152,
        1, 1, 128);

    // A task-launched mesh program (s_endpgm) compiled by a program cache that records into the list, as a session's does: its
    // prewarm identity, the task launch of its program and the host it compiled for.
    internal static (ulong Identity, MeshTaskLaunchInfo? Task, IShaderPipelineHost Host) RecordTaskLaunchedMeshProgram(
        ShaderPrewarmList list)
    {
        var guest = new PipelineTestGuest(Compile);
        guest.Host.ShaderPrewarm = list;
        guest.Host.MeshShadersSupported = true;
        guest.Host.MeshLimits = TaskMeshLimits;
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        var options = new StageCompileOptions
        {
            MeshInfo = new MeshDrawConfiguration
            {
                Execution = new MeshExecutionLimits { DeviceSubgroupLaneCount = 32 },
                Geometry = new GuestGeometryConfiguration
                {
                    WaveSize = 32, ThreadsPerGroup = 32, OutputVertexCapacity = 3, OutputPrimitiveCapacity = 1,
                    InputPrimitiveCountPerWorkgroup = 4, InputVertexCountPerWorkgroup = 3, InputPointList = true, TaskLaunched = true,
                },
            },
        };
        var cursor = 0u;
        guest.Programs.GetOrCompile(guest.Source(CodeAddress, ShaderStage.Mesh, []), options, ref cursor, out var stage);
        var (_, identity) = Assert.Single(guest.Host.RecordedVertexPrograms);
        return (identity, stage.Program!.MeshTask, guest.Host);
    }

    // A task-launched mesh program is recorded as any other mesh program, and translated again from its record it takes
    // the task launch its session's pipelines specialize the generic task stage with.
    [Fact]
    public void ATaskLaunchedMeshProgramIsRecordedWithItsSessionsTaskLaunch()
    {
        var state = new PreRasterizationState(0, 0, 0, false, false, true, false);
        MeshTaskLaunchInfo? runtime;
        IShaderPipelineHost host;
        using (var list = Open())
        {
            (var identity, runtime, host) = RecordTaskLaunchedMeshProgram(list);
            list.RecordPreRasterization(identity, state);
        }

        Assert.Equal(new MeshTaskLaunchInfo(4, true), runtime);
        using var reloaded = Open();
        var part = Assert.IsType<PreRasterizationPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.True(ShaderProgramCache.TryCompileVertexPrewarm(part.Program, part.Code, new FakeShaderCompiler(Compile), host,
            out var compiled, out _, out var task, out var error), error);
        Assert.NotNull(compiled);
        Assert.Equal(runtime, task);
    }
}
