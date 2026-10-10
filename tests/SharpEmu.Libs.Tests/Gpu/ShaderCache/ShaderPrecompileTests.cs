// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.ShaderCache;

public sealed class ShaderPrecompileTests : IDisposable
{
    private const byte ComputeType = 0;
    private const byte PixelType = 1;
    private const byte GeometryType = 2;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ShaderCacheFile Open() => ShaderCacheFile.Open(_directory) ?? throw new InvalidOperationException("The shader cache did not open.");

    private static byte[] ProgramCode => AgcHeaderBuilder.Code(PipelineTestGuest.FormatLoadProgram);

    private static CachedProgram Program(byte[] header, ulong address = GameShaderScanner.ScannedCodeAddress)
    {
        Assert.True(ShaderInventory.TryCreateCapture(ProgramCode, (uint)ProgramCode.Length, address, out var capture));
        Assert.True(AgcShaderHeader.TryParse(header, 0, out var parsed));
        var program = new InventoryProgram { Code = capture.Key, Source = InventorySource.Scanned, Header = parsed.Bytes };
        return new CachedProgram(program, capture, ShaderCacheFile.ProgramIdentity(program));
    }

    [Fact]
    public void AnAgcHeaderExposesItsRegistersSemanticsAndDispatchModifier()
    {
        var bytes = AgcHeaderBuilder.Build(
            ComputeType, 12,
            shaderRegisters: [(0x207, 64), (0x213, 0x90)],
            contextRegisters: [(0x1B3, 2)],
            inputSemantics: [0x11, 0x22],
            outputSemantics: [0x33],
            dispatchModifier: 0x8000);

        Assert.True(AgcShaderHeader.TryParse(bytes, 0, out var header));
        Assert.Equal(AgcShaderType.Compute, header.Type);
        Assert.Equal(12u, header.CodeSize);
        Assert.Equal([new AgcRegister(0x207, 64), new AgcRegister(0x213, 0x90)], header.ShaderRegisters);
        Assert.Equal([new AgcRegister(0x1B3, 2)], header.ContextRegisters);
        Assert.Equal(new uint[] { 0x11, 0x22 }, header.InputSemantics);
        Assert.Equal(new uint[] { 0x33 }, header.OutputSemantics);
        Assert.Equal(0x8000u, header.DispatchModifier);
    }

    [Fact]
    public void ARelocatedHeaderNormalizesToTheFileLayout()
    {
        const ulong headerAddress = 0x7_0000_1000;
        var bytes = AgcHeaderBuilder.Build(PixelType, 12, contextRegisters: [(0x1B3, 2)], inputSemantics: [0x11]);
        var relocated = AgcHeaderBuilder.WithAbsolutePointers(bytes, headerAddress);

        Assert.True(AgcShaderHeader.TryParse(relocated, headerAddress, out var header));
        Assert.True(AgcShaderHeader.TryParse(bytes, 0, out var original));
        Assert.Equal(original.Bytes, header.Bytes);
    }

    [Fact]
    public void AHeaderWithAForeignSignatureOrSizeIsRejected()
    {
        var bytes = AgcHeaderBuilder.Build(ComputeType, 12);
        bytes[0] = 0;
        Assert.False(AgcShaderHeader.TryParse(bytes, 0, out _));
        var wrongSize = AgcHeaderBuilder.Build(ComputeType, 12);
        Assert.False(AgcShaderHeader.TryParse(wrongSize.AsSpan(0, wrongSize.Length - 4), 0, out _));
    }

