// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.Tests.Gpu.Vulkan;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

// Slot selection over the typed context bank, and the depth/stencil pipeline state the depth resolver adds.
[Collection(SchedulingStateCollection.Name)]
public sealed class TargetResolverTests : IClassFixture<HeadlessVulkanFixture>
{
    private const ulong Base = 0x1_0000_0000;
    private const uint StencilEnable = 1;
    private const uint DepthEnable = 2;
    private const uint DepthWrite = 4;
    private const uint BackFaceEnable = 1u << 7;

    private readonly HeadlessVulkan? _vulkan;

    public TargetResolverTests(HeadlessVulkanFixture fixture) => _vulkan = fixture.Vulkan;

    private static Exception Fatal(string message) => new InvalidOperationException(message);

    private static uint DepthControl(CompareOp depth, CompareOp stencil, CompareOp stencilBack = CompareOp.Never, bool backFace = false) =>
        StencilEnable | DepthEnable | DepthWrite | ((uint)depth << 4) | ((uint)stencil << 8) | ((uint)stencilBack << 20) | (backFace ? BackFaceEnable : 0);

    private static ContextRegisters StencilContext(byte pass, byte writeMask, byte operationValue, uint depthControl)
    {
        var context = new ContextRegisters
        {
            DepthTarget = RegisterWords.Depth(Base, 64, 64, stencilBase: Base + 0x10_0000) with { DepthControl = depthControl },
            StencilControl = new StencilControlRegisters { Fail = 0, Pass = pass, DepthFail = 1 },
            StencilMask = new StencilMaskRegisters { TestValue = 0x10, Mask = 0xFF, WriteMask = writeMask, OperationValue = operationValue },
        };
        return context;
    }

    [Fact]
    public void FirstBoundSlot_SkipsUnmaskedAndEmptySlots()
    {
        var context = new ContextRegisters();
        context.ColorTargets[1] = RegisterWords.Color(Base, 64, 64);
        context.ColorTargets[2] = RegisterWords.Color(Base + 0x10_0000, 64, 64);

        context.RenderTargetMask = 0xF00;
        Assert.Equal(2u, ColorTargetResolver.FirstBound(context));
        context.RenderTargetMask = 0xFF0;
        Assert.Equal(1u, ColorTargetResolver.FirstBound(context));
        context.RenderTargetMask = 0xF;
        Assert.Equal(0u, ColorTargetResolver.FirstBound(context));
    }

    [Fact]
    public void ColorResolve_UsesTheSelectedSlotAndItsMaskNibble()
    {
        var context = new ContextRegisters();
        context.ColorTargets[1] = RegisterWords.Color(Base, 64, 64);
        context.RenderTargetMask = 0xF0;

        Assert.NotNull(ColorTargetResolver.Resolve(context, ColorTargetResolver.FirstBoundSlot, 0, false, out var slot));
        Assert.Equal(1u, slot);
        Assert.Null(ColorTargetResolver.Resolve(context, 0, 0, false, out _));
        context.RenderTargetMask = 0;
        Assert.Null(ColorTargetResolver.Resolve(context, 1, 0, false, out _));
        Assert.NotNull(ColorTargetResolver.Resolve(context, 1, 0, ignoreTargetMask: true, out _));
    }

    [Fact]
    public void DepthState_ConvertsTheStencilFacesAndRejectsAMismatchedReplacement()
    {
        var context = StencilContext(pass: 4, writeMask: 0xFF, operationValue: 0x10, DepthControl(CompareOp.Less, CompareOp.Always));

        var state = DepthTargetResolver.ResolveState(context, hasStencil: true, Fatal);

        Assert.True(state.DepthTestEnabled);
        Assert.True(state.DepthWriteEnabled);
        Assert.Equal(CompareOp.Less, state.DepthCompare);
        Assert.True(state.StencilTestEnabled);
        Assert.Equal(new StencilOperations(StencilOp.Keep, StencilOp.Replace, StencilOp.Zero, CompareOp.Always), state.FrontOperations);
        Assert.Equal(new StencilMasks(0xFF, 0xFF, 0x10), state.FrontMasks);
        Assert.Equal(state.FrontOperations, state.BackOperations);
        Assert.Equal(state.FrontMasks, state.BackMasks);

        context.StencilMask.OperationValue = 0x20;
        Assert.Contains("replacement", Assert.Throws<InvalidOperationException>(() => DepthTargetResolver.ResolveState(context, true, Fatal)).Message);

        // Without a write mask the operations have no effect, so the mismatch does not matter.
        context.StencilMask.WriteMask = 0;
        state = DepthTargetResolver.ResolveState(context, true, Fatal);
        Assert.Equal(StencilOperations.Default with { Compare = CompareOp.Always }, state.FrontOperations);
        Assert.Equal(0u, state.FrontMasks.WriteMask);
    }

