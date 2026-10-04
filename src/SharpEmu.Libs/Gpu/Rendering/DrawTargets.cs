// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// One color target of a draw: the resolved request, its slot and the cache image it found.
public struct ColorTargetState
{
    public ColorTargetResolution Resolution;
    public uint Slot;
    public ResourceSlotIdentifier Image;
    public ImageView View;

    // The host resolution the cache actually gave this image, which is one once the image
    // has been dropped back to guest resolution. Asking the scaling rules again would miss
    // that, so the render area, viewport and scissor read it from here.
    public float RenderScale;

    public ColorTargetState(in ColorTargetResolution resolution, uint slot, ResourceSlotIdentifier image, float renderScale)
    {
        Resolution = resolution;
        Slot = slot;
        Image = image;
        View = default;
        RenderScale = renderScale;
    }

    public readonly Extent2D HostExtent => new(
        RenderScalePolicy.ScaleLength(Resolution.Extent.Width, RenderScale),
        RenderScalePolicy.ScaleLength(Resolution.Extent.Height, RenderScale));
}

// The depth target of a draw; HasTarget is false when no depth or stencil state is active.
public struct DepthAttachmentState
{
    public DepthTargetState Target;
    public ResourceSlotIdentifier Image;
    public ImageView View;
    public bool MetadataClear;

    // See ColorTargetState.RenderScale.
    public float RenderScale;

    public DepthAttachmentState(in DepthTargetState target, ResourceSlotIdentifier image, float renderScale)
    {
        Target = target;
        Image = image;
        View = default;
        MetadataClear = false;
        RenderScale = renderScale;
    }

    public readonly uint HostWidth => RenderScalePolicy.ScaleLength(Target.Target.Width, RenderScale);

    public readonly uint HostHeight => RenderScalePolicy.ScaleLength(Target.Target.Height, RenderScale);

    public readonly bool HasTarget => Image.IsValid && Target.Target.Format != Format.Undefined;

    // The attachment loads a clear when the registers or the metadata say so.
    public readonly bool LoadClear => Target.State.DepthClearEnabled || MetadataClear;

    // The aspects and layout follow the load clear, so a metadata clear counts as a write.
    public readonly DepthStencilState LoadState => Target.State with { DepthClearEnabled = LoadClear };
}
