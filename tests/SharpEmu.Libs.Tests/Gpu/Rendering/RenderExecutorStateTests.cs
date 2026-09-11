// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// Dynamic state, attachment assembly, the rendering boundaries, the trace gate and queued-data order.
[Collection(SchedulingStateCollection.Name)]
public sealed class RenderExecutorStateTests : IDisposable
{
    private readonly RecordingRenderHost _host = new();
    private readonly FakePipelineProvider _pipelines = new();
    private readonly RenderExecutor _executor;
    private readonly FatalScope _fatal = new();

    public RenderExecutorStateTests() => _executor = new RenderExecutor(_host, _pipelines);

    public void Dispose() => _fatal.Dispose();

    private DynamicDrawState DrawAndTakeState(RegisterBanks banks)
    {
        _executor.DrawIndexed(1, banks, Indexed(3));
        return Assert.Single(_host.DynamicStates);
    }

    [Fact]
    public void DynamicState_TakesTheViewportScissorBlendAndDepthFromTheRegisters()
    {
        var banks = Banks(withDepth: true);
        banks.Context.BlendColor = new BlendColorRegisters { Red = 0.1f, Green = 0.2f, Blue = 0.3f, Alpha = 0.4f };
        banks.Context.LineWidth = 2.5f;
        banks.Context.ScreenViewport.ScreenScissorRight = 48;
        banks.Context.ScreenViewport.ScreenScissorBottom = 40;
        var state = DrawAndTakeState(banks);

        Assert.Equal((0f, 64f, 64f, -64f), (state.ViewportX, state.ViewportY, state.ViewportWidth, state.ViewportHeight));
        Assert.Equal((0.5f, 1f), (state.ViewportMinDepth, state.ViewportMaxDepth));
        Assert.Equal(new ScissorRectangle(0, 0, 48, 40), state.Scissor);
        Assert.Equal(1f, state.LineWidth);
        Assert.Equal((0.1f, 0.2f, 0.3f, 0.4f), (state.BlendRed, state.BlendGreen, state.BlendBlue, state.BlendAlpha));
        Assert.True(state.DepthTestEnabled);
        Assert.True(state.DepthWriteEnabled);
        Assert.Equal(CompareOp.Always, state.DepthCompare);
        Assert.False(state.DepthBiasEnabled);
        Assert.False(state.StencilTestEnabled);
        Assert.Equal((1u, (byte)1), (state.ColorWriteCount, state.ColorWriteEnableMask));
    }

    [Fact]
    public void DynamicState_ClipDisableUsesTheDeviceViewportLimit()
    {
        var banks = Banks();
        banks.Context.Clip = new ClipControlRegisters { ClipDisable = true };
        _host.Limits = new RenderHostLimits(16384, 8192, 32768, 4096);
        var state = DrawAndTakeState(banks);

        Assert.Equal((0f, 0f, 16384f, 4096f), (state.ViewportX, state.ViewportY, state.ViewportWidth, state.ViewportHeight));
    }

    [Fact]
    public void DynamicState_ADepthClearDisablesTheDepthWrite()
    {
        var banks = Banks(withDepth: true);
        banks.Context.DepthTarget = RegisterWords.Depth(DepthBase, 64, 64, depthClear: true);
        var state = DrawAndTakeState(banks);

        Assert.True(state.DepthTestEnabled);
        Assert.False(state.DepthWriteEnabled);
        Assert.True(_host.BegunRenderings[0].DepthStencilAttachment.DepthClear);
    }

    [Theory]
    [InlineData(true, false, false, false, true, 2f)]
    [InlineData(false, true, false, false, true, 4f)]
    [InlineData(true, false, true, false, false, 0f)]
    [InlineData(true, true, true, false, true, 4f)]
    [InlineData(false, false, false, false, false, 0f)]
    public void DynamicState_DepthBiasFollowsTheEnabledUncooledFace(bool frontEnable, bool backEnable, bool cullFront, bool cullBack, bool expectedEnabled, float expectedConstant)
    {
        var banks = Banks(withDepth: true);
        banks.Context.RasterMode = new RasterModeRegisters { PolygonOffsetFrontEnable = frontEnable, PolygonOffsetBackEnable = backEnable, CullFront = cullFront, CullBack = cullBack };
        banks.Context.PolygonOffset = new PolygonOffsetRegisters { FrontOffset = 2f, BackOffset = 4f, FrontScale = 32f, BackScale = 64f, Clamp = 0.5f };
        var state = DrawAndTakeState(banks);

        Assert.Equal(expectedEnabled, state.DepthBiasEnabled);
        Assert.Equal(expectedConstant, state.DepthBiasConstantFactor);
        Assert.Equal(expectedEnabled ? (frontEnable && !cullFront ? 2f : 4f) : 0f, state.DepthBiasSlopeFactor);
        Assert.Equal(expectedEnabled ? 0.5f : 0f, state.DepthBiasClamp);
    }

