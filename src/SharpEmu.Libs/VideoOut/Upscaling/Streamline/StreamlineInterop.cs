// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Libs.VideoOut.Upscaling.Streamline;

internal static class SlFeature
{
    public const uint Dlss = 0;
}

internal enum SlResult
{
    Ok = 0,
    ErrorIo,
    ErrorDriverOutOfDate,
    ErrorOsOutOfDate,
    ErrorOsDisabledHws,
    ErrorDeviceNotCreated,
    ErrorNoSupportedAdapterFound,
    ErrorAdapterNotSupported,
    ErrorNoPlugins,
    ErrorVulkanApi,
    ErrorDxgiApi,
    ErrorD3dApi,
    ErrorNrdApi,
    ErrorNvApi,
    ErrorReflexApi,
    ErrorNgxFailed,
    ErrorJsonParsing,
    ErrorMissingProxy,
    ErrorMissingResourceState,
    ErrorInvalidIntegration,
    ErrorMissingInputParameter,
    ErrorNotInitialized,
    ErrorComputeFailed,
    ErrorInitNotCalled,
    ErrorExceptionHandler,
    ErrorInvalidParameter,
    ErrorMissingConstants,
    ErrorDuplicatedConstants,
    ErrorMissingOrInvalidApi,
    ErrorCommonConstantsMissing,
    ErrorUnsupportedInterface,
    ErrorFeatureMissing,
    ErrorFeatureNotSupported,
    ErrorFeatureMissingHooks,
    ErrorFeatureFailedToLoad,
    ErrorFeatureWrongPriority,
    ErrorFeatureMissingDependency,
    ErrorFeatureManagerInvalidState,
    ErrorInvalidState,
    WarnOutOfVram,
}

internal static class SlBufferType
{
    public const uint Depth = 0;
    public const uint MotionVectors = 1;
    public const uint ScalingInputColor = 3;
    public const uint ScalingOutputColor = 4;
    public const uint Exposure = 13;
}

internal enum SlDlssMode : uint
{
    Off,
    MaxPerformance,
    Balanced,
    MaxQuality,
    UltraPerformance,
    UltraQuality,
    Dlaa,
}

internal static class SlBoolean
{
    public const byte False = 0;
    public const byte True = 1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SlHeader
{
    public nint Next;
    public Guid Type;
    public ulong Version;

    public SlHeader(Guid type, ulong version)
    {
        Next = 0;
        Type = type;
        Version = version;
    }
}

[StructLayout(LayoutKind.Explicit, Size = 144)]
internal struct SlPreferences
{
    public static readonly Guid TypeId = new(0x1ca10965, 0xbf8e, 0x432b, 0x8d, 0xa1, 0x67, 0x16, 0xd8, 0x79, 0xfb, 0x14);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public byte ShowConsole;
    [FieldOffset(36)] public uint LogLevel;
    [FieldOffset(40)] public nint PathsToPlugins;
    [FieldOffset(48)] public uint NumPathsToPlugins;
    [FieldOffset(56)] public nint PathToLogsAndData;
    [FieldOffset(64)] public nint AllocateCallback;
    [FieldOffset(72)] public nint ReleaseCallback;
    [FieldOffset(80)] public nint LogMessageCallback;
    [FieldOffset(88)] public ulong Flags;
    [FieldOffset(96)] public nint FeaturesToLoad;
    [FieldOffset(104)] public uint NumFeaturesToLoad;
    [FieldOffset(108)] public uint ApplicationId;
    [FieldOffset(112)] public uint Engine;
    [FieldOffset(120)] public nint EngineVersion;
    [FieldOffset(128)] public nint ProjectId;
    [FieldOffset(136)] public uint RenderApi;