    [Fact]
    public void DepthState_StencilClearDisablesTheOperationsAndBackFaceDecodesSeparately()
    {
        var context = StencilContext(pass: 4, writeMask: 0xFF, operationValue: 0x20, DepthControl(CompareOp.Less, CompareOp.Always, CompareOp.Equal, backFace: true));
        context.DepthTarget = context.DepthTarget with { RenderControl = 2 };
        context.StencilControl.FailBack = 5;
        context.StencilMask.MaskBack = 0x0F;
        context.StencilMask.WriteMaskBack = 0xFF;
        context.StencilMask.TestValueBack = 0x33;

        var state = DepthTargetResolver.ResolveState(context, hasStencil: true, Fatal);

        Assert.True(state.StencilClearEnabled);
        Assert.Equal(StencilOp.Keep, state.FrontOperations.PassOperation);
        Assert.Equal(0u, state.FrontMasks.WriteMask);
        Assert.Equal(CompareOp.Equal, state.BackOperations.Compare);
        Assert.Equal(StencilOp.Keep, state.BackOperations.FailOperation);
        Assert.Equal(new StencilMasks(0x0F, 0, 0x33), state.BackMasks);

        context.DepthTarget = context.DepthTarget with { RenderControl = 0 };
        context.StencilMask.OperationValue = 0x10;
        context.StencilMask.OperationValueBack = 0x33;
        state = DepthTargetResolver.ResolveState(context, hasStencil: true, Fatal);
        Assert.Equal(StencilOp.IncrementAndClamp, state.BackOperations.FailOperation);
        Assert.Equal(0xFFu, state.BackMasks.WriteMask);
    }

    [Fact]
    public void DepthState_ExclusiveOrMapsToInvertOverTheWrittenBitsOnly()
    {
        var context = StencilContext(pass: 0x0C, writeMask: 0x0F, operationValue: 0x0F, DepthControl(CompareOp.Less, CompareOp.Always));
        context.StencilMask.TestValue = 0x0F;

        Assert.Equal(StencilOp.Invert, DepthTargetResolver.ResolveState(context, true, Fatal).FrontOperations.PassOperation);
        context.StencilMask.OperationValue = 0;
        context.StencilMask.TestValue = 0;
        Assert.Equal(StencilOp.Keep, DepthTargetResolver.ResolveState(context, true, Fatal).FrontOperations.PassOperation);
        context.StencilMask.OperationValue = 0x03;
        context.StencilMask.TestValue = 0x03;
        Assert.Contains("XOR", Assert.Throws<InvalidOperationException>(() => DepthTargetResolver.ResolveState(context, true, Fatal)).Message);
        context.StencilControl.Pass = 0x0A;
        Assert.Contains("stencil operation", Assert.Throws<InvalidOperationException>(() => DepthTargetResolver.ResolveState(context, true, Fatal)).Message);
    }

    [Fact]
    public void DepthState_WithoutAnActiveStencilPlane_KeepsTheDefaults()
    {
        var context = StencilContext(pass: 4, writeMask: 0xFF, operationValue: 0x20, DepthControl(CompareOp.Less, CompareOp.Always));

        var state = DepthTargetResolver.ResolveState(context, hasStencil: false, Fatal);

        Assert.False(state.StencilTestEnabled);
        Assert.False(state.StencilClearEnabled);
        Assert.Equal(StencilOperations.Default, state.FrontOperations);
        Assert.Equal(default, state.FrontMasks);
    }

