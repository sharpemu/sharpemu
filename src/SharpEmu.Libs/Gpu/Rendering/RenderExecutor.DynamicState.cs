// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.VideoOut;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

public sealed partial class RenderExecutor
{
    private const uint MaxViewportDimension = 16384;

    // A guest bias for a fixed-point depth format scales by the host depth bits.
    public static float DepthBiasConstantFactor(float guestFactor, in PolygonOffsetRegisters offset, Format hostDepthFormat)
    {
        if (offset.DepthIsFloat)
        {
            return guestFactor;
        }

        var hostDepthBits = hostDepthFormat switch
        {
            Format.D16Unorm or Format.D16UnormS8Uint => 16,
            Format.D24UnormS8Uint => 24,
            _ => 0,
        };
        return hostDepthBits == 0 ? guestFactor : MathF.ScaleB(guestFactor, hostDepthBits + offset.NegativeDepthBits);
    }

    // The host resolution of the attachments a draw writes; all of them share one rule, so
    // the first bound target decides.
    private static float AttachmentRenderScale(in DrawState state)
    {
        if (state.ColorCount > 0 && state.Colors[0].Image.IsValid)
        {
            return state.Colors[0].RenderScale;
        }

        return state.Depth.Image.IsValid ? state.Depth.RenderScale : 1f;
    }

