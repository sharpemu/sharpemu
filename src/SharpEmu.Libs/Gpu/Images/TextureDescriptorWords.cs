// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;

namespace SharpEmu.Libs.Gpu.Images;

[InlineArray(8)]
public struct TextureDescriptorFields
{
    private uint _first;
}

// The eight dwords of a guest texture descriptor and their bit fields.
public readonly struct TextureDescriptorWords
{
    public readonly TextureDescriptorFields Fields;

    public TextureDescriptorWords(ReadOnlySpan<uint> words)
    {
        for (var index = 0; index < 8 && index < words.Length; index++)
        {
            Fields[index] = words[index];
        }
    }

    public uint this[int index] => Fields[index];

    public ulong BaseAddress => ((Fields[0] | ((ulong)Fields[1] << 32)) & 0xFFFFFFFFFF) << 8;

    public bool IsNull => BaseAddress == 0;

    public uint MinLod => (Fields[1] >> 8) & 0xFFF;

    public GuestPixelFormat Format => (GuestPixelFormat)((Fields[1] >> 20) & 0x1FF);

    public uint Width => ((Fields[1] >> 30) & 0x3) | (((Fields[2] >> 0) & 0xFFF) << 2);

    public uint Height => (Fields[2] >> 14) & 0x3FFF;

    public uint DestinationSelectX => (Fields[3] >> 0) & 0x7;

    public uint DestinationSelectY => (Fields[3] >> 3) & 0x7;

    public uint DestinationSelectZ => (Fields[3] >> 6) & 0x7;

    public uint DestinationSelectW => (Fields[3] >> 9) & 0x7;

    public uint DestinationSelectXyzw => Fields[3] & 0xFFF;

    public uint BaseLevel => (Fields[3] >> 12) & 0xF;

    public uint LastLevel => (Fields[3] >> 16) & 0xF;

    public GuestTileMode TileMode => (GuestTileMode)((Fields[3] >> 20) & 0x1F);

    public uint BlockCompressedSwizzle => (Fields[3] >> 25) & 0x7;

    public GuestImageType Type => (GuestImageType)((Fields[3] >> 28) & 0xF);

    public uint Depth => (Fields[4] >> 0) & 0x1FFF;

    public uint BaseArray => (Fields[4] >> 16) & 0x1FFF;

    public uint ArrayPitch => (Fields[5] >> 0) & 0xF;

    public uint MaxMip => (Fields[5] >> 4) & 0xF;

    public uint MinLodWarning => (Fields[5] >> 8) & 0xFFF;

    public bool MsaaDepth => ((Fields[6] >> 10) & 0x1) == 1;

    public bool WriteCompress => ((Fields[6] >> 20) & 0x1) == 1;

    public bool MetadataCompress => ((Fields[6] >> 21) & 0x1) == 1;

    public ulong MetadataAddress => ((Fields[6] >> 24) & 0xFF) | ((ulong)Fields[7] << 8);
}

[InlineArray(4)]
public struct SamplerDescriptorFields
{
    private uint _first;
}

// The four dwords of a guest sampler descriptor and their bit fields.
public readonly struct SamplerDescriptorWords
{
    public readonly SamplerDescriptorFields Fields;

    public SamplerDescriptorWords(ReadOnlySpan<uint> words)
    {
        for (var index = 0; index < 4 && index < words.Length; index++)
        {
            Fields[index] = words[index];
        }
    }

    public uint this[int index] => Fields[index];

    public uint ClampX => (Fields[0] >> 0) & 0x7;

    public uint ClampY => (Fields[0] >> 3) & 0x7;

    public uint ClampZ => (Fields[0] >> 6) & 0x7;

    public uint MaxAnisotropyRatio => (Fields[0] >> 9) & 0x7;

    public uint DepthCompareFunction => (Fields[0] >> 12) & 0x7;

    public bool ForceUnnormalizedCoordinates => ((Fields[0] >> 15) & 0x1) == 1;

    public uint MinLod => (Fields[1] >> 0) & 0xFFF;

    public uint MaxLod => (Fields[1] >> 12) & 0xFFF;

    public uint LodBias => (Fields[2] >> 0) & 0x3FFF;

    public uint MagnifyFilter => (Fields[2] >> 20) & 0x3;

    public uint MinifyFilter => (Fields[2] >> 22) & 0x3;

    public uint MipFilter => (Fields[2] >> 26) & 0x3;

    public uint BorderColorIndex => (Fields[3] >> 0) & 0xFFF;

    public uint BorderColorType => (Fields[3] >> 30) & 0x3;
}
