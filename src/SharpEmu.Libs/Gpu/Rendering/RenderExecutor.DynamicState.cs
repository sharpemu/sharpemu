// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
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

    // The dynamic state of a draw from the context bank and the resolved targets.
    private DynamicDrawState ResolveDynamicState(RegisterBanks banks, in DrawState state, uint writtenColors, ulong submitId, ulong packetAddress)
    {
        var resolution = _drawResolution;
        var boundSlots = 0u;
        for (var remaining = writtenColors; remaining != 0; remaining &= remaining - 1)
        {
            boundSlots |= 1u << (int)state.Colors[System.Numerics.BitOperations.TrailingZeroCount(remaining)].Slot;
        }

        var colorCount = (uint)System.Numerics.BitOperations.PopCount(writtenColors);

        var key = new DynamicStateKey(
            banks.ContextStamp,
            banks.Stamp(RegisterStamp.ContextDynamic),
            resolution.TargetVersion,
            colorCount,
            boundSlots,
            colorCount > 0,
            state.Depth.HasTarget);
        if (key == resolution.DynamicKey && ScissorTrace.Instance is not { Active: true })
        {
            return resolution.Dynamic;
        }

        resolution.Dynamic = BuildDynamicState(banks, in state, writtenColors, submitId, packetAddress);
        resolution.DynamicKey = key;
        return resolution.Dynamic;
    }

    private DynamicDrawState BuildDynamicState(RegisterBanks banks, in DrawState state, uint writtenColors, ulong submitId, ulong packetAddress)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawDynamicStatePreparation);
        var context = banks.Context;
        var viewportRegisters = context.ScreenViewport;
        var limits = _host.Limits;
        uint framebufferWidth;
        uint framebufferHeight;
        if (writtenColors != 0)
        {
            ref readonly var first = ref state.Colors[System.Numerics.BitOperations.TrailingZeroCount(writtenColors)];
            framebufferWidth = first.Resolution.Extent.Width;
            framebufferHeight = first.Resolution.Extent.Height;
        }
        else if (state.Depth.HasTarget)
        {
            framebufferWidth = state.Depth.Target.Target.Width;
            framebufferHeight = state.Depth.Target.Target.Height;
        }
        else
        {
            framebufferWidth = limits.MaxFramebufferWidth;
            framebufferHeight = limits.MaxFramebufferHeight;
        }

        var scissor = ResolveScissor(viewportRegisters, context.ScanMode, framebufferWidth, framebufferHeight, recoverStaleWindow: true);
        ref readonly var viewport = ref viewportRegisters.Primary;
        float viewportX;
        float viewportY;
        float viewportWidth;
        float viewportHeight;
        if (context.Clip.ClipDisable)
        {
            viewportX = 0;
            viewportY = 0;
            viewportWidth = Math.Min(limits.MaxViewportWidth, MaxViewportDimension);
            viewportHeight = Math.Min(limits.MaxViewportHeight, MaxViewportDimension);
        }
        else
        {
            viewportX = viewport.XOffset - viewport.XScale;
            viewportY = viewport.YOffset - viewport.YScale;
            viewportWidth = viewport.XScale * 2f;
            viewportHeight = viewport.YScale * 2f;
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
        var position = 0;
        for (var remaining = writtenColors; remaining != 0; remaining &= remaining - 1, position++)
        {
            if (context.RenderTargetMaskForSlot(state.Colors[System.Numerics.BitOperations.TrailingZeroCount(remaining)].Slot) != 0)
            {
                colorWriteMask |= (byte)(1 << position);
            }
        }

        var blend = context.BlendColor;
        var dynamicState = new DynamicDrawState(
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
            (uint)position,
            colorWriteMask,
            context.DepthCountControl,
            depthState.DepthBoundsTestEnabled,
            depthState.DepthMinBounds,
            depthState.DepthMaxBounds);
        if (ScissorTrace.Instance is { Active: true } trace)
            trace.Draw(banks, submitId, packetAddress, framebufferWidth, framebufferHeight,
                state.ColorCount > 0 ? state.Colors[0].Resolution.BaseAddress : 0,
                state.Depth.HasTarget ? state.Depth.Target.Target.DepthAddress : 0, in dynamicState);
        return dynamicState;
    }

    // The screen, window, generic, viewport and clip rectangles intersected and clamped to the extent.
    public static ScissorRectangle ResolveScissor(ScreenViewportRegisters viewport, in ScanModeRegisters scanMode, uint width, uint height,
        bool recoverStaleWindow = false)
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
        // Some AGC submissions restore a full-target viewport after a smaller offscreen pass
        // without rewriting PA_SC_WINDOW_SCISSOR. The stale window can be smaller in only
        // one dimension (e.g. 75% of the target height), so check both axes independently.
        if (window.IsSet && !(recoverStaleWindow && IsStaleOffscreenWindow(viewport, in scanMode, width, height, in window)))
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

        ref readonly var first = ref viewport.Primary;
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

    private static bool IsStaleOffscreenWindow(ScreenViewportRegisters viewport, in ScanModeRegisters scanMode,
        uint width, uint height, in ScissorRectangle window)
    {
        if (!scanMode.ViewportScissorEnable || viewport.WindowScissorWindowOffsetEnable ||
            width == 0 || height == 0 || window.Left != 0 || window.Top != 0 ||
            window.Right <= 0 || window.Bottom <= 0 ||
            window.Right >= width && window.Bottom >= height)
        {
            return false;
        }

        ref readonly var active = ref viewport.Primary;
        return !active.ScissorWindowOffsetEnable &&
            active.ScissorLeft == 0 && active.ScissorTop == 0 &&
            active.ScissorRight == width && active.ScissorBottom == height &&
            MathF.Abs(active.XScale * 2f) >= width && MathF.Abs(active.YScale * 2f) >= height &&
            MathF.Abs(active.XOffset - active.XScale) < 1f &&
            MathF.Abs(active.YOffset - MathF.Abs(active.YScale)) < 1f;
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