    [Theory]
    [InlineData(true, -23, Format.D16Unorm, 1f)]
    [InlineData(false, -23, Format.D32Sfloat, 1f)]
    [InlineData(false, -23, Format.D16Unorm, 1f / 128f)]
    [InlineData(false, -23, Format.D24UnormS8Uint, 2f)]
    [InlineData(false, -16, Format.D16UnormS8Uint, 1f)]
    public void DepthBiasConstantFactor_ScalesFixedPointFormatsByTheHostDepthBits(bool isFloat, int negativeBits, Format format, float expected)
    {
        var offset = new PolygonOffsetRegisters { DepthIsFloat = isFloat, NegativeDepthBits = (sbyte)negativeBits };
        Assert.Equal(expected, RenderExecutor.DepthBiasConstantFactor(1f, in offset, format));
    }

    [Fact]
    public void DynamicState_PassesTheStencilMasksWhenTheStencilTestIsOn()
    {
        var banks = Banks(withDepth: true);
        banks.Context.DepthTarget = RegisterWords.Depth(DepthBase, 64, 64, stencilBase: StencilBase) with { DepthControl = 0x1 | 0x2 | 0x4 | (7u << 4) | (7u << 8) };
        banks.Context.StencilMask = new StencilMaskRegisters { TestValue = 0x10, Mask = 0xF0, WriteMask = 0xFF, TestValueBack = 0x10, MaskBack = 0xF0, WriteMaskBack = 0xFF };
        var state = DrawAndTakeState(banks);

        Assert.True(state.StencilTestEnabled);
        Assert.Equal(new StencilMasks(0xF0, 0xFF, 0x10), state.FrontStencil);
        Assert.Equal(state.FrontStencil, state.BackStencil);
    }

    // A slot without a mask nibble is never bound, so every bound attachment writes.
    [Fact]
    public void DynamicState_ColorWriteEnableFollowsTheTargetMaskNibbleOfTheBoundSlot()
    {
        var banks = Banks();
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.RenderTargetMask = 0xF0;
        var state = DrawAndTakeState(banks);

        Assert.Equal((1u, (byte)0b1), (state.ColorWriteCount, state.ColorWriteEnableMask));
        Assert.Contains("acquire_color 1 100100000 image=1", _host.Calls);
    }

    [Fact]
    public void Scissor_IntersectsEveryRectangleAndClampsToTheExtent()
    {
        var viewport = new ScreenViewportRegisters
        {
            ScreenScissorLeft = 0,
            ScreenScissorTop = 0,
            ScreenScissorRight = 100,
            ScreenScissorBottom = 100,
            WindowScissorLeft = 10,
            WindowScissorTop = 10,
            WindowScissorRight = 90,
            WindowScissorBottom = 90,
            WindowScissorWindowOffsetEnable = true,
            WindowOffsetX = 5,
            WindowOffsetY = -5,
            GenericScissorLeft = 0,
            GenericScissorTop = 0,
            GenericScissorRight = 80,
            GenericScissorBottom = 80,
        };
        viewport.Viewports[0] = new ViewportRegisters { ScissorLeft = 20, ScissorTop = 0, ScissorRight = 70, ScissorBottom = 70 };
        var scanMode = new ScanModeRegisters { ViewportScissorEnable = true };

        Assert.Equal(new ScissorRectangle(20, 5, 64, 64), RenderExecutor.ResolveScissor(viewport, in scanMode, 64, 64));
        scanMode.ViewportScissorEnable = false;
        Assert.Equal(new ScissorRectangle(15, 5, 64, 64), RenderExecutor.ResolveScissor(viewport, in scanMode, 64, 64));
    }

