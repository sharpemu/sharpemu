// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Images;

// The raw color-target registers of one slot; the fields decode lazily as the request builder needs them.
public readonly record struct ColorTargetWords(
    ulong BaseAddress,
    uint View,
    uint Info,
    uint Attrib,
    uint Attrib2,
    uint Attrib3,
    uint DccControl,
    ulong CmaskAddress,
    ulong FmaskAddress,
    uint ClearWord0,
    ulong DccAddress)
{
    public uint SliceStart => View & 0x1FFF;
    public uint SliceMax => (View >> 13) & 0x1FFF;
    public uint MipLevel => (View >> 26) & 0xF;
    public ChannelLayout Layout => (ChannelLayout)((Info >> 2) & 0x1F);
    public ChannelType NumberType => (ChannelType)((Info >> 8) & 0x7);
    public ChannelOrder Order => (ChannelOrder)((Info >> 11) & 0x3);
    public bool FastClear => ((Info >> 13) & 0x1) != 0;
    public bool FmaskCompression => ((Info >> 14) & 0x1) != 0;
    public bool FmaskCompressionDisabled => ((Info >> 26) & 0x1) != 0;
    public bool FmaskOneFragment => ((Info >> 27) & 0x1) != 0;
    public bool DccEnabled => ((Info >> 28) & 0x1) != 0;
    public uint SamplesLog2 => (Attrib >> 12) & 0x7;
    public uint FragmentsLog2 => (Attrib >> 15) & 0x3;
    public uint Height => Attrib2 & 0x3FFF;
    public uint Width => (Attrib2 >> 14) & 0x3FFF;
    public uint MaxMip => (Attrib2 >> 28) & 0xF;
    public uint Depth => Attrib3 & 0x1FFF;
    public GuestTileMode TileMode => (GuestTileMode)((Attrib3 >> 14) & 0x1F);
    public uint Dimension => (Attrib3 >> 24) & 0x3;
    public bool DataWriteOnDccClearToRegister => ((DccControl >> 19) & 0x1) != 0;
}

// The raw depth-target registers; the sizes register is valid only after the guest wrote it.
public readonly record struct DepthTargetWords(
    uint ZInfo,
    uint StencilInfo,
    uint DepthView,
    uint DepthSizeXy,
    bool DepthSizeValid,
    uint HtileSurface,
    uint RenderControl,
    uint DepthControl,
    ulong ZReadBase,
    ulong ZWriteBase,
    ulong StencilReadBase,
    ulong StencilWriteBase,
    ulong HtileBase)
{
    public GuestDepthFormat DepthFormat => (GuestDepthFormat)(ZInfo & 0x3);
    public uint SamplesLog2 => (ZInfo >> 2) & 0x3;
    public bool ZPartiallyResident => ((ZInfo >> 12) & 0x1) != 0;
    public uint MaxMip => (ZInfo >> 16) & 0xF;
    public bool ZExpClear => ((ZInfo >> 27) & 0x1) != 0;
    public bool HtileAcceleration => ((ZInfo >> 29) & 0x1) != 0;
    public GuestStencilFormat StencilFormat => (GuestStencilFormat)(StencilInfo & 0x1);
    public bool StencilPartiallyResident => ((StencilInfo >> 12) & 0x1) != 0;
    public bool StencilExpClear => ((StencilInfo >> 27) & 0x1) != 0;
    public bool HtileStencilDisabled => ((StencilInfo >> 29) & 0x1) != 0;
    public uint SliceStart => (DepthView & 0x7FF) | (((DepthView >> 11) & 0x3) << 11);
    public uint SliceMax => ((DepthView >> 13) & 0x7FF) | (((DepthView >> 30) & 0x3) << 11);
    public bool DepthWriteDisabled => ((DepthView >> 24) & 0x1) != 0;
    public bool StencilWriteDisabled => ((DepthView >> 25) & 0x1) != 0;
    public uint ViewMipLevel => (DepthView >> 26) & 0xF;
    public uint XMax => DepthSizeXy & 0x3FFF;
    public uint YMax => (DepthSizeXy >> 16) & 0x3FFF;
    public uint ShadingRateEncoding => (HtileSurface >> 19) & 0x3;
    public bool DepthClearEnabled => (RenderControl & 0x1) != 0;
    public bool StencilClearEnabled => ((RenderControl >> 1) & 0x1) != 0;
    public bool CopyDepthToColor => ((RenderControl >> 2) & 0x1) != 0;
    public bool CopyStencilToColor => ((RenderControl >> 3) & 0x1) != 0;
    public bool CopyCentroid => ((RenderControl >> 7) & 0x1) != 0;
    public uint CopySample => (RenderControl >> 8) & 0xF;
    public bool StencilTestEnabled => (DepthControl & 0x1) != 0;
    public bool DepthTestEnabled => ((DepthControl >> 1) & 0x1) != 0;
    public bool DepthWriteEnabled => ((DepthControl >> 2) & 0x1) != 0;
    public bool DepthBoundsEnabled => ((DepthControl >> 3) & 0x1) != 0;
    public uint DepthCompare => (DepthControl >> 4) & 0x7;
    public bool BackFaceEnabled => ((DepthControl >> 7) & 0x1) != 0;
    public uint StencilCompare => (DepthControl >> 8) & 0x7;
    public uint StencilCompareBack => (DepthControl >> 20) & 0x7;
    public bool ResummarizeEnabled => ((RenderControl >> 4) & 0x1) != 0;
    public bool StencilCompressDisabled => ((RenderControl >> 5) & 0x1) != 0;
    public bool DepthCompressDisabled => ((RenderControl >> 6) & 0x1) != 0;
}

// The video-out surface attributes of a display buffer, as the guest registered them.
public readonly record struct DisplaySurfaceWords(
    ulong DataAddress,
    ulong MetadataAddress,
    ulong PixelFormat,
    uint Width,
    uint Height,
    uint TilingMode,
    ulong Option,
    uint DccControl,
    ulong DccClearColor,
    bool Compressed);

// The shape the shader module was compiled with; the view type must match it.
public readonly record struct ShaderImageShape(bool Volume, bool Arrayed, bool Cube, bool Storage, bool DynamicMip, TextureNumericClass NumericClass, bool OneDimensional = false, bool R128 = false, bool Multisampled = false, bool DepthCompare = false, bool Atomic = false)
{
    public ShaderImageShape(bool volume, bool arrayed, bool storage, bool dynamicMip, TextureNumericClass numericClass)
        : this(volume, arrayed, false, storage, dynamicMip, numericClass, false, false, false, false, false)
    {
    }
}