    [Fact]
    public void AttachmentWriteAspects_FollowClearsTestsAndTheFacesThatCanWrite()
    {
        var context = StencilContext(pass: 4, writeMask: 0xFF, operationValue: 0x10, DepthControl(CompareOp.Less, CompareOp.Always));
        var state = DepthTargetResolver.ResolveState(context, true, Fatal);

        Assert.Equal(ImageAspectFlags.DepthBit | ImageAspectFlags.StencilBit, state.AttachmentWriteAspects(Format.D32SfloatS8Uint));
        Assert.Equal(ImageLayout.DepthStencilAttachmentOptimal, state.AttachmentLayout(Format.D32SfloatS8Uint));
        Assert.Equal(ImageAspectFlags.DepthBit, state.AttachmentWriteAspects(Format.D32Sfloat));
        Assert.Equal(ImageLayout.DepthAttachmentOptimal, state.AttachmentLayout(Format.D32Sfloat));
        Assert.Equal(ImageAspectFlags.None, state.AttachmentWriteAspects(Format.Undefined));

        var testOnly = state with { DepthWriteEnabled = false, StencilTestEnabled = false };
        Assert.Equal(ImageAspectFlags.None, testOnly.AttachmentWriteAspects(Format.D32SfloatS8Uint));
        Assert.True(testOnly.IsReadOnly(Format.D32SfloatS8Uint));
        Assert.Equal(ImageLayout.DepthStencilReadOnlyOptimal, testOnly.AttachmentLayout(Format.D32SfloatS8Uint));

        // A compare that can never pass with an all-keep fail path writes nothing; a fail operation does.
        var neverPasses = state with
        {
            DepthWriteEnabled = false,
            FrontOperations = new StencilOperations(StencilOp.Keep, StencilOp.Replace, StencilOp.Keep, CompareOp.Never),
            FrontMasks = new StencilMasks(0, 0xFF, 0),
            BackOperations = new StencilOperations(StencilOp.Keep, StencilOp.Replace, StencilOp.Keep, CompareOp.Never),
            BackMasks = new StencilMasks(0, 0xFF, 0),
        };
        Assert.Equal(ImageAspectFlags.None, neverPasses.AttachmentWriteAspects(Format.D32SfloatS8Uint));
        var failWrites = neverPasses with { FrontOperations = neverPasses.FrontOperations with { FailOperation = StencilOp.Zero } };
        Assert.Equal(ImageAspectFlags.StencilBit, failWrites.AttachmentWriteAspects(Format.D32SfloatS8Uint));
        Assert.Equal(ImageLayout.DepthReadOnlyStencilAttachmentOptimal, failWrites.AttachmentLayout(Format.D32SfloatS8Uint));
        var depthOnlyWrite = state with { StencilTestEnabled = false };
        Assert.Equal(ImageLayout.DepthAttachmentStencilReadOnlyOptimal, depthOnlyWrite.AttachmentLayout(Format.D32SfloatS8Uint));
    }

    [Fact]
    public void DepthResolve_UsesTheBuilderThenAddsTheState()
    {
        if (!GatePrerequisites.Ready(_vulkan)) return;
        var context = new ContextRegisters { DepthTarget = RegisterWords.Depth(Base, 64, 64, depthClear: true), DepthClearValue = 0.5f };

        var resolved = DepthTargetResolver.Resolve(context, _vulkan.DeviceInfo, Fatal);

        Assert.NotNull(resolved);
        var state = resolved.Value;
        Assert.Equal(Format.D32Sfloat, state.Target.Format);
        Assert.True(state.State.DepthClearEnabled);
        Assert.Equal(0.5f, state.State.DepthClearValue);
        Assert.Equal(ImageAspectFlags.DepthBit, state.State.AttachmentWriteAspects(state.Target.Format));

        context.DepthTarget = RegisterWords.Depth(Base, 64, 64, depthTest: false, depthWrite: false);
        Assert.Null(DepthTargetResolver.Resolve(context, _vulkan.DeviceInfo, Fatal));
    }
}
