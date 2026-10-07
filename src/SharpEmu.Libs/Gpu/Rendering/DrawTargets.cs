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

    public ColorTargetState(in ColorTargetResolution resolution, uint slot, ResourceSlotIdentifier image)
    {
        Resolution = resolution;
        Slot = slot;
        Image = image;
        View = default;
    }
}

// The depth target of a draw; HasTarget is false when no depth or stencil state is active.
public struct DepthAttachmentState
{
    public DepthTargetState Target;
    public ResourceSlotIdentifier Image;
    public ImageView View;
    public bool MetadataClear;

    public DepthAttachmentState(in DepthTargetState target, ResourceSlotIdentifier image)
    {
        Target = target;
        Image = image;
        View = default;
        MetadataClear = false;
    }

    public readonly bool HasTarget => Image.IsValid && Target.Target.Format != Format.Undefined;

    // The attachment loads a clear when the registers or the metadata say so.
    public readonly bool LoadClear => Target.State.DepthClearEnabled || MetadataClear;

    // The aspects and layout follow the load clear, so a metadata clear counts as a write.
    public readonly DepthStencilState LoadState => Target.State with { DepthClearEnabled = LoadClear };
}
