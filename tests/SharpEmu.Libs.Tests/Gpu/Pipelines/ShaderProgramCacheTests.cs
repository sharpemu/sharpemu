// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Program identity is the content hash or the declared hash; entries key on static state, permutations on specialization.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderProgramCacheTests : IDisposable
{
    [Theory]
    [InlineData(1u, 4)]
    [InlineData(2u, 8)]
    [InlineData(0x10u, 4)]
    [InlineData(0x20u, 8)]
    public void CustomSampleOffsetsAreUsedOnlyForSampleInterpolation(uint activeInput, int expectedCount)
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        var shaderInterface = new SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderInterfaceRegisters
        {
            PixelInputEnable = activeInput,
            PixelInputAddress = activeInput,
        };
        var offsets = Enumerable.Repeat((X: -.25f, Y: 0f), expectedCount).ToArray();
        var info = PixelStageInputResolver.Resolve(_guest.Context, source.Registered, shaderInterface,
            new byte[8], new SharpEmu.Libs.Gpu.Images.ColorComponentMap[8], 0,
            rasterizationSamples: 2, customSampleOffsets: offsets);
        Assert.Equal((activeInput & 0x11u) != 0 ? expectedCount : 0, info.CustomSampleOffsets.Count);
    }

    [Fact]
    public void CustomSamplePositionsReachCompilerAndSeparateCachedPrograms()
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        ShaderProgram Compile(float x)
        {
            var cursor = 0u;
            return _guest.Programs.GetOrCompile(source,
                new StageCompileOptions { PixelInfo = new PixelInputInfo
                    { InterpolationSample = 0, CustomSampleOffsets = Enumerable.Repeat((X: x, Y: 0f), 4).ToArray() } },
                ref cursor, out _);
        }
        var left = Compile(-.25f);
        var right = Compile(.25f);
        Assert.NotEqual(left, right);
        Assert.Equal(left, Compile(-.25f));
        Assert.Equal(-.25f, _guest.Compiler.Requests[0].PixelCustomSampleOffsets[0].X);
        Assert.Equal(.25f, _guest.Compiler.Requests[1].PixelCustomSampleOffsets[0].X);
    }
    [Theory]
    [InlineData(0u, 0u)]
    [InlineData(1u, 0xFFFEu)]
    [InlineData(2u, 0u)]
    [InlineData(3u, 0u)]
    public void SampleExclusionIsInactiveOutsideEarlyDepthOrder(uint order, uint expectedMask)
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        var shaderInterface = new SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderInterfaceRegisters
        {
            DepthShaderControl = SharpEmu.Libs.Gpu.GpuCommands.Registers.DepthShaderControlRegisters.Decode(order << 4),
        };
        var info = PixelStageInputResolver.Resolve(_guest.Context, source.Registered, shaderInterface,
            new byte[8], new SharpEmu.Libs.Gpu.Images.ColorComponentMap[8], 0,
            rasterizationSamples: 2, pixelShaderIterationSamples: 1, shaderSampleExclusionMask: 0xABCDFFFE);
        Assert.Equal(expectedMask, info.ShaderSampleExclusionMask);
    }

    [Theory]
    [InlineData(0x110u, true)]
    [InlineData(0x50u, false)]
    public void ShaderCoverageChangesUseLateDepthWithoutForcedDepthBeforeShader(uint raw, bool maskExport)
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        var shaderInterface = new SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderInterfaceRegisters
        {
            DepthShaderControl = SharpEmu.Libs.Gpu.GpuCommands.Registers.DepthShaderControlRegisters.Decode(raw),
        };
        var info = PixelStageInputResolver.Resolve(_guest.Context, source.Registered, shaderInterface,
            new byte[8], new SharpEmu.Libs.Gpu.Images.ColorComponentMap[8], 0,
            maskExportSamples: 2, rasterizationSamples: 2, pixelShaderIterationSamples: 2,
            shaderSampleExclusionMask: 0xFFFE);
        Assert.False(info.EarlyDepth);
        Assert.Equal(maskExport, info.SampleMaskExportEnable);
        Assert.Equal(2u, info.MaskExportSamples);
        Assert.Equal(0u, info.ShaderSampleExclusionMask);
    }
    [Theory]
    [InlineData(0x11u, false, true)]
    [InlineData(0x51u, false, true)]
    [InlineData(0x111u, false, true)]
    [InlineData(0x151u, false, true)]
    [InlineData(0x51u, true, false)]
    [InlineData(0x50u, true, false)]
    [InlineData(0x1011u, false, false)]
    [InlineData(0x211u, false, false)]
    [InlineData(0x411u, false, false)]
    [InlineData(0x8011u, false, false)]
    public void DepthExportsUseLateFallbackOnlyWhenGuestOrderingIsNotForced(uint raw, bool forced, bool accepted)
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        var shaderInterface = new SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderInterfaceRegisters {
            DepthShaderControl = SharpEmu.Libs.Gpu.GpuCommands.Registers.DepthShaderControlRegisters.Decode(raw) };
        PixelInputInfo Resolve() => PixelStageInputResolver.Resolve(_guest.Context, source.Registered, shaderInterface,
            new byte[8], new SharpEmu.Libs.Gpu.Images.ColorComponentMap[8], 0,
            rasterizationSamples: 2, pixelShaderIterationSamples: 1, shaderSampleExclusionMask: 0xFFFE,
            forceShaderDepthOrder: forced);
        if (!accepted)
        {
            Assert.Throws<SchedulerFatalException>(() => Resolve());
            return;
        }
        var info = Resolve();
        Assert.False(info.EarlyDepth);
        Assert.True(info.DepthExportEnable);
        Assert.Equal((raw & 0x40) != 0, info.KillEnable);
        Assert.Equal((raw & 0x100) != 0, info.SampleMaskExportEnable);
        Assert.Equal(0u, info.ShaderSampleExclusionMask);
        var cursor = 0u;
        _guest.Programs.GetOrCompile(source, new StageCompileOptions { PixelInfo = info }, ref cursor, out _);
        var request = Assert.Single(_guest.Compiler.Requests);
        Assert.True(request.PixelDepthExportEnable);
        Assert.False(request.EarlyFragmentTests);
        Assert.Equal(info.SampleMaskExportEnable, request.PixelSampleMaskExportEnable);
    }

    [Fact]
    public void SampleExclusionRequiresSupportAndSeparatesEarlyPixelPrograms()
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        ShaderProgram Compile(uint mask)
        {
            var cursor = 0u;
            return _guest.Programs.GetOrCompile(source,
                new StageCompileOptions { PixelInfo = new PixelInputInfo
                { EarlyDepth = true, ShaderSampleExclusionMask = mask } }, ref cursor, out _);
        }
        Assert.Throws<SchedulerFatalException>(() => Compile(0xFFFE));
        _guest.Host.PostDepthCoverageSupported = true;
        var enabled = Compile(0xFFFE);
        Assert.NotEqual(enabled, Compile(0));
        Assert.Equal(enabled, Compile(0xFFFE));
        Assert.Contains(_guest.Compiler.Requests, request => request.PixelShaderSampleExclusionMask == 0xFFFE);
    }

    private const ulong CodeA = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong CodeB = PipelineTestGuest.MemoryBase + 0x2000;
    private const ulong HeaderA = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong HeaderB = PipelineTestGuest.MemoryBase + 0x8100;
    private const ulong DataBase = PipelineTestGuest.MemoryBase + 0x4_0000;
    private const uint Format32x4Uint = 75;
    private const uint Format32x4Float = 77;

    [Fact]
    public void TessellationRuntimeData_UsesOnePermutationAcrossDifferentPushCursors()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var source = _guest.Source(CodeA, ShaderStage.TessellationEvaluation, []);
        var options = new StageCompileOptions
        {
            VertexInfo = new VertexInputInfo(),
            Tessellation = new(Gen5TessellationDomain.Quads, Gen5TessellationSpacing.Equal, false, false),
        };
        var cursor = 0u;
        var first = _guest.Programs.GetOrCompile(source, options, ref cursor, out var firstStage);
        cursor = 9;
        var second = _guest.Programs.GetOrCompile(source, options, ref cursor, out var secondStage);
        Assert.Equal(first, second);
        Assert.Same(firstStage.Program, secondStage.Program);
        Assert.Single(_guest.Compiler.Requests);
        Assert.Single(Assert.Single(_guest.Programs.Entries).Permutations);
        Assert.True(firstStage.Program!.Bindings!.UsesTessellationData);
        Assert.False(firstStage.Program.Bindings.UsesPushData);
        Assert.Equal(9u, cursor);
    }

    [Fact]
    public void EarlyDepthStateReachesCompilerAndSeparatesCachedPrograms()
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        ShaderProgram CompilePixel(bool early)
        {
            var cursor = 0u;
            return _guest.Programs.GetOrCompile(source,
                new StageCompileOptions { PixelInfo = new PixelInputInfo { EarlyDepth = early } },
                ref cursor, out _);
        }
        var late = CompilePixel(false);
        var early = CompilePixel(true);
        Assert.NotEqual(late, early);
        Assert.Equal(late, CompilePixel(false));
        Assert.Equal(new[] { false, true }, _guest.Compiler.Requests.Select(request => request.EarlyFragmentTests));
    }

    [Fact]
    public void DepthExportEnableReachesCompilerAndSeparatesCachedPrograms()
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        ShaderProgram CompilePixel(bool depthExport)
        {
            var cursor = 0u;
            return _guest.Programs.GetOrCompile(source,
                new StageCompileOptions { PixelInfo = new PixelInputInfo { DepthExportEnable = depthExport } },
                ref cursor, out _);
        }
        var disabled = CompilePixel(false);
        var enabled = CompilePixel(true);
        Assert.NotEqual(disabled, enabled);
        Assert.Equal(enabled, CompilePixel(true));
        Assert.Equal(new[] { false, true }, _guest.Compiler.Requests.Select(request => request.PixelDepthExportEnable));
    }

    [Theory]
    [InlineData(1u, 1u, 2u, false, 0u)]
    [InlineData(16u, 1u, 2u, false, 0u)]
    [InlineData(1u, 2u, 2u, true, uint.MaxValue)]
    [InlineData(2u, 2u, 2u, true, uint.MaxValue)]
    [InlineData(2u, 1u, 2u, false, uint.MaxValue)]
    public void PixelIterationCountIsIndependentOfSampleQualifiedInputs(uint inputs, uint iterations,
        uint raster, bool sampleShading, uint fixedSample)
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        var shaderInterface = new SharpEmu.Libs.Gpu.GpuCommands.Registers.ShaderInterfaceRegisters
        { PixelInputEnable = inputs, PixelInputAddress = inputs };
        var info = PixelStageInputResolver.Resolve(_guest.Context, source.Registered, shaderInterface,
            new byte[8], new SharpEmu.Libs.Gpu.Images.ColorComponentMap[8], 0,
            rasterizationSamples: raster, pixelShaderIterationSamples: iterations);
        Assert.Equal(sampleShading, info.SampleShading);
        Assert.Equal(fixedSample == uint.MaxValue ? null : (uint?)fixedSample, info.InterpolationSample);
        var cursor = 0u;
        _guest.Programs.GetOrCompile(source, new StageCompileOptions { PixelInfo = info }, ref cursor, out _);
        Assert.Equal(info.InterpolationSample, Assert.Single(_guest.Compiler.Requests).PixelInterpolationSample);
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(4u, 2u)]
    [InlineData(2u, 4u)]
    public void UnverifiedPixelIterationMappingsAreRejected(uint iterations, uint raster)
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        var failure = Assert.Throws<SchedulerFatalException>(() => PixelStageInputResolver.Resolve(
            _guest.Context, source.Registered, new(), new byte[8],
            new SharpEmu.Libs.Gpu.Images.ColorComponentMap[8], 0,
            rasterizationSamples: raster, pixelShaderIterationSamples: iterations));
        Assert.Contains("pixel-shader iteration counts", failure.Message);
    }

    [Fact]
    public void ExplicitInterpolationSampleSeparatesCachedPixelPrograms()
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        ShaderProgram Compile(uint? sample)
        {
            var cursor = 0u;
            return _guest.Programs.GetOrCompile(source,
                new StageCompileOptions { PixelInfo = new PixelInputInfo { InterpolationSample = sample } }, ref cursor, out _);
        }
        var pixel = Compile(null); var fixedSample = Compile(0);
        Assert.NotEqual(pixel, fixedSample);
        Assert.Equal(fixedSample, Compile(0));
        Assert.Equal(new uint?[] { null, 0 }, _guest.Compiler.Requests.Select(request => request.PixelInterpolationSample));
    }

    [Fact]
    public void SampleMaskExportCountsReachCompilerAndSeparateCachedPrograms()
    {
        _guest.RegisterProgram(CodeA, HeaderA, [0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        ShaderProgram CompilePixel(uint exportSamples, uint rasterSamples)
        {
            var cursor = 0u;
            return _guest.Programs.GetOrCompile(source,
                new StageCompileOptions { PixelInfo = new PixelInputInfo
                {
                    SampleMaskExportEnable = true,
                    MaskExportSamples = exportSamples,
                    RasterizationSamples = rasterSamples,
                } }, ref cursor, out _);
        }
        var broadcast = CompilePixel(1, 2);
        var independent = CompilePixel(2, 2);
        var single = CompilePixel(1, 1);
        Assert.NotEqual(broadcast, independent);
        Assert.NotEqual(broadcast, single);
        Assert.Equal(independent, CompilePixel(2, 2));
        Assert.All(_guest.Compiler.Requests, request => Assert.True(request.PixelSampleMaskExportEnable));
        Assert.Equal(new[] { (1u, 2u), (2u, 2u), (1u, 1u) }, _guest.Compiler.Requests
            .Select(request => (request.PixelMaskExportSamples, request.PixelRasterizationSamples)));
    }

    [Fact]
    public void EmbeddedFetchIsReplacedBeforeTheProgramRequestsResourceTables()
    {
        _guest.RegisterProgram(CodeA, HeaderA,
            [0xF4080303, 0xFA000000, 0x02020B08, 0xE00C2000, 0x80030401, 0xBF810000]);
        var source = _guest.Source(CodeA, ShaderStage.Vertex, new uint[8]);
        var descriptor = PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Float);
        var options = new StageCompileOptions
        {
            VertexInfo = new VertexInputInfo
            {
                FetchEmbedded = true,
                FetchAttributeRegister = 4,
                FetchBufferRegister = 6,
                Attributes = [new VertexAttributeResource(new(descriptor[0], descriptor[1], descriptor[2], descriptor[3]), 4, 4, 0, 0, 0, 0)],
                Buffers = [new VertexInputBuffer(DataBase, 16, 4)],
            },
        };
        var cursor = 0u;
        _ = _guest.Programs.GetOrCompile(source, options, ref cursor, out var stage);
        var request = Assert.Single(_guest.Compiler.Requests);
        Assert.Single(request.VertexInputs);
        Assert.Empty(request.Memory.Entries);
        Assert.Empty(stage.Resources.FlattenedResourceTable);
        Assert.False(stage.Program!.UsesDeviceAddresses);
        Assert.Equal("SNop", request.Program.Instructions[0].Opcode);
    }

    private readonly FatalScope _fatal = new();
    private readonly PipelineTestGuest _guest = new();

    public void Dispose() => _fatal.Dispose();

    private ShaderProgram Compile(ulong code, uint[] userData, ref uint cursor, out ShaderStageResources stage, uint threadsX = 64) =>
        _guest.Programs.GetOrCompile(_guest.Source(code, ShaderStage.Compute, userData), PipelineTestGuest.ComputeOptions(threadsX), ref cursor, out stage);

    private ShaderProgram Compile(ulong code, uint[]? userData = null, uint threadsX = 64)
    {
        var cursor = 0u;
        return Compile(code, userData ?? [], ref cursor, out _, threadsX);
    }

    // The marker, the block offset, the code, padding, then the binary information block with its hash.
    private static uint[] DeclaredHashProgram(uint hash0, uint hash1) =>
    [
        ShaderBinaryInfo.MarkerWord, 1, 0xBF810000, 0,
        0, 0, 0, 0, hash0, hash1, 0,
    ];

    [Fact]
    public void UndeclaredHash_IsTheContentHash()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var registered = _guest.Registry.Require(CodeA, "compute");

        var hash = ShaderIdentity.Compute(_guest.Memory, CodeA, registered.CodeRanges, "compute");

        Assert.Equal(System.IO.Hashing.XxHash3.HashToUInt64([0x00, 0x00, 0x81, 0xBF]), hash);
        Assert.NotEqual(0UL, hash);
    }

    [Fact]
    public void DeclaredHash_IsTakenFromTheBinaryInfoBlock()
    {
        _guest.RegisterProgram(CodeA, HeaderA, DeclaredHashProgram(0xC0DE0001, 0xBEEF0002));

        Assert.True(ShaderIdentity.TryReadDeclaredHash(_guest.Memory, CodeA, out var declared));
        Assert.Equal(0xBEEF0002_C0DE0001UL, declared);
        var source = _guest.Source(CodeA, ShaderStage.Compute, []);
        Assert.Equal(0xBEEF0002_C0DE0001UL, source.Hash);
    }

    [Fact]
    public void ZeroDeclaredHash_UsesContentHash()
    {
        _guest.RegisterProgram(CodeA, HeaderA, DeclaredHashProgram(0, 0));
        _guest.RegisterProgram(CodeB, HeaderB, [.. DeclaredHashProgram(0, 0)[..10], 1]);

        var first = Compile(CodeA);
        var second = Compile(CodeB);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _guest.Programs.ProgramCount);
        Assert.NotEqual(_guest.Source(CodeA, ShaderStage.Compute, []).Hash, _guest.Source(CodeB, ShaderStage.Compute, []).Hash);
    }

    [Fact]
    public void RewriteAtTheSameAddress_CompilesASecondProgram()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var first = Compile(CodeA);

        _guest.RegisterProgram(CodeA, HeaderA, [0xBF800000, 0xBF810000]);
        var second = Compile(CodeA);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _guest.Programs.ProgramCount);
        Assert.Equal(2, _guest.Host.Modules.Count);
    }

    [Fact]
    public void SameCodeAtTwoAddresses_SharesOneEntry()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        _guest.RegisterProgram(CodeB, HeaderB, PipelineTestGuest.EndProgram);

        var first = Compile(CodeA);
        var second = Compile(CodeB);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, _guest.Programs.ProgramCount);
        Assert.Single(_guest.Host.Modules);
        Assert.Equal(1, _guest.Compiler.Compilations);
    }

    [Fact]
    public void StaticStateChange_IsANewEntryNotAPermutation()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);

        var first = Compile(CodeA, threadsX: 64);
        var second = Compile(CodeA, threadsX: 32);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, _guest.Programs.ProgramCount);
        Assert.All(_guest.Programs.Entries, entry => Assert.Single(entry.Permutations));
    }

    [Fact]
    public void PixelExportFormatChange_CompilesANewProgramForTheSameTargetKind()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var source = _guest.Source(CodeA, ShaderStage.Pixel, []);
        ShaderProgram CompileFormat(Gen5PixelExportFormat format)
        {
            var formats = new byte[PixelInputInfo.TargetCount];
            formats[0] = (byte)format;
            var options = new StageCompileOptions
            {
                PixelInfo = new PixelInputInfo { TargetExportFormats = formats },
                PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float) { ExportFormat = format }],
            };
            var cursor = 0u;
            return _guest.Programs.GetOrCompile(source, options, ref cursor, out _);
        }
        var half = CompileFormat(Gen5PixelExportFormat.Float16);
        var normalized = CompileFormat(Gen5PixelExportFormat.Unorm16);
        Assert.NotEqual(half.Id, normalized.Id);
        Assert.Equal(normalized.Id, CompileFormat(Gen5PixelExportFormat.Unorm16).Id);
        Assert.Equal(2, _guest.Compiler.Requests.Count);
        Assert.Equal(Gen5PixelExportFormat.Unorm16, _guest.Compiler.Requests[1].PixelOutputs[0].ExportFormat);
    }

    [Fact]
    public void SpecializationChange_IsANewPermutationOfTheSameEntry()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);

        var first = Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint));
        var same = Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase + 0x100, 16, 8, Format32x4Uint));
        var second = Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Float));

        Assert.Equal(first.Id, same.Id);
        Assert.NotEqual(first.Id, second.Id);
        var entry = Assert.Single(_guest.Programs.Entries);
        Assert.Equal(2, entry.Permutations.Count);
        Assert.Equal(2, _guest.Compiler.Compilations);
        Assert.Same(entry.Plan, entry.Plan);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(32u)]
    public void DispatchLimitsChangeWithoutRecompilingAndStayWithEachStage(uint startCursor)
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        var source = _guest.Source(CodeA, ShaderStage.Compute, []);
        ShaderStageResources Obtain(uint count, out ShaderProgram program)
        {
            var options = new StageCompileOptions
            {
                ComputeInfo = new ComputeInputInfo
                {
                    ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1, WaveSize = 32,
                    GroupIdX = true, ThreadIdCount = 1, DispatchThreadDimensions = true,
                    DispatchThreadsX = count, DispatchThreadsY = 1, DispatchThreadsZ = 1,
                },
            };
            var cursor = startCursor;
            program = _guest.Programs.GetOrCompile(source, options, ref cursor, out var stage);
            return stage;
        }

        var first = Obtain(100, out var firstProgram);
        var second = Obtain(4, out var secondProgram);
        Assert.Equal(firstProgram, secondProgram);
        Assert.Single(_guest.Compiler.Requests);
        Assert.Single(_guest.Host.Modules);
        var layout = first.Program!.Bindings!;
        Assert.True(layout.UsesDispatchThreadLimits);
        Assert.Equal(startCursor == 0, layout.UsesPushData);
        var firstData = new uint[layout.ShaderDataDwordCount];
        var secondData = new uint[layout.ShaderDataDwordCount];
        first.WriteDispatchThreadLimits(firstData);
        second.WriteDispatchThreadLimits(secondData);
        Assert.Equal(new uint[] { 100, 1, 1 }, firstData);
        Assert.Equal(new uint[] { 4, 1, 1 }, secondData);
        var missing = first with { ThreadLimits = null };
        Assert.Throws<SchedulerFatalException>(() => missing.WriteDispatchThreadLimits(firstData));
    }

    [Fact]
    public void UserDataChangesBetweenDraws_ChangeTheSnapshotWithoutRecompiling()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);
        var cursor = 0u;

        Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint), ref cursor, out var firstStage);
        cursor = 0;
        Compile(CodeA, PipelineTestGuest.BufferDescriptor(DataBase + 0x200, 16, 9, Format32x4Uint), ref cursor, out var secondStage);

        Assert.Equal(1, _guest.Compiler.Compilations);
        Assert.Equal(unchecked((uint)DataBase), firstStage.Resources.Buffers[0][0]);
        Assert.Equal(unchecked((uint)(DataBase + 0x200)), secondStage.Resources.Buffers[0][0]);
        Assert.Equal(9u, secondStage.Resources.Buffers[0][2]);
        Assert.Same(firstStage.Program, secondStage.Program);
    }

    [Fact]
    public void PushCursorChange_IsANewPermutation()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);
        var descriptor = PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint);
        var cursor = 0u;
        var first = Compile(CodeA, descriptor, ref cursor, out var firstStage);
        var firstEnd = cursor;

        cursor = 3;
        var second = Compile(CodeA, descriptor, ref cursor, out var secondStage);

        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(2, Assert.Single(_guest.Programs.Entries).Permutations.Count);
        Assert.Equal(0u, firstStage.Program!.Bindings!.PushDataStartDword);
        Assert.Equal(3u, secondStage.Program!.Bindings!.PushDataStartDword);
        Assert.Equal(firstStage.Program.Bindings.ShaderDataDwordCount, firstEnd);
        Assert.Equal(3u + secondStage.Program.Bindings.ShaderDataDwordCount, cursor);
    }

    [Fact]
    public void SamePushCursor_ReusesThePermutation()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.FormatLoadProgram);
        var descriptor = PipelineTestGuest.BufferDescriptor(DataBase, 16, 4, Format32x4Uint);
        var cursor = 2u;
        var first = Compile(CodeA, descriptor, ref cursor, out _);

        cursor = 2;
        var second = Compile(CodeA, descriptor, ref cursor, out _);

        Assert.Equal(first.Id, second.Id);
        Assert.Single(Assert.Single(_guest.Programs.Entries).Permutations);
    }

    [Fact]
    public void MissingHeader_IsFatalWithStageAndAddress()
    {
        var fatal = Assert.Throws<SchedulerFatalException>(() => _guest.Registry.Require(CodeA, "pixel"));

        Assert.Contains("label=pixel", fatal.Message);
        Assert.Contains($"shader=0x{CodeA:X16}", fatal.Message);
    }

    [Fact]
    public void ZeroOrUnalignedCodeSize_IsFatal()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        _guest.Write(HeaderA + 0x44, [0x02, 0x00, 0x00, 0x00]);

        var fatal = Assert.Throws<SchedulerFatalException>(() => _guest.Registry.Require(CodeA, "compute"));

        Assert.Contains("invalid code size", fatal.Message);
        Assert.Contains("size=0x00000002", fatal.Message);
    }

    [Fact]
    public void ProgramIds_AreMonotonicAcrossEntries()
    {
        _guest.RegisterProgram(CodeA, HeaderA, PipelineTestGuest.EndProgram);
        _guest.RegisterProgram(CodeB, HeaderB, [0xBF800000, 0xBF810000]);

        var first = Compile(CodeA);
        var second = Compile(CodeB);

        Assert.Equal(1UL, first.Id);
        Assert.Equal(2UL, second.Id);
        Assert.Equal([(ShaderStage.Compute, _guest.Source(CodeA, ShaderStage.Compute, []).Hash, 1UL), (ShaderStage.Compute, _guest.Source(CodeB, ShaderStage.Compute, []).Hash, 2UL)], _guest.Host.Modules);
    }
}
