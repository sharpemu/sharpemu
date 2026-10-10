// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.ShaderCache;

public sealed class ShaderCacheFileTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong VertexCodeAddress = PipelineTestGuest.MemoryBase + 0x2_0000;
    private const ulong VertexHeaderAddress = PipelineTestGuest.MemoryBase + 0x9000;
    private const ulong PixelCodeAddress = PipelineTestGuest.MemoryBase + 0x3_0000;
    private const ulong PixelHeaderAddress = PipelineTestGuest.MemoryBase + 0xA000;
    private const ulong BufferAddress = PipelineTestGuest.MemoryBase + 0x4_0000;
    private const uint Format32x4Float = 77;

    private static readonly uint[] EmbeddedFetchVertexProgram = [0xF4080303, 0xFA000000, 0x02020B08, 0xE00C2000, 0x80030401, 0xBF810000];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private ShaderCacheFile Open() => ShaderCacheFile.Open(_directory) ?? throw new InvalidOperationException("The shader cache did not open.");

    private static (PipelineTestGuest Guest, byte[] Spirv) CompileCompute(ShaderCacheFile file, uint format)
    {
        var guest = new PipelineTestGuest(Compile);
        guest.Host.ShaderCache = file;
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var cursor = 0u;
        guest.Programs.GetOrCompile(
            guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, format)),
            PipelineTestGuest.ComputeOptions(threadsX: 64),
            ref cursor,
            out _);
        return (guest, Assert.Single(guest.Compiler.Shaders).Spirv);
    }

    private static byte[] Replay(StageRecord record, ShaderCodeCapture code, IShaderPipelineHost host)
    {
        Assert.True(
            ShaderProgramCache.TryReplay(
                record, code, new FakeShaderCompiler(Compile), ShaderCompileHostFlags.From(host), out var compiled, out var info, out var error),
            error);
        Assert.NotNull(info);
        return Assert.IsType<FakeCompiledShader>(compiled).Spirv;
    }

    [Fact]
    public void ARecordedComputeProgramReplaysToTheSameSpirv()
    {
        PipelineTestGuest guest;
        byte[] runtime;
        using (var file = Open())
        {
            (guest, runtime) = CompileCompute(file, BufferDescriptorWords.Format32UInt);
        }

        using var reloaded = Open();
        var item = Assert.Single(reloaded.Computes);
        Assert.Equal(runtime, Replay(item.Record, item.Code, guest.Host));
    }

    [Fact]
    public void ARecordedGraphicsPipelineReplaysBothStagesToTheSameSpirv()
    {
        var descriptor = PipelineTestGuest.BufferDescriptor(BufferAddress, 16, 4, Format32x4Float);
        var descriptorWords = new BufferDescriptorWords(descriptor[0], descriptor[1], descriptor[2], descriptor[3]);
        PipelineTestGuest guest;
        byte[] vertexRuntime;
        byte[] pixelRuntime;
        using (var file = Open())
        {
            guest = new PipelineTestGuest(Compile);
            guest.Host.ShaderCache = file;
            guest.RegisterProgram(VertexCodeAddress, VertexHeaderAddress, EmbeddedFetchVertexProgram);
            guest.RegisterProgram(PixelCodeAddress, PixelHeaderAddress, PipelineTestGuest.EndProgram);
            var vertexOptions = new StageCompileOptions
            {
                VertexInfo = new VertexInputInfo
                {
                    FetchEmbedded = true,
                    FetchAttributeRegister = 4,
                    FetchBufferRegister = 6,
                    Attributes = [new VertexAttributeResource(descriptorWords, 4, 4, 0, 0, 0, 0)],
                    Buffers = [new VertexInputBuffer(BufferAddress, 16, 4)],
                },
            };
            var cursor = 0u;
            var vertex = guest.Programs.GetOrCompile(
                guest.Source(VertexCodeAddress, ShaderStage.Vertex, new uint[8]), vertexOptions, ref cursor, out _);
            var pixel = guest.Programs.GetOrCompile(
                guest.Source(PixelCodeAddress, ShaderStage.Pixel, []), new StageCompileOptions { PixelInfo = new PixelInputInfo() }, ref cursor, out _);
            vertexRuntime = guest.Compiler.Shaders[0].Spirv;
            pixelRuntime = guest.Compiler.Shaders[1].Spirv;

            Assert.True(guest.Programs.TryGetStageRecord(vertex.Id, out var vertexRecord, out var vertexCode));
            Assert.True(guest.Programs.TryGetStageRecord(pixel.Id, out var pixelRecord, out var pixelCode));
            var rendering = new PipelineRenderingState { ColorCount = 1, DepthFormat = Format.D32Sfloat };
            rendering.ColorFormats[0] = Format.R8G8B8A8Unorm;
            var vertexInput = new PipelineVertexInputState { BindingCount = 1, AttributeCount = 1 };
            vertexInput.Bindings[0] = new PipelineVertexBinding(16, false);
            vertexInput.Attributes[0] = new PipelineVertexAttribute(0, 0);
            var parameters = new PipelineStaticParameters { Topology = PrimitiveTopology.TriangleList, WithDepth = true };
            file.RecordGraphics(
                new GraphicsPipelineRecord
                {
                    Vertex = vertexRecord,
                    Pixel = pixelRecord,
                    Rendering = rendering,
                    VertexInput = vertexInput,
                    StaticParameters = parameters,
                    Attributes = [new VertexAttributeFormat(descriptorWords, 4)],
                },
                vertexCode,
                pixelCode);
        }

        using var reloaded = Open();
        var item = Assert.Single(reloaded.Graphics);
        Assert.Equal(vertexRuntime, Replay(item.Record.Vertex, item.VertexCode, guest.Host));
        Assert.Equal(pixelRuntime, Replay(item.Record.Pixel!, item.PixelCode!, guest.Host));
        Assert.Equal(Format.R8G8B8A8Unorm, item.Record.Rendering.ColorFormats[0]);
        Assert.Equal(Format.D32Sfloat, item.Record.Rendering.DepthFormat);
        Assert.Equal(16u, item.Record.VertexInput.Bindings[0].Stride);
        Assert.True(item.Record.StaticParameters.WithDepth);
        Assert.Equal(descriptorWords, Assert.Single(item.Record.Attributes).Descriptor);
        Assert.Equal(ShaderCacheFile.StageIdentity(item.Record.Vertex), item.VertexIdentity);
    }

    [Fact]
    public void TheSameCompileIsRecordedOnceAndAnotherSpecializationAddsARecord()
    {
        using (var file = Open())
        {
            CompileCompute(file, BufferDescriptorWords.Format32UInt);
            CompileCompute(file, BufferDescriptorWords.Format32UInt);
            CompileCompute(file, BufferDescriptorWords.Format32x4UInt);
        }

        using var reloaded = Open();
        Assert.Equal(2, reloaded.Computes.Count);
        Assert.Same(reloaded.Computes[0].Code, reloaded.Computes[1].Code);
    }

    [Fact]
    public void ATruncatedLastRecordIsDroppedAndTheFileKeepsAppending()
    {
        using (var file = Open())
        {
            CompileCompute(file, BufferDescriptorWords.Format32UInt);
        }

        var path = Path.Combine(_directory, ShaderCacheFile.FileName);
        var intactLength = new FileInfo(path).Length;
        using (var stream = new FileStream(path, FileMode.Append))
        {
            stream.Write([0x40, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8, 2, 9, 9]);
        }

        using (var file = Open())
        {
            Assert.Single(file.Computes);
            Assert.Equal(intactLength, new FileInfo(path).Length);
            CompileCompute(file, BufferDescriptorWords.Format32x4UInt);
        }

        using var reloaded = Open();
        Assert.Equal(2, reloaded.Computes.Count);
    }

    [Fact]
    public void CompiledStagesAndBinariesAreFoundOnlyUnderTheirKeys()
    {
        var stage = new CompiledStageRecord
        {
            Spirv = [1, 2, 3, 4],
            Bindings = [new LayoutBindingRecord(3, 7, 1, 32)],
            VertexFetchComponents = new byte[VertexInputInfo.MaxBuffers],
        };
        byte[] driver = [0xAA, 0xBB];
        using (var file = Open())
        {
            file.AddCompiledStage(11, 100, stage);
            file.AddBinary(driver, 42, [new PipelineBinaryPart([9, 9], [5, 6, 7])]);
        }

        using var reloaded = Open();
        Assert.True(reloaded.TryGetCompiledStage(11, 100, out var loaded));
        Assert.Equal(stage.Spirv, loaded.Spirv);
        Assert.Equal(stage.Bindings, loaded.Bindings);
        Assert.False(reloaded.TryGetCompiledStage(11, 101, out _));
        Assert.True(reloaded.TryReadBinary(driver, 42, out var parts));
        Assert.Equal(new byte[] { 5, 6, 7 }, Assert.Single(parts).Data);
        Assert.False(reloaded.TryReadBinary([0xAA, 0xBC], 42, out _));
        Assert.False(reloaded.TryReadBinary(driver, 43, out _));
    }

    [Fact]
    public void CompactionDropsStagesForeignBinariesAndStaleDoneMarkersAndKeepsPipelineRecords()
    {
        byte[] driver = [1];
        var stage = new CompiledStageRecord { Spirv = [1], Bindings = [], VertexFetchComponents = [] };
        using (var file = Open())
        {
            CompileCompute(file, BufferDescriptorWords.Format32UInt);
            file.AddCompiledStage(1, 100, stage);
            file.AddCompiledStage(2, 99, stage);
            file.AddBinary(driver, 10, [new PipelineBinaryPart([1], [1])]);
            file.AddBinary(driver, 11, [new PipelineBinaryPart([1], [1])]);
            file.AddBinary([2], 10, [new PipelineBinaryPart([1], [1])]);
            file.AddDone(5, 7);
            file.AddDone(6, 8);

            Assert.Null(file.Compact(100, driver, 7));
            var result = file.Compact(100, driver, 7, force: true);

            Assert.Equal(4, result?.RecordsDropped);
            Assert.True(result?.BytesAfter < result?.BytesBefore);
            Assert.Null(file.Compact(100, driver, 7, force: true));
        }

        using var reloaded = Open();
        Assert.Single(reloaded.Computes);
        Assert.False(reloaded.TryGetCompiledStage(1, 100, out _));
        Assert.False(reloaded.TryGetCompiledStage(2, 99, out _));
        Assert.True(reloaded.TryReadBinary(driver, 10, out _));
        Assert.True(reloaded.TryReadBinary(driver, 11, out _));
        Assert.False(reloaded.TryReadBinary([2], 10, out _));
        Assert.Equal(2, reloaded.BinaryCount);
        Assert.True(reloaded.IsDone(5, 7));
        Assert.False(reloaded.IsDone(6, 8));
    }

    [Fact]
    public void UnreferencedBinariesAreDroppedOnlyWhenTheCompactionAsksForIt()
    {
        byte[] driver = [1];
        using (var file = Open())
        {
            file.AddBinary(driver, 10, [new PipelineBinaryPart([1], [1])]);
            file.AddBinary(driver, 11, [new PipelineBinaryPart([1], [1])]);
            file.AddBinary(driver, 12, [new PipelineBinaryPart([1], [1])]);
            file.AddDone(5, 7, [10]);
            file.AddDone(6, 8, [11]);

            Assert.Equal(1, file.Compact(100, driver, 7, force: true)?.RecordsDropped);
            Assert.Equal(3, file.BinaryCount);
        }

        using (var reopened = Open())
        {
            Assert.Equal(2, reopened.Compact(100, driver, 7, force: true, dropUnreferencedBinaries: true)?.RecordsDropped);
        }

        using var reloaded = Open();
        Assert.True(reloaded.TryReadBinary(driver, 10, out _));
        Assert.False(reloaded.TryReadBinary(driver, 11, out _));
        Assert.False(reloaded.TryReadBinary(driver, 12, out _));
        Assert.True(reloaded.IsDone(5, 7));
        Assert.Null(reloaded.Compact(100, driver, 7, force: true, dropUnreferencedBinaries: true));
    }

    [Fact]
    public void AShaderSeedCarriesThePlayedPipelinesWithoutTheirAddresses()
    {
        var seedPath = Path.Combine(_directory, "shader_seeds", "PPSA00001.seed");
        StageRecord recorded;
        using (var file = Open())
        {
            CompileCompute(file, BufferDescriptorWords.Format32UInt);
            recorded = Assert.Single(file.Computes).Record;
            var learned = ShaderSeed.Learn(file);
            Assert.Equal(0ul, Assert.Single(learned.Computes).Address);
            Assert.True(learned.TrySave(seedPath));
            var items = ShaderWorkList.Build(file, true, ShaderSeed.Load(seedPath)).Items;
            Assert.DoesNotContain(items, static item => item.Kind == ShaderWorkKind.SeedCompute);
        }

        var loaded = Assert.Single(ShaderSeed.Load(seedPath).Computes);
        Assert.NotEqual(0ul, recorded.Address);
        Assert.Equal(ShaderSeed.ComputeIdentity(recorded), ShaderSeed.ComputeIdentity(loaded));
        Assert.Equal(recorded.Specialization, loaded.Specialization);
    }

    [Fact]
    public void ADifferenceNamesTheFirstChangedFieldAndItsCategory()
    {
        var recorded = new ResourceSpecialization
        {
            Images = [new ImageSpecialization(ImageNumericClass.Float, ImageDimension.Dim2D, 1, 0, 0xA04, 0, 0, 0, false)],
        };
        var derived = new ResourceSpecialization
        {
            Images = [new ImageSpecialization(ImageNumericClass.Float, ImageDimension.Dim2D, 1, 0, 0xFAC, 0, 0, 0, false)],
        };

        var difference = new RecordDifference().Compare("Specialization", recorded, derived).ToString();

        Assert.Equal("Specialization.Images[0].ShaderSwizzle 0xA04 -> 0xFAC", difference);
        Assert.Equal("Specialization.Images[*].ShaderSwizzle", RecordDifference.Category(difference));
        Assert.False(new RecordDifference().Compare("Specialization", recorded, recorded.Clone()).Any);
    }

    [Fact]
    public void AFileWithAnotherHeaderStartsEmpty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllBytes(Path.Combine(_directory, ShaderCacheFile.FileName), [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13]);

        using var file = Open();
        Assert.Empty(file.Computes);
        Assert.Equal(12, file.Length);
    }

    [Fact]
    public void AnOldPrewarmListIsImportedOnceAndRemoved()
    {
        var source = Path.Combine(_directory, "source");
        PipelineTestGuest guest;
        byte[] runtime;
        using (var file = ShaderCacheFile.Open(source)!)
        {
            (guest, runtime) = CompileCompute(file, BufferDescriptorWords.Format32UInt);
        }

        CachedCompute recorded;
        using (var file = ShaderCacheFile.Open(source)!)
        {
            recorded = Assert.Single(file.Computes);
        }

        var legacy = Path.Combine(_directory, "shader-prewarm.bin");
        var stamp = Path.Combine(_directory, "shader-prewarm.stamp");
        File.WriteAllBytes(legacy, LegacyPrewarmList(recorded.Record, recorded.Code));
        File.WriteAllText(stamp, "build");

        using (var file = Open())
        {
            Assert.Equal(1, file.ImportedLegacyComputes);
            var imported = Assert.Single(file.Computes);
            Assert.Equal(recorded.Identity, imported.Identity);
            Assert.Equal(runtime, Replay(imported.Record, imported.Code, guest.Host));
        }

        Assert.False(File.Exists(legacy));
        Assert.False(File.Exists(stamp));
        using var reloaded = Open();
        Assert.Equal(0, reloaded.ImportedLegacyComputes);
        Assert.Equal(recorded.Identity, Assert.Single(reloaded.Computes).Identity);
    }

    [Fact]
    public void AnOldPrewarmListWithAnotherLayoutIsRemovedWithoutImporting()
    {
        Directory.CreateDirectory(_directory);
        var legacy = Path.Combine(_directory, "shader-prewarm.bin");
        File.WriteAllBytes(legacy, [0x53, 0x45, 0x50, 0x57, 1, 0, 0, 0, 0, 0, 0, 0, 1]);

        using var file = Open();
        Assert.Equal(0, file.ImportedLegacyComputes);
        Assert.Empty(file.Computes);
        Assert.False(File.Exists(legacy));
    }

    private static byte[] LegacyPrewarmList(StageRecord record, ShaderCodeCapture code)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x57504553u);
        writer.Write(1u);
        writer.Write(LegacySchemaFingerprint());
        WriteLegacyRecord(writer, 1, ShaderCacheSerializer.Serialize(body => ShaderCacheSerializer.WriteCode(body, code)));
        WriteLegacyRecord(writer, 2, ShaderCacheSerializer.Serialize(body =>
        {
            body.Write(record.Hash);
            body.Write(record.CodeSize);
            body.Write(record.Address);
            body.Write(record.UserDataBase);
            body.Write(record.UserDataCount);
            body.Write(record.PushDataCursor);
            ShaderCacheSerializer.WriteCompute(body, record.Compute!, record.ComputeSystemRegisters);
            ShaderCacheSerializer.WriteSpecialization(body, record.Specialization!);
        }));
        writer.Flush();
        return stream.ToArray();
    }

    private static void WriteLegacyRecord(BinaryWriter writer, byte kind, byte[] payload)
    {
        var body = new byte[payload.Length + 1];
        body[0] = kind;
        payload.CopyTo(body, 1);
        writer.Write((uint)body.Length);
        writer.Write(System.IO.Hashing.XxHash3.HashToUInt64(body));
        writer.Write(body);
    }

    private static uint LegacySchemaFingerprint()
    {
        var builder = new System.Text.StringBuilder();
        foreach (var type in new[]
        {
            typeof(ComputeInputInfo), typeof(Gen5ComputeSystemRegisters), typeof(ResourceSpecialization),
            typeof(BufferSpecialization), typeof(ImageSpecialization), typeof(BufferCandidateTableSpecialization),
        })
        {
            builder.Append(type.FullName).Append('{');
            foreach (var property in type.GetProperties().OrderBy(static property => property.Name, StringComparer.Ordinal))
            {
                builder.Append(property.PropertyType.FullName).Append(' ').Append(property.Name).Append(';');
            }

            builder.Append('}');
        }

        return (uint)System.IO.Hashing.XxHash3.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(builder.ToString()));
    }
}