    public const ulong FlagDisableCommandListStateTracking = 1UL << 0;
    public const ulong FlagDisableDebugText = 1UL << 1;
    public const ulong FlagUseManualHooking = 1UL << 2;
    public const ulong FlagUseFrameBasedResourceTagging = 1UL << 7;
    public const uint RenderApiVulkan = 2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SlVersion
{
    public uint Major;
    public uint Minor;
    public uint Build;

    public override string ToString() => $"{Major}.{Minor}.{Build}";
}

[StructLayout(LayoutKind.Explicit, Size = 184)]
internal struct SlFeatureRequirements
{
    public static readonly Guid TypeId = new(0x66714097, 0xac6d, 0x4bc6, 0x89, 0x15, 0x1e, 0x0f, 0x55, 0xa6, 0xb6, 0x1f);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public uint Flags;
    [FieldOffset(36)] public uint MaxNumCpuThreads;
    [FieldOffset(40)] public uint MaxNumViewports;
    [FieldOffset(44)] public uint NumRequiredTags;
    [FieldOffset(48)] public nint RequiredTags;
    [FieldOffset(56)] public SlVersion OsVersionDetected;
    [FieldOffset(68)] public SlVersion OsVersionRequired;
    [FieldOffset(80)] public SlVersion DriverVersionDetected;
    [FieldOffset(92)] public SlVersion DriverVersionRequired;
    [FieldOffset(104)] public uint VkNumComputeQueuesRequired;
    [FieldOffset(108)] public uint VkNumGraphicsQueuesRequired;
    [FieldOffset(112)] public uint VkNumDeviceExtensions;
    [FieldOffset(120)] public nint VkDeviceExtensions;
    [FieldOffset(128)] public uint VkNumInstanceExtensions;
    [FieldOffset(136)] public nint VkInstanceExtensions;
    [FieldOffset(144)] public uint VkNumFeatures12;
    [FieldOffset(152)] public nint VkFeatures12;
    [FieldOffset(160)] public uint VkNumFeatures13;
    [FieldOffset(168)] public nint VkFeatures13;
    [FieldOffset(176)] public uint VkNumOpticalFlowQueuesRequired;
}

[StructLayout(LayoutKind.Explicit, Size = 56)]
internal struct SlAdapterInfo
{
    public static readonly Guid TypeId = new(0x0677315f, 0xa746, 0x4492, 0x9f, 0x42, 0xcb, 0x61, 0x42, 0xc9, 0xc3, 0xd4);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public nint DeviceLuid;
    [FieldOffset(40)] public uint DeviceLuidSizeInBytes;
    [FieldOffset(48)] public nint VkPhysicalDevice;
}

[StructLayout(LayoutKind.Explicit, Size = 96)]
internal struct SlVulkanInfo
{
    public static readonly Guid TypeId = new(0x0eed6fd5, 0x82cd, 0x43a9, 0xbd, 0xb5, 0x47, 0xa5, 0xba, 0x2f, 0x45, 0xd6);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public nint Device;
    [FieldOffset(40)] public nint Instance;
    [FieldOffset(48)] public nint PhysicalDevice;
    [FieldOffset(56)] public uint ComputeQueueIndex;
    [FieldOffset(60)] public uint ComputeQueueFamily;
    [FieldOffset(64)] public uint GraphicsQueueIndex;
    [FieldOffset(68)] public uint GraphicsQueueFamily;
    [FieldOffset(72)] public uint OpticalFlowQueueIndex;
    [FieldOffset(76)] public uint OpticalFlowQueueFamily;
    [FieldOffset(80)] public byte UseNativeOpticalFlowMode;
    [FieldOffset(84)] public uint ComputeQueueCreateFlags;
    [FieldOffset(88)] public uint GraphicsQueueCreateFlags;
    [FieldOffset(92)] public uint OpticalFlowQueueCreateFlags;
}

[StructLayout(LayoutKind.Explicit, Size = 40)]
internal struct SlViewportHandle
{
    public static readonly Guid TypeId = new(0x171b6435, 0x9b3c, 0x4fc8, 0x99, 0x94, 0xfb, 0xe5, 0x25, 0x69, 0xaa, 0xa4);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public uint Value;

