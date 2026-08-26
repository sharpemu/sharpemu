// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial converts guest draw state to Vulkan state.

    private sealed partial class Presenter
    {
        private static BlendFactor ToVkBlendFactor(uint factor) =>
            factor switch
            {
                0 => BlendFactor.Zero,
                1 => BlendFactor.One,
                2 => BlendFactor.SrcColor,
                3 => BlendFactor.OneMinusSrcColor,
                4 => BlendFactor.SrcAlpha,
                5 => BlendFactor.OneMinusSrcAlpha,
                6 => BlendFactor.DstAlpha,
                7 => BlendFactor.OneMinusDstAlpha,
                8 => BlendFactor.DstColor,
                9 => BlendFactor.OneMinusDstColor,
                10 => BlendFactor.SrcAlphaSaturate,
                13 => BlendFactor.ConstantColor,
                14 => BlendFactor.OneMinusConstantColor,
                15 => BlendFactor.Src1Color,
                16 => BlendFactor.OneMinusSrc1Color,
                17 => BlendFactor.Src1Alpha,
                18 => BlendFactor.OneMinusSrc1Alpha,
                19 => BlendFactor.ConstantAlpha,
                20 => BlendFactor.OneMinusConstantAlpha,
                _ => BlendFactor.One,
            };

        private static BlendOp ToVkBlendOp(uint function) =>
            function switch
            {
                0 => BlendOp.Add,
                1 => BlendOp.Subtract,
                2 => BlendOp.Min,
                3 => BlendOp.Max,
                4 => BlendOp.ReverseSubtract,
                _ => BlendOp.Add,
            };


        private static ColorComponentFlags ToVkColorWriteMask(uint mask)
        {
            var flags = default(ColorComponentFlags);
            if ((mask & 1u) != 0)
            {
                flags |= ColorComponentFlags.RBit;
            }

            if ((mask & 2u) != 0)
            {
                flags |= ColorComponentFlags.GBit;
            }

            if ((mask & 4u) != 0)
            {
                flags |= ColorComponentFlags.BBit;
            }

            if ((mask & 8u) != 0)
            {
                flags |= ColorComponentFlags.ABit;
            }

            return flags;
        }

        private static GuestRect ClampScissor(GuestRect? scissor, Extent2D extent)
        {
            if (scissor is not { } guestRect)
            {
                return new GuestRect(0, 0, extent.Width, extent.Height);
            }

            var rect = _renderResolutionScale == 1.0
                ? guestRect
                : new GuestRect(
                    (int)Math.Round(guestRect.X * _renderResolutionScale),
                    (int)Math.Round(guestRect.Y * _renderResolutionScale),
                    Math.Max(1u, (uint)Math.Round(guestRect.Width * _renderResolutionScale)),
                    Math.Max(1u, (uint)Math.Round(guestRect.Height * _renderResolutionScale)));

            var left = Math.Clamp(rect.X, 0, checked((int)extent.Width));
            var top = Math.Clamp(rect.Y, 0, checked((int)extent.Height));
            var right = Math.Clamp(
                rect.X + checked((int)rect.Width),
                left,
                checked((int)extent.Width));
            var bottom = Math.Clamp(
                rect.Y + checked((int)rect.Height),
                top,
                checked((int)extent.Height));
            return new GuestRect(
                left,
                top,
                checked((uint)(right - left)),
                checked((uint)(bottom - top)));
        }

        private static readonly float ViewportDebugEpsilon = float.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_VIEWPORT_EPSILON"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var viewportEpsilon)
            ? viewportEpsilon
            : 0f;

        private static Viewport ClampViewport(GuestViewport? viewport, Extent2D extent)
        {
            if (viewport is not { } guestRect)
            {
                return new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
            }

            var scale = (float)_renderResolutionScale;
            var rect = scale == 1f
                ? guestRect
                : guestRect with
                {
                    X = guestRect.X * scale,
                    Y = guestRect.Y * scale,
                    Width = guestRect.Width * scale,
                    Height = guestRect.Height * scale,
                };

            // Do NOT trim the rectangle to the render target: Vulkan allows
            // viewports that extend beyond the framebuffer (rendering is
            // confined by the scissor), and trimming changes the guest's
            // scale and offset. That skews texel addressing on 1:1 draws -
            // source rows get skipped or duplicated - which shredded the
            // game's pre-composed tile surfaces. Only guard what the spec
            // requires: a positive width and hardware viewport bounds.
            const float bound = 32767f;
            var x = Math.Clamp(rect.X, -bound, bound);
            var y = Math.Clamp(rect.Y, -bound, bound);
            var width = Math.Clamp(rect.Width, 1e-3f, bound);
            var height = Math.Clamp(rect.Height, -bound, bound);
            if (height == 0f)
            {
                height = extent.Height;
            }

            var minDepth = Math.Clamp(rect.MinDepth, 0f, 1f);
            var maxDepth = Math.Clamp(rect.MaxDepth, minDepth, 1f);
            return new Viewport(x, y, width, height, minDepth, maxDepth);
        }



        private static readonly bool _forceTitleDefaultBlend =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DEFAULT_BLEND") == "1";
        private static readonly bool _forceTitleDefaultViewportScissor =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DEFAULT_VIEWPORT_SCISSOR") == "1";
        private static readonly bool _forceTitleDisableCull =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DISABLE_CULL") == "1";
        private static readonly bool _forceTitleDisableDepth =
            Environment.GetEnvironmentVariable("SHARPEMU_FORCE_TITLE_DISABLE_DEPTH") == "1";
        private static readonly bool _traceTitleState =
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_TITLE_STATE") == "1";
    }
}
