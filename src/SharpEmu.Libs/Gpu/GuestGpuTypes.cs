// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu;

// The types that cross the guest-GPU backend seam. Every field is either a neutral
// primitive (dimensions, counts, host pixel bytes) or a raw guest/AGC value (guest
// addresses, guest format and number-type codes, guest register bitfields). Host
// graphics-API values must never appear here: each backend owns the guest -> native
// translation for its API.

/// <summary>A guest texture referenced by a draw or dispatch. Format/NumberType/
/// TileMode/DstSelect are raw guest descriptor codes.</summary>
internal sealed record GuestDrawTexture(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    byte[] RgbaPixels,
    bool IsFallback,
    bool IsStorage,
    uint MipLevels = 1,
    uint MipLevel = 0,
    uint BaseMipLevel = 0,
    uint ResourceMipLevels = 1,
    uint Pitch = 0,
    uint TileMode = 0,
    uint DstSelect = 0xFAC,
    GuestSampler Sampler = default);

/// <summary>Raw guest sampler descriptor dwords, copied verbatim from guest memory.</summary>
internal readonly record struct GuestSampler(
    uint Word0,
    uint Word1,
    uint Word2,
    uint Word3);

internal sealed record GuestMemoryBuffer(
    ulong BaseAddress,
    byte[] Data,
    int Length,
    bool Pooled,
    bool Writable = false,
    bool WriteBackToGuest = true);

/// <summary>DataFormat/NumberFormat are raw guest vertex-attribute codes.</summary>
internal sealed record GuestVertexBuffer(
    uint Location,
    uint ComponentCount,
    uint DataFormat,
    uint NumberFormat,
    ulong BaseAddress,
    uint Stride,
    uint OffsetBytes,
    byte[] Data,
    int Length,
    bool Pooled);

internal sealed record GuestIndexBuffer(
    byte[] Data,
    int Length,
    bool Is32Bit,
    bool Pooled);

internal readonly record struct GuestRect(
    int X,
    int Y,
    uint Width,
    uint Height);

internal readonly record struct GuestViewport(
    float X,
    float Y,
    float Width,
    float Height,
    float MinDepth,
    float MaxDepth);

internal readonly record struct GuestRasterState(
    bool CullFront,
    bool CullBack,
    bool FrontFaceClockwise,
    bool Wireframe)
{
    public static GuestRasterState Default { get; } = new(false, false, false, false);
}

// CompareOp uses the GCN DB_DEPTH_CONTROL ZFUNC encoding, which matches the
// Vulkan CompareOp ordering (0=Never through 7=Always).
internal readonly record struct GuestDepthState(
    bool TestEnable,
    bool WriteEnable,
    uint CompareOp,
    bool ClearEnable = false)
{
    public static GuestDepthState Default { get; } = new(false, false, 7, false);
}

/// <summary>Factors/funcs are raw guest CB_BLEND*_CONTROL register bitfields; the
/// defaults (1/0) are the guest ONE/ZERO codes.</summary>
internal readonly record struct GuestBlendState(
    bool Enable,
    uint ColorSrcFactor,
    uint ColorDstFactor,
    uint ColorFunc,
    uint AlphaSrcFactor,
    uint AlphaDstFactor,
    uint AlphaFunc,
    bool SeparateAlphaBlend,
    uint WriteMask)
{
    public static GuestBlendState Default { get; } = new(
        Enable: false,
        ColorSrcFactor: 1,
        ColorDstFactor: 0,
        ColorFunc: 0,
        AlphaSrcFactor: 1,
        AlphaDstFactor: 0,
        AlphaFunc: 0,
        SeparateAlphaBlend: false,
        WriteMask: 0xFu);
}

internal sealed record GuestRenderState(
    IReadOnlyList<GuestBlendState> Blends,
    GuestRect? Scissor,
    GuestViewport? Viewport,
    GuestRasterState Raster,
    GuestDepthState Depth)
{
    public static GuestRenderState Default { get; } = new(
        [GuestBlendState.Default],
        Scissor: null,
        Viewport: null,
        GuestRasterState.Default,
        GuestDepthState.Default);

    public GuestBlendState Blend =>
        Blends.Count == 0 ? GuestBlendState.Default : Blends[0];
}

/// <summary>Format/NumberType are raw guest render-target register codes.</summary>
internal sealed record GuestRenderTarget(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    uint MipLevels = 1);

/// <summary>Guest DB surface bound alongside a color render target.</summary>
internal sealed record GuestDepthTarget(
    ulong ReadAddress,
    ulong WriteAddress,
    uint Width,
    uint Height,
    uint GuestFormat,
    uint SwizzleMode,
    float ClearDepth,
    bool ReadOnly)
{
    public ulong Address => WriteAddress != 0 ? WriteAddress : ReadAddress;
}
