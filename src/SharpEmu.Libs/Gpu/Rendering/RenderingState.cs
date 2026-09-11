// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Rendering;

// One attachment of a dynamic rendering scope, with the format the pipeline must match.
public readonly record struct RenderingAttachment(
    ImageView View,
    ImageLayout Layout,
    Format Format,
    uint ClearWord0,
    uint ClearWord1,
    uint ClearWord2,
    uint ClearWord3,
    bool IsClear,
    bool HasDepth,
    bool DepthClear,
    bool HasStencil,
    bool StencilClear);

[InlineArray(RenderingState.ColorAttachmentCapacity)]
public struct RenderingColorAttachments
{
    private RenderingAttachment _element0;
}

// The dynamic rendering scope of a draw: its attachments, area, layers and sample count.
public struct RenderingState : IEquatable<RenderingState>
{
    public const int ColorAttachmentCapacity = 8;

    public RenderingColorAttachments ColorAttachments;
    public RenderingAttachment DepthStencilAttachment;
    public uint Width;
    public uint Height;
    public uint Layers;
    public uint ColorAttachmentCount;
    public uint Samples;

    public readonly Format DepthFormat => DepthStencilAttachment.HasDepth ? DepthStencilAttachment.Format : Format.Undefined;

    public readonly Format StencilFormat => DepthStencilAttachment.HasStencil ? DepthStencilAttachment.Format : Format.Undefined;

    public readonly bool Equals(RenderingState other)
    {
        if (Width != other.Width || Height != other.Height || Layers != other.Layers ||
            ColorAttachmentCount != other.ColorAttachmentCount || Samples != other.Samples ||
            !DepthStencilAttachment.Equals(other.DepthStencilAttachment))
        {
            return false;
        }

        for (var i = 0; i < ColorAttachmentCapacity; i++)
        {
            if (!ColorAttachments[i].Equals(other.ColorAttachments[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override readonly bool Equals(object? obj) => obj is RenderingState other && Equals(other);

    public override readonly int GetHashCode()
    {
        var hash = HashCode.Combine(Width, Height, Layers, ColorAttachmentCount, Samples, DepthStencilAttachment);
        for (var i = 0; i < ColorAttachmentCapacity; i++)
        {
            hash = HashCode.Combine(hash, ColorAttachments[i]);
        }

        return hash;
    }

    public static bool operator ==(RenderingState left, RenderingState right) => left.Equals(right);

    public static bool operator !=(RenderingState left, RenderingState right) => !left.Equals(right);
}
