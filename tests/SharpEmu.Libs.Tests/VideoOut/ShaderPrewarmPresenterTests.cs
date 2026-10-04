// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed class ShaderPrewarmPresenterTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>, IDisposable
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

    private static byte[] Compile(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    [Fact]
    public void APrewarmRecordBuildsTheDevicePipeline()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        byte[] runtime;
        using (var list = ShaderPrewarmList.Open(_directory)!)
        {
            var guest = new PipelineTestGuest(Compile);
            guest.Host.ShaderPrewarm = list;
            guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
            var cursor = 0u;
            guest.Programs.GetOrCompile(
                guest.Source(CodeAddress, ShaderStage.Compute,
                    PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, BufferDescriptorWords.Format32UInt)),
                PipelineTestGuest.ComputeOptions(threadsX: 64), ref cursor, out _);
            runtime = Assert.Single(guest.Compiler.Shaders).Spirv;
        }

        using var reloaded = ShaderPrewarmList.Open(_directory)!;
        var (record, code) = Assert.Single(reloaded.LoadedComputes());
        using var presenter = new PresenterUnderTest(vulkan);
        var prewarmed = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
        presenter.SetField("_prewarmedShaderIdentities", prewarmed);

        presenter.InvokeMethod("PrewarmComputePipeline", record, code, new FakeShaderCompiler(Compile), presenter.Instance);

        Assert.Equal(0, presenter.GetField<int>("_shaderPrewarmFailed"));
        Assert.Equal(1, presenter.GetField<int>("_shaderPrewarmCompiled"));
        Assert.True(prewarmed.ContainsKey(SharpEmu.Libs.VideoOut.VulkanPipelineCacheStorage.CompiledShaderIdentity(runtime)));
        vulkan.AssertNoValidationMessages();
    }
}
