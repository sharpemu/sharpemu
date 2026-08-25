// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.Libs.Gpu;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns guest texture formats, samplers, and pixel conversion.

    private static uint GetGuestTextureFormat(uint format, uint numberType) =>
        IsKnownGuestTextureFormat(format)
            ? 0x8000_0000u | ((format & 0x1FFu) << 8) | (numberType & 0xFFu)
            : 0;

    private static bool IsKnownGuestTextureFormat(uint format) =>
        format is >= 1 and <= 19 or 34 or >= 169 and <= 182;

    private sealed partial class Presenter
    {
        private readonly Dictionary<GuestSampler, Sampler> _samplers = new();

        private Sampler CreateSampler(GuestSampler sampler)
        {
            if (_samplers.TryGetValue(sampler, out var cachedSampler))
            {
                return cachedSampler;
            }

            var minLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMinLod(sampler);
            var maxLod = DecodeSamplerMipFilter(sampler) == 0
                ? 0f
                : DecodeSamplerMaxLod(sampler);
            var samplerInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo,
                MagFilter = ToVkFilter(DecodeSamplerMagFilter(sampler)),
                MinFilter = ToVkFilter(DecodeSamplerMinFilter(sampler)),
                MipmapMode = ToVkMipFilter(DecodeSamplerMipFilter(sampler)),
                AddressModeU = ToVkSamplerAddressMode(DecodeSamplerClampX(sampler)),
                AddressModeV = ToVkSamplerAddressMode(DecodeSamplerClampY(sampler)),
                AddressModeW = ToVkSamplerAddressMode(DecodeSamplerClampZ(sampler)),
                MipLodBias = DecodeSamplerLodBias(sampler),
                CompareEnable = DecodeSamplerDepthCompare(sampler) != 0,
                CompareOp = ToVkCompareOp(DecodeSamplerDepthCompare(sampler)),
                MinLod = minLod,
                MaxLod = Math.Max(minLod, maxLod),
                BorderColor = ToVkBorderColor(DecodeSamplerBorderColor(sampler)),
            };
            Sampler vkSampler;
            Check(
                _vk.CreateSampler(_device, &samplerInfo, null, out vkSampler),
                "vkCreateSampler(texture)");
            _samplers.Add(sampler, vkSampler);
            return vkSampler;
        }

        private static ComponentMapping ToVkComponentMapping(uint dstSelect)
        {
            return new ComponentMapping(
                ToVkComponentSwizzle(dstSelect & 0x7),
                ToVkComponentSwizzle((dstSelect >> 3) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 6) & 0x7),
                ToVkComponentSwizzle((dstSelect >> 9) & 0x7));
        }

        private static ComponentSwizzle ToVkComponentSwizzle(uint selector) =>
            selector switch
            {
                0 => ComponentSwizzle.Zero,
                1 => ComponentSwizzle.One,
                4 => ComponentSwizzle.R,
                5 => ComponentSwizzle.G,
                6 => ComponentSwizzle.B,
                7 => ComponentSwizzle.A,
                _ => ComponentSwizzle.Identity,
            };

        private static byte[] ExpandRgb32Pixels(byte[] pixels)
        {
            var texelCount = pixels.Length / 12;
            var expanded = new byte[checked(texelCount * 16)];
            for (var texel = 0; texel < texelCount; texel++)
            {
                System.Buffer.BlockCopy(pixels, texel * 12, expanded, texel * 16, 12);
                expanded[texel * 16 + 14] = 0x80;
                expanded[texel * 16 + 15] = 0x3F;
            }

            return expanded;
        }

        private static uint DecodeSamplerClampX(GuestSampler sampler) =>
            sampler.Word0 & 0x7u;

        private static uint DecodeSamplerClampY(GuestSampler sampler) =>
            (sampler.Word0 >> 3) & 0x7u;

        private static uint DecodeSamplerClampZ(GuestSampler sampler) =>
            (sampler.Word0 >> 6) & 0x7u;

        private static uint DecodeSamplerDepthCompare(GuestSampler sampler) =>
            (sampler.Word0 >> 12) & 0x7u;

        private static float DecodeSamplerMinLod(GuestSampler sampler) =>
            (sampler.Word1 & 0xFFFu) / 256.0f;

        private static float DecodeSamplerMaxLod(GuestSampler sampler) =>
            ((sampler.Word1 >> 12) & 0xFFFu) / 256.0f;

        private static float DecodeSamplerLodBias(GuestSampler sampler)
        {
            var raw = sampler.Word2 & 0x3FFFu;
            var signed = (short)((raw ^ 0x2000u) - 0x2000u);
            return signed / 256.0f;
        }

        private static uint DecodeSamplerMagFilter(GuestSampler sampler) =>
            (sampler.Word2 >> 20) & 0x3u;

        private static uint DecodeSamplerMinFilter(GuestSampler sampler) =>
            (sampler.Word2 >> 22) & 0x3u;

        private static uint DecodeSamplerMipFilter(GuestSampler sampler) =>
            (sampler.Word2 >> 26) & 0x3u;

        private static uint DecodeSamplerBorderColor(GuestSampler sampler) =>
            (sampler.Word3 >> 30) & 0x3u;

        private static SamplerAddressMode ToVkSamplerAddressMode(uint mode) =>
            mode switch
            {
                0 => SamplerAddressMode.Repeat,
                1 => SamplerAddressMode.MirroredRepeat,
                2 => SamplerAddressMode.ClampToEdge,
                3 or 5 or 7 => SamplerAddressMode.MirrorClampToEdge,
                4 or 6 => SamplerAddressMode.ClampToBorder,
                _ => SamplerAddressMode.ClampToEdge,
            };

        private static Filter ToVkFilter(uint filter) =>
            filter is 1 or 3 ? Filter.Linear : Filter.Nearest;

        private static SamplerMipmapMode ToVkMipFilter(uint filter) =>
            filter == 2 ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest;

        private static CompareOp ToVkCompareOp(uint compare) =>
            compare switch
            {
                1 => CompareOp.Less,
                2 => CompareOp.Equal,
                3 => CompareOp.LessOrEqual,
                4 => CompareOp.Greater,
                5 => CompareOp.NotEqual,
                6 => CompareOp.GreaterOrEqual,
                7 => CompareOp.Always,
                _ => CompareOp.Never,
            };

        private static BorderColor ToVkBorderColor(uint color) =>
            color switch
            {
                1 => BorderColor.FloatTransparentBlack,
                2 => BorderColor.FloatOpaqueWhite,
                _ => BorderColor.FloatOpaqueBlack,
            };

        private static ulong GetTextureBytesPerPixel(uint format) =>
            format switch
            {
                1 => 1UL,
                2 => 2UL,
                3 => 2UL,
                4 => 4UL,
                5 => 4UL,
                6 => 4UL,
                7 => 4UL,
                9 => 4UL,
                10 => 4UL,
                11 => 8UL,
                12 => 8UL,
                13 => 12UL,
                14 => 16UL,
                16 => 2UL,
                17 => 2UL,
                19 => 2UL,
                _ => 4UL,
            };

        private static ulong GetTextureByteCount(uint format, uint width, uint height)
            => GetGuestImageByteCount(format, width, height);

        private static ulong GetTextureByteCount(
            uint format,
            uint width,
            uint height,
            uint depth) =>
            GetGuestImageByteCount(format, width, height, depth);

        private static ulong GetVulkanImageByteCount(Format format, uint width, uint height)
        {
            var blockBytes = format switch
            {
                Format.BC1RgbUnormBlock or
                Format.BC1RgbSrgbBlock or
                Format.BC1RgbaUnormBlock or
                Format.BC1RgbaSrgbBlock or
                Format.BC4UnormBlock or
                Format.BC4SNormBlock => 8UL,
                Format.BC2UnormBlock or
                Format.BC2SrgbBlock or
                Format.BC3UnormBlock or
                Format.BC3SrgbBlock or
                Format.BC5UnormBlock or
                Format.BC5SNormBlock or
                Format.BC6HUfloatBlock or
                Format.BC6HSfloatBlock or
                Format.BC7UnormBlock or
                Format.BC7SrgbBlock => 16UL,
                _ => 0UL,
            };
            if (blockBytes != 0)
            {
                return checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
            }

            var bitsPerTexel = GetFormatCompatibilityClass(format);
            if (bitsPerTexel == 0)
            {
                bitsPerTexel = format switch
                {
                    Format.B5G6R5UnormPack16 or
                    Format.R5G5B5A1UnormPack16 or
                    Format.R4G4B4A4UnormPack16 => 16,
                    Format.B8G8R8A8Unorm or
                    Format.B8G8R8A8Srgb => 32,
                    _ => 0,
                };
            }

            return bitsPerTexel == 0
                ? 0
                : checked((ulong)width * height * bitsPerTexel / 8);
        }

        private static ulong GetVulkanImageByteCount(
            Format format,
            uint width,
            uint height,
            uint depth) =>
            checked(GetVulkanImageByteCount(format, width, height) * Math.Max(depth, 1u));

        internal static Format GetTextureFormat(uint format, uint numberType) =>
            (format, numberType) switch
            {
                (9, _) => Format.A2B10G10R10UnormPack32,
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 2) => Format.R8Uscaled,
                (1, 3) => Format.R8Sscaled,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (2, 7) => Format.R16Sfloat,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 2) => Format.R16Uscaled,
                (2, 3) => Format.R16Sscaled,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 2) => Format.R8G8Uscaled,
                (3, 3) => Format.R8G8Sscaled,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (7, 7) => Format.B10G11R11UfloatPack32,
                (8, 0) => Format.A2B10G10R10UnormPack32,
                (8, 1) => Format.A2B10G10R10SNormPack32,
                (8, 2) => Format.A2B10G10R10UscaledPack32,
                (8, 3) => Format.A2B10G10R10SscaledPack32,
                (8, 4) => Format.A2B10G10R10UintPack32,
                (8, 5) => Format.A2B10G10R10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (1, 9) => Format.R8Srgb,
                (3, 9) => Format.R8G8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32A32Uint,
                (13, 5) => Format.R32G32B32A32Sint,
                (13, _) => Format.R32G32B32A32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                (169, _) => Format.BC1RgbaUnormBlock,
                (170, _) => Format.BC1RgbaSrgbBlock,
                (171, _) => Format.BC2UnormBlock,
                (172, _) => Format.BC2SrgbBlock,
                (173, _) => Format.BC3UnormBlock,
                (174, _) => Format.BC3SrgbBlock,
                (175, 1) => Format.BC4SNormBlock,
                (175, _) => Format.BC4UnormBlock,
                (176, _) => Format.BC4SNormBlock,
                (177, 1) => Format.BC5SNormBlock,
                (177, _) => Format.BC5UnormBlock,
                (178, _) => Format.BC5SNormBlock,
                (179, _) => Format.BC6HUfloatBlock,
                (180, _) => Format.BC6HSfloatBlock,
                (181, _) => Format.BC7UnormBlock,
                (182, _) => Format.BC7SrgbBlock,
                _ => Format.R8G8B8A8Unorm,
            };

        internal static Format GetStorageImageFormat(Format format) =>
            format switch
            {
                Format.R8Srgb => Format.R8Unorm,
                Format.R8G8Srgb => Format.R8G8Unorm,
                Format.R8G8B8A8Srgb => Format.R8G8B8A8Unorm,
                Format.BC1RgbaSrgbBlock => Format.BC1RgbaUnormBlock,
                Format.BC2SrgbBlock => Format.BC2UnormBlock,
                Format.BC3SrgbBlock => Format.BC3UnormBlock,
                Format.BC7SrgbBlock => Format.BC7UnormBlock,
                _ => format,
            };

        private static bool IsBlockCompressedFormat(Format format) =>
            format is Format.BC1RgbaUnormBlock or
                Format.BC1RgbaSrgbBlock or
                Format.BC2UnormBlock or
                Format.BC2SrgbBlock or
                Format.BC3UnormBlock or
                Format.BC3SrgbBlock or
                Format.BC4UnormBlock or
                Format.BC4SNormBlock or
                Format.BC5UnormBlock or
                Format.BC5SNormBlock or
                Format.BC6HUfloatBlock or
                Format.BC6HSfloatBlock or
                Format.BC7UnormBlock or
                Format.BC7SrgbBlock;

        internal static bool IsCompatibleViewFormat(Format imageFormat, Format viewFormat)
        {
            if (imageFormat == viewFormat)
            {
                return true;
            }

            var imageClass = GetFormatCompatibilityClass(imageFormat);
            return imageClass != 0 && imageClass == GetFormatCompatibilityClass(viewFormat);
        }

        private static uint GetFormatCompatibilityClass(Format format) =>
            format switch
            {
                Format.R8Unorm or
                Format.R8SNorm or
                Format.R8Srgb or
                Format.R8Uint or
                Format.R8Sint => 8,
                // Every single-channel 16-bit format shares this class, not just
                // the float one. Omitting the rest made GetVulkanImageByteCount
                // return zero for them, and a zero expected size rejects the
                // guest's upload outright — the texture then samples as blank
                // for the life of the run. Silent Hill uploads R16Unorm at
                // 144x81 through 1024x1024 and every one was dropped.
                Format.R16Sfloat or
                Format.R16Unorm or
                Format.R16SNorm or
                Format.R16Uint or
                Format.R16Sint or
                Format.R8G8Unorm or
                Format.R8G8SNorm or
                Format.R8G8Srgb or
                Format.R8G8Uint or
                Format.R8G8Sint => 16,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.R16G16Unorm or
                Format.R16G16SNorm or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Unorm or
                Format.R8G8B8A8SNorm or
                Format.R8G8B8A8Srgb or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.B8G8R8A8Unorm or
                Format.B8G8R8A8SNorm or
                Format.B8G8R8A8Srgb or
                Format.A8B8G8R8UnormPack32 or
                Format.A8B8G8R8SrgbPack32 or
                Format.A2R10G10B10UnormPack32 or
                Format.A2B10G10R10UnormPack32 or
                Format.B10G11R11UfloatPack32 or
                Format.E5B9G9R9UfloatPack32 => 32,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat or
                Format.R16G16B16A16Unorm or
                Format.R16G16B16A16SNorm or
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 64,
                Format.R32G32B32Sfloat => 96,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 128,
                _ => 0,
            };
    }
}
