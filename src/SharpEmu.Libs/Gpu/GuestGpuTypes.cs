// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;

namespace SharpEmu.Libs.Gpu;

// The types that cross the guest-GPU backend seam. Every field is either a neutral
// primitive (dimensions, counts, host pixel bytes) or a raw guest/AGC value (guest
// addresses, guest format and number-type codes, guest register bitfields). Host
// graphics-API values must never appear here: each backend owns the guest -> native
// translation for its API.

/// <summary>A guest texture referenced by a draw or dispatch. Format/NumberType/
/// TileMode/DstSelect/Type are raw guest descriptor codes. Depth is the
/// normalized volume depth (one for non-3D resources).</summary>
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
    GuestSampler Sampler = default,
    // Guest CPU write-tracker generation of the memory RgbaPixels was read
    // from; -1 when the range is untracked or the pixels were not read here.
    long WriteGeneration = -1,
    bool ArrayedView = false,
    uint ArrayLayers = 1,
    uint Type = 9,
    uint Depth = 1,
    // GPU-detile opt-in (SHARPEMU_GPU_DETILE): when Detile is non-null the AGC
    // layer skipped the CPU deswizzle and shipped the raw TILED bytes here in
    // TiledSource; the Vulkan backend detiles them on the GPU. RgbaPixels is
    // empty in that case. Both are neutral (no host graphics-API values).
    byte[]? TiledSource = null,
    DetileParams? Detile = null);

/// <summary>Raw guest sampler descriptor dwords, copied verbatim from guest memory.</summary>
internal readonly record struct GuestSampler(
    uint Word0,
    uint Word1,
    uint Word2,
    uint Word3);

/// <summary>Identity of a texture's content in a backend texture cache, keyed
/// entirely on raw guest descriptor values; the AGC layer uses it to skip texel
/// copies for content the backend already holds.</summary>
internal readonly record struct TextureContentIdentity(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    uint DstSelect,
    uint TileMode,
    uint Pitch,
    GuestSampler Sampler,
    bool Arrayed = false,
    uint ArrayLayers = 1,
    uint Type = 9,
    uint Depth = 1);

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
    bool Pooled,
    bool PerInstance = false,
    uint BaseRecord = 0)
{
    public ulong BindingOffsetBytes => (ulong)BaseRecord * Stride;
}

internal sealed record GuestIndexBuffer(
    byte[] Data,
    int Length,
    bool Is32Bit,
    bool Pooled,
    GuestIndexBufferLease? Lease = null)
{
    public bool LeaseReturned => Lease?.Returned ?? false;

    public bool TryReturnPooledData()
    {
        if (!Pooled)
        {
            return false;
        }

        if (Lease is not null)
        {
            return Lease.TryReturn();
        }

        GuestDataPool.Shared.Return(Data);
        return true;
    }
}

/// <summary>
/// This class owns one pool lease. Record copies share this class. Thus, only
/// one copy can return the array. A late copy cannot return a newer lease.
/// </summary>
internal sealed class GuestIndexBufferLease
{
    private readonly byte[] _data;
    private int _returned;

    public GuestIndexBufferLease(byte[] data)
    {
        _data = data;
    }

    public bool Returned => Volatile.Read(ref _returned) != 0;

    public bool TryReturn()
    {
        if (Interlocked.Exchange(ref _returned, 1) != 0)
        {
            return false;
        }

        GuestDataPool.Shared.Return(_data);
        return true;
    }
}

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
    bool Wireframe,
    bool DepthBiasEnable = false,
    float DepthBiasConstantFactor = 0,
    float DepthBiasClamp = 0,
    float DepthBiasSlopeFactor = 0,
    sbyte DepthBiasNegNumDbBits = -23,
    bool DepthBiasIsFloatFormat = true)
{
    public static GuestRasterState Default { get; } = new(false, false, false, false);

    public float ResolveDepthBiasConstantFactor(int hostDepthBits)
    {
        if (DepthBiasIsFloatFormat || hostDepthBits is not (16 or 24))
        {
            return DepthBiasConstantFactor;
        }

        return MathF.ScaleB(
            DepthBiasConstantFactor,
            hostDepthBits + DepthBiasNegNumDbBits);
    }
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

/// <summary>CB_BLEND_RED..ALPHA: the constant color referenced by the
/// CONSTANT_COLOR / CONSTANT_ALPHA blend factors. One constant serves every
/// render target of a draw; the hardware reset value is transparent black.</summary>
internal readonly record struct GuestBlendConstant(
    float Red,
    float Green,
    float Blue,
    float Alpha);

internal sealed record GuestRenderState(
    IReadOnlyList<GuestBlendState> Blends,
    GuestRect? Scissor,
    GuestViewport? Viewport,
    GuestRasterState Raster,
    GuestDepthState Depth,
    GuestBlendConstant BlendConstant = default)
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

/// <summary>Format, NumberType, ComponentSwap, and TileMode are raw guest render-target codes.</summary>
internal sealed record GuestRenderTarget(
    ulong Address,
    uint Width,
    uint Height,
    uint Format,
    uint NumberType,
    uint MipLevels = 1,
    uint ComponentSwap = 0,
    uint TileMode = 0);

/// <summary>Guest DB surface bound alongside a color render target.</summary>
internal sealed record GuestDepthTarget(
    ulong ReadAddress,
    ulong WriteAddress,
    uint Width,
    uint Height,
    uint GuestFormat,
    uint SwizzleMode,
    float ClearDepth,
    bool ReadOnly,
    ulong HtileAddress = 0,
    uint HtileBaseLayer = 0,
    bool HtileAcceleration = false,
    bool MetadataClear = false)
{
    public ulong Address => WriteAddress != 0 ? WriteAddress : ReadAddress;
}

/// <summary>Separates an attachment clear from a direct DB clear operation.</summary>
internal readonly record struct GuestDepthClearMode(
    bool ClearAttachment,
    bool SuppressDrawDepthState)
{
    public static GuestDepthClearMode Resolve(
        GuestDepthState depthState,
        GuestDepthTarget? depthTarget) => new(
            ClearAttachment: depthState.ClearEnable || depthTarget?.MetadataClear == true,
            SuppressDrawDepthState: depthState.ClearEnable);
}
