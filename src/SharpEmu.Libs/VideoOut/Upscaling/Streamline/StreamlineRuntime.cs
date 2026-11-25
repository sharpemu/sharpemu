// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Vulkan;
using SharpEmu.Libs.Gpu.Upscaling;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut.Upscaling.Streamline;

internal sealed unsafe class StreamlineRuntime : IDisposable
{
    private const string ProjectId = "6b1e3a3c-5f0b-4d55-9a8e-2f3c1d7e9b41";
    private const uint ImageLayoutGeneral = 1;
    private const float InvalidFloat = 3.40282346638528859811704183484516925440e38f;
    private const int MaxReportedFailures = 8;
    private const int FailuresBeforeDisable = 30;

    private static readonly bool VerboseLog =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STREAMLINE"), "1", StringComparison.Ordinal);

    private const int MaxRepeatedMessages = 3;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> MessageCounts = new();
    private static int _ngxFailures;
    private static int _vramWarned;

    private readonly List<nint> _allocations = [];
    private readonly Dictionary<uint, SlDlssOptions> _viewportOptions = [];
    private readonly Dictionary<(uint, uint, uint, uint), SlDlssMode> _modes = [];
    private uint _frameIndex;
    private int _failures;
    private int _reportedFailures;
    private bool _deviceAttached;
    private bool _shutdown;

    public IReadOnlyList<string> InstanceExtensions { get; private set; } = [];

    public IReadOnlyList<string> DeviceExtensions { get; private set; } = [];

    public IReadOnlyList<string> Features12 { get; private set; } = [];

    public IReadOnlyList<string> Features13 { get; private set; } = [];

    public bool IsActive => _deviceAttached && !_shutdown && Volatile.Read(ref _failures) < FailuresBeforeDisable;

    public delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, Result> CreateSwapchainProxy { get; private set; }

    public delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, void> DestroySwapchainProxy { get; private set; }

    public delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result> GetSwapchainImagesProxy { get; private set; }

    public delegate* unmanaged<Device, SwapchainKHR, ulong, VkSemaphore, Fence, uint*, Result> AcquireNextImageProxy { get; private set; }

    public delegate* unmanaged<Queue, PresentInfoKHR*, Result> QueuePresentProxy { get; private set; }

    public bool HasSwapchainProxies => _deviceAttached && !_shutdown && QueuePresentProxy != null;

    private StreamlineRuntime()
    {
    }

    private static Task<StreamlineRuntime?>? _prepared;

    public static void BeginStart(string directory)
    {
        if (!OperatingSystem.IsWindows() || Volatile.Read(ref _prepared) is not null)
        {
            return;
        }

        var task = new Task<StreamlineRuntime?>(() => TryStart(directory));
        if (Interlocked.CompareExchange(ref _prepared, task, null) is null)
        {
            task.Start(TaskScheduler.Default);
        }
    }

    public static StreamlineRuntime? Start(string directory) =>
        Interlocked.Exchange(ref _prepared, null) is { } prepared
            ? prepared.GetAwaiter().GetResult()
            : TryStart(directory);

    public static StreamlineRuntime? TryStart(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var missing = DlssRuntimeFiles.Missing(directory);
        if (missing.Count > 0)
        {
            Console.Error.WriteLine(
                $"[DLSS][WARN] DLSS is off: missing {string.Join(", ", missing.Select(file => file.Name))} in {directory}.");
            return null;
        }

        var interposer = Path.Combine(directory, DlssRuntimeFiles.Interposer);
        if (!AuthenticodeVerifier.IsSignedBy(interposer, "NVIDIA", out var signature))
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS is off: {DlssRuntimeFiles.Interposer} is not an NVIDIA-signed file ({signature}).");
            return null;
        }

