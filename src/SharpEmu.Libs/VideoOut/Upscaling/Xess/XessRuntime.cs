// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Upscaling;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.VideoOut.Upscaling.Streamline;
using Silk.NET.Vulkan;
using static SharpEmu.Libs.VideoOut.Upscaling.Xess.XessInterop;

namespace SharpEmu.Libs.VideoOut.Upscaling.Xess;

internal sealed unsafe class XessRuntime : IDisposable
{
    private const string MutableDescriptors = "VK_EXT_mutable_descriptor_type";
    private readonly Vk _vk;
    private readonly nint _library;
    private readonly delegate* unmanaged[Cdecl]<uint*, byte***, uint*, int> _instanceExtensions;
    private readonly delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, uint*, byte***, int> _deviceExtensions;
    private readonly delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, void**, int> _deviceFeatures;
    private readonly delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, Device, nint*, int> _create;
    private readonly delegate* unmanaged[Cdecl]<nint, int> _destroy;
    private readonly delegate* unmanaged[Cdecl]<nint, Extent2D*, Quality, Extent2D*, Extent2D*, Extent2D*, int> _resolution;
    private readonly delegate* unmanaged[Cdecl]<nint, InitParameters*, int> _init;
    private readonly delegate* unmanaged[Cdecl]<nint, float, float, int> _motionScale;
    private readonly delegate* unmanaged[Cdecl]<nint, float, int> _exposureMultiplier;
    private readonly delegate* unmanaged[Cdecl]<nint, CommandBuffer, ExecuteParameters*, int> _execute;
    private readonly List<(nint Context, ulong Tick)> _retired = [];
    private Instance _instance;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private nint _context;
    private ContextKey _key;
    private (ContextKey Key, Extent2D Input)? _failed;
    private Extent2D _minimumInput;
    private Extent2D _maximumInput;
    private ulong _lastTick;
    private bool _history;
    private int _warnings;

    private readonly record struct ContextKey(uint GuestId, uint Width, uint Height, InitFlags Flags);

    private XessRuntime(Vk vk, nint library)
    {
        _vk = vk;
        _library = library;
        nint Export(string name) => NativeLibrary.GetExport(library, name);
        _instanceExtensions = (delegate* unmanaged[Cdecl]<uint*, byte***, uint*, int>)Export("xessVKGetRequiredInstanceExtensions");
        _deviceExtensions = (delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, uint*, byte***, int>)Export("xessVKGetRequiredDeviceExtensions");
        _deviceFeatures = (delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, void**, int>)Export("xessVKGetRequiredDeviceFeatures");
        _create = (delegate* unmanaged[Cdecl]<Instance, PhysicalDevice, Device, nint*, int>)Export("xessVKCreateContext");
        _destroy = (delegate* unmanaged[Cdecl]<nint, int>)Export("xessDestroyContext");
        _resolution = (delegate* unmanaged[Cdecl]<nint, Extent2D*, Quality, Extent2D*, Extent2D*, Extent2D*, int>)Export("xessGetOptimalInputResolution");
        _init = (delegate* unmanaged[Cdecl]<nint, InitParameters*, int>)Export("xessVKInit");
        _motionScale = (delegate* unmanaged[Cdecl]<nint, float, float, int>)Export("xessSetVelocityScale");
        _exposureMultiplier = (delegate* unmanaged[Cdecl]<nint, float, int>)Export("xessSetExposureMultiplier");
        _execute = (delegate* unmanaged[Cdecl]<nint, CommandBuffer, ExecuteParameters*, int>)Export("xessVKExecute");
    }

    public IReadOnlyList<string> InstanceExtensions { get; private set; } = [];
    public IReadOnlyList<string> DeviceExtensions { get; private set; } = [];
    public bool HoldsResources => _context != 0 || _retired.Count != 0;