    [Fact]
    public void Scissor_UnsetScreenRectangleDefaultsToTheExtentAndAnEmptyRuleClearsIt()
    {
        var viewport = new ScreenViewportRegisters();
        var scanMode = new ScanModeRegisters();
        Assert.Equal(new ScissorRectangle(0, 0, 64, 32), RenderExecutor.ResolveScissor(viewport, in scanMode, 64, 32));

        viewport.ClipRectangleRule = 0;
        Assert.Equal(new ScissorRectangle(0, 0, 0, 0), RenderExecutor.ResolveScissor(viewport, in scanMode, 64, 32));
    }

    [Fact]
    public void Scissor_ClipRectangleRulesIntersectTheSelectedRectangles()
    {
        var viewport = new ScreenViewportRegisters { ScreenScissorRight = 100, ScreenScissorBottom = 100 };
        viewport.ClipRectangleLeft[0] = 10;
        viewport.ClipRectangleTop[0] = 10;
        viewport.ClipRectangleRight[0] = 50;
        viewport.ClipRectangleBottom[0] = 50;
        viewport.ClipRectangleRight[1] = 30;
        viewport.ClipRectangleBottom[1] = 90;
        viewport.ClipRectangleWindowOffsetEnable[1] = true;
        viewport.WindowOffsetX = 2;
        viewport.WindowOffsetY = 3;
        var scanMode = new ScanModeRegisters();

        // Rule 0xAAAA passes every combination that includes rectangle 0.
        viewport.ClipRectangleRule = 0xAAAA;
        Assert.Equal(new ScissorRectangle(10, 10, 50, 50), RenderExecutor.ResolveScissor(viewport, in scanMode, 100, 100));

        // Rule 0x8888 needs rectangles 0 and 1.
        viewport.ClipRectangleRule = 0x8888;
        Assert.Equal(new ScissorRectangle(10, 10, 32, 50), RenderExecutor.ResolveScissor(viewport, in scanMode, 100, 100));

        // An unsupported rule leaves the scissor unchanged.
        viewport.ClipRectangleRule = 0x1234;
        Assert.Equal(new ScissorRectangle(0, 0, 100, 100), RenderExecutor.ResolveScissor(viewport, in scanMode, 100, 100));
    }

    [Fact]
    public void Scissor_AnInvertedResultCollapsesToItsOrigin()
    {
        var viewport = new ScreenViewportRegisters { ScreenScissorLeft = 80, ScreenScissorTop = 80, ScreenScissorRight = 120, ScreenScissorBottom = 120 };
        var scanMode = new ScanModeRegisters();
        Assert.Equal(new ScissorRectangle(64, 64, 64, 64), RenderExecutor.ResolveScissor(viewport, in scanMode, 64, 64));
    }

