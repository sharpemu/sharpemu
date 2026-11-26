// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE.GpuMemory;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Pipelines;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Images.ImageCacheTestSupport;

namespace SharpEmu.Libs.Tests.VideoOut;

public sealed unsafe partial class RenderHostDeviceTests
{
    // A draw records the state its pre-rasterization part compiled with under its program's prewarm identity, and the
    // graphics prewarm builds that part again from the recorded program and state.
    [Fact]
    public void ADrawRecordsItsPreRasterizationState_AndThePrewarmBuildsThatPartAgain()
    {
        if (!Ready() || !_vulkan.SupportsGraphicsPipelineLibrary)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var presenter = new PresenterUnderTest(_vulkan);
            presenter.SetField("_graphicsPipelineLibraries", true);
            presenter.SetField("_vertexPrewarmIdentities", new ConcurrentDictionary<ulong, ulong>());
            presenter.LoadRenderingCommands();
            var harness = presenter.Harness;
            var target = harness.MapBacked(0x10000, ReadWrite);
            var vertices = harness.MapBacked(0x10000, ReadWrite);
            var words = RegisterWords.Color(target, Size, Size);
            harness.Write(vertices, Triangle(-1f, -1f, 3f, -1f, -1f, 3f));
            var host = (IShaderPipelineHost)presenter.Instance;
            var provider = new FixedProgramProvider(host, vertices);
            var executor = new RenderExecutor(presenter.RenderHost, provider);
            using (var list = ShaderPrewarmList.Open(directory) ?? throw new InvalidOperationException("The list did not open."))
            {
                var (identity, _, _) = VertexPrewarmTests.RecordEmbeddedFetchProgram(list);
                presenter.SetField("_shaderPrewarmList", list);
                presenter.Run(provider.CreateModules);
                // The fixed vertex module stands for the recorded program: the draw records its part's state under the identity.
                var vertexModule = presenter.GetField<ConcurrentDictionary<ulong, ShaderModule>>("_shaderModules")[1];
                host.VertexProgramRecorded(vertexModule.Handle, identity);
                GuestGpuMemoryHook.Attach(harness.Gpu);
                try
                {
                    presenter.Run(() => executor.DrawAuto(1, Banks(words), Draw()));
                    presenter.Run(() => presenter.InvokeMethod("FlushBatchedGuestCommands"));
                    harness.Finish();
                }
                finally
                {
                    GuestGpuMemoryHook.Attach(null);
                }

                presenter.SetField("_shaderPrewarmList", null!);
            }

            using (var reloaded = ShaderPrewarmList.Open(directory) ?? throw new InvalidOperationException("The list did not open."))
            {
                var part = Assert.IsType<PreRasterizationPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
                Assert.Equal(new PreRasterizationState((int)PolygonMode.Fill, (int)CullModeFlags.None, part.State.FrontFace, false, false,
                    part.State.DepthClipEnable, part.State.NegativeOneToOne), part.State);
                presenter.SetField("_prewarmedGraphicsModules", new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
                var compiler = new FakeShaderCompiler(VertexPrewarmTests.Compile);
                Assert.True((bool)presenter.InvokeMethod("PrewarmPreRasterizationLibrary", part.Program, part.Code, part.State, compiler)!);
                Assert.Equal(1, presenter.GetField<int>("_graphicsPrewarmCompiled"));
                Assert.Equal(0, presenter.GetField<int>("_graphicsPrewarmFailed"));
            }

            harness.Shutdown();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    // The graphics prewarm builds a task-launched mesh program's pre-rasterization part with the generic task stage, as the
    // session's pipelines take it.
    [Fact]
    public void ThePrewarmBuildsATaskLaunchedMeshPartWithItsTaskStage()
    {
        if (!Ready() || !_vulkan.SupportsGraphicsPipelineLibrary || !_vulkan.SupportsMeshShaders || !_vulkan.SupportsTaskShaders)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
        try
        {
            using var presenter = new PresenterUnderTest(_vulkan);
            presenter.SetField("_graphicsPipelineLibraries", true);
            presenter.SetField("_supportsMeshShader", true);
            presenter.SetField("_supportsTaskShader", true);
            presenter.SetField("_meshShaderLimits", VertexPrewarmTests.TaskMeshLimits);
            presenter.SetField("_prewarmedGraphicsModules", new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            var state = new PreRasterizationState((int)PolygonMode.Fill, (int)CullModeFlags.None, (int)FrontFace.CounterClockwise,
                false, false, true, false);
            using (var list = ShaderPrewarmList.Open(directory) ?? throw new InvalidOperationException("The list did not open."))
            {
                var (identity, _, _) = VertexPrewarmTests.RecordTaskLaunchedMeshProgram(list);
                list.RecordPreRasterization(identity, state);
            }

            using (var reloaded = ShaderPrewarmList.Open(directory) ?? throw new InvalidOperationException("The list did not open."))
            {
                var part = Assert.IsType<PreRasterizationPrewarmItem>(Assert.Single(reloaded.LoadedGraphicsPrewarm()));
                var compiler = new FakeShaderCompiler(VertexPrewarmTests.Compile);
                Assert.True((bool)presenter.InvokeMethod("PrewarmPreRasterizationLibrary", part.Program, part.Code, part.State, compiler)!);
                Assert.Equal(1, presenter.GetField<int>("_graphicsPrewarmCompiled"));
                Assert.Equal(0, presenter.GetField<int>("_graphicsPrewarmFailed"));
            }

            presenter.Harness.Shutdown();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