    [Fact]
    public void AStaticComputeRecordReadsTheDispatchShapeFromTheHeader()
    {
        var resource2 = (4u << 1) | (1u << 7) | (2u << 11) | (3u << 15);
        var program = Program(AgcHeaderBuilder.Build(
            ComputeType, ProgramCode.Length,
            shaderRegisters: [(0x207, 64), (0x208, 2), (0x209, 1), (0x213, resource2)],
            dispatchModifier: 1u << 15,
            scratchDwords: 5));

        Assert.True(StaticStageInputs.TryCompute(program, computeWave64Supported: false, out var record));

        var info = Assert.IsType<ComputeInputInfo>(record.Compute);
        Assert.Equal(ShaderStage.Compute, record.Stage);
        Assert.Equal(4u, record.UserDataCount);
        Assert.Null(record.Specialization);
        Assert.Equal((64u, 2u, 1u), (info.ThreadsX, info.ThreadsY, info.ThreadsZ));
        Assert.Equal(32u, info.WaveSize);
        Assert.Equal(3u * 128u, info.LocalDataShareDwords);
        Assert.Equal(5u, info.ScratchDwords);
        Assert.Equal(3, info.ThreadIdCount);
        Assert.True(info.GroupIdX);
        Assert.True(info.NeedsLocalDataShareBarriers);
        Assert.Equal(4u, record.ComputeSystemRegisters!.Value.WorkGroupXRegister);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AStaticComputeRecordReplaysWithTheDefaultSpecialization(bool preserveFloat32)
    {
        var program = Program(AgcHeaderBuilder.Build(ComputeType, ProgramCode.Length, shaderRegisters: [(0x207, 64), (0x213, 4u << 1)]));
        Assert.True(StaticStageInputs.TryCompute(program, computeWave64Supported: false, out var record));

        var host = new FakePipelineHost(new ReplayCpuMemory([]))
        {
            ShaderSignedZeroInfNanPreserveFloat32Supported = preserveFloat32,
        };
        Assert.True(ShaderProgramCache.TryReplay(
            record, program.Code, new FakeShaderCompiler(static _ => new byte[] { 1, 2, 3, 4 }), ShaderCompileHostFlags.From(host),
            out var compiled, out var info, out var error), error);
        Assert.Equal(preserveFloat32, Assert.IsType<FakeCompiledShader>(compiled).Request.ShaderSignedZeroInfNanPreserveFloat32Supported);
        Assert.Single(info!.Buffers);
    }

    [Fact]
    public void TheScannerFindsCodeBeforeEachHeaderAndPairsAdjacentStages()
    {
        var game = Path.Combine(_directory, "game");
        Directory.CreateDirectory(game);
        var code = ProgramCode;
        var bundle = new MemoryStream();
        foreach (var (type, semantics) in new[] { (ComputeType, 0u), (GeometryType, 1u), (PixelType, 2u) })
        {
            bundle.Write(code);
            bundle.Write(AgcHeaderBuilder.Build(type, code.Length, inputSemantics: type == PixelType ? [semantics] : [], outputSemantics: type == GeometryType ? [semantics] : []));
        }

        File.WriteAllBytes(Path.Combine(game, "shaders.csdr"), bundle.ToArray());
        File.WriteAllBytes(Path.Combine(game, "movie.mp4"), bundle.ToArray());
        using (var file = Open())
        {
            var progress = new List<(long Done, long Total)>();
            var result = GameShaderScanner.Scan(game, file, (done, total) => progress.Add((done, total)), CancellationToken.None);
            Assert.Equal((0L, bundle.Length), progress[0]);
            Assert.Equal((bundle.Length, bundle.Length), progress[^1]);
            Assert.Equal(1, result.FilesScanned);
            Assert.Equal(3, result.Programs);
            Assert.Equal(3, file.Programs.Count);
            var (vertex, pixel) = Assert.Single(file.Pairs);
            Assert.True(file.TryGetProgram(vertex, out var vertexProgram));
            Assert.True(file.TryGetProgram(pixel, out var pixelProgram));
            Assert.True(StaticStageInputs.TryParse(vertexProgram, out var vertexHeader) && vertexHeader.Type == AgcShaderType.Geometry);
            Assert.True(StaticStageInputs.TryParse(pixelProgram, out var pixelHeader) && pixelHeader.Type == AgcShaderType.Pixel);
        }

        using var reopened = Open();
        var again = GameShaderScanner.Scan(game, reopened, null, CancellationToken.None);
        Assert.Equal(0, again.FilesScanned);
        Assert.Equal(1, again.FilesSkipped);
        Assert.Equal(3, reopened.Programs.Count);
    }

    [Fact]
    public void InventoryPairsScansAndDoneMarkersSurviveAReopen()
    {
        var compute = Program(AgcHeaderBuilder.Build(ComputeType, ProgramCode.Length, shaderRegisters: [(0x207, 64)]));
        using (var file = Open())
        {
            Assert.Equal(compute.Identity, file.RecordProgram(compute.Program, compute.Code));
            Assert.Null(file.RecordProgram(compute.Program, compute.Code));
            file.RecordPair(1, 2);
            file.RecordPair(1, 2);
            file.RecordScannedFile(new ScannedFile("a/b.csdr", 10, 20, 1, 1));
            file.RecordScannedFile(new ScannedFile("a/b.csdr", 11, 21, 2, 1));
            file.AddDone(7, 9);
        }

        using var reopened = Open();
        Assert.True(reopened.TryGetProgram(compute.Identity, out var program));
        Assert.Equal(compute.Program.Header, program.Program.Header);
        Assert.Equal([(1ul, 2ul)], reopened.Pairs);
        Assert.True(reopened.TryGetScannedFile("a/b.csdr", out var scanned));
        Assert.Equal(new ScannedFile("a/b.csdr", 11, 21, 2, 1), scanned);
        Assert.True(reopened.IsDone(7, 9));
        Assert.False(reopened.IsDone(7, 10));
    }

    [Fact]
    public void ASnapshotReadsUpToItsLengthAndNeverWrites()
    {
        long length;
        string path;
        using (var file = Open())
        {
            file.AddDone(1, 5);
            length = file.Length;
            file.AddDone(2, 5);
            path = file.Path;
        }

        using (var snapshot = ShaderCacheFile.OpenSnapshot(path, length)!)
        {
            Assert.True(snapshot.IsDone(1, 5));
            Assert.False(snapshot.IsDone(2, 5));
            snapshot.AddDone(3, 5);
            Assert.False(snapshot.IsDone(3, 5));
        }

        using var reopened = Open();
        Assert.True(reopened.IsDone(2, 5));
        Assert.False(reopened.IsDone(3, 5));
    }

    [Fact]
    public void LoadingReportsIndexedBytesAndCancellationDoesNotDamageTheCache()
    {
        string path;
        using (var file = Open())
        {
            path = file.Path;
            for (var index = 0ul; index < 256; index++) file.AddDone(index, 5);
        }
        var original = File.ReadAllBytes(path);
        Assert.Throws<OperationCanceledException>(() => ShaderCacheFile.Open(_directory,
            (_, _) => throw new OperationCanceledException()));
        Assert.Equal(original, File.ReadAllBytes(path));

        var progress = new List<(long Done, long Total)>();
        using var reopened = ShaderCacheFile.Open(_directory, (done, total) => progress.Add((done, total)))!;
        Assert.True(progress.Count >= 4);
        Assert.Equal((0L, (long)original.Length), progress[0]);
        Assert.Equal((reopened.Length, reopened.Length), progress[^1]);
        Assert.True(reopened.IsDone(255, 5));
    }

    [Fact]
    public void AWorkerPartMergesItsStagesBinariesAndDoneMarkers()
    {
        var stage = new CompiledStageRecord { Spirv = [1, 2], Bindings = [], VertexFetchComponents = [], PushDataEnd = 6 };
        var partPath = Path.Combine(_directory, "worker.part");
        Directory.CreateDirectory(_directory);
        using (var part = ShaderCacheFile.OpenPart(partPath)!)
        {
            part.AddCompiledStage(3, 4, stage);
            part.AddBinary([9], 42, [new PipelineBinaryPart([1], [7, 7])]);
            part.AddDone(5, 6);
        }

        using (var file = Open())
        {
            Assert.Equal(3, file.MergePart(partPath));
            Assert.Equal(0, file.MergePart(partPath));
        }

        using var reopened = Open();
        Assert.True(reopened.TryGetCompiledStage(3, 4, out var merged));
        Assert.Equal(6u, merged.PushDataEnd);
        Assert.True(reopened.HasBinary([9], 42));
        Assert.True(reopened.TryReadBinary([9], 42, out var parts));
        Assert.Equal(new byte[] { 7, 7 }, Assert.Single(parts).Data);
        Assert.True(reopened.IsDone(5, 6));
    }

    [Fact]
    public void ADamagedBinaryIsNotHandedOut()
    {
        string path;
        using (var file = Open())
        {
            file.AddBinary([9], 42, [new PipelineBinaryPart([1], [7, 7, 7, 7])]);
            path = file.Path;
        }

        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);
        using var reopened = Open();
        Assert.True(reopened.HasBinary([9], 42));
        Assert.False(reopened.TryReadBinary([9], 42, out _));
    }