    // The dynamic state of a draw from the context bank and the resolved targets.
    private DynamicDrawState BuildDynamicState(ContextRegisters context, in DrawState state)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawDynamicStatePreparation);
        var viewportRegisters = context.ScreenViewport;
        var limits = _host.Limits;
        uint framebufferWidth;
        uint framebufferHeight;
        // The guest describes its viewport and scissor in guest pixels; the attachments decide
        // how many host texels one of those covers.
        var renderScale = AttachmentRenderScale(in state);
        if (state.ColorCount > 0 && state.Colors[0].Image.IsValid)
        {
            framebufferWidth = state.Colors[0].Resolution.Extent.Width;
            framebufferHeight = state.Colors[0].Resolution.Extent.Height;
        }
        else if (state.Depth.Image.IsValid)
        {
            framebufferWidth = state.Depth.Target.Target.Width;
            framebufferHeight = state.Depth.Target.Target.Height;
        }
        else
        {
            framebufferWidth = limits.MaxFramebufferWidth;
            framebufferHeight = limits.MaxFramebufferHeight;
        }

        var scissor = ResolveScissor(viewportRegisters, context.ScanMode, framebufferWidth, framebufferHeight);
        if (renderScale != 1f)
        {
            scissor = ScaleScissor(scissor, renderScale,
                RenderScalePolicy.ScaleLength(framebufferWidth, renderScale),
                RenderScalePolicy.ScaleLength(framebufferHeight, renderScale));
        }

        ref readonly var viewport = ref viewportRegisters.Viewports[0];
        float viewportX;
        float viewportY;
        float viewportWidth;
        float viewportHeight;
        if (context.Clip.ClipDisable)
        {
            // The vertex program bakes the transform onto the same reference extent, so
            // multiplying only the host viewport puts guest pixels at host positions.
            viewportX = 0;
            viewportY = 0;
            viewportWidth = RenderScalePolicy.ClipSpaceReferenceExtent(Math.Min(limits.MaxViewportWidth, MaxViewportDimension)) * renderScale;
            viewportHeight = RenderScalePolicy.ClipSpaceReferenceExtent(Math.Min(limits.MaxViewportHeight, MaxViewportDimension)) * renderScale;
        }
        else
        {
            viewportX = (viewport.XOffset - viewport.XScale) * renderScale;
            viewportY = (viewport.YOffset - viewport.YScale) * renderScale;
            viewportWidth = viewport.XScale * 2f * renderScale;
            viewportHeight = viewport.YScale * 2f * renderScale;
        }

        var lineWidth = context.LineWidth;
        if (lineWidth != 1f)
        {
            if (RenderTrace.Enabled && RenderTrace.LineWidthClamp())
            {
                RenderTrace.Write($"Clamping the line width to 1.0 because wide lines are not enabled: lineWidth={lineWidth}");
            }

            lineWidth = 1f;
        }

        var depthTarget = state.Depth.HasTarget ? state.Depth.Target : default;
        var depthState = depthTarget.State;
        var depthFormat = depthTarget.Target.Format;
        var mode = context.RasterMode;
        var polygonOffset = context.PolygonOffset;
        var useFront = mode.PolygonOffsetFrontEnable && !mode.CullFront;
        var useBack = mode.PolygonOffsetBackEnable && !mode.CullBack;
        var depthBiasEnabled = useFront || useBack;
        var depthBiasConstant = 0f;
        var depthBiasSlope = 0f;
        if (depthBiasEnabled)
        {
            // One host bias serves both faces; a visible front face wins.
            var guestConstant = useFront ? polygonOffset.FrontOffset : polygonOffset.BackOffset;
            depthBiasConstant = DepthBiasConstantFactor(guestConstant, in polygonOffset, depthFormat);
            depthBiasSlope = (useFront ? polygonOffset.FrontScale : polygonOffset.BackScale) / 16f;
        }

        byte colorWriteMask = 0;
        for (var i = 0; i < state.ColorCount; i++)
        {
            if (context.RenderTargetMaskForSlot(state.Colors[i].Slot) != 0)
            {
                colorWriteMask |= (byte)(1 << i);
            }
        }

        var blend = context.BlendColor;
        return new DynamicDrawState(
            viewportX,
            viewportY,
            viewportWidth,
            viewportHeight,
            viewport.ZOffset - (context.Clip.DirectXClipSpace ? 0f : viewport.ZScale),
            viewport.ZScale + viewport.ZOffset,
            scissor,
            lineWidth,
            blend.Red,
            blend.Green,
            blend.Blue,
            blend.Alpha,
            depthState.DepthTestEnabled,
            depthState.DepthWriteEnabled && !depthState.DepthClearEnabled,
            depthState.DepthCompare,
            depthBiasEnabled,
            depthBiasConstant,
            depthBiasEnabled ? polygonOffset.Clamp : 0f,
            depthBiasSlope,
            depthState.StencilTestEnabled,
            depthState.FrontMasks,
            depthState.BackMasks,
            state.ColorCount,
            colorWriteMask);
    }

    // A guest scissor in host texels. The edges round outward so no guest pixel the game
    // asked for loses its host coverage, and the result stays inside the attachment.
    public static ScissorRectangle ScaleScissor(ScissorRectangle scissor, float scale, uint hostWidth, uint hostHeight)
    {
        static int Floor(int value, float scale) => (int)MathF.Floor(value * scale);
        static int Ceiling(int value, float scale) => (int)MathF.Ceiling(value * scale);
        var left = Math.Clamp(Floor(scissor.Left, scale), 0, (int)hostWidth);
        var top = Math.Clamp(Floor(scissor.Top, scale), 0, (int)hostHeight);
        return new ScissorRectangle(
            left,
            top,
            Math.Clamp(Ceiling(scissor.Right, scale), left, (int)hostWidth),
            Math.Clamp(Ceiling(scissor.Bottom, scale), top, (int)hostHeight));
    }

    // The screen, window, generic, viewport and clip rectangles intersected and clamped to the extent.
    public static ScissorRectangle ResolveScissor(ScreenViewportRegisters viewport, in ScanModeRegisters scanMode, uint width, uint height)
    {
        var screen = new ScissorRectangle(viewport.ScreenScissorLeft, viewport.ScreenScissorTop, viewport.ScreenScissorRight, viewport.ScreenScissorBottom);
        var final = screen;
        if (!screen.IsSet)
        {
            final = new ScissorRectangle(0, 0, (int)width, (int)height);
            if (RenderTrace.Enabled && RenderTrace.ScissorDefault())
            {
                RenderTrace.Write($"Defaulting an unset screen scissor to the framebuffer extent: width={width} height={height}");
            }
        }

        var window = new ScissorRectangle(viewport.WindowScissorLeft, viewport.WindowScissorTop, viewport.WindowScissorRight, viewport.WindowScissorBottom);
        if (window.IsSet)
        {
            if (viewport.WindowScissorWindowOffsetEnable)
            {
                window = window.Offset(viewport.WindowOffsetX, viewport.WindowOffsetY);
            }

            final = final.Intersect(in window);
        }

        var generic = new ScissorRectangle(viewport.GenericScissorLeft, viewport.GenericScissorTop, viewport.GenericScissorRight, viewport.GenericScissorBottom);
        if (generic.IsSet)
        {
            if (viewport.GenericScissorWindowOffsetEnable)
            {
                generic = generic.Offset(viewport.WindowOffsetX, viewport.WindowOffsetY);
            }

            final = final.Intersect(in generic);
        }

        ref readonly var first = ref viewport.Viewports[0];
        var viewportScissor = new ScissorRectangle(first.ScissorLeft, first.ScissorTop, first.ScissorRight, first.ScissorBottom);
        if (scanMode.ViewportScissorEnable && viewportScissor.IsSet)
        {
            if (first.ScissorWindowOffsetEnable)
            {
                viewportScissor = viewportScissor.Offset(viewport.WindowOffsetX, viewport.WindowOffsetY);
            }

            final = final.Intersect(in viewportScissor);
        }

        if (viewport.ClipRectangleRule == 0)
        {
            final = new ScissorRectangle(0, 0, 0, 0);
        }
        else if (viewport.ClipRectangleRule != 0xFFFF)
        {
            if (TryClipRuleToIntersectionMask(viewport.ClipRectangleRule, out var clipMask))
            {
                for (var i = 0; i < ScreenViewportRegisters.ClipRectangleCount; i++)
                {
                    if ((clipMask & (1 << i)) == 0)
                    {
                        continue;
                    }

                    var clip = new ScissorRectangle(viewport.ClipRectangleLeft[i], viewport.ClipRectangleTop[i], viewport.ClipRectangleRight[i], viewport.ClipRectangleBottom[i]);
                    if (viewport.ClipRectangleWindowOffsetEnable[i])
                    {
                        clip = clip.Offset(viewport.WindowOffsetX, viewport.WindowOffsetY);
                    }

                    final = final.Intersect(in clip);
                }
            }
            else if (RenderTrace.Enabled && RenderTrace.ClipRuleWarning())
            {
                RenderTrace.Write($"Leaving the scissor unchanged for an unsupported clip rectangle rule: rule=0x{viewport.ClipRectangleRule:X4}");
            }
        }

        return ClampScissor(final, width, height);
    }

    private static ScissorRectangle ClampScissor(ScissorRectangle rectangle, uint width, uint height)
    {
        var maxRight = (int)width;
        var maxBottom = (int)height;
        var clamped = new ScissorRectangle(
            Math.Clamp(rectangle.Left, 0, maxRight),
            Math.Clamp(rectangle.Top, 0, maxBottom),
            Math.Clamp(rectangle.Right, 0, maxRight),
            Math.Clamp(rectangle.Bottom, 0, maxBottom));
        return clamped.IsValid ? clamped : clamped with { Right = clamped.Left, Bottom = clamped.Top };
    }

    // A rule is the set of rectangle combinations that pass; only "all of a subset" rules are supported.
    private static bool TryClipRuleToIntersectionMask(ushort rule, out byte mask)
    {
        for (var candidate = 0u; candidate < 16; candidate++)
        {
            ushort combinations = 0;
            for (var combination = 0u; combination < 16; combination++)
            {
                if ((combination & candidate) == candidate)
                {
                    combinations |= (ushort)(1u << (int)combination);
                }
            }

            if (combinations == rule)
            {
                mask = (byte)candidate;
                return true;
            }
        }

        mask = 0;
        return false;
    }
}