    [Fact]
    public void Attachments_CarryTheFormatsLayoutClearAndSampleCount()
    {
        var banks = Banks(withDepth: true);
        _executor.DrawIndexed(1, banks, Indexed(3));

        var rendering = Assert.Single(_host.BegunRenderings);
        Assert.Equal(rendering, Assert.Single(_pipelines.PipelineRenderings));
        Assert.Equal((64u, 64u, 1u, 1u, 1u), (rendering.Width, rendering.Height, rendering.Layers, rendering.ColorAttachmentCount, rendering.Samples));
        var color = rendering.ColorAttachments[0];
        Assert.Equal((new ImageView(0x1001), ImageLayout.ColorAttachmentOptimal, Format.R8G8B8A8Unorm, false), (color.View, color.Layout, color.Format, color.IsClear));
        var depth = rendering.DepthStencilAttachment;
        Assert.Equal((new ImageView(0x2002), Format.D32Sfloat, true, false, false, false), (depth.View, depth.Format, depth.HasDepth, depth.DepthClear, depth.HasStencil, depth.StencilClear));
        Assert.Equal(ImageLayout.DepthAttachmentOptimal, depth.Layout);
        Assert.Equal(Format.D32Sfloat, rendering.DepthFormat);
        Assert.Equal(Format.Undefined, rendering.StencilFormat);
        Assert.Contains($"transition_depth 2 {ImageLayout.DepthAttachmentOptimal} {ImageAspectFlags.DepthBit} loadClear=False", _host.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Attachments_ADccMetadataClearReplacesTheClearValueAndLoadsAClear(bool fastClearEnabled)
    {
        var clearValue = default(ClearColorValue);
        clearValue.Uint32_0 = 0x11;
        clearValue.Uint32_3 = 0x44;
        _host.ColorMetadataClears[ColorBase] = clearValue;
        var banks = Banks();
        if (fastClearEnabled)
        {
            var target = banks.Context.ColorTargets[0];
            banks.Context.ColorTargets[0] = target with { Info = target.Info | (1u << 13) };
        }

        _executor.DrawIndexed(1, banks, Indexed(3));

        var color = _host.BegunRenderings[0].ColorAttachments[0];
        Assert.True(color.IsClear);
        Assert.Equal((0x11u, 0u, 0u, 0x44u), (color.ClearWord0, color.ClearWord1, color.ClearWord2, color.ClearWord3));
    }

    [Fact]
    public void Attachments_AnHtileMetadataClearLoadsADepthClearWithoutTouchingTheDepthWrite()
    {
        _host.DepthMetadataClear = true;
        var banks = Banks(withDepth: true);
        _executor.DrawIndexed(1, banks, Indexed(3));

        var depth = _host.BegunRenderings[0].DepthStencilAttachment;
        Assert.True(depth.DepthClear);
        Assert.Equal(ImageLayout.DepthAttachmentOptimal, depth.Layout);
        Assert.True(_host.DynamicStates[0].DepthWriteEnabled);
        Assert.Contains($"transition_depth 2 {ImageLayout.DepthAttachmentOptimal} {ImageAspectFlags.DepthBit} loadClear=True", _host.Calls);
    }

    [Fact]
    public void Attachments_AReadOnlyDepthTargetUsesTheReadOnlyLayout()
    {
        var banks = Banks(withDepth: true);
        banks.Context.DepthTarget = RegisterWords.Depth(DepthBase, 64, 64, depthWrite: false);
        _executor.DrawIndexed(1, banks, Indexed(3));

        Assert.Equal(ImageLayout.DepthReadOnlyOptimal, _host.BegunRenderings[0].DepthStencilAttachment.Layout);
        Assert.Contains($"transition_depth 2 {ImageLayout.DepthReadOnlyOptimal} {ImageAspectFlags.None} loadClear=False", _host.Calls);
    }

    [Fact]
    public void Attachments_DepthOnlyDrawHasNoColorAttachments()
    {
        var banks = Banks(withDepth: true);
        banks.Context.RenderTargetMask = 0;
        banks.Context.ShaderInterface.DepthShaderControl = new DepthShaderControlRegisters { DepthExportEnable = true };
        _executor.DrawIndexed(1, banks, Indexed(3));

        var rendering = _host.BegunRenderings[0];
        Assert.Equal((0u, 64u, 64u), (rendering.ColorAttachmentCount, rendering.Width, rendering.Height));
        Assert.Equal(Format.D32Sfloat, rendering.DepthFormat);
        Assert.Contains("create_graphics_pipeline colors=0 depth=True topology=TriangleList restart=False", _pipelines.Calls);
    }

    [Fact]
    public void Attachments_StencilFormatIsReportedOnlyWithAStencilAspect()
    {
        var banks = Banks(withDepth: true);
        banks.Context.DepthTarget = RegisterWords.Depth(DepthBase, 64, 64, stencilBase: StencilBase);
        _executor.DrawIndexed(1, banks, Indexed(3));

        var rendering = _host.BegunRenderings[0];
        Assert.True(rendering.DepthStencilAttachment.HasStencil);
        Assert.Equal(rendering.DepthStencilAttachment.Format, rendering.StencilFormat);
        Assert.NotEqual(Format.Undefined, rendering.StencilFormat);
    }

    [Fact]
    public void Attachments_MixedColorSampleCountsAreFatal()
    {
        var banks = Banks();
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.RenderTargetMask = 0xFF;
        _host.ImageSamples[SecondColorBase] = 4;
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, banks, Indexed(3)));
        Assert.Contains("imageSamples=4 targetSamples=1", fatal.Message);
    }