    public static SlViewportHandle Create(uint value) => new() { Header = new SlHeader(TypeId, 1), Value = value };
}

[StructLayout(LayoutKind.Explicit, Size = 112)]
internal struct SlResource
{
    public static readonly Guid TypeId = new(0x3a9d70cf, 0x2418, 0x4b72, 0x83, 0x91, 0x13, 0xf8, 0x72, 0x1c, 0x72, 0x61);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public byte Type;
    [FieldOffset(40)] public nint Native;
    [FieldOffset(48)] public nint Memory;
    [FieldOffset(56)] public nint View;
    [FieldOffset(64)] public uint State;
    [FieldOffset(68)] public uint Width;
    [FieldOffset(72)] public uint Height;
    [FieldOffset(76)] public uint NativeFormat;
    [FieldOffset(80)] public uint MipLevels;
    [FieldOffset(84)] public uint ArrayLayers;
    [FieldOffset(88)] public ulong GpuVirtualAddress;
    [FieldOffset(96)] public uint Flags;
    [FieldOffset(100)] public uint Usage;
    [FieldOffset(104)] public ushort InternalFlags;
    [FieldOffset(106)] public ushort Reserved;

    public const byte TypeTexture2D = 0;
}

[StructLayout(LayoutKind.Sequential)]
internal struct SlExtent
{
    public uint Top;
    public uint Left;
    public uint Width;
    public uint Height;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct SlResourceTag
{
    public static readonly Guid TypeId = new(0x4c6a5aad, 0xb445, 0x496c, 0x87, 0xff, 0x1a, 0xf3, 0x84, 0x5b, 0xe6, 0x53);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public nint Resource;
    [FieldOffset(40)] public uint Type;
    [FieldOffset(44)] public uint Lifecycle;
    [FieldOffset(48)] public SlExtent Extent;

    public const uint LifecycleOnlyValidNow = 0;
    public const uint LifecycleValidUntilEvaluate = 2;
}

[StructLayout(LayoutKind.Explicit, Size = 456)]
internal unsafe struct SlConstants
{
    public static readonly Guid TypeId = new(0xdcd35ad7, 0x4e4a, 0x4bad, 0xa9, 0x0c, 0xe0, 0xc4, 0x9e, 0xb2, 0x3a, 0xfe);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public fixed float CameraViewToClip[16];
    [FieldOffset(96)] public fixed float ClipToCameraView[16];
    [FieldOffset(160)] public fixed float ClipToLensClip[16];
    [FieldOffset(224)] public fixed float ClipToPrevClip[16];
    [FieldOffset(288)] public fixed float PrevClipToClip[16];
    [FieldOffset(352)] public float JitterOffsetX;
    [FieldOffset(356)] public float JitterOffsetY;
    [FieldOffset(360)] public float MvecScaleX;
    [FieldOffset(364)] public float MvecScaleY;
    [FieldOffset(368)] public float CameraPinholeOffsetX;
    [FieldOffset(372)] public float CameraPinholeOffsetY;
    [FieldOffset(376)] public fixed float CameraPos[3];
    [FieldOffset(388)] public fixed float CameraUp[3];
    [FieldOffset(400)] public fixed float CameraRight[3];
    [FieldOffset(412)] public fixed float CameraFwd[3];
    [FieldOffset(424)] public float CameraNear;
    [FieldOffset(428)] public float CameraFar;
    [FieldOffset(432)] public float CameraFov;
    [FieldOffset(436)] public float CameraAspectRatio;
    [FieldOffset(440)] public float MotionVectorsInvalidValue;
    [FieldOffset(444)] public byte DepthInverted;
    [FieldOffset(445)] public byte CameraMotionIncluded;
    [FieldOffset(446)] public byte MotionVectors3D;
    [FieldOffset(447)] public byte Reset;
    [FieldOffset(448)] public byte OrthographicProjection;
    [FieldOffset(449)] public byte MotionVectorsDilated;
    [FieldOffset(450)] public byte MotionVectorsJittered;
    [FieldOffset(452)] public float MinRelativeLinearDepthObjectSeparation;
}

[StructLayout(LayoutKind.Explicit, Size = 88)]
internal struct SlDlssOptions
{
    public static readonly Guid TypeId = new(0x6ac826e4, 0x4c61, 0x4101, 0xa9, 0x2d, 0x63, 0x8d, 0x42, 0x10, 0x57, 0xb8);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public SlDlssMode Mode;
    [FieldOffset(36)] public uint OutputWidth;
    [FieldOffset(40)] public uint OutputHeight;
    [FieldOffset(44)] public float Sharpness;
    [FieldOffset(48)] public float PreExposure;
    [FieldOffset(52)] public float ExposureScale;
    [FieldOffset(56)] public byte ColorBuffersHdr;
    [FieldOffset(57)] public byte IndicatorInvertAxisX;
    [FieldOffset(58)] public byte IndicatorInvertAxisY;
    [FieldOffset(60)] public uint DlaaPreset;
    [FieldOffset(64)] public uint QualityPreset;
    [FieldOffset(68)] public uint BalancedPreset;
    [FieldOffset(72)] public uint PerformancePreset;
    [FieldOffset(76)] public uint UltraPerformancePreset;
    [FieldOffset(80)] public uint UltraQualityPreset;
    [FieldOffset(84)] public byte UseAutoExposure;
    [FieldOffset(85)] public byte AlphaUpscalingEnabled;
}

[StructLayout(LayoutKind.Explicit, Size = 64)]
internal struct SlDlssOptimalSettings
{
    public static readonly Guid TypeId = new(0xef1d0957, 0xfd58, 0x4df7, 0xb5, 0x04, 0x8b, 0x69, 0xd8, 0xaa, 0x6b, 0x76);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public uint OptimalRenderWidth;
    [FieldOffset(36)] public uint OptimalRenderHeight;
    [FieldOffset(40)] public float OptimalSharpness;
    [FieldOffset(44)] public uint RenderWidthMin;
    [FieldOffset(48)] public uint RenderHeightMin;
    [FieldOffset(52)] public uint RenderWidthMax;
    [FieldOffset(56)] public uint RenderHeightMax;
}

[StructLayout(LayoutKind.Explicit, Size = 56)]
internal struct SlFeatureVersion
{
    public static readonly Guid TypeId = new(0x6d5b51f0, 0x076b, 0x486d, 0x99, 0x95, 0x5a, 0x56, 0x10, 0x43, 0xf5, 0xc1);
    [FieldOffset(0)] public SlHeader Header;
    [FieldOffset(32)] public SlVersion StreamlineVersion;
    [FieldOffset(44)] public SlVersion NgxVersion;
}

internal static unsafe class SlExports
{
    public const ulong SdkVersion = (2UL << 48) | (14UL << 32) | (16UL << 16) | 0xfedcUL;

