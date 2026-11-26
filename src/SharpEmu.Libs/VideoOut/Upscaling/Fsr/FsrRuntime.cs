// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.VideoOut.Upscaling.Streamline;
using Silk.NET.Vulkan;
using static SharpEmu.Libs.VideoOut.Upscaling.Fsr.FsrInterop;

namespace SharpEmu.Libs.VideoOut.Upscaling.Fsr;

internal sealed unsafe class FsrRuntime : IDisposable
{
    // The SDK's Vulkan backend has four slots and frees the next slot after dispatch.
    private const int MaxPendingDispatches = 3;
    private readonly Queue<ulong> _pending = new(MaxPendingDispatches);
    private readonly List<(nint Context, ulong Tick)> _retired = [];
    private readonly nint _loader;
    private readonly nint _upscaler;
    private readonly nint _getDeviceProcAddr;
    private readonly PhysicalDevice _physicalDevice;
    private readonly Device _device;
    private readonly delegate* unmanaged[Cdecl]<nint*, Header*, nint, uint> _create;
    private readonly delegate* unmanaged[Cdecl]<nint*, nint, uint> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint*, Header*, uint> _dispatch;
    private readonly delegate* unmanaged[Cdecl]<nint*, Header*, uint> _query;
    private nint _context;
    private ContextKey _key;
    private ContextKey? _failedKey;
    private ulong _lastTick;
    private long _lastFrame;
    private string? _provider;
    private int _warnings;

    private readonly record struct ContextKey(uint GuestId, Size Render, Size Output, ContextFlags Flags);

    private FsrRuntime(nint loader, nint upscaler, nint getDeviceProcAddr, PhysicalDevice physicalDevice, Device device)
    {
        _loader = loader;
        _upscaler = upscaler;
        _getDeviceProcAddr = getDeviceProcAddr;
        _physicalDevice = physicalDevice;
        _device = device;
        _create = (delegate* unmanaged[Cdecl]<nint*, Header*, nint, uint>)NativeLibrary.GetExport(loader, "ffxCreateContext");
        _destroy = (delegate* unmanaged[Cdecl]<nint*, nint, uint>)NativeLibrary.GetExport(loader, "ffxDestroyContext");
        _dispatch = (delegate* unmanaged[Cdecl]<nint*, Header*, uint>)NativeLibrary.GetExport(loader, "ffxDispatch");
        _query = (delegate* unmanaged[Cdecl]<nint*, Header*, uint>)NativeLibrary.GetExport(loader, "ffxQuery");
    }