    [Fact]
    public void Attachments_ADepthSampleMismatchIsFatal()
    {
        _host.ImageSamples[DepthBase] = 2;
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, Banks(withDepth: true), Indexed(3)));
        Assert.Contains("imageSamples=2 targetSamples=1", fatal.Message);
    }

    [Fact]
    public void Attachments_SingleSampleColorWithMultisampledDepthIsFatal()
    {
        var banks = Banks(withDepth: true);
        banks.Context.DepthTarget = RegisterWords.Depth(DepthBase, 64, 64, samplesLog2: 2);
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, banks, Indexed(3)));
        Assert.Contains("color=1 depth=4", fatal.Message);
    }

    [Fact]
    public void RenderingBoundary_ADispatchEndsRenderingBeforeItsPipeline()
    {
        _executor.DrawIndexed(1, Banks(), Indexed(3));
        _executor.Dispatch(2, Banks(), 1, 1, 1, 0x41);

        var begin = _host.Calls.IndexOf("begin_rendering 64x64x1 colors=1 samples=1");
        var end = _host.Calls.IndexOf("end_rendering");
        var dispatch = _host.Calls.IndexOf("dispatch 1 1 1");
        Assert.True(begin < end && end < dispatch);
        Assert.Equal(1, _host.Calls.Count(c => c == "end_rendering"));
    }

    [Fact]
    public void RenderingBoundary_ConsecutiveDrawsBeginTheSameStateAgainAndTheHostDecidesReuse()
    {
        _executor.DrawIndexed(1, Banks(), Indexed(3));
        _executor.DrawIndexed(2, Banks(), Indexed(3));

        Assert.Equal(2, _host.BegunRenderings.Count);
        Assert.Equal(_host.BegunRenderings[0], _host.BegunRenderings[1]);
        Assert.DoesNotContain("end_rendering", _host.Calls);
    }

    [Fact]
    public void RenderingState_EqualityCoversEveryAttachment()
    {
        _executor.DrawIndexed(1, Banks(), Indexed(3));
        var original = _host.BegunRenderings[0];
        var changed = original;
        changed.ColorAttachments[0] = original.ColorAttachments[0] with { IsClear = true };
        Assert.NotEqual(original, changed);
        Assert.NotEqual(original.GetHashCode(), changed.GetHashCode());

        var samples = original;
        samples.Samples = 4;
        Assert.NotEqual(original, samples);
    }

    [Fact]
    public void TraceOff_ProducesNoLinesNoExtraGuestReadsAndNoExtraAllocations()
    {
        Assert.False(RenderTrace.Enabled);
        var error = new StringWriter();
        var original = Console.Error;
        Console.SetError(error);
        try
        {
            void Sequence()
            {
                _host.Calls.Clear();
                _host.BegunRenderings.Clear();
                _host.DynamicStates.Clear();
                _pipelines.Calls.Clear();
                _pipelines.PipelineRenderings.Clear();
                _pipelines.PipelineRequests.Clear();
                _executor.DrawIndexed(1, Banks(), Indexed(3));
                _executor.DrawAuto(2, Banks(), Auto(3));
                _executor.Dispatch(3, Banks(), 1, 1, 1, 0x41);
            }

            Sequence();
            Sequence();
            var baselineReads = _host.GuestReads;
            var before = GC.GetAllocatedBytesForCurrentThread();
            Sequence();
            var baselineBytes = GC.GetAllocatedBytesForCurrentThread() - before;
            var readsPerSequence = _host.GuestReads - baselineReads;
            before = GC.GetAllocatedBytesForCurrentThread();
            Sequence();
            var measuredBytes = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, readsPerSequence);
            Assert.Equal(readsPerSequence, _host.GuestReads - baselineReads - readsPerSequence);
            Assert.True(measuredBytes <= baselineBytes, $"allocated {measuredBytes} bytes against a baseline of {baselineBytes}");
        }
        finally
        {
            Console.SetError(original);
        }

        Assert.Equal(string.Empty, error.ToString());
    }

    // Requirement A, recording-host half: each draw binds the handle and offset it obtained in its own recording.
    [Fact]
    public void TwoDraws_BindTheBufferVersionEachObtainedAndTheMergeCopyLandsBetweenThem()
    {
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase + 0x1000, 16, 0x100)]);
        _executor.DrawAuto(1, Banks(), Auto(3));
        _host.WriteGuest(VertexBase + 0x1000, new byte[0x10]);
        _pipelines.Graphics = Programs(vertexBuffers: [new VertexInputBuffer(VertexBase, 16, 0x300)]);
        _executor.DrawAuto(2, Banks(), Auto(3));

        var events = _host.Calls.Where(c => c.StartsWith("obtain", StringComparison.Ordinal) || c.StartsWith("bind_vertex", StringComparison.Ordinal) ||
                                           c.StartsWith("draw ", StringComparison.Ordinal) || c.StartsWith("merge", StringComparison.Ordinal) ||
                                           c.StartsWith("cpu_write", StringComparison.Ordinal) || c.StartsWith("upload", StringComparison.Ordinal)).ToList();
        Assert.Equal(
        [
            "obtain 100401000 1000 written=False -> 100:0",
            "bind_vertex 100:0",
            "draw 3 1 0 0",
            "cpu_write 100401000 10",
            "merge 100->101",
            "upload 101 100401000",
            "obtain 100400000 3000 written=False -> 101:0",
            "bind_vertex 101:0",
            "draw 3 1 0 0",
        ], events);
    }

    public static TheoryData<string, Action<RegisterBanks>, string> FatalRegisterStates => new()
    {
        { "user vectors", banks => banks.UserConfig.GeometryEngineUserVectorEnable = new GeometryEngineUserVectorEnableRegisters { VectorRegister2 = true }, "v1=False v2=True v3=False" },
        { "line stipple", banks => banks.Context.ScanMode = new ScanModeRegisters { LineStippleEnable = true }, "Line stipple" },
        { "clip planes", banks => banks.Context.Clip = new ClipControlRegisters { UserClipPlanes = 3 }, "planes=3" },
        { "copy centroid", banks => banks.Context.DepthTarget = banks.Context.DepthTarget with { RenderControl = 1u << 7 }, "renderControl=0x00000080" },
        { "copy sample", banks => banks.Context.DepthTarget = banks.Context.DepthTarget with { RenderControl = 2u << 8 }, "copySample=2" },
        { "front polygon type", banks => banks.Context.RasterMode = new RasterModeRegisters { FrontPolygonType = 1 }, "type=1" },
        { "back polygon type", banks => banks.Context.RasterMode = new RasterModeRegisters { BackPolygonType = 3 }, "type=3" },
        { "provoking vertex", banks => banks.Context.RasterMode = new RasterModeRegisters { ProvokingVertexLast = true }, "provoking vertex" },
        { "perspective correction", banks => banks.Context.RasterMode = new RasterModeRegisters { PerspectiveCorrectionDisable = true }, "perspective correction" },
        { "inverted slices", banks => banks.Context.ColorTargets[0] = banks.Context.ColorTargets[0] with { View = 2 }, "start=2 last=0" },
        { "single-sample fmask", banks => banks.Context.ColorTargets[0] = banks.Context.ColorTargets[0] with { Info = banks.Context.ColorTargets[0].Info | (1u << 14) }, "FMASK compression" },
        { "fmask data compression disable", banks => banks.Context.ColorTargets[0] = banks.Context.ColorTargets[0] with { Info = banks.Context.ColorTargets[0].Info | (1u << 26) }, "FMASK data compression" },
        { "shading rate hint", banks => banks.Context.ColorTargets[0] = banks.Context.ColorTargets[0] with { Attrib3 = 1u << 31 }, "shading rate hint" },
        { "overwrite combiner", banks => banks.Context.ColorTargets[0] = banks.Context.ColorTargets[0] with { DccControl = 1 }, "dccControl=0x00000001" },
        { "key clear", banks => banks.Context.ColorTargets[0] = banks.Context.ColorTargets[0] with { DccControl = 2 }, "dccControl=0x00000002" },
    };

    [Theory]
    [MemberData(nameof(FatalRegisterStates))]
    public void UnsupportedRegisterState_IsFatalWithItsValues(string state, Action<RegisterBanks> mutate, string expected)
    {
        var banks = Banks();
        mutate(banks);
        var fatal = Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, banks, Indexed(3)));
        Assert.True(fatal.Message.Contains(expected, StringComparison.Ordinal), $"{state}: {fatal.Message}");
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("find_image", StringComparison.Ordinal));
    }

    [Fact]
    public void ExportMappings_UnboundSlotsAreIdentityAndBoundSlotsFollowTheirFormat()
    {
        var banks = Banks();
        var alternate = RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.ColorTargets[2] = alternate with { Info = alternate.Info | (2u << 11) };
        banks.Context.RenderTargetMask = 0xF0F;
        _executor.DrawIndexed(1, banks, Indexed(3));

        var mapping = Assert.Single(_pipelines.ExportMappings);
        var expected = ColorTargetResolver.Resolve(banks.Context, 2, 0, false, out _)!.Value.ExportMapping;
        Assert.NotEqual(ColorComponentMap.Identity, expected);
        Assert.Equal(8, mapping.Length);
        Assert.Equal(ColorComponentMap.Identity, mapping[0]);
        Assert.Equal(expected, mapping[2]);
        foreach (var slot in new[] { 1, 3, 4, 5, 6, 7 })
        {
            Assert.Equal(ColorComponentMap.Identity, mapping[slot]);
        }
    }

    [Fact]
    public void ExportMappings_DepthOnlyDrawHandsEverySlotIdentity()
    {
        var banks = Banks(withDepth: true);
        banks.Context.RenderTargetMask = 0;
        banks.Context.ShaderInterface.DepthShaderControl = new DepthShaderControlRegisters { DepthExportEnable = true };
        _executor.DrawIndexed(1, banks, Indexed(3));

        var mapping = Assert.Single(_pipelines.ExportMappings);
        Assert.All(mapping, map => Assert.Equal(ColorComponentMap.Identity, map));
    }

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

    // The scope opens before the first preparation and closes after the draw and its barrier are recorded.
    [Fact]
    public void Preparation_SpansEveryResourceOperationAndRefusesARingWrap()
    {
        _host.WriteGuest(IndexBase, [1, 2, 3]);
        _host.PendingRingWrap = true;
        _executor.DrawIndexed(1, Banks(), Indexed(3, indexType: 2));

        AssertOrder(
            "preparation_begin",
            "prepare_bindings Vertex",
            "bind_resources 2",
            "upload_transient 6 align=16",
            "wrap_refused acquire_color",
            "acquire_color 0",
            "commit Graphics",
            "draw_indexed 3 1 0 0 0",
            "preparation_end",
            "reset_bindings");
        Assert.Equal(new byte[] { 1, 0, 2, 0, 3, 0 }, _host.LastTransient);
        Assert.Equal((0, 1), (_host.PreparationDepth, _host.PreparationsOpened));
        Assert.False(_host.PendingRingWrap);
    }

    [Fact]
    public void Preparation_CoversTheBufferWriteBarrier()
    {
        var written = new BufferResourceInfo(false, true, false, false, false, 4, 0);
        _pipelines.Graphics = Programs(vertexStage: Stage(Program(ShaderStageKind.Vertex, buffers: [written]), buffers: [BufferDescriptor(VertexBase, 4, 16)]));
        _executor.DrawIndexed(1, Banks(), Indexed(3));

        AssertOrder("preparation_begin", "draw_indexed", "end_rendering", "write_barrier", "preparation_end", "reset_bindings");
        Assert.Equal(0, _host.PreparationDepth);
    }

    [Fact]
    public void Preparation_IsReleasedWhenTheDrawFailsInsideTheScope()
    {
        var banks = Banks();
        banks.Context.ColorTargets[1] = RegisterWords.Color(SecondColorBase, 64, 64);
        banks.Context.RenderTargetMask = 0xFF;
        _host.ImageSamples[SecondColorBase] = 4;
        Assert.Throws<RenderExecutorFatalException>(() => _executor.DrawIndexed(1, banks, Indexed(3)));

        AssertOrder("preparation_begin", "acquire_color 1", "preparation_end");
        Assert.Equal((0, 1), (_host.PreparationDepth, _host.PreparationsOpened));
        Assert.DoesNotContain(_host.Calls, c => c.StartsWith("draw", StringComparison.Ordinal));
    }

    [Fact]
    public void Preparation_IsNotOpenedForASkippedDraw()
    {
        var banks = Banks(withPixel: false);
        banks.Context.RenderTargetMask = 0;
        _executor.DrawIndexed(1, banks, Indexed(3));
        _executor.DrawIndexed(1, Banks(primitiveType: 0), Indexed(3));

        Assert.Equal(0, _host.PreparationsOpened);
    }
}