    public static XessRuntime? Load(Vk vk, string directory, Func<string, bool> hasInstanceExtension)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            LogFallback("The Intel XeSS SDK requires Windows x64");
            return null;
        }

        var path = Path.Combine(directory, XessRuntimeFiles.Library);
        if (!AuthenticodeVerifier.IsSignedBy(path, "Intel Corporation", out var signature))
        {
            LogFallback($"{path}: {signature}. SDK: {XessRuntimeFiles.SdkReleaseUrl}");
            return null;
        }

        nint library = 0;
        try
        {
            const DllImportSearchPath search = DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.SafeDirectories;
            library = NativeLibrary.Load(path, typeof(XessRuntime).Assembly, search);
            var runtime = new XessRuntime(vk, library);
            uint count = 0, version = 0;
            byte** names = null;
            var result = runtime._instanceExtensions(&count, &names, &version);
            if (result != 0 || version > Vk.Version13)
            {
                LogFallback($"Vulkan instance requirements could not be satisfied (SDK result {result}, API {version >> 22}.{(version >> 12) & 0x3ff})");
                return null;
            }

            runtime.InstanceExtensions = ReadNames(count, names);
            foreach (var name in runtime.InstanceExtensions)
            {
                if (!hasInstanceExtension(name))
                {
                    LogFallback($"The Vulkan loader does not expose {name}");
                    return null;
                }
            }
            library = 0;
            return runtime;
        }
        catch (Exception exception) when (exception is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            LogFallback($"Could not load the Intel SDK ({exception.Message})");
            return null;
        }
        finally
        {
            if (library != 0)
            {
                NativeLibrary.Free(library);
            }
        }
    }

    public bool SelectDevice(Instance instance, PhysicalDevice physicalDevice, Func<string, bool> hasDeviceExtension)
    {
        uint count = 0;
        byte** names = null;
        var result = _deviceExtensions(instance, physicalDevice, &count, &names);
        if (result != 0)
        {
            Warn($"Vulkan device extension query returned {result}");
            return false;
        }

        var extensions = ReadNames(count, names);
        foreach (var name in extensions)
        {
            if (!hasDeviceExtension(name))
            {
                Warn($"The GPU does not expose {name}");
                return false;
            }
        }

        DeviceExtensions = extensions;
        _instance = instance;
        _physicalDevice = physicalDevice;
        return true;
    }

    public bool EnableDeviceFeatures(ref PhysicalDeviceFeatures features, ref PhysicalDeviceVulkan12Features vulkan12,
        ref PhysicalDeviceVulkan13Features vulkan13, PhysicalDeviceMutableDescriptorTypeFeaturesEXT* mutable)
    {
        // Let XeSS fill the requested fields on copies so an unsupported adapter cannot change the presenter's feature chain.
        var requested13 = vulkan13;
        var requested12 = vulkan12;
        var needsMutable = DeviceExtensions.Contains(MutableDescriptors, StringComparer.Ordinal);
        *mutable = new PhysicalDeviceMutableDescriptorTypeFeaturesEXT { SType = StructureType.PhysicalDeviceMutableDescriptorTypeFeaturesExt };
        requested13.PNext = &requested12;
        requested12.PNext = needsMutable ? mutable : null;
        var requested = new PhysicalDeviceFeatures2 { SType = StructureType.PhysicalDeviceFeatures2, Features = features, PNext = &requested13 };
        void* chain = &requested;
        var result = _deviceFeatures(_instance, _physicalDevice, &chain);
        if (result != 0 || chain != &requested || requested.PNext != &requested13 || requested13.PNext != &requested12 ||
            requested12.PNext != (needsMutable ? mutable : null) || mutable->PNext != null)
        {
            Warn($"Vulkan device feature requirements could not be satisfied (SDK result {result})");
            return false;
        }

        features = requested.Features;
        requested12.PNext = vulkan12.PNext;
        vulkan12 = requested12;
        requested13.PNext = vulkan13.PNext;
        vulkan13 = requested13;
        if (needsMutable)
        {
            mutable->PNext = vulkan13.PNext;
            vulkan13.PNext = mutable;
        }
        return true;
    }

    public void Attach(Device device) => _device = device;

    public bool Evaluate(CommandBuffer command, SuperResolutionFrame frame, ulong tick, ulong completedTick, object queueGate)
    {
        for (var index = _retired.Count - 1; index >= 0; index--)
        {
            if (_retired[index].Tick <= completedTick)
            {
                _ = _destroy(_retired[index].Context);
                _retired.RemoveAt(index);
            }
        }

        var hadHistory = _history;
        _history = false;
        var guest = frame.Dispatch;
        if ((guest.Flags & SuperResolutionFlags.PassThrough) != 0)
        {
            return false;
        }

        var input = new Extent2D(frame.RenderWidth, frame.RenderHeight);
        var output = new Extent2D(frame.OutputWidth, frame.OutputHeight);
        var origin = new Extent2D(frame.RenderX, frame.RenderY);
        var highResMotion = frame.MotionVectors.Width == output.Width && frame.MotionVectors.Height == output.Height &&
            (input.Width != output.Width || input.Height != output.Height);
        var motionBase = highResMotion ? default : origin;
        var motionSize = highResMotion ? output : input;
        if (input.Width == 0 || input.Height == 0 || input.Width > output.Width || input.Height > output.Height ||
            !Fits(frame.Color, origin, input) || !Fits(frame.MotionVectors, motionBase, motionSize) ||
            (!highResMotion && !Fits(frame.Depth, origin, input)) || !Fits(frame.Output, default, output) ||
            (Format)frame.MotionVectors.Format != Format.R16G16Sfloat ||
            guest.MotionVectorOffsetX != 0 || guest.MotionVectorOffsetY != 0 ||
            (guest.Flags & SuperResolutionFlags.ViewSpaceDepth) != 0)
        {
            Warn("The PSSR dispatch uses unsupported XeSS input resources, motion vectors or dimensions");
            return false;
        }

        var hdr = SuperResolutionTuning.Hdr switch
        {
            "0" => false,
            "1" => true,
            _ => SuperResolutionTuning.IsHdrFormat(frame.Color.Format),
        };
        var exposure = hdr && frame.Exposure.Width == 1 && frame.Exposure.Height == 1 &&
            frame.Exposure.XessCompatibleView && SuperResolutionTuning.Exposure != "auto" &&
            (SuperResolutionTuning.Exposure == "texture" || (guest.Flags & SuperResolutionFlags.AutoExposure) == 0);
        var mask = Fits(frame.Reactive, origin, input);
        var depth = highResMotion ? default : frame.Depth;
        var exposureImage = exposure ? frame.Exposure : default;
        var maskImage = mask ? frame.Reactive : default;
        if (frame.Output.Image == frame.Color.Image || frame.Output.Image == frame.MotionVectors.Image ||
            frame.Output.Image == depth.Image || frame.Output.Image == exposureImage.Image || frame.Output.Image == maskImage.Image)
        {
            Warn("XeSS requires separate input and output images");
            return false;
        }

        var flags = highResMotion ? InitFlags.HighResolutionMotion : 0;
        flags |= guest.MotionVectorsJittered ? InitFlags.JitteredMotion : 0;
        flags |= !highResMotion && ((guest.Flags & SuperResolutionFlags.ReverseDepth) != 0 ||
            SuperResolutionTuning.InferReverseDepth(SuperResolutionTuning.Projection(guest)) == true) ? InitFlags.InvertedDepth : 0;
        flags |= !hdr ? InitFlags.LdrInput : exposure ? InitFlags.ExposureTexture : InitFlags.AutoExposure;
        flags |= mask ? InitFlags.ResponsiveMask : 0;
        var key = new ContextKey(guest.ContextId, output.Width, output.Height, flags);
        var keepHistory = hadHistory && _key == key && InRange(input, _minimumInput, _maximumInput);
        if (!EnsureContext(key, input, queueGate))
        {
            return false;
        }

        var scaleX = MotionScale(guest.MotionVectorScaleX, motionSize.Width) * SuperResolutionTuning.MotionSignX;
        var scaleY = MotionScale(guest.MotionVectorScaleY, motionSize.Height) * SuperResolutionTuning.MotionSignY;
        var preExposure = hdr && float.IsFinite(guest.PreExposure) && guest.PreExposure > 0 ? guest.PreExposure : 1;
        var result = _motionScale(_context, scaleX, scaleY);
        if (result == 0)
        {
            result = _exposureMultiplier(_context, 1f / preExposure);
        }
        if (result != 0)
        {
            Warn($"XeSS frame parameters returned {result}");
            return false;
        }

        var parameters = new ExecuteParameters
        {
            Color = Wrap(frame.Color),
            Motion = Wrap(frame.MotionVectors),
            Depth = Wrap(depth),
            Exposure = Wrap(exposureImage),
            ResponsiveMask = Wrap(maskImage),
            Output = Wrap(frame.Output),
            JitterX = Jitter(guest.JitterX) * SuperResolutionTuning.JitterSignX,
            JitterY = -Jitter(guest.JitterY) * SuperResolutionTuning.JitterSignY,
            ExposureScale = 1,
            Reset = guest.Reset || !keepHistory ? 1u : 0u,
            Input = input,
            ColorBase = origin,
            MotionBase = motionBase,
            DepthBase = origin,
            MaskBase = origin,
        };
        var barriers = stackalloc ImageMemoryBarrier2[5];
        uint count = 0;
        AddInputBarrier(barriers, ref count, parameters.Color);
        AddInputBarrier(barriers, ref count, parameters.Motion);
        AddInputBarrier(barriers, ref count, parameters.Depth);
        AddInputBarrier(barriers, ref count, parameters.Exposure);
        AddInputBarrier(barriers, ref count, parameters.ResponsiveMask);
        VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.AllCommandsBit, PipelineStageFlags.ComputeShaderBit,
            0, 0, null, 0, null, count, barriers, BarrierSource.Image);
        result = _execute(_context, command, &parameters);
        _lastTick = tick;
        for (var index = 0u; index < count; index++)
        {
            barriers[index].OldLayout = ImageLayout.ShaderReadOnlyOptimal;
            barriers[index].NewLayout = ImageLayout.General;
            barriers[index].SrcAccessMask = AccessFlags2.ShaderReadBit;
            barriers[index].DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit;
        }
        var outputBarrier = new MemoryBarrier2
        {
            SType = StructureType.MemoryBarrier2,
            SrcAccessMask = AccessFlags2.ShaderWriteBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        // Restore the cache's general layout and order a fallback after any commands the SDK recorded.
        VulkanSynchronization.PipelineBarrier(_vk, command, PipelineStageFlags.ComputeShaderBit, PipelineStageFlags.AllCommandsBit,
            0, result == 0 ? 0u : 1u, &outputBarrier, 0, null, count, barriers, BarrierSource.Image);
        if (result != 0)
        {
            Warn($"XeSS dispatch returned {result}");
            RetireContext();
            _failed = (key, input);
            return false;
        }

        if (!keepHistory)
        {
            Console.Error.WriteLine($"[PSSR][INFO] Upscaling with XeSS, {input.Width}x{input.Height} to {output.Width}x{output.Height}.");
        }
        _history = true;
        return true;
    }

    private bool EnsureContext(ContextKey key, Extent2D input, object queueGate)
    {
        if (_context != 0 && _key == key && InRange(input, _minimumInput, _maximumInput))
        {
            return true;
        }
        if (_failed is { } failure && failure.Key == key &&
            failure.Input.Width == input.Width && failure.Input.Height == input.Height)
        {
            return false;
        }
        if (_retired.Count >= 2)
        {
            return false;
        }

        nint context = 0;
        var result = _create(_instance, _physicalDevice, _device, &context);
        var output = new Extent2D(key.Width, key.Height);
        if (result == 0 && context != 0 && SelectQuality(context, input, output, out var quality, out var minimum, out var maximum))
        {
            var init = new InitParameters { Output = output, Quality = quality, Flags = key.Flags };
            // XeSS can submit setup copies during initialization. Take the same gate as the presenter.
            lock (queueGate)
            {
                result = _init(context, &init);
            }
            if (result == 0)
            {
                RetireContext();
                _context = context;
                _key = key;
                _minimumInput = minimum;
                _maximumInput = maximum;
                return true;
            }
        }

        if (context != 0)
        {
            _ = _destroy(context);
        }
        _failed = (key, input);
        Warn($"No XeSS context for {input.Width}x{input.Height} to {key.Width}x{key.Height} (SDK result {result})");
        return false;
    }

    private bool SelectQuality(nint context, Extent2D input, Extent2D output,
        out Quality selected, out Extent2D minimum, out Extent2D maximum)
    {
        var best = long.MaxValue;
        selected = default;
        minimum = maximum = default;
        for (var quality = Quality.NativeAa; quality >= Quality.UltraPerformance; quality--)
        {
            Extent2D optimal, min, max;
            if (_resolution(context, &output, quality, &optimal, &min, &max) != 0 || !InRange(input, min, max))
            {
                continue;
            }

            var score = Math.Abs((long)input.Width - optimal.Width) + Math.Abs((long)input.Height - optimal.Height);
            if (score < best)
            {
                best = score;
                selected = quality;
                minimum = min;
                maximum = max;
            }
        }
        return best != long.MaxValue;
    }

    private void RetireContext()
    {
        if (_context != 0)
        {
            _retired.Add((_context, _lastTick));
            _context = 0;
        }
        _lastTick = 0;
        _history = false;
    }

    // The presenter drains the GPU before releasing inactive contexts or the device.
    public void ReleaseResources()
    {
        RetireContext();
        foreach (var (context, _) in _retired)
        {
            _ = _destroy(context);
        }
        _retired.Clear();
    }

    public void Dispose()
    {
        ReleaseResources();
        NativeLibrary.Free(_library);
    }

    private static bool Fits(in SuperResolutionImage image, Extent2D offset, Extent2D size) =>
        image.IsValid && image.XessCompatibleView && size.Width <= image.Width && size.Height <= image.Height &&
        offset.Width <= image.Width - size.Width && offset.Height <= image.Height - size.Height;

    private static bool InRange(Extent2D size, Extent2D min, Extent2D max) =>
        size.Width >= min.Width && size.Height >= min.Height && size.Width <= max.Width && size.Height <= max.Height;

    private static float MotionScale(float value, uint fallback) => float.IsFinite(value) && value != 0 ? value : fallback;
    private static float Jitter(float value) => float.IsFinite(value) ? Math.Clamp(value, -0.5f, 0.5f) : 0;

    private static ImageViewInfo Wrap(in SuperResolutionImage image) => !image.IsValid ? default : new ImageViewInfo
    {
        View = new ImageView(image.View),
        Image = new Image(image.Image),
        Range = new ImageSubresourceRange(image.IsDepth ? ImageAspectFlags.DepthBit : ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        Format = (Format)image.Format,
        Width = image.Width,
        Height = image.Height,
    };

    private static void AddInputBarrier(ImageMemoryBarrier2* barriers, ref uint count, ImageViewInfo input)
    {
        if (input.Image.Handle == 0)
        {
            return;
        }
        for (var index = 0u; index < count; index++)
        {
            if (barriers[index].Image.Handle == input.Image.Handle)
            {
                return;
            }
        }

        // A depth-stencil image may require a joint layout transition even though the shader reads depth only.
        var range = input.Range;
        range.AspectMask = ViewFormatRules.FullAspects(input.Format);
        barriers[count++] = new ImageMemoryBarrier2
        {
            SType = StructureType.ImageMemoryBarrier2,
            Image = input.Image,
            SubresourceRange = range,
            OldLayout = ImageLayout.General,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcAccessMask = AccessFlags2.MemoryWriteBit,
            DstAccessMask = AccessFlags2.ShaderReadBit,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
        };
    }

    private static string[] ReadNames(uint count, byte** names)
    {
        var result = new string[count];
        for (var index = 0; index < result.Length; index++)
        {
            result[index] = Marshal.PtrToStringAnsi((nint)names[index])!;
        }
        return result;
    }

    private void Warn(string detail)
    {
        if (++_warnings <= 4)
        {
            LogFallback(detail);
        }
    }

    private static void LogFallback(string detail) => Console.Error.WriteLine($"[XESS][WARN] {detail}. Using the basic upscaler.");
}
