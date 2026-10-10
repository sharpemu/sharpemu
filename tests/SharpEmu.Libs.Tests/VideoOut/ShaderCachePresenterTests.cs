// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.ShaderCache;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using System.Collections.Concurrent;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ShaderCachePresenterTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>, IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong BufferAddress = PipelineTestGuest.MemoryBase + 0x4_0000;
    private const ulong Stamp = 42;

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

    private PipelineTestGuest RecordCompute()
    {
        using var file = ShaderCacheFile.Open(_directory)!;
        var guest = new PipelineTestGuest(Compile);
        guest.Host.ShaderCache = file;
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
        var cursor = 0u;
        guest.Programs.GetOrCompile(
            guest.Source(CodeAddress, ShaderStage.Compute,
                PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, BufferDescriptorWords.Format32UInt)),
            PipelineTestGuest.ComputeOptions(threadsX: 64), ref cursor, out _);
        return guest;
    }

    private static PresenterUnderTest Presenter(HeadlessVulkan vulkan, ShaderCacheFile file, PipelineTestGuest guest)
    {
        var presenter = new PresenterUnderTest(vulkan);
        presenter.SetField("_shaderCacheStamp", Stamp);
        presenter.SetField("_shaderCacheFlags", ShaderCompileHostFlags.From(guest.Host));
        presenter.InvokeMethod("UseShaderCacheStores", file, new[] { file });
        return presenter;
    }

    [Fact]
    public async Task AFastComputeBuildIsReplacedByACompletedNativeOptimization()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        var guest = RecordCompute();
        using var file = ShaderCacheFile.Open(_directory)!;
        var item = Assert.Single(file.Computes);
        using var presenter = Presenter(vulkan!, file, guest);
        presenter.SetField("_physicalDeviceVendorId", 0x10DEu);
        presenter.SetField("_fastPipelineBuild", true);
        Assert.True(ShaderProgramCache.TryReplay(item.Record, item.Code, guest.Compiler, ShaderCompileHostFlags.From(guest.Host),
            out var compiled, out var info, out var error), error);
        presenter.Run(() =>
        {
            var host = (IShaderPipelineHost)presenter.Instance;
            var module = host.CreateShaderModule(compiled!, ShaderStage.Compute, item.Record.Hash, 1);
            host.CreateComputePipeline(new ComputePipelineDescription
            {
                Input = item.Record.Compute!,
                Program = new ShaderProgram(1, module),
                Stage = info!,
            });
        });

        var pending = Assert.Single(presenter.GetField<ConcurrentDictionary<ulong, Task<Pipeline>>>("_pipelineOptimizations"));
        var optimized = await pending.Value.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(0ul, optimized.Handle);
        Assert.NotEqual(pending.Key, optimized.Handle);
        Assert.Equal(optimized.Handle, ((Pipeline)presenter.InvokeMethod("ResolveOptimizedPipeline", new Pipeline(pending.Key))!).Handle);
        vulkan!.AssertNoValidationMessages();
    }

    [Fact]
    public void ARecordedComputeIsPrecompiledWithoutGrowingTheFile()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        var guest = RecordCompute();
        using var file = ShaderCacheFile.Open(_directory)!;
        var item = Assert.Single(file.Computes);
        var lengthBefore = file.Length;
        using var presenter = Presenter(vulkan!, file, guest);

        Assert.Equal(ShaderCacheOutcome.Compiled, presenter.InvokeMethod("CompileComputeItem", item.Record, item.Code));
        Assert.Equal(ShaderCacheOutcome.Compiled, presenter.InvokeMethod("CompileComputeItem", item.Record, item.Code));
        Assert.Equal(lengthBefore, file.Length);
        presenter.InvokeMethod("StopShaderCache");
        vulkan!.AssertNoValidationMessages();
    }

    [Fact]
    public void AStaticComputeFromItsHeaderCompilesOnTheDevice()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        var guest = RecordCompute();
        using var file = ShaderCacheFile.Open(_directory)!;
        var code = AgcHeaderBuilder.Code(PipelineTestGuest.FormatLoadProgram);
        Assert.True(ShaderInventory.TryCreateCapture(code, (uint)code.Length, GameShaderScanner.ScannedCodeAddress, out var capture));
        var header = AgcHeaderBuilder.Build(0, code.Length, shaderRegisters: [(0x207, 64), (0x213, 4u << 1)]);
        var program = new InventoryProgram { Code = capture.Key, Source = InventorySource.Scanned, Header = header };
        var identity = file.RecordProgram(program, capture)!.Value;
        Assert.True(file.TryGetProgram(identity, out var cached));
        Assert.True(StaticStageInputs.TryCompute(cached, computeWave64Supported: false, out var record));
        using var presenter = Presenter(vulkan!, file, guest);

        Assert.Equal(ShaderCacheOutcome.Compiled, presenter.InvokeMethod("CompileComputeItem", record, capture));
        vulkan!.AssertNoValidationMessages();
    }
}
