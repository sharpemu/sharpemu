// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    // Every draw resolves its targets against layer zero of the view range.
    private const uint DrawLayerOffset = 0;

    // Finds the color and depth targets; false when the draw has nothing to render into.
    private bool TryResolveDrawTargets(RegisterBanks banks, in DrawCall draw, ref DrawState state)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawTargetResolution);
        var context = banks.Context;
        if (TryResolveMultisampleColor(context))
        {
            TraceDrawDisposition(banks, in draw, "multisample-color-resolve");
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
        if (state.ColorCount == 0 && !state.Depth.HasTarget && !state.PixelActive &&
            VertexStageWritesMemory(banks, ref state))
        {
            // No attachment and no pixel stage, but the vertex (or emulated geometry) stage
            // still stores to memory: a GPU-culling or stream-out pass whose only output is
            // the buffer a later indirect draw reads. Rasterization has nothing to write, so
            // the draw runs for its stores alone.
            TraceDrawDisposition(banks, in draw, "vertex-stores-only");
            return true;
        }

        if (state.ColorCount == 0 && !state.Depth.HasTarget && !state.PixelActive)
        {
            TraceDrawDisposition(banks, in draw, "no-framebuffer");
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

    // Whether the bound vertex (or emulated geometry) stage stores to a buffer or a
    // storage image, which is the only observable effect a draw without attachments
    // and without a pixel stage can have.
    private bool VertexStageWritesMemory(RegisterBanks banks, ref DrawState state)
    {
        if (banks.Shader.Vertex.ExportAddress == 0)
        {
            return false;
        }

        ResolveShaderPrograms(banks, ref state);
        var stage = state.Programs.VertexInput.Stage;
        return stage.Program is { } program && (WritesStorageImage(program) || HasBufferWrites(stage));
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

    // DB_RENDER_OVERRIDE can turn a draw packet into a depth/stencil read-to-write copy.
    private bool TryDepthStencilCopy(ContextRegisters context)
    {
        if (context.ColorControl.Mode != 0)
        {
            return false;
        }

        ref readonly var words = ref context.DepthTarget;
        var renderOverride = context.DepthRenderOverride;
        var depthCopy =
            renderOverride.ForceZDirty &&
            renderOverride.ForceZValid &&
            words.DepthFormat != GuestDepthFormat.Invalid &&
            words.ZReadBase != 0 &&
            words.ZWriteBase != 0 &&
            words.ZReadBase != words.ZWriteBase;
        var stencilCopy =
            renderOverride.ForceStencilDirty &&
            renderOverride.ForceStencilValid &&
            words.StencilFormat != GuestStencilFormat.Invalid &&
            words.StencilReadBase != 0 &&
            words.StencilWriteBase != 0 &&
            words.StencilReadBase != words.StencilWriteBase;
        if (!depthCopy && !stencilCopy)
        {
            return false;
        }

        var read = ImageRequestBuilders.DepthTargetCopy(in words, _host.FormatSupport, writeBuffer: false)
            ?? throw _host.Fatal("A depth/stencil copy has no readable depth target.");
        var write = ImageRequestBuilders.DepthTargetCopy(in words, _host.FormatSupport, writeBuffer: true)
            ?? throw _host.Fatal("A depth/stencil copy has no writable depth target.");
        var readRequest = read.Request;
        var writeRequest = write.Request;
        var source = _host.FindImage(ref readRequest, exactFormat: true);
        var destination = _host.FindImage(ref writeRequest, exactFormat: true);
        _host.BindRenderTarget(source);
        _host.BindRenderTarget(destination);
        if (source == destination || read.Format != write.Format)
        {
            throw _host.Fatal(
                $"A depth/stencil copy must use distinct images with the same format: source={source.Index} destination={destination.Index} readFormat={(int)read.Format} writeFormat={(int)write.Format}.");
        }

        var range = new SubresourceRange(
            readRequest.View.BaseLevel,
            readRequest.View.LevelCount,
            readRequest.View.BaseLayer,
            readRequest.View.LayerCount);
        var extent = new Extent3D(write.Width, write.Height, 1);
        var aspects = (depthCopy ? ImageAspectFlags.DepthBit : 0) | (stencilCopy ? ImageAspectFlags.StencilBit : 0);
        _host.MarkGpuWritten(destination);
        _host.CopyDepthStencilImage(source, destination, in range, in extent, aspects);
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

    // A depth pass can leave a color target bound that it never writes (target mask 0, or a
    // slot the pixel program does not export). Hardware bounds such a draw only by the
    // scissor, but a host attachment also bounds the render area: a 1024x1024 color target
    // left bound by Astro Bot's depth clear kept the clear out of most of a 1080p depth buffer.
    // The pixel outputs drop the same slots (ShaderPipelineCache.ResolveBoundTargets), so host
    // locations stay dense and aligned with the remaining attachments.
    private static void DropUnwrittenColorTargets(ContextRegisters context, ref DrawState state, ShaderProgramInfo? pixelProgram)
    {
        if (!state.Depth.HasTarget)
        {
            return;
        }

        var exportMasks = pixelProgram?.PixelColorExportMasks ?? 0u;
        var kept = 0u;
        for (var i = 0; i < state.ColorCount; i++)
        {
            ref readonly var target = ref state.Colors[i];
            if (IsUnwrittenColorTarget(context, target.Slot, target.Resolution.ExportMapping, exportMasks))
            {
                continue;
            }

            state.Colors[(int)kept++] = target;
        }

        state.ColorCount = kept;
    }

    internal static bool IsUnwrittenColorTarget(ContextRegisters context, uint slot, ColorComponentMap exportMapping, uint pixelColorExportMasks) =>
        exportMapping.ApplyMask(context.RenderTargetMaskForSlot(slot)) == 0 ||
        ((pixelColorExportMasks >> (int)(slot * 4)) & 0xFu) == 0;

    // Acquires every attachment through the host and assembles the rendering scope.
    private RenderingState AcquireAttachments(ref DrawState state)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawAttachmentPreparation);
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
            var layout = DepthStencilState.AttachmentLayoutFor(
                loadState,
                target.Format,
                _host.SamplesDepthAttachment(in depth),
                _host.DepthAttachmentLayout(in depth));
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
