// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut.Upscaling.Xess;

// Vulkan ABI from the Intel XeSS SDK's xess_vk.h and xess.h.
internal static class XessInterop
{
    internal enum Quality
    {
        UltraPerformance = 100,
        Performance,
        Balanced,
        Quality,
        UltraQuality,
        UltraQualityPlus,
        NativeAa,
    }

    [Flags]
    internal enum InitFlags : uint
    {
        HighResolutionMotion = 1 << 0,
        InvertedDepth = 1 << 1,
        ExposureTexture = 1 << 2,
        ResponsiveMask = 1 << 3,
        LdrInput = 1 << 6,
        JitteredMotion = 1 << 7,
        AutoExposure = 1 << 8,
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct ImageViewInfo
    {
        public ImageView View;
        public Image Image;
        public ImageSubresourceRange Range;
        public Format Format;
        public uint Width;
        public uint Height;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct InitParameters
    {
        public Extent2D Output;
        public Quality Quality;
        public InitFlags Flags;
        public uint CreationNodeMask;
        public uint VisibleNodeMask;
        public DeviceMemory BufferHeap;
        public ulong BufferOffset;
        public DeviceMemory TextureHeap;
        public ulong TextureOffset;
        public PipelineCache PipelineCache;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 8)]
    internal struct ExecuteParameters
    {
        public ImageViewInfo Color;
        public ImageViewInfo Motion;
        public ImageViewInfo Depth;
        public ImageViewInfo Exposure;
        public ImageViewInfo ResponsiveMask;
        public ImageViewInfo Output;
        public float JitterX;
        public float JitterY;
        public float ExposureScale;
        public uint Reset;
        public Extent2D Input;
        public Extent2D ColorBase;
        public Extent2D MotionBase;
        public Extent2D DepthBase;
        public Extent2D MaskBase;
        public Extent2D Reserved;
        public Extent2D OutputBase;
    }
}