    public static delegate* unmanaged<SlPreferences*, ulong, SlResult> Init;
    public static delegate* unmanaged<SlResult> Shutdown;
    public static delegate* unmanaged<uint, SlAdapterInfo*, SlResult> IsFeatureSupported;
    public static delegate* unmanaged<uint, SlFeatureRequirements*, SlResult> GetFeatureRequirements;
    public static delegate* unmanaged<uint, SlFeatureVersion*, SlResult> GetFeatureVersion;
    public static delegate* unmanaged<SlVulkanInfo*, SlResult> SetVulkanInfo;
    public static delegate* unmanaged<nint*, uint*, SlResult> GetNewFrameToken;
    public static delegate* unmanaged<SlConstants*, nint, SlViewportHandle*, SlResult> SetConstants;
    public static delegate* unmanaged<nint, SlViewportHandle*, SlResourceTag*, uint, nint, SlResult> SetTagForFrame;
    public static delegate* unmanaged<uint, nint, nint*, uint, nint, SlResult> EvaluateFeature;
    public static delegate* unmanaged<uint, SlViewportHandle*, SlResult> FreeResources;
    public static delegate* unmanaged<uint, byte*, nint*, SlResult> GetFeatureFunction;
    public static delegate* unmanaged<nint, byte*, nint> GetDeviceProcAddr;
    public static delegate* unmanaged<SlViewportHandle*, SlDlssOptions*, SlResult> DlssSetOptions;
    public static delegate* unmanaged<SlDlssOptions*, SlDlssOptimalSettings*, SlResult> DlssGetOptimalSettings;

