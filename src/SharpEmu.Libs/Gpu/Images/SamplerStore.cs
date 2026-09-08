// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Images;

public enum SamplerClampMode : uint
{
    Wrap = 0,
    Mirror = 1,
    ClampLastTexel = 2,
    MirrorOnceLastTexel = 3,
    ClampHalfBorder = 4,
    MirrorOnceHalfBorder = 5,
    ClampBorder = 6,
    MirrorOnceBorder = 7,
}

public enum SamplerFilter : uint
{
    Point = 0,
    Bilinear = 1,
    AnisotropicPoint = 2,
    AnisotropicLinear = 3,
}

public enum SamplerMipFilter : uint
{
    None = 0,
    Point = 1,
    Linear = 2,
}

public enum SamplerBorderColor : uint
{
    TransparentBlack = 0,
    OpaqueBlack = 1,
    OpaqueWhite = 2,
    FromTable = 3,
}

// One Vulkan sampler per distinct guest sampler descriptor, created on first use.
public sealed unsafe class SamplerStore : IDisposable
{
    private readonly GpuDeviceInfo _device;
    private readonly Dictionary<(uint, uint, uint, uint), Sampler> _samplers = new();
    private readonly object _gate = new();

    public SamplerStore(GpuDeviceInfo device) => _device = device;

    public int Count => _samplers.Count;

    public Sampler GetSampler(in SamplerDescriptorWords words)
    {
        lock (_gate)
        {
            var key = (words[0], words[1], words[2], words[3]);
            if (_samplers.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var sampler = CreateSampler(words);
            _samplers.Add(key, sampler);
            return sampler;
        }
    }

    private static bool IsAnisotropic(uint filter) => (SamplerFilter)filter switch
    {
        SamplerFilter.AnisotropicPoint or SamplerFilter.AnisotropicLinear => true,
        SamplerFilter.Point or SamplerFilter.Bilinear => false,
        _ => throw SubmissionScheduler.Fatal($"The sampler filter is unknown: filter={filter}."),
    };

    private static Filter ToFilter(uint filter) => (SamplerFilter)filter switch
    {
        SamplerFilter.Point or SamplerFilter.AnisotropicPoint => Filter.Nearest,
        SamplerFilter.Bilinear or SamplerFilter.AnisotropicLinear => Filter.Linear,
        _ => throw SubmissionScheduler.Fatal($"The sampler filter is unknown: filter={filter}."),
    };

    private static SamplerAddressMode ToAddressMode(uint clamp) => (SamplerClampMode)clamp switch
    {
        SamplerClampMode.Wrap => SamplerAddressMode.Repeat,
        SamplerClampMode.Mirror => SamplerAddressMode.MirroredRepeat,
        SamplerClampMode.ClampLastTexel => SamplerAddressMode.ClampToEdge,
        SamplerClampMode.MirrorOnceLastTexel => SamplerAddressMode.MirrorClampToEdge,
        SamplerClampMode.ClampHalfBorder => SamplerAddressMode.ClampToBorder,
        SamplerClampMode.MirrorOnceHalfBorder => SamplerAddressMode.MirrorClampToEdge,
        SamplerClampMode.ClampBorder => SamplerAddressMode.ClampToBorder,
        SamplerClampMode.MirrorOnceBorder => SamplerAddressMode.MirrorClampToEdge,
        _ => throw SubmissionScheduler.Fatal($"The sampler clamp mode is unknown: clamp={clamp}."),
    };

    private Sampler CreateSampler(in SamplerDescriptorWords words)
    {
        var magnify = words.MagnifyFilter;
        var minify = words.MinifyFilter;
        var anisotropic = IsAnisotropic(magnify) || IsAnisotropic(minify);
        var anisotropyRatio = 1.0f;
        if (anisotropic)
        {
            anisotropyRatio = words.MaxAnisotropyRatio switch
            {
                0 => 1.0f,
                1 => 2.0f,
                2 => 4.0f,
                3 => 8.0f,
                4 => 16.0f,
                _ => throw SubmissionScheduler.Fatal(
                    $"The anisotropy ratio is unknown: ratio={words.MaxAnisotropyRatio} words={words[0]:x8},{words[1]:x8},{words[2]:x8},{words[3]:x8}."),
            };
        }

        var mipFilter = (SamplerMipFilter)words.MipFilter;
        var minLod = 0.0f;
        var maxLod = 0.0f;
        if (mipFilter != SamplerMipFilter.None)
        {
            minLod = words.MinLod / 256.0f;
            maxLod = words.MaxLod / 256.0f;
        }

        BorderColor border;
        switch ((SamplerBorderColor)words.BorderColorType)
        {
            case SamplerBorderColor.TransparentBlack:
                border = BorderColor.IntTransparentBlack;
                break;
            case SamplerBorderColor.OpaqueBlack:
                border = BorderColor.IntOpaqueBlack;
                break;
            case SamplerBorderColor.OpaqueWhite:
                border = BorderColor.IntOpaqueWhite;
                break;
            case SamplerBorderColor.FromTable:
                Console.Error.WriteLine($"[LOADER][WARN] A table border color is approximated as transparent black: index={words.BorderColorIndex}.");
                border = BorderColor.IntTransparentBlack;
                break;
            default:
                throw SubmissionScheduler.Fatal($"The border color type is unknown: type={words.BorderColorType}.");
        }

        var info = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = ToFilter(magnify),
            MinFilter = ToFilter(minify),
            MipmapMode = mipFilter == SamplerMipFilter.Linear ? SamplerMipmapMode.Linear : SamplerMipmapMode.Nearest,
            AddressModeU = ToAddressMode(words.ClampX),
            AddressModeV = ToAddressMode(words.ClampY),
            AddressModeW = ToAddressMode(words.ClampZ),
            MipLodBias = (short)((words.LodBias ^ 0x2000) - 0x2000) / 256.0f,
            AnisotropyEnable = anisotropic,
            MaxAnisotropy = anisotropyRatio,
            CompareEnable = words.DepthCompareFunction != 0,
            CompareOp = (CompareOp)words.DepthCompareFunction,
            MinLod = minLod,
            MaxLod = maxLod,
            BorderColor = border,
            UnnormalizedCoordinates = words.ForceUnnormalizedCoordinates,
        };

        if (words.ForceUnnormalizedCoordinates)
        {
            info.AddressModeU = SamplerAddressMode.ClampToEdge;
            info.AddressModeV = SamplerAddressMode.ClampToEdge;
            info.AddressModeW = SamplerAddressMode.ClampToEdge;
            info.MipmapMode = SamplerMipmapMode.Nearest;
            info.MinLod = 0.0f;
            info.MaxLod = 0.0f;
            info.AnisotropyEnable = false;
            info.MaxAnisotropy = 1.0f;
            info.CompareEnable = false;
            info.MipLodBias = 0.0f;
        }

        var result = _device.Vk.CreateSampler(_device.Device, &info, null, out var sampler);
        if (result != Result.Success || sampler.Handle == 0)
        {
            throw SubmissionScheduler.Fatal($"vkCreateSampler failed: result={result} words={words[0]:x8},{words[1]:x8},{words[2]:x8},{words[3]:x8}.");
        }

        return sampler;
    }

    public void Dispose()
    {
        foreach (var sampler in _samplers.Values)
        {
            _device.Vk.DestroySampler(_device.Device, sampler, null);
        }

        _samplers.Clear();
    }
}
