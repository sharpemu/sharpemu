// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut.Upscaling.Fsr;

// C ABI from FidelityFX SDK v2.4.0 (ffx_api.h, ffx_api_vk.h, ffx_upscale.h).
internal static unsafe class FsrInterop
{
    internal const ulong VulkanBackendType = 0x0100_0003;
    internal const ulong CreateType = 0x0001_0000;
    internal const ulong DispatchType = 0x0001_0001;
    internal const ulong VersionType = 0x0001_000B;
    internal const ulong ProviderVersionType = 6;
    internal const uint UpscaleApiVersion = (4u << 22) | (1u << 12) | 2u;

    [Flags]
    internal enum ContextFlags : uint
    {
        HighDynamicRange = 1 << 0,
        DisplayResolutionMotion = 1 << 1,
        MotionJitterCancellation = 1 << 2,
        DepthInverted = 1 << 3,
        DepthInfinite = 1 << 4,
        AutoExposure = 1 << 5,
        DynamicResolution = 1 << 6,
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Header(ulong type, Header* next = null)
    {
        public ulong Type = type;
        public Header* Next = next;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct Size(uint Width, uint Height);

    [StructLayout(LayoutKind.Sequential)]
    internal readonly record struct FloatPair(float X, float Y);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Create
    {
        public Header Header;
        public ContextFlags Flags;
        public Size MaxRenderSize;
        public Size MaxUpscaleSize;
        public nint Message;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct VulkanBackend
    {
        public Header Header;
        public nint Device;
        public nint PhysicalDevice;
        public nint GetDeviceProcAddr;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ApiVersion
    {
        public Header Header;
        public uint Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProviderVersion
    {
        public Header Header;
        public ulong Id;
        public nint Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Resource
    {
        public nint Handle;
        public uint Type;
        public uint Format;
        public uint Width;
        public uint Height;
        public uint Depth;
        public uint MipCount;
        public uint Flags;
        public uint Usage;
        public uint State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dispatch
    {
        public Header Header;
        public nint CommandList;
        public Resource Color;
        public Resource Depth;
        public Resource MotionVectors;
        public Resource Exposure;
        public Resource Reactive;
        public Resource TransparencyAndComposition;
        public Resource Output;
        public FloatPair JitterOffset;
        public FloatPair MotionVectorScale;
        public Size RenderSize;
        public Size UpscaleSize;
        public byte EnableSharpening;
        public float Sharpness;
        public float FrameTimeDelta;
        public float PreExposure;
        public byte Reset;
        public float CameraNear;
        public float CameraFar;
        public float CameraFovAngleVertical;
        public float ViewSpaceToMetersFactor;
        public uint Flags;
    }

    internal static Resource Wrap(SuperResolutionImage image, bool output = false)
    {
        const uint texture2D = 2;
        const uint unorderedAccess = 1 << 1;
        const uint depthTarget = 1 << 2;
        const uint stencilTarget = 1 << 5;
        // Both SDK states map to GENERAL, the layout tracked by the guest image cache.
        const uint genericRead = (1 << 2) | (1 << 4);
        var format = (Format)image.Format;
        var surfaceFormat = SurfaceFormat(format);
        var requiredUsage = output ? ImageUsageFlags.StorageBit : ImageUsageFlags.SampledBit;
        if (!image.IsValid || !image.FsrCompatibleView || surfaceFormat == 0 ||
            ((ImageUsageFlags)image.Usage & requiredUsage) == 0 ||
            (output && format is Format.R8G8B8A8Srgb or Format.B8G8R8A8Srgb))
        {
            return default;
        }

        var usage = output ? unorderedAccess : image.IsDepth ? depthTarget : 0u;
        if (format is Format.D16UnormS8Uint or Format.D24UnormS8Uint or Format.D32SfloatS8Uint)
        {
            usage |= stencilTarget;
        }

        return new Resource
        {
            Handle = (nint)image.Image,
            Type = texture2D,
            Format = surfaceFormat,
            Width = image.Width,
            Height = image.Height,
            Depth = 1,
            MipCount = image.MipLevels,
            Usage = usage,
            State = output ? unorderedAccess : genericRead,
        };
    }

    // FfxApiSurfaceFormat values used by ffxApiGetSurfaceFormatVK.
    private static uint SurfaceFormat(Format format) => format switch
    {
        Format.R32G32B32A32Sfloat => 3,
        Format.R16G16B16A16Sfloat => 4,
        Format.R32G32B32Sfloat => 5,
        Format.R32G32Sfloat => 6,
        Format.D24UnormS8Uint or Format.X8D24UnormPack32 => 8,
        Format.R8G8B8A8Unorm => 10,
        Format.R8G8B8A8SNorm => 11,
        Format.R8G8B8A8Srgb => 12,
        Format.B8G8R8A8Unorm => 14,
        Format.B8G8R8A8Srgb => 15,
        Format.B10G11R11UfloatPack32 => 16,
        Format.A2B10G10R10UnormPack32 => 17,
        Format.R16G16Sfloat => 18,
        Format.R16Sfloat => 21,
        Format.R16Unorm or Format.D16Unorm or Format.D16UnormS8Uint => 23,
        Format.R16SNorm => 24,
        Format.R8Unorm => 25,
        Format.R8G8Unorm => 26,
        Format.R32Sfloat or Format.D32Sfloat or Format.D32SfloatS8Uint => 28,
        Format.E5B9G9R9UfloatPack32 => 29,
        Format.R8SNorm => 39,
        _ => 0,
    };
}