    public static bool TryBind(nint library)
    {
        nint Export(string name) => NativeLibrary.TryGetExport(library, name, out var address) ? address : 0;
        var init = Export("slInit");
        var shutdown = Export("slShutdown");
        var supported = Export("slIsFeatureSupported");
        var requirements = Export("slGetFeatureRequirements");
        var version = Export("slGetFeatureVersion");
        var vulkanInfo = Export("slSetVulkanInfo");
        var frameToken = Export("slGetNewFrameToken");
        var constants = Export("slSetConstants");
        var tags = Export("slSetTagForFrame");
        var evaluate = Export("slEvaluateFeature");
        var free = Export("slFreeResources");
        var function = Export("slGetFeatureFunction");
        var deviceProcAddr = Export("vkGetDeviceProcAddr");
        if (init == 0 || shutdown == 0 || supported == 0 || requirements == 0 || vulkanInfo == 0 || frameToken == 0 ||
            constants == 0 || tags == 0 || evaluate == 0 || free == 0 || function == 0 || deviceProcAddr == 0)
        {
            return false;
        }

        Init = (delegate* unmanaged<SlPreferences*, ulong, SlResult>)init;
        Shutdown = (delegate* unmanaged<SlResult>)shutdown;
        IsFeatureSupported = (delegate* unmanaged<uint, SlAdapterInfo*, SlResult>)supported;
        GetFeatureRequirements = (delegate* unmanaged<uint, SlFeatureRequirements*, SlResult>)requirements;
        GetFeatureVersion = (delegate* unmanaged<uint, SlFeatureVersion*, SlResult>)version;
        SetVulkanInfo = (delegate* unmanaged<SlVulkanInfo*, SlResult>)vulkanInfo;
        GetNewFrameToken = (delegate* unmanaged<nint*, uint*, SlResult>)frameToken;
        SetConstants = (delegate* unmanaged<SlConstants*, nint, SlViewportHandle*, SlResult>)constants;
        SetTagForFrame = (delegate* unmanaged<nint, SlViewportHandle*, SlResourceTag*, uint, nint, SlResult>)tags;
        EvaluateFeature = (delegate* unmanaged<uint, nint, nint*, uint, nint, SlResult>)evaluate;
        FreeResources = (delegate* unmanaged<uint, SlViewportHandle*, SlResult>)free;
        GetFeatureFunction = (delegate* unmanaged<uint, byte*, nint*, SlResult>)function;
        GetDeviceProcAddr = (delegate* unmanaged<nint, byte*, nint>)deviceProcAddr;
        return true;
    }

    public static bool TryBindDlssFunctions()
    {
        nint Function(string name)
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
            nint address = 0;
            fixed (byte* pointer = bytes)
            {
                return GetFeatureFunction(SlFeature.Dlss, pointer, &address) == SlResult.Ok ? address : 0;
            }
        }

        var setOptions = Function("slDLSSSetOptions");
        var optimal = Function("slDLSSGetOptimalSettings");
        if (setOptions == 0 || optimal == 0)
        {
            return false;
        }

        DlssSetOptions = (delegate* unmanaged<SlViewportHandle*, SlDlssOptions*, SlResult>)setOptions;
        DlssGetOptimalSettings = (delegate* unmanaged<SlDlssOptions*, SlDlssOptimalSettings*, SlResult>)optimal;
        return true;
    }

    public static nint DeviceProcAddr(nint device, string name)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(name + "\0");
        fixed (byte* pointer = bytes)
        {
            return GetDeviceProcAddr(device, pointer);
        }
    }
}
