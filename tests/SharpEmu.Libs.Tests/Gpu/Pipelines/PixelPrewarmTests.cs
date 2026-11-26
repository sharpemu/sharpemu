// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// Pixel programs recorded in the prewarm list beside the compute ones, and translated again from the record.
public sealed class PixelPrewarmTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong BufferAddress = PipelineTestGuest.MemoryBase + 0x4_0000;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static StageCompileOptions PixelOptions(bool ieeeMode) => new()
    {
        PixelInfo = new PixelInputInfo { InputCount = 1, IeeeMode = ieeeMode },
        PixelOutputs = [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)],
        PixelInputEnable = 0x2,
        PixelInputAddress = 0x2,
    };

    [Fact]
    public void APixelRecordRoundTripsWithEveryField()
    {
        var code = new ShaderCodeCapture
        {
            Hash = 0x1234_5678_9ABC_DEF0, CodeSize = 12, Address = CodeAddress, Generation = Generation.Gen5,
            Ranges = [new CodeRange(CodeAddress, [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12])],
        };
        var info = new PixelInputInfo
        {
            InputCount = 3,
            SystemInputBase = 5,
            CustomInterpolationMask = 0x6,
            PerspectiveCenterRegister = 2,
            InterpolatorSettings = Enumerable.Range(0, PixelInputInfo.InterpolatorCount).Select(index => (uint)(index * 7 + 1)).ToArray(),
            TargetOutputModes = [1, 2, 3, 4, 5, 6, 7, 8],
            TargetExportFormats = [8, 7, 6, 5, 4, 3, 2, 1],
            TargetExportMappings = Enumerable.Range(0, PixelInputInfo.TargetCount).Select(index => new ColorComponentMap((byte)(0xE4 ^ index))).ToArray(),
            IeeeMode = true,
            ScratchDwords = 9,
            PositionX = true,
            PositionW = true,
            FrontFace = true,
            KillEnable = true,
            DepthExportEnable = true,
            SampleShading = true,
            EarlyDepth = true,
            ExecuteOnNoop = true,
        };
        var record = new PixelPrewarmRecord
        {
            Hash = code.Hash,
            CodeSize = code.CodeSize,
            Address = code.Address,
            UserDataBase = 4,
            UserDataCount = 16,
            PushDataCursor = 3,
            Info = info,
            Outputs =
            [
                new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float),
                new Gen5PixelOutputBinding(1, 2, Gen5PixelOutputKind.Uint, new Gen5ColorComponentMapping(0x1B))
                {
                    DualSourceAlpha = true, ExportTarget = 3, Index = 1, ExportFormat = Gen5PixelExportFormat.Uint16,
                },
            ],
            InputEnable = 0x22,
            InputAddress = 0x23,
            Specialization = new ResourceSpecialization { BaseBufferCount = 1, Buffers = [new BufferSpecialization(4, 0xFAC)] },
        };

        using (var list = Open())
        {
            list.RecordPixel(code, record);
            list.RecordPixel(code, record);
        }

        using var reloaded = Open();
        Assert.Equal(0, reloaded.LoadedComputeCount);
        var (loaded, loadedCode, _) = Assert.IsType<PixelPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.Equal(ShaderPrewarmList.Identity(record), ShaderPrewarmList.Identity(loaded));
        Assert.Equal(code.Ranges[0].Bytes, Assert.Single(loadedCode.Ranges).Bytes);
        Assert.Equal(info.InterpolatorSettings, loaded.Info.InterpolatorSettings);
        Assert.True(loaded.Info.IeeeMode && loaded.Info.ExecuteOnNoop && !loaded.Info.PositionY);
        Assert.Equal(new uint[] { 0, 3 }, loaded.Outputs.Select(output => output.ExportTarget));
        Assert.Equal(Gen5PixelExportFormat.Uint16, loaded.Outputs[1].ExportFormat);
        Assert.Equal((byte)0x1B, loaded.Outputs[1].ComponentMapping.Packed);
        Assert.Equal(new BufferSpecialization(4, 0xFAC), Assert.Single(loaded.Specialization.Buffers));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARecordedPixelProgramTranslatesToTheSameSpirvAfterReload(bool ieeeMode)
    {
        byte[] runtime;
        IShaderPipelineHost host;
        using (var list = Open())
        {
            var guest = new PipelineTestGuest(Compile);
            guest.Host.ShaderPrewarm = list;
            guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
            var userData = PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, BufferDescriptorWords.Format32UInt);
            var cursor = 0u;
            guest.Programs.GetOrCompile(guest.Source(CodeAddress, ShaderStage.Pixel, userData), PixelOptions(ieeeMode), ref cursor, out _);
            host = guest.Host;
            runtime = Assert.Single(guest.Compiler.Shaders).Spirv;
        }

        using var reloaded = Open();
        Assert.Equal(0, reloaded.LoadedComputeCount);
        var (record, code, _) = Assert.IsType<PixelPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
        Assert.Equal(ieeeMode, record.Info.IeeeMode);
        Assert.NotEmpty(record.Specialization.Buffers);
        var compiler = new FakeShaderCompiler(Compile);
        Assert.True(ShaderProgramCache.TryCompilePixelPrewarm(record, code, compiler, host, out var compiled, out var layout, out var error), error);
        Assert.NotNull(layout);
        Assert.Equal(runtime, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
    }
}