    [Fact]
    public void APixelExportTargetSurvivesTheStageRecord()
    {
        var record = new StageRecord
        {
            Stage = ShaderStage.Pixel,
            Hash = 1,
            CodeSize = 12,
            Address = 0x1000,
            UserDataBase = 0,
            UserDataCount = 0,
            PushDataCursor = 0,
            Pixel = new PixelStageInputs(0, [new Gen5PixelOutputBinding(2, 0, Gen5PixelOutputKind.Float) { ExportTarget = 0 }], 2, 0, 2, [0]),
        };

        var payload = ShaderCacheSerializer.Serialize(writer => ShaderCacheSerializer.WriteStage(writer, record));
        var read = ShaderCacheSerializer.Deserialize(payload, ShaderCacheSerializer.ReadStage);
        var output = Assert.Single(read.Pixel!.Outputs);
        Assert.Equal(2u, output.GuestSlot);
        Assert.Equal(0u, output.ExportTarget);
    }

    [Fact]
    public void AShaderSeedKeepsStatesAndPairsWithoutShaderCode()
    {
        var state = ShaderSeed.CreateState(
            new PipelineRenderingState { ColorCount = 1 },
            new PipelineStaticParameters { ColorCount = 1 },
            [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
            default);
        var seedPath = Path.Combine(_directory, "shader_seeds", "PPSA00001.seed");
        var pixel = Program(AgcHeaderBuilder.Build(PixelType, ProgramCode.Length, contextRegisters: [(0x1C5, 4)]));
        Assert.True(StaticStageInputs.TryParse(pixel, out var pixelHeader));
        var empty = new ShaderSeed();
        Assert.True(empty.IsEmpty);

        using (var file = Open())
        {
            file.RecordPair(11, 22);
            var learned = ShaderSeed.Learn(file);
            Assert.Equal(new[] { (11ul, 22ul) }, learned.Pairs);
            Assert.True(learned.TrySave(seedPath));
        }

        var loaded = ShaderSeed.Load(seedPath);
        Assert.Equal(new[] { (11ul, 22ul) }, loaded.Pairs);
        Assert.Empty(loaded.Palette(StaticStageInputs.PixelSignature(pixelHeader), 8));
        Assert.NotEqual(0ul, state.Identity);
        Assert.Equal(StaticStageInputs.PixelSignature(pixelHeader), StaticStageInputs.PixelSignature(pixelHeader));
    }

    [Fact]
    public void PortableWordsCarryTheStrideAndFormatAndDestinationSelect()
    {
        var descriptor = PipelineTestGuest.BufferDescriptor(0x1000, 48, 4, BufferDescriptorWords.Format32x4UInt);
        var word = PortableBufferWord.Pack(descriptor);
        Assert.Equal(48u, word.Stride);
        Assert.Equal(BufferDescriptorWords.Format32x4UInt, (word.DescriptorWord3 >> 12) & 0x7F);
        Assert.Equal(descriptor[3] & 0xFFF, word.DescriptorWord3 & 0xFFF);
        descriptor[3] |= 1u << 30;
        Assert.Equal((0u, 0u), PortableBufferWord.Pack(descriptor));
    }

    [Fact]
    public void APortableLayoutPlacesTwoWordsPerBufferAfterTheMemoryOffsets()
    {
        var info = new ShaderResourceInfo();
        for (var index = 0; index < 5; index++)
        {
            info.Buffers.Add(new BufferResource { Read = true });
        }

        var plain = BindingLayout.Allocate(info, [], false, false, false, usesDispatchThreadLimits: true);
        var portable = BindingLayout.Allocate(info, [], false, false, false, usesDispatchThreadLimits: true, portableBuffers: true);

        Assert.Equal(0u, plain.BufferWordCount);
        Assert.Equal(10u, portable.BufferWordCount);
        Assert.Equal(plain.DispatchThreadLimitsDword, portable.BufferWordDword);
        Assert.Equal(portable.BufferWordDword + 10, portable.DispatchThreadLimitsDword);
        Assert.Equal(plain.ShaderDataDwordCount + 10, portable.ShaderDataDwordCount);
        Assert.NotEqual(plain, portable);
    }
}