        if (!NativeLibrary.TryLoad(interposer, out var library))
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS is off: {DlssRuntimeFiles.Interposer} could not be loaded.");
            return null;
        }

        if (!SlExports.TryBind(library))
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS is off: {DlssRuntimeFiles.Interposer} lacks the Streamline API this build uses.");
            return null;
        }

        var runtime = new StreamlineRuntime();
        if (!runtime.Initialize(directory))
        {
            runtime.Dispose();
            return null;
        }

        return runtime;
    }

    private bool Initialize(string directory)
    {
        var pluginPath = Allocate(Marshal.StringToHGlobalUni(directory));
        var pluginPaths = Allocate(Marshal.AllocHGlobal(sizeof(nint)));
        *(nint*)pluginPaths = pluginPath;
        var features = Allocate(Marshal.AllocHGlobal(sizeof(uint)));
        *(uint*)features = SlFeature.Dlss;
        var preferences = new SlPreferences
        {
            Header = new SlHeader(SlPreferences.TypeId, 1),
            ShowConsole = 0,
            LogLevel = VerboseLog ? 2u : 1u,
            PathsToPlugins = pluginPaths,
            NumPathsToPlugins = 1,
            PathToLogsAndData = VerboseLog ? Allocate(Marshal.StringToHGlobalUni(LogDirectory())) : 0,
            LogMessageCallback = (nint)(delegate* unmanaged<uint, byte*, void>)&OnLog,
            Flags = SlPreferences.FlagDisableCommandListStateTracking | SlPreferences.FlagDisableDebugText |
                SlPreferences.FlagUseManualHooking | SlPreferences.FlagUseFrameBasedResourceTagging,
            FeaturesToLoad = features,
            NumFeaturesToLoad = 1,
            Engine = 0,
            EngineVersion = Allocate(Marshal.StringToHGlobalAnsi("1.0")),
            ProjectId = Allocate(Marshal.StringToHGlobalAnsi(ProjectId)),
            RenderApi = SlPreferences.RenderApiVulkan,
        };
        var result = SlExports.Init(&preferences, SlExports.SdkVersion);
        if (result != SlResult.Ok)
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS is off: slInit failed ({result}).");
            return false;
        }

        var requirements = new SlFeatureRequirements { Header = new SlHeader(SlFeatureRequirements.TypeId, 2) };
        result = SlExports.GetFeatureRequirements(SlFeature.Dlss, &requirements);
        if (result != SlResult.Ok)
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS is off: the DLSS plugin did not load ({result}).");
            return false;
        }

        InstanceExtensions = ReadStrings(requirements.VkInstanceExtensions, requirements.VkNumInstanceExtensions);
        DeviceExtensions = ReadStrings(requirements.VkDeviceExtensions, requirements.VkNumDeviceExtensions);
        Features12 = ReadStrings(requirements.VkFeatures12, requirements.VkNumFeatures12);
        Features13 = ReadStrings(requirements.VkFeatures13, requirements.VkNumFeatures13);
        var version = string.Empty;
        if (SlExports.GetFeatureVersion != null)
        {
            var featureVersion = new SlFeatureVersion { Header = new SlHeader(SlFeatureVersion.TypeId, 1) };
            if (SlExports.GetFeatureVersion(SlFeature.Dlss, &featureVersion) == SlResult.Ok)
            {
                version = $" streamline={featureVersion.StreamlineVersion} ngx={featureVersion.NgxVersion}";
            }
        }

        Console.Error.WriteLine(
            $"[DLSS][INFO] Streamline loaded{version} driver={requirements.DriverVersionDetected} " +
            $"(needs {requirements.DriverVersionRequired}) instance_ext={InstanceExtensions.Count} device_ext={DeviceExtensions.Count}.");
        return true;
    }

    public bool SupportsAdapter(PhysicalDevice physicalDevice)
    {
        var adapter = new SlAdapterInfo
        {
            Header = new SlHeader(SlAdapterInfo.TypeId, 1),
            VkPhysicalDevice = physicalDevice.Handle,
        };
        var result = SlExports.IsFeatureSupported(SlFeature.Dlss, &adapter);
        if (result != SlResult.Ok)
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS is off: this GPU or driver does not support it ({result}).");
            return false;
        }

        return true;
    }

    public bool AttachDevice(Instance instance, PhysicalDevice physicalDevice, Device device, uint queueFamily)
    {
        var info = new SlVulkanInfo
        {
            Header = new SlHeader(SlVulkanInfo.TypeId, 3),
            Device = device.Handle,
            Instance = instance.Handle,
            PhysicalDevice = physicalDevice.Handle,
            ComputeQueueIndex = 1,
            ComputeQueueFamily = queueFamily,
            GraphicsQueueIndex = 1,
            GraphicsQueueFamily = queueFamily,
        };
        var result = SlExports.SetVulkanInfo(&info);
        if (result != SlResult.Ok)
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS is off: slSetVulkanInfo failed ({result}).");
            return false;
        }

        if (!SlExports.TryBindDlssFunctions())
        {
            Console.Error.WriteLine("[DLSS][WARN] DLSS is off: the DLSS plugin exposes no options API.");
            return false;
        }

        CreateSwapchainProxy = (delegate* unmanaged<Device, SwapchainCreateInfoKHR*, AllocationCallbacks*, SwapchainKHR*, Result>)
            SlExports.DeviceProcAddr(device.Handle, "vkCreateSwapchainKHR");
        DestroySwapchainProxy = (delegate* unmanaged<Device, SwapchainKHR, AllocationCallbacks*, void>)
            SlExports.DeviceProcAddr(device.Handle, "vkDestroySwapchainKHR");
        GetSwapchainImagesProxy = (delegate* unmanaged<Device, SwapchainKHR, uint*, Image*, Result>)
            SlExports.DeviceProcAddr(device.Handle, "vkGetSwapchainImagesKHR");
        AcquireNextImageProxy = (delegate* unmanaged<Device, SwapchainKHR, ulong, VkSemaphore, Fence, uint*, Result>)
            SlExports.DeviceProcAddr(device.Handle, "vkAcquireNextImageKHR");
        QueuePresentProxy = (delegate* unmanaged<Queue, PresentInfoKHR*, Result>)
            SlExports.DeviceProcAddr(device.Handle, "vkQueuePresentKHR");
        if (CreateSwapchainProxy == null || DestroySwapchainProxy == null || GetSwapchainImagesProxy == null ||
            AcquireNextImageProxy == null || QueuePresentProxy == null)
        {
            QueuePresentProxy = null;
            Console.Error.WriteLine("[DLSS][WARN] DLSS is off: Streamline returned no swapchain proxies.");
            return false;
        }

        _deviceAttached = true;
        Console.Error.WriteLine("[DLSS][INFO] DLSS is ready; PSSR dispatches run through DLSS Super Resolution.");
        return true;
    }

    public bool Evaluate(CommandBuffer command, SuperResolutionFrame frame)
    {
        if (!IsActive)
        {
            return false;
        }

        var dispatch = frame.Dispatch;
        var viewport = SlViewportHandle.Create(dispatch.ContextId == 0 ? 1u : dispatch.ContextId);
        var useExposure = frame.Exposure.IsValid && (dispatch.Flags & SuperResolutionFlags.AutoExposure) == 0;
        var options = new SlDlssOptions
        {
            Header = new SlHeader(SlDlssOptions.TypeId, 3),
            Mode = SelectMode(frame),
            OutputWidth = frame.OutputWidth,
            OutputHeight = frame.OutputHeight,
            Sharpness = 0,
            PreExposure = dispatch.PreExposure > 0 && float.IsFinite(dispatch.PreExposure) ? dispatch.PreExposure : 1f,
            ExposureScale = 1f,
            ColorBuffersHdr = SlBoolean.True,
            UseAutoExposure = useExposure ? SlBoolean.False : SlBoolean.True,
        };
        if (options.Mode == SlDlssMode.Off)
        {
            return false;
        }

        SlResult result;
        if (!_viewportOptions.TryGetValue(viewport.Value, out var previous) || !SameOptions(previous, options))
        {
            result = SlExports.DlssSetOptions(&viewport, &options);
            if (result != SlResult.Ok)
            {
                return Fail($"slDLSSSetOptions failed ({result})");
            }

            _viewportOptions[viewport.Value] = options;
            Console.Error.WriteLine(
                $"[DLSS][INFO] Context {viewport.Value}: mode={options.Mode} render={frame.RenderWidth}x{frame.RenderHeight} " +
                $"output={frame.OutputWidth}x{frame.OutputHeight} exposure={(useExposure ? "texture" : "auto")}.");
        }

        nint token = 0;
        var index = ++_frameIndex;
        result = SlExports.GetNewFrameToken(&token, &index);
        if (result != SlResult.Ok || token == 0)
        {
            return Fail($"slGetNewFrameToken failed ({result})");
        }

        var constants = BuildConstants(frame);
        result = SlExports.SetConstants(&constants, token, &viewport);
        if (result != SlResult.Ok)
        {
            return Fail($"slSetConstants failed ({result})");
        }

        var resources = stackalloc SlResource[5];
        var tags = stackalloc SlResourceTag[5];
        var count = 0u;
        var renderExtent = new SlExtent { Left = frame.RenderX, Top = frame.RenderY, Width = frame.RenderWidth, Height = frame.RenderHeight };
        Tag(resources, tags, ref count, frame.Color, SlBufferType.ScalingInputColor, renderExtent);
        Tag(resources, tags, ref count, frame.Output, SlBufferType.ScalingOutputColor,
            new SlExtent { Width = frame.OutputWidth, Height = frame.OutputHeight });
        Tag(resources, tags, ref count, frame.Depth, SlBufferType.Depth, renderExtent);
        Tag(resources, tags, ref count, frame.MotionVectors, SlBufferType.MotionVectors, renderExtent);
        if (useExposure)
        {
            Tag(resources, tags, ref count, frame.Exposure, SlBufferType.Exposure, new SlExtent { Width = 1, Height = 1 });
        }

        result = SlExports.SetTagForFrame(token, &viewport, tags, count, command.Handle);
        if (result != SlResult.Ok)
        {
            return Fail($"slSetTagForFrame failed ({result})");
        }

        var inputs = stackalloc nint[1];
        inputs[0] = (nint)(&viewport);
        var ngxFailuresBefore = Volatile.Read(ref _ngxFailures);
        result = SlExports.EvaluateFeature(SlFeature.Dlss, token, inputs, 1, command.Handle);
        if (result == SlResult.WarnOutOfVram)
        {
            if (Interlocked.Exchange(ref _vramWarned, 1) == 0)
            {
                Console.Error.WriteLine("[DLSS][WARN] DLSS reports that the GPU memory budget is exceeded; the game and DLSS together use more VRAM than the GPU has.");
            }
        }
        else if (result != SlResult.Ok)
        {
            return Fail($"slEvaluateFeature failed ({result})");
        }

        if (Volatile.Read(ref _ngxFailures) != ngxFailuresBefore)
        {
            return Fail($"NGX rejected the {options.Mode} evaluation of {frame.RenderWidth}x{frame.RenderHeight} to {frame.OutputWidth}x{frame.OutputHeight}");
        }

        Volatile.Write(ref _failures, 0);
        return true;
    }

    private SlDlssMode SelectMode(SuperResolutionFrame frame)
    {
        var key = (frame.RenderWidth, frame.RenderHeight, frame.OutputWidth, frame.OutputHeight);
        if (_modes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var ranges = new Dictionary<SlDlssMode, ModeRange>();
        foreach (var mode in AllModes)
        {
            if (TryQueryRange(frame, mode, out var range))
            {
                ranges[mode] = range;
            }
        }

        var percent = frame.OutputWidth == 0 ? 0 : frame.RenderWidth * 100.0 / frame.OutputWidth;
        Console.Error.WriteLine(
            $"[DLSS][INFO] Game renders {frame.RenderWidth}x{frame.RenderHeight} for {frame.OutputWidth}x{frame.OutputHeight} ({percent:0}%). DLSS input ranges: " +
            string.Join(", ", AllModes.Select(mode => ranges.TryGetValue(mode, out var range)
                ? $"{mode} {range.MinimumWidth}x{range.MinimumHeight}-{range.MaximumWidth}x{range.MaximumHeight}{(range.Accepts(frame) ? " (fits)" : string.Empty)}"
                : $"{mode} unsupported")) + ".");

        var selected = ClosestFittingMode(frame, ranges);
        Console.Error.WriteLine(selected == SlDlssMode.Off
            ? $"[DLSS][WARN] No DLSS mode accepts {frame.RenderWidth}x{frame.RenderHeight} for {frame.OutputWidth}x{frame.OutputHeight}; the basic upscaler runs."
            : $"[DLSS][INFO] DLSS mode {selected}.");

        _modes[key] = selected;
        return selected;
    }

    private static readonly SlDlssMode[] AllModes =
    [
        SlDlssMode.Dlaa,
        SlDlssMode.UltraQuality,
        SlDlssMode.MaxQuality,
        SlDlssMode.Balanced,
        SlDlssMode.MaxPerformance,
        SlDlssMode.UltraPerformance,
    ];

    private static SlDlssMode ClosestFittingMode(SuperResolutionFrame frame, Dictionary<SlDlssMode, ModeRange> ranges)
    {
        var selected = SlDlssMode.Off;
        var bestDistance = float.MaxValue;
        var ratio = frame.RenderWidth / (float)Math.Max(frame.OutputWidth, 1u);
        foreach (var (mode, range) in ranges)
        {
            if (!range.Accepts(frame))
            {
                continue;
            }

            var distance = MathF.Abs(range.OptimalWidth / (float)Math.Max(frame.OutputWidth, 1u) - ratio);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                selected = mode;
            }
        }

        return selected;
    }

    private readonly record struct ModeRange(
        bool Valid,
        uint OptimalWidth,
        uint MinimumWidth,
        uint MinimumHeight,
        uint MaximumWidth,
        uint MaximumHeight)
    {
        public bool Accepts(SuperResolutionFrame frame) =>
            Valid && frame.RenderWidth >= MinimumWidth && frame.RenderWidth <= MaximumWidth &&
            frame.RenderHeight >= MinimumHeight && frame.RenderHeight <= MaximumHeight;
    }

    private static bool TryQueryRange(SuperResolutionFrame frame, SlDlssMode mode, out ModeRange range)
    {
        range = default;
        var options = new SlDlssOptions
        {
            Header = new SlHeader(SlDlssOptions.TypeId, 3),
            Mode = mode,
            OutputWidth = frame.OutputWidth,
            OutputHeight = frame.OutputHeight,
            PreExposure = 1f,
            ExposureScale = 1f,
            ColorBuffersHdr = SlBoolean.True,
        };
        var settings = new SlDlssOptimalSettings { Header = new SlHeader(SlDlssOptimalSettings.TypeId, 1) };
        if (SlExports.DlssGetOptimalSettings(&options, &settings) != SlResult.Ok || settings.OptimalRenderWidth == 0)
        {
            if (mode != SlDlssMode.Dlaa)
            {
                return false;
            }

            range = new ModeRange(true, frame.OutputWidth, frame.OutputWidth, frame.OutputHeight, frame.OutputWidth, frame.OutputHeight);
            return true;
        }

        range = new ModeRange(
            true,
            settings.OptimalRenderWidth,
            settings.RenderWidthMin != 0 ? settings.RenderWidthMin : settings.OptimalRenderWidth,
            settings.RenderHeightMin != 0 ? settings.RenderHeightMin : settings.OptimalRenderHeight,
            settings.RenderWidthMax != 0 ? settings.RenderWidthMax : settings.OptimalRenderWidth,
            settings.RenderHeightMax != 0 ? settings.RenderHeightMax : settings.OptimalRenderHeight);
        return true;
    }

    private static bool SameOptions(in SlDlssOptions left, in SlDlssOptions right) =>
        left.Mode == right.Mode && left.OutputWidth == right.OutputWidth && left.OutputHeight == right.OutputHeight &&
        left.PreExposure == right.PreExposure && left.UseAutoExposure == right.UseAutoExposure;

    private static void Tag(SlResource* resources, SlResourceTag* tags, ref uint count, in SuperResolutionImage image, uint type, SlExtent extent)
    {
        if (!image.IsValid)
        {
            return;
        }

        var index = (int)count++;
        resources[index] = new SlResource
        {
            Header = new SlHeader(SlResource.TypeId, 1),
            Type = SlResource.TypeTexture2D,
            Native = (nint)image.Image,
            View = (nint)image.View,
            State = ImageLayoutGeneral,
            Width = image.Width,
            Height = image.Height,
            NativeFormat = image.Format,
            MipLevels = image.MipLevels,
            ArrayLayers = image.ArrayLayers,
            Flags = image.CreateFlags,
            Usage = image.Usage,
        };
        tags[index] = new SlResourceTag
        {
            Header = new SlHeader(SlResourceTag.TypeId, 1),
            Resource = (nint)(&resources[index]),
            Type = type,
            Lifecycle = SlResourceTag.LifecycleOnlyValidNow,
            Extent = extent,
        };
    }

    private static SlConstants BuildConstants(SuperResolutionFrame frame)
    {
        var dispatch = frame.Dispatch;
        var constants = new SlConstants { Header = new SlHeader(SlConstants.TypeId, 2) };
        var projection = new Matrix4x4(
            dispatch.Projection(0, 0), dispatch.Projection(0, 1), dispatch.Projection(0, 2), dispatch.Projection(0, 3),
            dispatch.Projection(1, 0), dispatch.Projection(1, 1), dispatch.Projection(1, 2), dispatch.Projection(1, 3),
            dispatch.Projection(2, 0), dispatch.Projection(2, 1), dispatch.Projection(2, 2), dispatch.Projection(2, 3),
            dispatch.Projection(3, 0), dispatch.Projection(3, 1), dispatch.Projection(3, 2), dispatch.Projection(3, 3));
        var inverse = Matrix4x4.Identity;
        var hasProjection = projection != default && Matrix4x4.Invert(projection, out inverse);
        if (!hasProjection)
        {
            projection = Matrix4x4.Identity;
            inverse = Matrix4x4.Identity;
        }

        WriteMatrix(constants.CameraViewToClip, projection);
        WriteMatrix(constants.ClipToCameraView, inverse);
        WriteMatrix(constants.ClipToLensClip, Matrix4x4.Identity);
        WriteMatrix(constants.ClipToPrevClip, Matrix4x4.Identity);
        WriteMatrix(constants.PrevClipToClip, Matrix4x4.Identity);
        constants.JitterOffsetX = dispatch.JitterX * SuperResolutionTuning.JitterSignX;
        constants.JitterOffsetY = dispatch.JitterY * SuperResolutionTuning.JitterSignY;
        var renderWidth = Math.Max(frame.RenderWidth, 1u);
        var renderHeight = Math.Max(frame.RenderHeight, 1u);
        var scaleX = dispatch.MotionVectorScaleX != 0 && float.IsFinite(dispatch.MotionVectorScaleX) ? dispatch.MotionVectorScaleX : renderWidth;
        var scaleY = dispatch.MotionVectorScaleY != 0 && float.IsFinite(dispatch.MotionVectorScaleY) ? dispatch.MotionVectorScaleY : renderHeight;
        constants.MvecScaleX = scaleX / renderWidth * SuperResolutionTuning.MotionSignX;
        constants.MvecScaleY = scaleY / renderHeight * SuperResolutionTuning.MotionSignY;
        constants.CameraPinholeOffsetX = 0;
        constants.CameraPinholeOffsetY = 0;
        var position = dispatch.CameraPosition;
        var up = dispatch.CameraUp;
        var right = dispatch.CameraRight;
        var forward = dispatch.CameraForward;
        constants.CameraPos[0] = (float)position.X;
        constants.CameraPos[1] = (float)position.Y;
        constants.CameraPos[2] = (float)position.Z;
        constants.CameraUp[0] = up.X;
        constants.CameraUp[1] = up.Y;
        constants.CameraUp[2] = up.Z;
        constants.CameraRight[0] = right.X;
        constants.CameraRight[1] = right.Y;
        constants.CameraRight[2] = right.Z;
        constants.CameraFwd[0] = forward.X;
        constants.CameraFwd[1] = forward.Y;
        constants.CameraFwd[2] = forward.Z;
        constants.CameraNear = dispatch.NearZ > 0 && float.IsFinite(dispatch.NearZ) ? dispatch.NearZ : 0.1f;
        constants.CameraFar = dispatch.FarZ > 0 && float.IsFinite(dispatch.FarZ) ? dispatch.FarZ : 10000f;
        var yScale = MathF.Abs(projection.M22);
        var xScale = MathF.Abs(projection.M11);
        constants.CameraFov = hasProjection && yScale > 0 ? 2f * MathF.Atan(1f / yScale) : MathF.PI / 3f;
        constants.CameraAspectRatio = hasProjection && xScale > 0 ? yScale / xScale : frame.OutputWidth / (float)Math.Max(frame.OutputHeight, 1u);
        constants.MotionVectorsInvalidValue = InvalidFloat;
        constants.DepthInverted = (dispatch.Flags & SuperResolutionFlags.ReverseDepth) != 0 ? SlBoolean.True : SlBoolean.False;
        constants.CameraMotionIncluded = SlBoolean.True;
        constants.MotionVectors3D = SlBoolean.False;
        constants.Reset = dispatch.Reset ? SlBoolean.True : SlBoolean.False;
        constants.OrthographicProjection = SlBoolean.False;
        constants.MotionVectorsDilated = SlBoolean.False;
        constants.MotionVectorsJittered = SlBoolean.False;
        constants.MinRelativeLinearDepthObjectSeparation = 40f;
        return constants;
    }

    private static void WriteMatrix(float* destination, in Matrix4x4 matrix)
    {
        destination[0] = matrix.M11;
        destination[1] = matrix.M12;
        destination[2] = matrix.M13;
        destination[3] = matrix.M14;
        destination[4] = matrix.M21;
        destination[5] = matrix.M22;
        destination[6] = matrix.M23;
        destination[7] = matrix.M24;
        destination[8] = matrix.M31;
        destination[9] = matrix.M32;
        destination[10] = matrix.M33;
        destination[11] = matrix.M34;
        destination[12] = matrix.M41;
        destination[13] = matrix.M42;
        destination[14] = matrix.M43;
        destination[15] = matrix.M44;
    }

    private bool Fail(string reason)
    {
        var failures = Interlocked.Increment(ref _failures);
        if (Interlocked.Increment(ref _reportedFailures) <= MaxReportedFailures)
        {
            Console.Error.WriteLine($"[DLSS][WARN] DLSS evaluation failed: {reason}.");
        }

        if (failures == FailuresBeforeDisable)
        {
            Console.Error.WriteLine("[DLSS][WARN] DLSS keeps failing; PSSR falls back to the basic upscaler.");
        }

        return false;
    }

    public bool HoldsResources => _viewportOptions.Count > 0;

    public void FreeResources()
    {
        if (!_deviceAttached || _shutdown)
        {
            return;
        }

        foreach (var value in _viewportOptions.Keys)
        {
            var viewport = SlViewportHandle.Create(value);
            _ = SlExports.FreeResources(SlFeature.Dlss, &viewport);
        }

        _viewportOptions.Clear();
    }

    public void Dispose()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        if (_deviceAttached)
        {
            foreach (var value in _viewportOptions.Keys)
            {
                var viewport = SlViewportHandle.Create(value);
                _ = SlExports.FreeResources(SlFeature.Dlss, &viewport);
            }
        }

        if (SlExports.Shutdown != null)
        {
            _ = SlExports.Shutdown();
        }

        foreach (var allocation in _allocations)
        {
            Marshal.FreeHGlobal(allocation);
        }

        _allocations.Clear();
    }

    private nint Allocate(nint allocation)
    {
        _allocations.Add(allocation);
        return allocation;
    }

    private static IReadOnlyList<string> ReadStrings(nint array, uint count)
    {
        if (array == 0 || count == 0)
        {
            return [];
        }

        var values = new List<string>((int)count);
        for (var index = 0; index < count; index++)
        {
            var pointer = ((nint*)array)[index];
            if (pointer != 0 && Marshal.PtrToStringUTF8(pointer) is { Length: > 0 } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static string LogDirectory()
    {
        var directory = Path.Combine(SharpEmuPaths.DataRoot, "user", "logs", "streamline");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static bool IsExpectedWarning(string text) =>
        text.Contains("since it is was not requested by the host", StringComparison.Ordinal) ||
        text.Contains("VK_EXT_debug_utils extension not enabled", StringComparison.Ordinal) ||
        text.Contains("Exceeded VRAM budget", StringComparison.Ordinal) ||
        text.Contains("'getOptimalSettings' returns invalid size", StringComparison.Ordinal) ||
        (text.Contains("Hook sl.common:Vulkan:", StringComparison.Ordinal) && text.Contains("is NOT supported", StringComparison.Ordinal));

    private static string MessageKey(string text)
    {
        var start = 0;
        for (var bracket = 0; bracket < 5; bracket++)
        {
            var close = text.IndexOf(']', start);
            if (close < 0)
            {
                return text;
            }

            start = close + 1;
        }

        return text[start..];
    }

    [UnmanagedCallersOnly]
    private static void OnLog(uint type, byte* message)
    {
        if (message == null || (type == 0 && !VerboseLog))
        {
            return;
        }

        var text = Marshal.PtrToStringUTF8((nint)message)?.TrimEnd('\r', '\n');
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        if (text.Contains("NGX evaluate feature failed", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _ngxFailures);
        }

        if (!VerboseLog && type == 1 && IsExpectedWarning(text))
        {
            return;
        }

        var repeats = MessageCounts.AddOrUpdate(MessageKey(text), 1, static (_, count) => count + 1);
        if (repeats <= MaxRepeatedMessages)
        {
            Console.Error.WriteLine($"[DLSS][{(type == 2 ? "ERROR" : type == 1 ? "WARN" : "INFO")}] {text}");
        }
        else if (repeats is 10 or 100 or 1000 or 10000)
        {
            Console.Error.WriteLine($"[DLSS][{(type == 2 ? "ERROR" : "WARN")}] The previous Streamline message repeated {repeats} times: {MessageKey(text)}");
        }
    }
}
