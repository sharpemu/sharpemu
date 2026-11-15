// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
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
            guest.Host.TableBlocks = true;
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

        presenter.InvokeMethod("PrewarmComputePipeline", record, code, new FakeShaderCompiler(Compile), false, true);

        Assert.Equal(0, presenter.GetField<int>("_shaderPrewarmFailed"));
        Assert.Equal(1, presenter.GetField<int>("_shaderPrewarmCompiled"));
        Assert.True(prewarmed.ContainsKey(SharpEmu.Libs.VideoOut.VulkanPipelineCacheStorage.CompiledShaderIdentity(runtime)));
        // The pipeline waits for the session's request; teardown destroys it when none came.
        var handover = presenter.GetField<ICollection>("_prewarmedComputePipelines");
        Assert.Equal(1, handover.Count);
        presenter.InvokeMethod("DestroyPrewarmedComputePipelines");
        Assert.Equal(0, handover.Count);
        vulkan.AssertNoValidationMessages();
    }

    // The session's request for the module and layout the prewarm built takes the prewarm's pipeline, with no compile.
    [Fact]
    public void TheSessionTakesThePrewarmsPipeline()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;

        byte[] runtime;
        ShaderProgramInfo info;
        using (var list = ShaderPrewarmList.Open(_directory)!)
        {
            var guest = new PipelineTestGuest(Compile);
            guest.Host.ShaderPrewarm = list;
            guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.FormatLoadProgram);
            var cursor = 0u;
            guest.Programs.GetOrCompile(
                guest.Source(CodeAddress, ShaderStage.Compute,
                    PipelineTestGuest.BufferDescriptor(BufferAddress, 4, 64, BufferDescriptorWords.Format32UInt)),
                PipelineTestGuest.ComputeOptions(threadsX: 64), ref cursor, out var stage);
            runtime = Assert.Single(guest.Compiler.Shaders).Spirv;
            info = stage.Program!;
        }

        using var reloaded = ShaderPrewarmList.Open(_directory)!;
        var (record, code) = Assert.Single(reloaded.LoadedComputes());
        using var presenter = new PresenterUnderTest(vulkan);
        presenter.SetField("_prewarmedShaderIdentities", new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
        presenter.InvokeMethod("PrewarmComputePipeline", record, code, new FakeShaderCompiler(Compile), false, true);
        var handover = presenter.GetField<ICollection>("_prewarmedComputePipelines");
        Assert.Equal(1, handover.Count);

        var registered = presenter.Run(() =>
        {
            var host = (IShaderPipelineHost)presenter.Instance;
            var program = new ShaderProgram(1, host.CreateShaderModule(
                new SharpEmu.Libs.Gpu.Vulkan.VulkanCompiledGuestShader(runtime), ShaderStage.Compute, info.Hash, 1));
            return host.TryCreateComputePipeline(new ComputePipelineDescription
            {
                Program = program, Stage = info, Input = PipelineTestGuest.ComputeOptions(threadsX: 64).ComputeInfo!,
            }, out _);
        });

        // A compile would leave the request pending (false) until the next call.
        Assert.True(registered);
        Assert.Equal(0, handover.Count);
        Assert.Equal(1, presenter.GetField<int>("_prewarmedComputeTaken"));
        vulkan.AssertNoValidationMessages();
    }

    // A prewarm thread does not wait for the linked admission: while a large linked compile runs, another large one
    // is refused at once.
    [Fact]
    public void ThePrewarmsLinkedAdmissionDoesNotWait()
    {
        var presenter = RuntimeHelpers.GetUninitializedObject(PresenterUnderTest.PresenterType);
        var wait = PresenterUnderTest.PresenterType.GetMethod("WaitForLinkedAdmission", PresenterUnderTest.InstanceMembers)!;
        var admission = (SharpEmu.Libs.VideoOut.LinkedCompileAdmission)PresenterUnderTest.PresenterType
            .GetField("_linkedCompileAdmission", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        const long large = 8L << 20;
        var running = admission.TryAdmit(large, out _, out _);
        try
        {
            var answer = Task.Run(() => (bool)wait.Invoke(presenter, [1UL, large, false])!);
            Assert.True(answer.Wait(TimeSpan.FromSeconds(10)));
            Assert.False(answer.Result);
        }
        finally
        {
            if (running)
            {
                admission.Release(large);
            }
        }
    }

    [Fact]
    public void Completion_IsCertifiedOnlyByASuccessfulNonemptyCacheSave()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var list = ShaderPrewarmList.Open(_directory)!;
        using var presenter = new PresenterUnderTest(vulkan);
        presenter.SetField("_runtimeComputeIdentities", new ConcurrentDictionary<string, byte>());
        presenter.SetField("_prewarmedShaderIdentities", new ConcurrentDictionary<string, byte>());
        presenter.SetField("_pipelineCacheShardDirectory", _directory);
        var path = Path.Combine(_directory, "cache.bin");
        presenter.SetField("_pipelineCachePath", path);
        var cache = ((Lazy<PipelineCache>)presenter.InvokeMethod("GetGuestPipelineCacheSource", "test")!).Value;
        try
        {
            var stamp = list.CoverageStamp("build");
            presenter.InvokeMethod("FinishShaderPrewarm", list, stamp, Stopwatch.GetTimestamp());
            Assert.False(File.Exists(list.StampPath));

            Directory.CreateDirectory(path);
            Assert.False((bool)presenter.InvokeMethod("SaveDriverPipelineCache", cache, path, ulong.MaxValue, null)!);
            Assert.False(File.Exists(list.StampPath));
            Directory.Delete(path);

            Assert.True((bool)presenter.InvokeMethod("SaveDriverPipelineCache", cache, path, ulong.MaxValue, null)!);
            var signature = (string)presenter.InvokeMethod("DriverCacheSignature")!;
            Assert.True(PipelineCacheSignature.TryUnwrap(signature, File.ReadAllBytes(path), out var saved));
            Assert.True((bool)presenter.InvokeMethod("CanSkipShaderPrewarm", list, stamp, saved)!);

            Assert.True((bool)presenter.InvokeMethod("SaveDriverPipelineCache", cache, path, 1UL, null)!);
            Assert.True(PipelineCacheSignature.TryUnwrap(signature, File.ReadAllBytes(path), out var reset));
            Assert.Empty(reset);
            Assert.False((bool)presenter.InvokeMethod("CanSkipShaderPrewarm", list, stamp, reset)!);
            vulkan.AssertNoValidationMessages();
        }
        finally
        {
            presenter.InvokeMethod("DestroyGuestPipelineCaches");
        }
    }
}
