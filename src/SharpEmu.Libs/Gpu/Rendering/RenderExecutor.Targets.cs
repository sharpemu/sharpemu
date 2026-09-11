// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    // Every draw resolves its targets against layer zero of the view range.
    private const uint DrawLayerOffset = 0;

    // Finds the color and depth targets; false when the draw has nothing to render into.
    private bool TryResolveDrawTargets(RegisterBanks banks, in DrawCall draw, ref DrawState state)
    {
        var context = banks.Context;
        if (TryResolveMultisampleColor(context))
        {
            return false;
        }

        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (slot != 0 && (context.RenderTargetMaskForSlot(slot) == 0 || context.ColorTargets[slot].BaseAddress == 0))
            {
                continue;
            }

            if (ColorTargetResolver.Resolve(context, slot, DrawLayerOffset, ignoreTargetMask: false, out var resolvedSlot) is not { } resolution)
            {
                continue;
            }

            var request = resolution.Request;
            var image = _host.FindImage(ref request, exactFormat: false);
            _host.BindRenderTarget(image);
            state.Colors[(int)state.ColorCount++] = new ColorTargetState(in resolution, resolvedSlot, image);
        }

        if (DepthTargetResolver.Resolve(context, _host.FormatSupport, _host.Fatal) is { } depthTarget)
        {
            var request = depthTarget.Target.Request;
            var image = _host.FindImage(ref request, exactFormat: false);
            _host.BindRenderTarget(image);
            state.Depth = new DepthAttachmentState(in depthTarget, image);
        }

        state.PixelActive = HasActivePixelShader(banks);
        if (state.ColorCount == 0 && !state.Depth.HasTarget && !state.PixelActive)
        {
            if (RenderTrace.Enabled && RenderTrace.FramebufferSkip())
            {
                RenderTrace.Write(
                    $"Skipping a draw without a framebuffer: name={draw.Name} count={draw.Count} targetMask=0x{context.RenderTargetMask:X8} " +
                    $"pixel=0x{banks.Shader.Pixel.Address:X16} colorShaderMask=0x{context.ShaderInterface.ColorShaderMask:X8}");
            }

            return false;
        }

        return true;
    }

    // Color control mode 3 resolves slot 0 into slot 1 instead of drawing; true consumes the draw.
    private bool TryResolveMultisampleColor(ContextRegisters context)
    {
        if (context.ColorControl.Mode != ColorModeResolve)
        {
            return false;
        }

        if (context.ColorTargets[0].BaseAddress == 0 || context.ColorTargets[1].BaseAddress == 0)
        {
            return false;
        }

        var source = ResolveMultisampleSlot(context, 0);
        var destination = ResolveMultisampleSlot(context, 1);
        if (source is not { } from || destination is not { } to)
        {
            return false;
        }

        if (from.Resolution.BaseAddress == to.Resolution.BaseAddress &&
            from.Resolution.BaseMipLevel == to.Resolution.BaseMipLevel &&
            from.Resolution.BaseArrayLayer == to.Resolution.BaseArrayLayer)
        {
            return true;
        }

        _host.MarkGpuWritten(to.Image);
        _host.ResolveImage(from.Image, from.Resolution.BaseMipLevel, from.Resolution.BaseArrayLayer, to.Image, to.Resolution.BaseMipLevel, to.Resolution.BaseArrayLayer);
        return true;
    }

    private ColorTargetState? ResolveMultisampleSlot(ContextRegisters context, uint slot)
    {
        if (ColorTargetResolver.Resolve(context, slot, DrawLayerOffset, ignoreTargetMask: true, out var resolvedSlot) is not { } resolution)
        {
            return null;
        }

        var request = resolution.Request;
        var image = _host.FindImage(ref request, exactFormat: true);
        _host.BindRenderTarget(image);
        return image.IsValid ? new ColorTargetState(in resolution, resolvedSlot, image) : null;
    }

    private static bool IsSupportedSampleCount(uint samples) => samples is 1 or 2 or 4 or 8;

    // Acquires every attachment through the host and assembles the rendering scope.
    private RenderingState AcquireAttachments(ref DrawState state)
    {
        var rendering = new RenderingState
        {
            Width = uint.MaxValue,
            Height = uint.MaxValue,
            Layers = uint.MaxValue,
            ColorAttachmentCount = state.ColorCount,
        };
        var attachmentSamples = 0u;
        for (var i = 0; i < state.ColorCount; i++)
        {
            ref var target = ref state.Colors[i];
            if (!target.Image.IsValid)
            {
                throw _host.Fatal($"A color target has no image: slot={target.Slot} address=0x{target.Resolution.BaseAddress:X16}.");
            }

            var acquired = _host.AcquireColorAttachment(in target);
            target.Image = acquired.Image;
            target.View = acquired.View;
            var samples = target.Resolution.Samples;
            if (acquired.Samples != samples || acquired.View.Handle == 0)
            {
                throw _host.Fatal($"The color attachment does not match its target: slot={target.Slot} imageSamples={acquired.Samples} targetSamples={samples} view=0x{acquired.View.Handle:X}.");
            }

            if (attachmentSamples == 0)
            {
                attachmentSamples = samples;
            }
            else if (attachmentSamples != samples)
            {
                throw _host.Fatal($"Mixed color attachment sample counts are not supported: first={attachmentSamples} next={samples}.");
            }

            var view = target.Resolution.Request.View;
            rendering.Width = Math.Min(rendering.Width, target.Resolution.Extent.Width);
            rendering.Height = Math.Min(rendering.Height, target.Resolution.Extent.Height);
            rendering.Layers = Math.Min(rendering.Layers, view.LayerCount);
            var clear = acquired.MetadataClear ? acquired.MetadataClearValue : target.Resolution.ColorClearValue;
            rendering.ColorAttachments[i] = new RenderingAttachment(
                acquired.View,
                acquired.Layout,
                view.Format,
                clear.Uint32_0,
                clear.Uint32_1,
                clear.Uint32_2,
                clear.Uint32_3,
                acquired.MetadataClear,
                HasDepth: false,
                DepthClear: false,
                HasStencil: false,
                StencilClear: false);
        }

        if (state.Depth.HasTarget)
        {
            ref var depth = ref state.Depth;
            var target = depth.Target.Target;
            var acquired = _host.AcquireDepthAttachment(in depth);
            depth.View = acquired.View;
            depth.MetadataClear = acquired.MetadataClear;
            if (acquired.View.Handle == 0 || acquired.Samples != target.Samples)
            {
                throw _host.Fatal($"The depth attachment does not match its target: imageSamples={acquired.Samples} targetSamples={target.Samples} view=0x{acquired.View.Handle:X}.");
            }

            if (attachmentSamples == 0)
            {
                attachmentSamples = target.Samples;
            }
            else if (attachmentSamples != target.Samples)
            {
                throw _host.Fatal($"Mixed color and depth sample counts are not supported: color={attachmentSamples} depth={target.Samples}.");
            }

            var loadState = depth.LoadState;
            var layout = loadState.AttachmentLayout(target.Format);
            _host.TransitionDepthAttachment(in depth, layout, loadState.AttachmentWriteAspects(target.Format));
            var view = target.Request.View;
            rendering.Width = Math.Min(rendering.Width, target.Width);
            rendering.Height = Math.Min(rendering.Height, target.Height);
            rendering.Layers = Math.Min(rendering.Layers, view.LayerCount);
            var aspects = ViewFormatRules.DepthAspects(target.Format);
            rendering.DepthStencilAttachment = new RenderingAttachment(
                acquired.View,
                layout,
                target.Format,
                BitConverter.SingleToUInt32Bits(depth.Target.State.DepthClearValue),
                depth.Target.State.StencilClearValue,
                0,
                0,
                IsClear: false,
                HasDepth: (aspects & ImageAspectFlags.DepthBit) != 0,
                DepthClear: depth.LoadClear,
                HasStencil: (aspects & ImageAspectFlags.StencilBit) != 0,
                StencilClear: depth.Target.State.StencilClearEnabled);
        }

        if (state.ColorCount == 0 && !state.Depth.HasTarget)
        {
            rendering.Width = _host.Limits.MaxFramebufferWidth;
            rendering.Height = _host.Limits.MaxFramebufferHeight;
        }
        else if (!IsSupportedSampleCount(attachmentSamples))
        {
            throw _host.Fatal($"The render state has no valid attachments: samples={attachmentSamples} colors={state.ColorCount} depth={state.Depth.HasTarget}.");
        }

        if (rendering.Layers == uint.MaxValue)
        {
            rendering.Layers = 1;
        }

        if (rendering.Width == 0 || rendering.Height == 0 || rendering.Layers == 0 || rendering.Width == uint.MaxValue || rendering.Height == uint.MaxValue)
        {
            throw _host.Fatal($"The rendering area is invalid: width={rendering.Width} height={rendering.Height} layers={rendering.Layers}.");
        }

        rendering.Samples = attachmentSamples == 0 ? 1 : attachmentSamples;
        return rendering;
    }
}