    public static FsrRuntime? Start(Vk vk, Instance instance, PhysicalDevice physicalDevice, Device device, string directory)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            Console.Error.WriteLine("[FSR][WARN] The AMD FSR SDK Vulkan DLLs require Windows x64. Using the basic upscaler.");
            return null;
        }

        var missing = FsrRuntimeFiles.Missing(directory);
        if (missing.Count != 0)
        {
            Console.Error.WriteLine($"[FSR][WARN] Missing {string.Join(", ", missing)} in {directory}. Using the basic upscaler. SDK: {FsrRuntimeFiles.SdkReleaseUrl}");
            return null;
        }

        foreach (var name in FsrRuntimeFiles.Required)
        {
            if (!AuthenticodeVerifier.IsSignedBy(Path.Combine(directory, name), "Advanced Micro Devices", out var detail))
            {
                Console.Error.WriteLine($"[FSR][WARN] {name}: {detail}. Install the signed AMD SDK DLLs. Using the basic upscaler.");
                return null;
            }
        }

        var getDeviceProcAddr = (nint)vk.GetInstanceProcAddr(instance, "vkGetDeviceProcAddr").Handle;
        if (getDeviceProcAddr == 0)
        {
            Console.Error.WriteLine("[FSR][WARN] vkGetDeviceProcAddr is unavailable. Using the basic upscaler.");
            return null;
        }

        nint upscaler = 0;
        nint loader = 0;
        try
        {
            // Preload the effect from this folder so the SDK loader resolves that copy.
            const DllImportSearchPath searchPath = DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories;
            var assembly = typeof(FsrRuntime).Assembly;
            upscaler = NativeLibrary.Load(Path.Combine(directory, FsrRuntimeFiles.Upscaler), assembly, searchPath);
            loader = NativeLibrary.Load(Path.Combine(directory, FsrRuntimeFiles.Loader), assembly, searchPath);
            var runtime = new FsrRuntime(loader, upscaler, getDeviceProcAddr, physicalDevice, device);
            loader = upscaler = 0;
            return runtime;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            Console.Error.WriteLine($"[FSR][WARN] Could not load the AMD SDK: {ex.Message}. Using the basic upscaler.");
            return null;
        }
        finally
        {
            if (loader != 0)
            {
                NativeLibrary.Free(loader);
            }

            if (upscaler != 0)
            {
                NativeLibrary.Free(upscaler);
            }
        }
    }

    public ulong WaitTick(ulong completedTick)
    {
        while (_pending.TryPeek(out var tick) && tick <= completedTick)
        {
            _pending.Dequeue();
        }

        return _pending.Count == MaxPendingDispatches ? _pending.Peek() : 0;
    }

    public bool Evaluate(CommandBuffer command, SuperResolutionFrame frame, ulong tick, ulong completedTick)
    {
        for (var index = _retired.Count - 1; index >= 0; index--)
        {
            if (_retired[index].Tick <= completedTick)
            {
                Destroy(_retired[index].Context);
                _retired.RemoveAt(index);
            }
        }

        var guest = frame.Dispatch;
        var color = Wrap(frame.Color);
        var depth = Wrap(frame.Depth);
        var motion = Wrap(frame.MotionVectors);
        var output = Wrap(frame.Output, output: true);
        var renderSize = new Size(frame.RenderWidth, frame.RenderHeight);
        var outputSize = new Size(frame.OutputWidth, frame.OutputHeight);
        if (!Fits(color, renderSize) || !Fits(depth, renderSize) || !Fits(motion, renderSize) || !Fits(output, outputSize))
        {
            var (name, image, required) = !Fits(color, renderSize) ? ("color", frame.Color, renderSize) :
                !Fits(depth, renderSize) ? ("depth", frame.Depth, renderSize) :
                !Fits(motion, renderSize) ? ("motion vectors", frame.MotionVectors, renderSize) :
                ("output", frame.Output, outputSize);
            Warn($"FSR cannot use the {name} image ({(Format)image.Format}, {image.Width}x{image.Height}, usage={(ImageUsageFlags)image.Usage}, compatible view={image.FsrCompatibleView}, required={required.Width}x{required.Height})");
            return false;
        }

        if (output.Handle == color.Handle || output.Handle == depth.Handle || output.Handle == motion.Handle ||
            renderSize.Width > outputSize.Width || renderSize.Height > outputSize.Height)
        {
            Warn($"FSR requires a separate output image at least as large as the render dimensions ({renderSize.Width}x{renderSize.Height} to {outputSize.Width}x{outputSize.Height})");
            return false;
        }

        if (frame.RenderX != 0 || frame.RenderY != 0 || guest.MotionVectorOffsetX != 0 || guest.MotionVectorOffsetY != 0 ||
            (guest.Flags & SuperResolutionFlags.ViewSpaceDepth) != 0)
        {
            Warn($"unsupported input space (render offset={frame.RenderX},{frame.RenderY}, motion bias={guest.MotionVectorOffsetX},{guest.MotionVectorOffsetY}, flags={guest.Flags})");
            return false;
        }

        var exposure = Wrap(frame.Exposure);
        var reactive = Wrap(frame.Reactive);
        var useExposure = exposure.Width == 1 && exposure.Height == 1 && exposure.Handle != output.Handle &&
            SuperResolutionTuning.Exposure != "auto" &&
            (SuperResolutionTuning.Exposure == "texture" || (guest.Flags & SuperResolutionFlags.AutoExposure) == 0);
        var hdr = SuperResolutionTuning.Hdr switch
        {
            "0" => false,
            "1" => true,
            _ => SuperResolutionTuning.IsHdrFormat(frame.Color.Format),
        };
        var displayMotion = renderSize != outputSize && motion.Width == outputSize.Width && motion.Height == outputSize.Height;
        var projection = SuperResolutionTuning.Projection(guest);
        var invertedDepth = (guest.Flags & SuperResolutionFlags.ReverseDepth) != 0 ||
            SuperResolutionTuning.InferReverseDepth(projection) == true;
        var flags = ContextFlags.DynamicResolution;
        flags |= hdr ? ContextFlags.HighDynamicRange : 0;
        flags |= displayMotion ? ContextFlags.DisplayResolutionMotion : 0;
        flags |= guest.MotionVectorsJittered ? ContextFlags.MotionJitterCancellation : 0;
        flags |= invertedDepth ? ContextFlags.DepthInverted : 0;
        flags |= float.IsPositiveInfinity(guest.FarZ) ? ContextFlags.DepthInfinite : 0;
        flags |= useExposure ? 0 : ContextFlags.AutoExposure;
        var maxRenderSize = new Size(Math.Min(color.Width, outputSize.Width), Math.Min(color.Height, outputSize.Height));
        var key = new ContextKey(guest.ContextId, maxRenderSize, outputSize, flags);
        if (!EnsureContext(key))
        {
            return false;
        }

        var now = Stopwatch.GetTimestamp();
        var motionSize = displayMotion ? outputSize : renderSize;
        var near = PositiveOr(guest.NearZ, 0.1f);
        var far = PositiveOr(guest.FarZ, 10000f);
        if (far == near)
        {
            far = near * 2;
        }

        var descriptor = new Dispatch
        {
            Header = new Header(DispatchType),
            CommandList = command.Handle,
            Color = color,
            Depth = depth,
            MotionVectors = motion,
            Exposure = useExposure ? exposure : default,
            Reactive = reactive.Handle != output.Handle && Fits(reactive, renderSize) ? reactive : default,
            Output = output,
            JitterOffset = new FloatPair(
                FiniteOr(guest.JitterX, 0) * SuperResolutionTuning.JitterSignX,
                FiniteOr(guest.JitterY, 0) * SuperResolutionTuning.JitterSignY),
            MotionVectorScale = new FloatPair(
                MotionScale(guest.MotionVectorScaleX, motionSize.Width) * SuperResolutionTuning.MotionSignX,
                MotionScale(guest.MotionVectorScaleY, motionSize.Height) * SuperResolutionTuning.MotionSignY),
            RenderSize = renderSize,
            UpscaleSize = outputSize,
            FrameTimeDelta = _lastFrame == 0 || guest.Reset ? 1000f / 60f : (float)Stopwatch.GetElapsedTime(_lastFrame, now).TotalMilliseconds,
            PreExposure = PositiveOr(guest.PreExposure, 1),
            Reset = (byte)(_lastFrame == 0 || guest.Reset ? 1 : 0),
            CameraNear = near,
            CameraFar = far,
            CameraFovAngleVertical = 2 * MathF.Atan(1 / PositiveOr(MathF.Abs(projection.M22), MathF.Sqrt(3))),
            ViewSpaceToMetersFactor = 1,
        };
        var context = _context;
        var result = _dispatch(&context, &descriptor.Header);
        _lastTick = tick;
        _pending.Enqueue(tick);
        if (result != 0)
        {
            Warn($"SDK dispatch returned {result}");
            _failedKey = key;
            RetireContext();
            return false;
        }

        if (_lastFrame == 0)
        {
            Console.Error.WriteLine($"[PSSR][INFO] Upscaling with FSR ({_provider}), {renderSize.Width}x{renderSize.Height} to {outputSize.Width}x{outputSize.Height}.");
        }

        _lastFrame = now;
        return true;
    }

    private bool EnsureContext(ContextKey key)
    {
        if (_context != 0 && _key == key)
        {
            return true;
        }

        if (_failedKey == key)
        {
            return false;
        }

        // This declares the header ABI. The AMD loader selects the provider for the device.
        var version = new ApiVersion { Header = new Header(VersionType), Value = UpscaleApiVersion };
        var backend = new VulkanBackend
        {
            Header = new Header(VulkanBackendType, &version.Header),
            Device = _device.Handle,
            PhysicalDevice = _physicalDevice.Handle,
            GetDeviceProcAddr = _getDeviceProcAddr,
        };
        var descriptor = new Create
        {
            Header = new Header(CreateType, &backend.Header),
            Flags = key.Flags,
            MaxRenderSize = key.Render,
            MaxUpscaleSize = key.Output,
        };
        nint context = 0;
        var result = _create(&context, &descriptor.Header, 0);
        if (result != 0 || context == 0)
        {
            Warn($"SDK context creation returned {result} for {key.Render} to {key.Output} ({key.Flags})");
            _failedKey = key;
            return false;
        }

        RetireContext();
        _context = context;
        _key = key;
        var provider = new ProviderVersion { Header = new Header(ProviderVersionType) };
        _provider = _query(&context, &provider.Header) == 0 ? Marshal.PtrToStringAnsi(provider.Name) : null;
        _provider ??= "SDK-selected provider";
        return true;
    }

    private void RetireContext()
    {
        if (_context != 0)
        {
            _retired.Add((_context, _lastTick));
            _context = 0;
        }

        _pending.Clear();
        _lastTick = 0;
        _lastFrame = 0;
    }

    public void ReleaseResources()
    {
        RetireContext();
        foreach (var (context, _) in _retired)
        {
            Destroy(context);
        }

        _retired.Clear();
    }

    private void Destroy(nint context)
    {
        var result = _destroy(&context, 0);
        if (result != 0)
        {
            Console.Error.WriteLine($"[FSR][WARN] SDK context destruction returned {result}.");
        }
    }

    public bool HoldsResources => _context != 0 || _retired.Count != 0;

    public void Dispose()
    {
        ReleaseResources();
        NativeLibrary.Free(_loader);
        NativeLibrary.Free(_upscaler);
    }

    private void Warn(string reason)
    {
        if (++_warnings <= 4)
        {
            Console.Error.WriteLine($"[FSR][WARN] {reason}. Using the basic upscaler.");
        }
    }

    private static bool Fits(Resource resource, Size size) =>
        size.Width != 0 && size.Height != 0 && size.Width <= resource.Width && size.Height <= resource.Height;

    private static float FiniteOr(float value, float fallback) => float.IsFinite(value) ? value : fallback;

    private static float PositiveOr(float value, float fallback) => value > 0 ? FiniteOr(value, fallback) : fallback;

    private static float MotionScale(float value, uint fallback) => value != 0 ? FiniteOr(value, fallback) : fallback;
}
