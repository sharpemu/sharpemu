// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Silk.NET.Input;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Windowing;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using VkBuffer = Silk.NET.Vulkan.Buffer;
using VkSemaphore = Silk.NET.Vulkan.Semaphore;

namespace SharpEmu.Libs.VideoOut;

internal readonly record struct VulkanRenderTargetFormat(
    Format Format,
    Gen5PixelOutputKind OutputKind)
{
    public bool IsInteger => OutputKind is Gen5PixelOutputKind.Uint or Gen5PixelOutputKind.Sint;
}

internal sealed record VulkanTranslatedGuestDraw(
    byte[] VertexSpirv,
    byte[] PixelSpirv,
    IReadOnlyList<GuestDrawTexture> Textures,
    IReadOnlyList<GuestMemoryBuffer> GlobalMemoryBuffers,
    IReadOnlyList<GuestVertexBuffer> VertexBuffers,
    uint AttributeCount,
    uint VertexCount,
    uint InstanceCount,
    uint PrimitiveType,
    GuestIndexBuffer? IndexBuffer,
    GuestRenderState RenderState);

internal sealed record VulkanOffscreenGuestDraw(
    VulkanTranslatedGuestDraw Draw,
    IReadOnlyList<GuestRenderTarget> Targets,
    bool PublishTarget);

internal sealed record VulkanComputeGuestDispatch(
    ulong ShaderAddress,
    byte[] ComputeSpirv,
    IReadOnlyList<GuestDrawTexture> Textures,
    IReadOnlyList<GuestMemoryBuffer> GlobalMemoryBuffers,
    uint GroupCountX,
    uint GroupCountY,
    uint GroupCountZ);

internal static unsafe class VulkanVideoPresenter
{
    private const uint DefaultWindowWidth = 1280;
    private const uint DefaultWindowHeight = 720;
    private const int MaxPendingGuestWork = 16;
    // A single guest frame commonly contains 30-50 translated draws.  Limiting
    // this to 16 split one frame across several 60 Hz window callbacks and
    // unnecessarily throttled the producer behind the bounded work queue.
    private const int MaxGuestWorkPerRender = 128;
    private const uint GuestPrimitiveRectList = 0x11;
    private const uint GuestFormatR32Uint = 0x10004;
    private const uint GuestFormatR32Sint = 0x20004;
    private const uint GuestFormatR32Sfloat = 0x30004;
    private const uint GuestFormatR16G16Uint = 0x10005;
    private const uint GuestFormatR16G16Sint = 0x20005;
    private const uint GuestFormatR16G16Sfloat = 0x30005;
    private const uint GuestFormatR8G8B8A8Uint = 0x1000A;
    private const uint GuestFormatR8G8B8A8Sint = 0x2000A;
    private const uint GuestFormatR16G16B16A16Uint = 0x1000C;
    private const uint GuestFormatR16G16B16A16Sint = 0x2000C;

    private static readonly object _gate = new();
    private static readonly Queue<object> _pendingGuestWork = new();
    private static readonly Dictionary<ulong, uint> _availableGuestImages = new();
    private static readonly Dictionary<ulong, uint> _gpuGuestImages = new();
    private static readonly HashSet<(ulong Address, uint Width, uint Height)>
        _tracedGuestImageSubmissions = [];
    private static Thread? _thread;
    private static Presentation? _latestPresentation;
    private static byte[]? _copyFragmentSpirv;
    private static uint _windowWidth;
    private static uint _windowHeight;
    private static bool _closed;
    private static bool _presenterCloseRequested;
    private const string DebugUtilsExtensionName = "VK_EXT_debug_utils";
    private const uint NvidiaVendorId = 0x10DE;
    private const string PortabilityEnumerationExtensionName = "VK_KHR_portability_enumeration";
    private const string PortabilitySubsetExtensionName = "VK_KHR_portability_subset";
    private static bool _splashHidden;
    private static long _enqueuedGuestWorkSequence;
    private static long _completedGuestWorkSequence;

    private static bool ShouldTracePresentedGuestImageContentsForDiagnostics()
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        return string.Equals(mode, "1", StringComparison.Ordinal) ||
               string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldTraceGuestImageSubmissionsForDiagnostics()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES"),
            "1",
            StringComparison.Ordinal);
    }

    private static bool ShouldSamplePresentedGuestImageForDiagnostics(long frame)
    {
        var mode = Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES");
        if (string.Equals(mode, "present", StringComparison.OrdinalIgnoreCase))
        {
            // A 4K Vulkan readback is deliberately synchronous and can take
            // several seconds on Linux.  The lightweight "present" mode only
            // needs one proof that the final image is non-black.
            return frame == 1;
        }

        return string.Equals(mode, "1", StringComparison.Ordinal) &&
               (frame is 1 or 30 or 120 || frame % 600 == 0);
    }

    public static void EnsureStarted(uint width, uint height)
    {
        if (width == 0 || height == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }
        }

        var hasSplash = PngSplashLoader.TryLoad(
            out var splashPixels,
            out var splashWidth,
            out var splashHeight);
        lock (_gate)
        {
            if (_closed || _thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            _latestPresentation ??= _splashHidden
                ? new Presentation(
                    CreateBlackFrame(width, height),
                    width,
                    height,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: false)
                : hasSplash
                ? new Presentation(
                    splashPixels,
                    splashWidth,
                    splashHeight,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: true)
                : new Presentation(
                    null,
                    width,
                    height,
                    0,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                    IsSplash: false);
            StartPresenterLocked();
        }
    }

    public static void HideSplashScreen()
    {
        lock (_gate)
        {
            _splashHidden = true;
            if (_closed || _latestPresentation is not { IsSplash: true } latest)
            {
                return;
            }

            var sequence = latest.Sequence + 1;
            _latestPresentation = new Presentation(
                CreateBlackFrame(latest.Width, latest.Height),
                latest.Width,
                latest.Height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            Console.Error.WriteLine("[LOADER][INFO] Vulkan VideoOut hid splash");
        }
    }

    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        if (ShouldTraceGuestImageSubmissionsForDiagnostics())
        {
            Console.Error.WriteLine($"[LOADER][TRACE] vk.submit_call kind=Submit {width}x{height}");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                bgraFrame,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height)
    {
        if (drawKind == GuestDrawKind.None || width == 0 || height == 0)
        {
            return;
        }

        if (ShouldTraceGuestImageSubmissionsForDiagnostics())
        {
            Console.Error.WriteLine($"[LOADER][TRACE] vk.submit_call kind=SubmitGuestDraw({drawKind}) {width}x{height}");
        }

        lock (_gate)
        {
            if (_closed ||
                _latestPresentation is { Pixels: null } latest &&
                latest.DrawKind == drawKind &&
                latest.Width == width &&
                latest.Height == height)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                drawKind,
                TranslatedDraw: null,
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null)
    {
        if (pixelSpirv.Length == 0 || width == 0 || height == 0)
        {
            return;
        }

        if (ShouldTraceGuestImageSubmissionsForDiagnostics())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_call kind=SubmitTranslatedDraw {width}x{height} textures={textures.Count}");
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                new VulkanTranslatedGuestDraw(
                    vertexSpirv ?? [],
                    pixelSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    vertexBuffers?.ToArray() ?? [],
                    attributeCount,
                    vertexCount,
                    instanceCount,
                    primitiveType,
                    indexBuffer,
                    renderState ?? GuestRenderState.Default),
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    public static void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        GuestRenderTarget target,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null)
    {
        SubmitOffscreenTranslatedDraw(
            pixelSpirv,
            textures,
            globalMemoryBuffers,
            attributeCount,
            [target],
            vertexSpirv,
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState);
    }

    public static void SubmitOffscreenTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<GuestRenderTarget> targets,
        byte[]? vertexSpirv = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null)
    {
        if (pixelSpirv.Length == 0 ||
            targets.Count == 0 ||
            targets.Count > 8 ||
            targets.Any(target =>
                target.Address == 0 || target.Width == 0 || target.Height == 0))
        {
            return;
        }

        var firstTarget = targets[0];
        if (targets.Any(target =>
                target.Width != firstTarget.Width || target.Height != firstTarget.Height) ||
            targets.Select(target => target.Address).Distinct().Count() != targets.Count)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Vulkan skipped MRT draw with mismatched dimensions or aliased targets.");
            return;
        }

        if (ShouldTraceGuestImageSubmissionsForDiagnostics())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_call kind=SubmitOffscreenTranslatedDraw " +
                $"targets={targets.Count} first=0x{firstTarget.Address:X16} " +
                $"{firstTarget.Width}x{firstTarget.Height} textures={textures.Count}");
        }

        var effectiveRenderState = renderState ?? GuestRenderState.Default;
        if (effectiveRenderState.Blends.Count == 1 && targets.Count > 1)
        {
            effectiveRenderState = effectiveRenderState with
            {
                Blends = Enumerable.Repeat(effectiveRenderState.Blends[0], targets.Count).ToArray(),
            };
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            foreach (var target in targets)
            {
                var guestTextureFormat = GetGuestTextureFormat(
                    target.Format,
                    target.NumberType);
                if (guestTextureFormat != 0)
                {
                    _availableGuestImages[target.Address] = guestTextureFormat;
                }
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        vertexSpirv ?? [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        vertexBuffers?.ToArray() ?? [],
                        attributeCount,
                        vertexCount,
                        instanceCount,
                        primitiveType,
                        indexBuffer,
                        effectiveRenderState),
                    targets.ToArray(),
                    PublishTarget: true));
        }
    }

    public static void SubmitStorageTranslatedDraw(
        byte[] pixelSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height)
    {
        if (pixelSpirv.Length == 0 ||
            width == 0 ||
            height == 0 ||
            textures.All(texture => !texture.IsStorage))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanOffscreenGuestDraw(
                    new VulkanTranslatedGuestDraw(
                        [],
                        pixelSpirv,
                        textures.ToArray(),
                        globalMemoryBuffers.ToArray(),
                        [],
                        attributeCount,
                        3,
                        1,
                        4,
                        null,
                        GuestRenderState.Default),
                    [new GuestRenderTarget(
                        Address: 0,
                        width,
                        height,
                        Format: 12,
                        NumberType: 7)],
                    PublishTarget: false));
        }
    }

    public static void SubmitComputeDispatch(
        ulong shaderAddress,
        byte[] computeSpirv,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ)
    {
        if (computeSpirv.Length == 0 ||
            groupCountX == 0 ||
            groupCountY == 0 ||
            groupCountZ == 0 ||
            textures.All(texture => !texture.IsStorage))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            EnqueueGuestWorkLocked(
                new VulkanComputeGuestDispatch(
                    shaderAddress,
                    computeSpirv,
                    textures.ToArray(),
                    globalMemoryBuffers.ToArray(),
                    groupCountX,
                    groupCountY,
                    groupCountZ));
        }
    }

    public static bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        var traceSubmission = false;
        lock (_gate)
        {
            // VideoOut registration does not imply a rendered Vulkan image.
            var known = _gpuGuestImages.ContainsKey(address);
            if (ShouldTraceGuestImageSubmissionsForDiagnostics())
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.submit_call kind=TrySubmitGuestImage addr=0x{address:X16} " +
                    $"{width}x{height} known={known}");
            }

            if (_closed)
            {
                return false;
            }

            // The caller (VideoOutExports.SubmitFlip) reports the flip as successful either
            // way, so an unregistered address means the frame is dropped silently; warn once
            // per address so that shows up in the log.
            if (!known)
            {
                if (_tracedGuestImageSubmissions.Add((address, width, height)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.submit_guest_image_unknown addr=0x{address:X16} " +
                        $"{width}x{height} - flip target has no completed GPU render output");
                }

                return false;
            }

            traceSubmission =
                _tracedGuestImageSubmissions.Add((address, width, height));
            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            _latestPresentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                // A flip targets the image produced by all work already queued
                // for this frame.  Do not expose it until those draws finish.
                RequiredGuestWorkSequence: _enqueuedGuestWorkSequence,
                IsSplash: false,
                GuestImageAddress: address);
            System.Threading.Monitor.PulseAll(_gate);
            if (_thread is not null)
            {
                return true;
            }

            _windowWidth = width;
            _windowHeight = height;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "SharpEmu Vulkan VideoOut",
            };
            _thread.Start();
        }

        if (traceSubmission)
        {
            var effectivePitch = pitchInPixel == 0 ? width : pitchInPixel;
            Console.Error.WriteLine(
                $"[LOADER][TRACE] vk.submit_guest_image addr=0x{address:X16} " +
                $"size={width}x{height} pitch={effectivePitch}");
        }

        return true;
    }

    // Display buffers registered through sceVideoOutRegisterBuffers are valid flip targets
    // even when no AGC render-target write to them was ever observed.
    internal static void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        if (address == 0 || guestFormat == 0)
        {
            return;
        }

        lock (_gate)
        {
            _availableGuestImages[address] = guestFormat;
        }
    }

    internal static bool IsGpuGuestImageAvailable(
        ulong address,
        uint format,
        uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _gpuGuestImages.TryGetValue(address, out var availableFormat) &&
                availableFormat == guestFormat;
        }
    }

    public static bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat)
    {
        if (sourceAddress == 0 ||
            destinationAddress == 0 ||
            sourceWidth == 0 ||
            sourceHeight == 0 ||
            destinationWidth == 0 ||
            destinationHeight == 0 ||
            !TryGetCopyFragmentShader(out var fragmentSpirv))
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed ||
                !_availableGuestImages.ContainsKey(sourceAddress) ||
                GetGuestTextureFormat(destinationFormat, 0) == 0)
            {
                return false;
            }
        }

        SubmitOffscreenTranslatedDraw(
            fragmentSpirv,
            [
                new GuestDrawTexture(
                    sourceAddress,
                    sourceWidth,
                    sourceHeight,
                    sourceFormat,
                    NumberType: 0,
                    [],
                    IsFallback: false,
                    IsStorage: false),
            ],
            [],
            attributeCount: 1,
            new GuestRenderTarget(
                destinationAddress,
                destinationWidth,
                destinationHeight,
                destinationFormat,
                NumberType: 0));
        return true;
    }

    private static bool TryGetCopyFragmentShader(out byte[] spirv)
    {
        lock (_gate)
        {
            if (_copyFragmentSpirv is not null)
            {
                spirv = _copyFragmentSpirv;
                return true;
            }
        }

        spirv = SpirvFixedShaders.CreateCopyFragment();

        lock (_gate)
        {
            _copyFragmentSpirv ??= spirv;
            spirv = _copyFragmentSpirv;
        }

        return true;
    }

    private static uint GetGuestTextureFormat(uint format, uint numberType)
    {
        if (!TryDecodeRenderTargetFormat(format, numberType, out var decoded))
        {
            return 0;
        }

        return decoded.Format switch
        {
            Format.R8Unorm => 36,
            Format.R8Uint => 49,
            Format.R8G8Unorm => 3,
            Format.A2R10G10B10UnormPack32 => 9,
            Format.B10G11R11UfloatPack32 => 7,
            Format.R32Uint => GuestFormatR32Uint,
            Format.R32Sint => GuestFormatR32Sint,
            Format.R32Sfloat => GuestFormatR32Sfloat,
            Format.R16G16Unorm => 5,
            Format.R16G16Uint => GuestFormatR16G16Uint,
            Format.R16G16Sint => GuestFormatR16G16Sint,
            Format.R16G16Sfloat => GuestFormatR16G16Sfloat,
            Format.R32G32Sfloat => 75,
            Format.R8G8B8A8Unorm => 56,
            Format.R8G8B8A8Uint => GuestFormatR8G8B8A8Uint,
            Format.R8G8B8A8Sint => GuestFormatR8G8B8A8Sint,
            Format.R16G16B16A16Unorm => 12,
            Format.R16G16B16A16Uint => GuestFormatR16G16B16A16Uint,
            Format.R16G16B16A16Sint => GuestFormatR16G16B16A16Sint,
            Format.R16G16B16A16Sfloat => 71,
            Format.R32G32B32A32Sfloat => 14,
            _ => 0,
        };
    }

    internal static bool TryDecodeRenderTargetFormat(
        uint dataFormat,
        uint numberType,
        out VulkanRenderTargetFormat result)
    {
        var format = (dataFormat, numberType) switch
        {
            (4, 4) => Format.R32Uint,
            (4, 5) => Format.R32Sint,
            (4, 7) => Format.R32Sfloat,
            (5, 4) => Format.R16G16Uint,
            (5, 5) => Format.R16G16Sint,
            (5, 7) => Format.R16G16Sfloat,
            (9, _) => Format.A2R10G10B10UnormPack32,
            (10, 4) => Format.R8G8B8A8Uint,
            (10, 5) => Format.R8G8B8A8Sint,
            (10, _) => Format.R8G8B8A8Unorm,
            (12, 4) => Format.R16G16B16A16Uint,
            (12, 5) => Format.R16G16B16A16Sint,
            (12, 7) => Format.R16G16B16A16Sfloat,
            (GuestFormatR32Uint, _) or (20, 0) => Format.R32Uint,
            (GuestFormatR32Sint, _) => Format.R32Sint,
            (GuestFormatR32Sfloat, _) or (29, 0) or (4, 0) => Format.R32Sfloat,
            (GuestFormatR16G16Uint, _) => Format.R16G16Uint,
            (GuestFormatR16G16Sint, _) => Format.R16G16Sint,
            (GuestFormatR16G16Sfloat, _) => Format.R16G16Sfloat,
            (GuestFormatR8G8B8A8Uint, _) => Format.R8G8B8A8Uint,
            (GuestFormatR8G8B8A8Sint, _) => Format.R8G8B8A8Sint,
            (GuestFormatR16G16B16A16Uint, _) => Format.R16G16B16A16Uint,
            (GuestFormatR16G16B16A16Sint, _) => Format.R16G16B16A16Sint,
            (1, 0) or (36, 0) => Format.R8Unorm,
            (49, 0) => Format.R8Uint,
            (3, 0) => Format.R8G8Unorm,
            (5, 0) => Format.R16G16Unorm,
            (7, 0) => Format.B10G11R11UfloatPack32,
            (12, 0) => Format.R16G16B16A16Unorm,
            (13, 0) or (14, 0) => Format.R32G32B32A32Sfloat,
            (22, 0) or (71, 0) => Format.R16G16B16A16Sfloat,
            (56, 0) or (62, 0) or (64, 0) => Format.R8G8B8A8Unorm,
            (75, 0) => Format.R32G32Sfloat,
            _ => Format.Undefined,
        };

        if (format == Format.Undefined)
        {
            result = default;
            return false;
        }

        var outputKind = format switch
        {
            Format.R8Uint or Format.R32Uint or Format.R16G16Uint or
                Format.R8G8B8A8Uint or Format.R16G16B16A16Uint => Gen5PixelOutputKind.Uint,
            Format.R32Sint or Format.R16G16Sint or Format.R8G8B8A8Sint or
                Format.R16G16B16A16Sint => Gen5PixelOutputKind.Sint,
            _ => Gen5PixelOutputKind.Float,
        };
        result = new VulkanRenderTargetFormat(format, outputKind);
        return true;
    }

    private static bool IsKnownGuestTextureFormat(uint format) =>
        format is 4 or 5 or 7 or 9 or 13 or 14 or 22 or 29 or 36 or 56 or 62 or 64 or 71;

    private static byte[] CreateBlackFrame(uint width, uint height)
    {
        if (width == 0 || height == 0 || width > 8192 || height > 8192)
        {
            width = 1;
            height = 1;
        }

        var pixels = GC.AllocateUninitializedArray<byte>(checked((int)(width * height * 4)));
        pixels.AsSpan().Clear();
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 0xFF;
        }

        return pixels;
    }

    private static void StartPresenterLocked()
    {
        if (HostMainThread.IsAvailable)
        {
            // GLFW windowing must run on the process main thread (AppKit on
            // macOS, X11's single event queue on Linux), so hand the whole
            // window loop to the main-thread pump the CLI parked for us.
            // _thread only marks the presenter as running; Run() clears it on
            // exit either way.
            _thread = Thread.CurrentThread;
            HostMainThread.SetShutdownRequestHandler(RequestClose);
            HostMainThread.Post(Run);
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu Vulkan VideoOut",
        };
        _thread.Start();
    }

    /// <summary>
    /// Asks a running presenter to close its window; used at emulator
    /// shutdown so a main-thread-hosted window loop returns to the pump.
    /// </summary>
    public static void RequestClose()
    {
        Volatile.Write(ref _presenterCloseRequested, true);
    }

    /// <summary>
    /// GLFW resolves Vulkan with dlopen("libvulkan.1.dylib"), which cannot
    /// find the app-local MoltenVK on macOS (Homebrew's Vulkan libraries are
    /// arm64-only and this is an x86-64 process). GLFW 3.4 accepts the
    /// loader entry point directly instead, so hand it MoltenVK's
    /// vkGetInstanceProcAddr before any window exists.
    /// </summary>
    private static unsafe void InitializeMacVulkanLoader()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            nint vulkan = 0;
            foreach (var candidate in new[]
            {
                Path.Combine(AppContext.BaseDirectory, "libvulkan.1.dylib"),
                Path.Combine(AppContext.BaseDirectory, "libMoltenVK.dylib"),
                "libvulkan.1.dylib",
                "libMoltenVK.dylib",
            })
            {
                if (System.Runtime.InteropServices.NativeLibrary.TryLoad(candidate, out vulkan))
                {
                    break;
                }
            }

            if (vulkan == 0 ||
                !System.Runtime.InteropServices.NativeLibrary.TryGetExport(
                    vulkan, "vkGetInstanceProcAddr", out var procAddr))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] No Vulkan loader for GLFW; place a universal libMoltenVK.dylib " +
                    "next to SharpEmu as libvulkan.1.dylib.");
                return;
            }

            var glfw = System.Runtime.InteropServices.NativeLibrary.Load(
                Path.Combine(AppContext.BaseDirectory, "libglfw.3.dylib"));
            var initVulkanLoader = (delegate* unmanaged<nint, void>)
                System.Runtime.InteropServices.NativeLibrary.GetExport(glfw, "glfwInitVulkanLoader");
            initVulkanLoader(procAddr);
            Console.Error.WriteLine("[LOADER][INFO] GLFW Vulkan loader wired to MoltenVK.");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] GLFW Vulkan loader setup failed: {exception.Message}");
        }
    }

    // GLFW platform enum (GLFW 3.4): glfwInitHint(GLFW_PLATFORM, ...) selects a
    // backend, glfwGetPlatform() reports the one in use.
    private const int GlfwPlatformHint = 0x00050003;
    private const int GlfwPlatformWin32 = 0x00060001;
    private const int GlfwPlatformCocoa = 0x00060002;
    private const int GlfwPlatformWayland = 0x00060003;
    private const int GlfwPlatformX11 = 0x00060004;
    private const int GlfwPlatformNull = 0x00060005;

    /// <summary>
    /// GLFW's native Wayland backend does not reliably map the Vulkan window
    /// with some drivers (notably NVIDIA): the surface presents frames but the
    /// window never becomes visible, so the game runs with no picture while
    /// audio works. XWayland is dependable, so on a Wayland session that also
    /// exposes an X server (DISPLAY set) we force GLFW's X11 backend through
    /// its GLFW_PLATFORM init hint before GLFW initializes — the supported way
    /// to pick a backend, applied by calling into the same libglfw GLFW loads.
    /// Opt back into native Wayland with SHARPEMU_ENABLE_WAYLAND=1.
    /// </summary>
    private static unsafe void PreferX11OnLinuxWayland()
    {
        if (!OperatingSystem.IsLinux() ||
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_ENABLE_WAYLAND"),
                "1",
                StringComparison.Ordinal))
        {
            return;
        }

        // Only steer on a Wayland session (WAYLAND_DISPLAY set). Forcing X11
        // needs an X server to fall back to (XWayland, DISPLAY set); without
        // one, forcing it would make glfwInit fail outright, so leave GLFW
        // alone and say why the window may not appear.
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
        {
            return;
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Wayland session without an X server (DISPLAY unset); " +
                "cannot steer GLFW to XWayland. If the window does not appear, install " +
                "XWayland, or run natively with SHARPEMU_ENABLE_WAYLAND=1.");
            return;
        }

        if (!TryLoadGlfw(out var glfw))
        {
            return;
        }

        try
        {
            var initHint = (delegate* unmanaged<int, int, void>)
                System.Runtime.InteropServices.NativeLibrary.GetExport(glfw, "glfwInitHint");
            initHint(GlfwPlatformHint, GlfwPlatformX11);
            Console.Error.WriteLine(
                "[LOADER][INFO] Wayland session detected; requested GLFW X11/XWayland " +
                "backend (set SHARPEMU_ENABLE_WAYLAND=1 to force native Wayland).");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"[LOADER][WARN] Could not set GLFW X11 platform hint: {exception.Message}");
        }
    }

    /// <summary>Logs the backend GLFW actually selected, so a "no window"
    /// report shows Wayland vs X11 at a glance.</summary>
    private static unsafe void LogGlfwPlatformInUse()
    {
        if (OperatingSystem.IsWindows() || !TryLoadGlfw(out var glfw))
        {
            return;
        }

        try
        {
            var getPlatform = (delegate* unmanaged<int>)
                System.Runtime.InteropServices.NativeLibrary.GetExport(glfw, "glfwGetPlatform");
            var platform = getPlatform();
            var label = platform switch
            {
                GlfwPlatformWin32 => "Win32",
                GlfwPlatformCocoa => "Cocoa",
                GlfwPlatformWayland => "Wayland",
                GlfwPlatformX11 => "X11",
                GlfwPlatformNull => "Null",
                _ => $"0x{platform:X}",
            };
            Console.Error.WriteLine($"[LOADER][INFO] GLFW windowing platform in use: {label}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Could not query GLFW platform: {exception.Message}");
        }
    }

    private static bool TryLoadGlfw(out nint handle)
    {
        var name = OperatingSystem.IsMacOS() ? "libglfw.3.dylib" : "libglfw.so.3";
        return System.Runtime.InteropServices.NativeLibrary.TryLoad(
            Path.Combine(AppContext.BaseDirectory, name), out handle) ||
            System.Runtime.InteropServices.NativeLibrary.TryLoad(name, out handle);
    }

    private static void Run()
    {
        uint width;
        uint height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? _latestPresentation?.Width ?? 1280 : _windowWidth;
            height = _windowHeight == 0 ? _latestPresentation?.Height ?? 720 : _windowHeight;
        }

        InitializeMacVulkanLoader();
        PreferX11OnLinuxWayland();

        try
        {
            using var presenter = new Presenter(width, height);
            presenter.Run();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Vulkan VideoOut presenter failed: {exception}");
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                _thread = null;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }
    }

    private static bool TryTakePresentation(long presentedSequence, out Presentation presentation)
    {
        lock (_gate)
        {
            if (_latestPresentation is not { } latest ||
                latest.Sequence == presentedSequence ||
                latest.RequiredGuestWorkSequence > _completedGuestWorkSequence)
            {
                presentation = default;
                return false;
            }

            presentation = latest;
            return true;
        }
    }

    private static void EnqueueGuestWorkLocked(object work)
    {
        while (!_closed &&
               _thread is not null &&
               _pendingGuestWork.Count >= MaxPendingGuestWork)
        {
            System.Threading.Monitor.Wait(_gate);
        }

        if (_closed)
        {
            return;
        }

        _pendingGuestWork.Enqueue(work);
        _enqueuedGuestWorkSequence++;
        System.Threading.Monitor.PulseAll(_gate);
    }

    private static bool TryTakeGuestWork(out object work)
    {
        lock (_gate)
        {
            return _pendingGuestWork.TryDequeue(out work!);
        }
    }

    private static void CompleteGuestWork()
    {
        lock (_gate)
        {
            _completedGuestWorkSequence++;
            System.Threading.Monitor.PulseAll(_gate);
        }
    }

    private readonly record struct Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        GuestDrawKind DrawKind,
        VulkanTranslatedGuestDraw? TranslatedDraw,
        long RequiredGuestWorkSequence,
        bool IsSplash,
        ulong GuestImageAddress = 0);

    private sealed class Presenter : IDisposable
    {
        private const string FullscreenBarycentricVertexSpirv =
            "AwIjBwAAAQALAAgAMgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ACAAAAAAABAAAAG1haW4AAAAADQAAABoAAAApAAAAAwADAAIAAADCAQAABQAEAAQAAABtYWluAAAAAAUABgALAAAAZ2xfUGVyVmVydGV4AAAAAAYABgALAAAAAAAAAGdsX1Bvc2l0aW9uAAYABwALAAAAAQAAAGdsX1BvaW50U2l6ZQAAAAAGAAcACwAAAAIAAABnbF9DbGlwRGlzdGFuY2UABgAHAAsAAAADAAAAZ2xfQ3VsbERpc3RhbmNlAAUAAwANAAAAAAAAAAUABgAaAAAAZ2xfVmVydGV4SW5kZXgAAAUABQAdAAAAaW5kZXhhYmxlAAAABQAFACkAAABiYXJ5Y2VudHJpYwAFAAUALwAAAGluZGV4YWJsZQAAAEcAAwALAAAAAgAAAEgABQALAAAAAAAAAAsAAAAAAAAASAAFAAsAAAABAAAACwAAAAEAAABIAAUACwAAAAIAAAALAAAAAwAAAEgABQALAAAAAwAAAAsAAAAEAAAARwAEABoAAAALAAAAKgAAAEcABAApAAAAHgAAAAAAAAATAAIAAgAAACEAAwADAAAAAgAAABYAAwAGAAAAIAAAABcABAAHAAAABgAAAAQAAAAVAAQACAAAACAAAAAAAAAAKwAEAAgAAAAJAAAAAQAAABwABAAKAAAABgAAAAkAAAAeAAYACwAAAAcAAAAGAAAACgAAAAoAAAAgAAQADAAAAAMAAAALAAAAOwAEAAwAAAANAAAAAwAAABUABAAOAAAAIAAAAAEAAAArAAQADgAAAA8AAAAAAAAAFwAEABAAAAAGAAAAAgAAACsABAAIAAAAEQAAAAMAAAAcAAQAEgAAABAAAAARAAAAKwAEAAYAAAATAAAAAACAvywABQAQAAAAFAAAABMAAAATAAAAKwAEAAYAAAAVAAAAAABAQCwABQAQAAAAFgAAABUAAAATAAAALAAFABAAAAAXAAAAEwAAABUAAAAsAAYAEgAAABgAAAAUAAAAFgAAABcAAAAgAAQAGQAAAAEAAAAOAAAAOwAEABkAAAAaAAAAAQAAACAABAAcAAAABwAAABIAAAAgAAQAHgAAAAcAAAAQAAAAKwAEAAYAAAAhAAAAAAAAACsABAAGAAAAIgAAAAAAgD8gAAQAJgAAAAMAAAAHAAAAIAAEACgAAAADAAAAEAAAADsABAAoAAAAKQAAAAMAAAAsAAUAEAAAACoAAAAiAAAAIQAAACwABQAQAAAAKwAAACEAAAAiAAAALAAFABAAAAAsAAAAIQAAACEAAAAsAAYAEgAAAC0AAAAqAAAAKwAAACwAAAA2AAUAAgAAAAQAAAAAAAAAAwAAAPgAAgAFAAAAOwAEABwAAAAdAAAABwAAADsABAAcAAAALwAAAAcAAAA9AAQADgAAABsAAAAaAAAAPgADAB0AAAAYAAAAQQAFAB4AAAAfAAAAHQAAABsAAAA9AAQAEAAAACAAAAAfAAAAUQAFAAYAAAAjAAAAIAAAAAAAAABRAAUABgAAACQAAAAgAAAAAQAAAFAABwAHAAAAJQAAACMAAAAkAAAAIQAAACIAAABBAAUAJgAAACcAAAANAAAADwAAAD4AAwAnAAAAJQAAAD0ABAAOAAAALgAAABoAAAA+AAMALwAAAC0AAABBAAUAHgAAADAAAAAvAAAALgAAAD0ABAAQAAAAMQAAADAAAAA+AAMAKQAAADEAAAD9AAEAOAABAA==";

        private const string FullscreenBarycentricFragmentSpirv =
            "AwIjBwAAAQALAAgAEgAAAAAAAAARAAIAAQAAAAsABgABAAAAR0xTTC5zdGQuNDUwAAAAAA4AAwAAAAAAAQAAAA8ABwAEAAAABAAAAG1haW4AAAAACQAAAAwAAAAQAAMABAAAAAcAAAADAAMAAgAAAMIBAAAFAAQABAAAAG1haW4AAAAABQAFAAkAAABvdXRDb2xvcgAAAAAFAAUADAAAAGJhcnljZW50cmljAEcABAAJAAAAHgAAAAAAAABHAAQADAAAAB4AAAAAAAAAEwACAAIAAAAhAAMAAwAAAAIAAAAWAAMABgAAACAAAAAXAAQABwAAAAYAAAAEAAAAIAAEAAgAAAADAAAABwAAADsABAAIAAAACQAAAAMAAAAXAAQACgAAAAYAAAACAAAAIAAEAAsAAAABAAAACgAAADsABAALAAAADAAAAAEAAAArAAQABgAAAA4AAAAAAAAANgAFAAIAAAAEAAAAAAAAAAMAAAD4AAIABQAAAD0ABAAKAAAADQAAAAwAAABRAAUABgAAAA8AAAANAAAAAAAAAFEABQAGAAAAEAAAAA0AAAABAAAAUAAHAAcAAAARAAAADwAAABAAAAAOAAAADgAAAD4AAwAJAAAAEQAAAP0AAQA4AAEA";

        private readonly IWindow _window;
        private const int MaxInFlightGuestSubmissions = 8;
        private const double PerformanceHudSampleSeconds = 0.5;
        private const uint ThreadQueryLimitedInformation = 0x0800;
        private static readonly bool _performanceHudEnabled =
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_PERF_HUD"),
                "0",
                StringComparison.Ordinal);
        private Vk _vk = null!;
        private KhrSurface _surfaceApi = null!;
        private KhrSwapchain _swapchainApi = null!;
        private delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result> _setDebugUtilsObjectName;
        private delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void> _cmdBeginDebugUtilsLabel;
        private delegate* unmanaged<CommandBuffer, void> _cmdEndDebugUtilsLabel;
        private Instance _instance;
        private SurfaceKHR _surface;
        private DebugUtilsMessengerEXT _debugMessenger;
        private ExtDebugUtils? _debugUtils;
        private PhysicalDevice _physicalDevice;
        private bool _supportsIndependentBlend;
        private uint _maxColorAttachments;
        private Device _device;
        private PipelineCache _pipelineCache;
        private string? _pipelineCachePath;
        private Queue _queue;
        private uint _queueFamilyIndex;
        private SwapchainKHR _swapchain;
        private Image[] _swapchainImages = [];
        private ImageView[] _swapchainImageViews = [];
        private Framebuffer[] _framebuffers = [];
        private bool[] _imageInitialized = [];
        private Format _swapchainFormat;
        private Extent2D _extent;
        private RenderPass _renderPass;
        private PipelineLayout _pipelineLayout;
        private Pipeline _barycentricPipeline;
        private CommandPool _commandPool;
        private CommandBuffer _commandBuffer;
        private CommandBuffer _presentationCommandBuffer;
        private VkSemaphore _imageAvailable;
        private VkSemaphore _renderFinished;
        private Fence _presentationFence;
        private bool _presentationInFlight;
        private TranslatedDrawResources? _pendingPresentationResources;
        private VkBuffer _stagingBuffer;
        private DeviceMemory _stagingMemory;
        private ulong _stagingSize;
        private long _presentedSequence;
        private long _performanceHudLastTimestamp;
        private TimeSpan _performanceHudLastProcessCpu;
        private long _performanceHudPresentedFrames;
        private long _performanceHudLastPresentedFrames;
        private long _performanceHudLastReadCount;
        private long _performanceHudLastReadBytes;
        private long _performanceHudLastReadHits;
        private long _performanceHudLastReadPvmBytes;
        private long _performanceHudLastReadLibcBytes;
        private readonly Dictionary<int, TimeSpan> _performanceHudThreadCpu = [];
        private readonly Dictionary<int, string> _performanceHudThreadNames = [];
        private bool _vulkanReady;
        private bool _firstFramePresented;
        private bool _firstGuestDrawPresented;
        private bool _splashPresented;
        private bool _swapchainRecreateDeferred;
        private bool _tracedPresentedSwapchain;
        private bool _swapchainReadbackPending;
        private static int _guestImageDumpSequence;
        private readonly System.Collections.Concurrent.ConcurrentQueue<GuestImageResource> _pendingAliasImageDumps = new();
        private bool _deviceLost;
        private bool _deviceLostLogged;
        private int _directPresentationCount;
        private readonly Dictionary<ulong, GuestImageResource> _guestImages = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureCacheHits = new();
        private static readonly bool _dumpTextures = string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_DUMP_TEXTURES"),
            "1",
            StringComparison.Ordinal);
        private readonly HashSet<(ulong Address, uint Width, uint Height, Format Format)> _tracedTextureUploads = new();
        private readonly HashSet<(ulong Address, uint Width, uint Height, uint Format)> _dumpedTextures = new();
        private readonly HashSet<(ulong Address, int Size)> _tracedGlobalBuffers = new();
        private readonly HashSet<ulong> _tracedGuestImageContents = new();
        private readonly Dictionary<ulong, int> _tracedGuestWriteCounts = new();
        private int _tracedVertexBufferCount;
        private readonly Dictionary<byte[], Pipeline> _computePipelines =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<GraphicsPipelineKey, Pipeline> _graphicsPipelines = new();
        private readonly Dictionary<GuestSampler, Sampler> _samplers = new();
        private readonly Dictionary<byte[], string> _shaderDigests =
            new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<DescriptorLayoutKey, DescriptorLayoutBundle>
            _descriptorLayouts = new();
        private readonly Dictionary<HostBufferPoolKey, Stack<HostBufferAllocation>>
            _hostBufferPool = new();
        private readonly Dictionary<ulong, HostBufferAllocation> _hostBufferAllocations = new();
        private readonly Queue<PendingGuestSubmission> _pendingGuestSubmissions = new();

        private readonly record struct GraphicsPipelineKey(
            string VertexShader,
            string FragmentShader,
            string RenderTargetLayout,
            PrimitiveTopology Topology,
            string BlendLayout,
            string ResourceLayout,
            string VertexLayout);

        private readonly record struct HostBufferPoolKey(
            BufferUsageFlags Usage,
            ulong Capacity);

        private readonly record struct DescriptorLayoutKey(
            ShaderStageFlags Stages,
            string Resources);

        private sealed record DescriptorLayoutBundle(
            DescriptorSetLayout DescriptorSetLayout,
            PipelineLayout PipelineLayout);

        private sealed record HostBufferAllocation(
            VkBuffer Buffer,
            DeviceMemory Memory,
            HostBufferPoolKey Key);

        private sealed class TranslatedDrawResources
        {
            public string DebugName = "SharpEmu translated";
            public PipelineLayout PipelineLayout;
            public Pipeline Pipeline;
            public bool PipelineCached;
            public bool DescriptorLayoutCached;
            public DescriptorSetLayout DescriptorSetLayout;
            public DescriptorPool DescriptorPool;
            public DescriptorSet DescriptorSet;
            public TextureResource[] Textures = [];
            public GlobalBufferResource[] GlobalMemoryBuffers = [];
            public VertexBufferResource[] VertexBuffers = [];
            public VkBuffer IndexBuffer;
            public DeviceMemory IndexMemory;
            public bool Index32Bit;
            public uint VertexCount = 3;
            public uint InstanceCount = 1;
            public PrimitiveTopology Topology = PrimitiveTopology.TriangleList;
            public GuestBlendState[] Blends = [GuestBlendState.Default];
            public GuestRect? Scissor;
            public GuestViewport? Viewport;
            public RenderPass TransientRenderPass;
            public Framebuffer TransientFramebuffer;
        }

        private sealed class TextureResource
        {
            public ulong Address;
            public VkBuffer StagingBuffer;
            public DeviceMemory StagingMemory;
            public Image Image;
            public DeviceMemory ImageMemory;
            public ImageView View;
            public uint Width;
            public uint Height;
            public uint RowLength;
            public uint DstSelect;
            public bool NeedsUpload;
            public bool OwnsStorage;
            public bool IsStorage;
            public GuestSampler SamplerState;
            public Sampler Sampler;
            public GuestImageResource? GuestImage;
            public ulong CpuContentFingerprint;
            public bool UpdatesCpuContent;
        }

        private sealed class GlobalBufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public ulong Size;
        }

        private sealed class VertexBufferResource
        {
            public VkBuffer Buffer;
            public DeviceMemory Memory;
            public ulong Size;
            public uint Location;
            public uint ComponentCount;
            public uint DataFormat;
            public uint NumberFormat;
            public uint Stride;
            public uint OffsetBytes;
        }

        private sealed class GuestImageResource
        {
            public ulong Address;
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public Format Format;
            public Image Image;
            public DeviceMemory Memory;
            public ImageView View;
            public ImageView[] MipViews = [];
            public Dictionary<(Format Format, uint MipLevel, uint LevelCount, uint DstSelect), ImageView> FormatViews { get; } = new();
            public RenderPass RenderPass;
            public Framebuffer Framebuffer;
            public bool Initialized;
            public bool InitialUploadPending;
            public bool IsCpuBacked;
            public ulong CpuContentFingerprint;
        }

        private sealed record PendingGuestSubmission(
            Fence Fence,
            CommandBuffer CommandBuffer,
            TranslatedDrawResources Resources,
            IReadOnlyList<GuestImageResource> TraceImages,
            string DebugName);

        public Presenter(uint width, uint height)
        {
            var options = WindowOptions.DefaultVulkan;
            options.Size = new Vector2D<int>((int)DefaultWindowWidth, (int)DefaultWindowHeight);
            options.Title = VideoOutExports.GetWindowTitle();
            options.WindowBorder = WindowBorder.Fixed;
            // FIFO already provides the presentation clock. Throttling Silk's render loop
            // as well can miss alternating vblanks and collapse delivery to 30 FPS or less.
            options.VSync = false;
            options.FramesPerSecond = 0;
            options.UpdatesPerSecond = 0;
            _window = Window.Create(options);
            _window.Load += Initialize;
            _window.Render += Render;
            _window.Closing += OnWindowClosing;
        }

        private void OnWindowClosing()
        {
            VideoOutExports.NotifyPresentationWindowClosed();
            DisposeVulkan();
        }

        public void Run() => _window.Run();

        public void Dispose()
        {
            DisposeVulkan();
            try
            {
                _window.Dispose();
            }
            catch (InvalidOperationException exception)
                when (exception.Message.Contains("render loop", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan VideoOut window dispose skipped during render loop: {exception.Message}");
            }
        }

        private void Initialize()
        {
            if (PngSplashLoader.TryLoadIcon(out var iconPixels, out var iconWidth, out var iconHeight))
            {
                var icon = new RawImage((int)iconWidth, (int)iconHeight, iconPixels);
                _window.SetWindowIcon(ref icon);
            }

            LogGlfwPlatformInUse();
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    Pad.HostWindowInput.Attach(_window.CreateInput());
                    Console.Error.WriteLine("[LOADER][INFO] Window keyboard input attached for pad emulation.");
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Window keyboard input unavailable: {exception.Message}");
                }
            }

            WaitForRenderDocAttachIfRequested();
            _vk = Vk.GetApi();
            CreateInstance();
            CreateSurface();
            SelectPhysicalDevice();
            CreateDevice();
            CreatePipelineCache();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            _vulkanReady = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut ready: {_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private static void WaitForRenderDocAttachIfRequested()
        {
            var value = Environment.GetEnvironmentVariable("SHARPEMU_RENDERDOC_WAIT");
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            if (string.Equals(value, "enter", StringComparison.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Waiting for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}. Press Enter to continue.");
                _ = Console.ReadLine();
                return;
            }

            var seconds = 15;
            if (int.TryParse(value, out var parsedSeconds))
            {
                seconds = Math.Clamp(parsedSeconds, 1, 300);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Waiting {seconds}s for RenderDoc attach before Vulkan init. pid={Environment.ProcessId}");
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
        }

        private bool IsInstanceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateInstanceExtensionProperties((byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceExtensionProperties(
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool Utf8NullTerminatedEquals(byte* actual, ReadOnlySpan<byte> expected)
        {
            for (var index = 0; index < expected.Length; index++)
            {
                if (actual[index] != expected[index])
                {
                    return false;
                }
            }

            return actual[expected.Length] == 0;
        }

        private void LoadDebugUtilsCommands()
        {
            var setObjectName = _vk.GetDeviceProcAddr(_device, "vkSetDebugUtilsObjectNameEXT");
            var beginLabel = _vk.GetDeviceProcAddr(_device, "vkCmdBeginDebugUtilsLabelEXT");
            var endLabel = _vk.GetDeviceProcAddr(_device, "vkCmdEndDebugUtilsLabelEXT");
            _setDebugUtilsObjectName =
                (delegate* unmanaged<Device, DebugUtilsObjectNameInfoEXT*, Result>)
                setObjectName.Handle;
            _cmdBeginDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, DebugUtilsLabelEXT*, void>)
                beginLabel.Handle;
            _cmdEndDebugUtilsLabel =
                (delegate* unmanaged<CommandBuffer, void>)
                endLabel.Handle;

            if (_setDebugUtilsObjectName is not null)
            {
                Console.Error.WriteLine("[LOADER][INFO] Vulkan debug labels enabled.");
            }
        }

        private void SetDebugName(ObjectType objectType, ulong objectHandle, string name)
        {
            if (_setDebugUtilsObjectName is null ||
                _device.Handle == 0 ||
                objectHandle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var info = new DebugUtilsObjectNameInfoEXT
                {
                    SType = StructureType.DebugUtilsObjectNameInfoExt,
                    ObjectType = objectType,
                    ObjectHandle = objectHandle,
                    PObjectName = namePointer,
                };
                _ = _setDebugUtilsObjectName(_device, &info);
            }
        }

        private void BeginDebugLabel(CommandBuffer commandBuffer, string name)
        {
            if (_cmdBeginDebugUtilsLabel is null ||
                commandBuffer.Handle == 0)
            {
                return;
            }

            var bytes = NullTerminatedUtf8(name);
            fixed (byte* namePointer = bytes)
            {
                var label = new DebugUtilsLabelEXT
                {
                    SType = StructureType.DebugUtilsLabelExt,
                    PLabelName = namePointer,
                };
                label.Color[0] = 0.20f;
                label.Color[1] = 0.60f;
                label.Color[2] = 1.00f;
                label.Color[3] = 1.00f;
                _cmdBeginDebugUtilsLabel(commandBuffer, &label);
            }
        }

        private void EndDebugLabel(CommandBuffer commandBuffer)
        {
            if (_cmdEndDebugUtilsLabel is not null &&
                commandBuffer.Handle != 0)
            {
                _cmdEndDebugUtilsLabel(commandBuffer);
            }
        }

        private static byte[] NullTerminatedUtf8(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            Array.Resize(ref bytes, bytes.Length + 1);
            return bytes;
        }

        private static string BuildComputeDebugName(VulkanComputeGuestDispatch dispatch)
        {
            var storage = dispatch.Textures.FirstOrDefault(texture => texture.IsStorage && texture.Address != 0);
            return storage is null
                ? $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}"
                : $"SharpEmu compute cs=0x{dispatch.ShaderAddress:X16} " +
                  $"storage=0x{storage.Address:X16} " +
                  $"{storage.Width}x{storage.Height} fmt{storage.Format} " +
                  $"{dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ}";
        }

        private static string GuestImageDebugName(GuestRenderTarget target, Format format) =>
            $"SharpEmu guest 0x{target.Address:X16} {target.Width}x{target.Height} " +
            $"fmt{target.Format}/{format}";

        private static string TextureDebugName(GuestDrawTexture texture, Format format) =>
            $"SharpEmu texture 0x{texture.Address:X16} {texture.Width}x{texture.Height} " +
            $"fmt{texture.Format}/{format}";

        private void CreateInstance()
        {
            var applicationName = (byte*)SilkMarshal.StringToPtr("SharpEmu");
            var enableValidation = Environment.GetEnvironmentVariable("SHARPEMU_VK_VALIDATION") == "1";
            byte* validationLayerName = null;

            try
            {
                var applicationInfo = new ApplicationInfo
                {
                    SType = StructureType.ApplicationInfo,
                    PApplicationName = applicationName,
                    ApplicationVersion = Vk.MakeVersion(0, 0, 1),
                    PEngineName = applicationName,
                    EngineVersion = Vk.MakeVersion(0, 0, 1),
                    ApiVersion = Vk.Version12,
                };

                var extensions = _window.VkSurface!.GetRequiredExtensions(out var extensionCount);
                byte* debugUtilsExtension = null;
                byte* portabilityExtension = null;
                var instanceCreateFlags = InstanceCreateFlags.None;
                var enabledExtensionCount = (int)extensionCount;
                var enabledExtensions = stackalloc byte*[(int)extensionCount + 2];
                for (var index = 0; index < (int)extensionCount; index++)
                {
                    enabledExtensions[index] = extensions[index];
                }

                if (IsInstanceExtensionAvailable(DebugUtilsExtensionName))
                {
                    debugUtilsExtension = (byte*)SilkMarshal.StringToPtr(DebugUtilsExtensionName);
                    enabledExtensions[enabledExtensionCount++] = debugUtilsExtension;
                }

                if (IsInstanceExtensionAvailable(PortabilityEnumerationExtensionName))
                {
                    // MoltenVK is a portability (non-conformant) implementation;
                    // without this flag + extension the loader hides it.
                    portabilityExtension = (byte*)SilkMarshal.StringToPtr(PortabilityEnumerationExtensionName);
                    enabledExtensions[enabledExtensionCount++] = portabilityExtension;
                    instanceCreateFlags |= InstanceCreateFlags.EnumeratePortabilityBitKhr;
                }

                if (enableValidation && IsInstanceLayerAvailable("VK_LAYER_KHRONOS_validation"))
                {
                    validationLayerName = (byte*)SilkMarshal.StringToPtr("VK_LAYER_KHRONOS_validation");
                }
                else if (enableValidation)
                {
                    Console.Error.WriteLine("[LOADER][WARN] SHARPEMU_VK_VALIDATION=1 but VK_LAYER_KHRONOS_validation not found (Vulkan SDK installed?).");
                }

                var layers = stackalloc byte*[1];
                if (validationLayerName is not null)
                {
                    layers[0] = validationLayerName;
                }

                var createInfo = new InstanceCreateInfo
                {
                    SType = StructureType.InstanceCreateInfo,
                    Flags = instanceCreateFlags,
                    PApplicationInfo = &applicationInfo,
                    EnabledExtensionCount = (uint)enabledExtensionCount,
                    PpEnabledExtensionNames = enabledExtensions,
                    EnabledLayerCount = validationLayerName is not null ? 1u : 0u,
                    PpEnabledLayerNames = validationLayerName is not null ? layers : null,
                };

                try
                {
                    Check(_vk.CreateInstance(&createInfo, null, out _instance), "vkCreateInstance");
                    if (!_vk.TryGetInstanceExtension(_instance, out _surfaceApi))
                    {
                        throw new InvalidOperationException("VK_KHR_surface is unavailable.");
                    }

                    if (validationLayerName is not null && _vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils))
                    {
                        _debugUtils = debugUtils;
                        RegisterDebugMessenger(debugUtils);
                        Console.Error.WriteLine("[LOADER][INFO] Vulkan Validation Layers active (SHARPEMU_VK_VALIDATION=1).");
                    }
                }
                finally
                {
                    if (debugUtilsExtension is not null)
                    {
                        SilkMarshal.Free((nint)debugUtilsExtension);
                    }
                    if (portabilityExtension is not null)
                    {
                        SilkMarshal.Free((nint)portabilityExtension);
                    }
                }
            }
            finally
            {
                SilkMarshal.Free((nint)applicationName);
                if (validationLayerName is not null)
                {
                    SilkMarshal.Free((nint)validationLayerName);
                }
            }
        }

        private bool IsDeviceExtensionAvailable(string extensionName)
        {
            uint extensionCount = 0;
            if (_vk.EnumerateDeviceExtensionProperties(_physicalDevice, (byte*)null, &extensionCount, null) != Result.Success ||
                extensionCount == 0)
            {
                return false;
            }

            var properties = new ExtensionProperties[extensionCount];
            fixed (ExtensionProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateDeviceExtensionProperties(
                        _physicalDevice,
                        (byte*)null,
                        &extensionCount,
                        propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(extensionName);
                for (var index = 0; index < extensionCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].ExtensionName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private bool IsInstanceLayerAvailable(string layerName)
        {
            uint layerCount = 0;
            if (_vk.EnumerateInstanceLayerProperties(&layerCount, null) != Result.Success || layerCount == 0)
            {
                return false;
            }

            var properties = new LayerProperties[layerCount];
            fixed (LayerProperties* propertyPointer = properties)
            {
                if (_vk.EnumerateInstanceLayerProperties(&layerCount, propertyPointer) != Result.Success)
                {
                    return false;
                }

                var expected = Encoding.UTF8.GetBytes(layerName);
                for (var index = 0; index < layerCount; index++)
                {
                    if (Utf8NullTerminatedEquals(propertyPointer[index].LayerName, expected))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        private void RegisterDebugMessenger(ExtDebugUtils debugUtils)
        {
            var messengerInfo = new DebugUtilsMessengerCreateInfoEXT
            {
                SType = StructureType.DebugUtilsMessengerCreateInfoExt,
                MessageSeverity = DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt
                                  | DebugUtilsMessageSeverityFlagsEXT.WarningBitExt,
                MessageType = DebugUtilsMessageTypeFlagsEXT.ValidationBitExt
                              | DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt
                              | DebugUtilsMessageTypeFlagsEXT.GeneralBitExt,
                PfnUserCallback = new PfnDebugUtilsMessengerCallbackEXT(DebugCallback),
            };

            Check(debugUtils.CreateDebugUtilsMessenger(_instance, &messengerInfo, null, out _debugMessenger),
                "vkCreateDebugUtilsMessengerEXT");
        }

        private static unsafe uint DebugCallback(
            DebugUtilsMessageSeverityFlagsEXT severity,
            DebugUtilsMessageTypeFlagsEXT type,
            DebugUtilsMessengerCallbackDataEXT* callbackData,
            void* userData)
        {
            var message = SilkMarshal.PtrToString((nint)callbackData->PMessage);
            var prefix = severity switch
            {
                DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt => "[VULKAN][ERROR]",
                DebugUtilsMessageSeverityFlagsEXT.WarningBitExt => "[VULKAN][WARN]",
                _ => "[VULKAN][INFO]",
            };
            Console.Error.WriteLine($"{prefix} {message}");
            return Vk.False;
        }
        private void CreateSurface()
        {
            var instanceHandle = new VkHandle(_instance.Handle);
            var surfaceHandle = _window.VkSurface!.Create<AllocationCallbacks>(instanceHandle, null);
            _surface = new SurfaceKHR(surfaceHandle.Handle);
        }

        private void SelectPhysicalDevice()
        {
            uint deviceCount = 0;
            Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, null), "vkEnumeratePhysicalDevices");
            if (deviceCount == 0)
            {
                throw new InvalidOperationException("No Vulkan physical device was found.");
            }

            var devices = new PhysicalDevice[deviceCount];
            fixed (PhysicalDevice* devicePointer = devices)
            {
                Check(_vk.EnumeratePhysicalDevices(_instance, &deviceCount, devicePointer), "vkEnumeratePhysicalDevices");
            }

            // Hybrid laptops enumerate the iGPU first, and AMD's integrated driver
            // segfaults compiling some translated shaders, so rank rather than take
            // the first hit. SHARPEMU_VK_DEVICE=<substring> pins an adapter by name.
            var deviceOverride = Environment.GetEnvironmentVariable("SHARPEMU_VK_DEVICE");
            var bestScore = int.MinValue;
            var found = false;

            foreach (var device in devices)
            {
                uint queueCount = 0;
                _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, null);
                var queues = new QueueFamilyProperties[queueCount];
                fixed (QueueFamilyProperties* queuePointer = queues)
                {
                    _vk.GetPhysicalDeviceQueueFamilyProperties(device, &queueCount, queuePointer);
                }

                for (uint index = 0; index < queueCount; index++)
                {
                    var supportsGraphics = (queues[index].QueueFlags & QueueFlags.GraphicsBit) != 0;
                    _surfaceApi.GetPhysicalDeviceSurfaceSupport(device, index, _surface, out var supportsPresent);
                    if (!supportsGraphics || !supportsPresent)
                    {
                        continue;
                    }

                    _vk.GetPhysicalDeviceProperties(device, out var properties);
                    var name = SilkMarshal.PtrToString((nint)properties.DeviceName) ?? string.Empty;
                    var score = ScorePhysicalDevice(properties, name, deviceOverride);
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan candidate: {name} ({properties.DeviceType}) score={score}");

                    if (score > bestScore)
                    {
                        bestScore = score;
                        _physicalDevice = device;
                        _queueFamilyIndex = index;
                        found = true;
                    }

                    break;
                }
            }

            if (!found)
            {
                throw new InvalidOperationException("No Vulkan graphics/present queue was found.");
            }

            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var selected);
            _maxColorAttachments = selected.Limits.MaxColorAttachments;
            var selectedName = SilkMarshal.PtrToString((nint)selected.DeviceName) ?? "unknown";
            var apiMajor = (selected.ApiVersion >> 22) & 0x7F;
            var apiMinor = (selected.ApiVersion >> 12) & 0x3FF;
            var apiPatch = selected.ApiVersion & 0xFFF;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan device: {selectedName} " +
                $"(type={selected.DeviceType}, api={apiMajor}.{apiMinor}.{apiPatch})");
            VideoOutExports.SetSelectedGpuName(selectedName);
            _window.Title = VideoOutExports.GetWindowTitle();
        }

        private static int ScorePhysicalDevice(
            PhysicalDeviceProperties properties,
            string name,
            string? deviceOverride)
        {
            if (!string.IsNullOrWhiteSpace(deviceOverride))
            {
                return name.Contains(deviceOverride, StringComparison.OrdinalIgnoreCase) ? 1000 : -1000;
            }

            var score = properties.DeviceType switch
            {
                PhysicalDeviceType.DiscreteGpu => 300,
                PhysicalDeviceType.VirtualGpu => 100,
                PhysicalDeviceType.Cpu => 50,
                // Last resort: only picked when nothing else can present.
                PhysicalDeviceType.IntegratedGpu => -100,
                _ => 10,
            };

            if (properties.VendorID == NvidiaVendorId)
            {
                score += 500;
            }

            return score;
        }

        private void CreateDevice()
        {
            var priority = 1.0f;
            var queueInfo = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = _queueFamilyIndex,
                QueueCount = 1,
                PQueuePriorities = &priority,
            };
            _vk.GetPhysicalDeviceFeatures(_physicalDevice, out var supportedFeatures);
            _supportsIndependentBlend = supportedFeatures.IndependentBlend;
            var enabledFeatures = new PhysicalDeviceFeatures
            {
                IndependentBlend = supportedFeatures.IndependentBlend,
                VertexPipelineStoresAndAtomics = supportedFeatures.VertexPipelineStoresAndAtomics,
                FragmentStoresAndAtomics = supportedFeatures.FragmentStoresAndAtomics,
                ShaderInt64 = supportedFeatures.ShaderInt64,
                ShaderImageGatherExtended = supportedFeatures.ShaderImageGatherExtended,
                ShaderStorageImageExtendedFormats = supportedFeatures.ShaderStorageImageExtendedFormats,
                ShaderStorageImageReadWithoutFormat = supportedFeatures.ShaderStorageImageReadWithoutFormat,
                ShaderStorageImageWriteWithoutFormat = supportedFeatures.ShaderStorageImageWriteWithoutFormat,
                RobustBufferAccess = supportedFeatures.RobustBufferAccess,
            };

            if (!supportedFeatures.RobustBufferAccess)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support robustBufferAccess " +
                    "translated shaders performing out-of-bounds buffer access may cause device loss.");
            }

            if (!supportedFeatures.ShaderInt64)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderInt64 " +
                    "translated shaders using 64-bit integers will fail.");
            }

            if (!supportedFeatures.VertexPipelineStoresAndAtomics || !supportedFeatures.FragmentStoresAndAtomics)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support vertexPipelineStoresAndAtomics/fragmentStoresAndAtomics " +
                    "translated shaders using storage buffers in vertex/fragment stages may fail.");
            }

            if (!supportedFeatures.ShaderImageGatherExtended)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderImageGatherExtended " +
                    "translated shaders using image gather with offsets/LOD/bias will fail.");
            }

            if (!supportedFeatures.ShaderStorageImageReadWithoutFormat ||
                !supportedFeatures.ShaderStorageImageWriteWithoutFormat)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support shaderStorageImage(Read|Write)WithoutFormat " +
                    "translated shaders using unformatted storage image load/store will fail.");
            }

            var maintenance8Features = new PhysicalDeviceMaintenance8FeaturesKHR
            {
                SType = StructureType.PhysicalDeviceMaintenance8FeaturesKhr,
            };
            var robustness2Features = new PhysicalDeviceRobustness2FeaturesEXT
            {
                SType = StructureType.PhysicalDeviceRobustness2FeaturesExt,
                PNext = &maintenance8Features,
            };
            var featuresQuery = new PhysicalDeviceFeatures2
            {
                SType = StructureType.PhysicalDeviceFeatures2,
                PNext = &robustness2Features,
            };
            _vk.GetPhysicalDeviceFeatures2(_physicalDevice, &featuresQuery);
            var supportsMaintenance8 = maintenance8Features.Maintenance8;
            var supportsRobustBufferAccess2 = robustness2Features.RobustBufferAccess2;
            var supportsRobustImageAccess2 = robustness2Features.RobustImageAccess2;
            var supportsNullDescriptor = robustness2Features.NullDescriptor;
            var supportsRobustness2 = supportsRobustImageAccess2 || supportsNullDescriptor;
            if (!supportsMaintenance8)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_KHR_maintenance8 " +
                    "translated shaders using a dynamic texel offset on non-gather image samples will fail.");
            }

            if (!supportsRobustImageAccess2)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] GPU does not support VK_EXT_robustness2 robustImageAccess2 " +
                    "translated shaders performing out-of-bounds image access may cause device loss.");
            }

            var swapchainExtension = (byte*)SilkMarshal.StringToPtr("VK_KHR_swapchain");
            var maintenance8Extension = (byte*)SilkMarshal.StringToPtr("VK_KHR_maintenance8");
            var robustness2Extension = (byte*)SilkMarshal.StringToPtr("VK_EXT_robustness2");
            var portabilitySubsetExtension = (byte*)SilkMarshal.StringToPtr(PortabilitySubsetExtensionName);
            try
            {
                var extensions = stackalloc byte*[4];
                var extensionCount = 0u;
                extensions[extensionCount++] = swapchainExtension;
                if (supportsMaintenance8)
                {
                    extensions[extensionCount++] = maintenance8Extension;
                }

                if (supportsRobustness2)
                {
                    extensions[extensionCount++] = robustness2Extension;
                }

                if (IsDeviceExtensionAvailable(PortabilitySubsetExtensionName))
                {
                    // The spec requires enabling this when the (MoltenVK)
                    // device advertises it.
                    extensions[extensionCount++] = portabilitySubsetExtension;
                }

                maintenance8Features.Maintenance8 = supportsMaintenance8;
                maintenance8Features.PNext = null;
                robustness2Features.RobustBufferAccess2 =
                    supportsRobustBufferAccess2 && supportedFeatures.RobustBufferAccess;
                robustness2Features.RobustImageAccess2 = supportsRobustImageAccess2;
                robustness2Features.NullDescriptor = supportsNullDescriptor;
                robustness2Features.PNext = supportsMaintenance8 ? &maintenance8Features : null;
                var features2 = new PhysicalDeviceFeatures2
                {
                    SType = StructureType.PhysicalDeviceFeatures2,
                    PNext = supportsRobustness2
                        ? &robustness2Features
                        : (supportsMaintenance8 ? &maintenance8Features : null),
                    Features = enabledFeatures,
                };
                var createInfo = new DeviceCreateInfo
                {
                    SType = StructureType.DeviceCreateInfo,
                    PNext = &features2,
                    QueueCreateInfoCount = 1,
                    PQueueCreateInfos = &queueInfo,
                    EnabledExtensionCount = extensionCount,
                    PpEnabledExtensionNames = extensions,
                };

                Check(_vk.CreateDevice(_physicalDevice, &createInfo, null, out _device), "vkCreateDevice");
            }
            finally
            {
                SilkMarshal.Free((nint)swapchainExtension);
                SilkMarshal.Free((nint)maintenance8Extension);
                SilkMarshal.Free((nint)robustness2Extension);
                SilkMarshal.Free((nint)portabilitySubsetExtension);
            }

            _vk.GetDeviceQueue(_device, _queueFamilyIndex, 0, out _queue);
            LoadDebugUtilsCommands();
            if (!_vk.TryGetDeviceExtension(_instance, _device, out _swapchainApi))
            {
                throw new InvalidOperationException("VK_KHR_swapchain is unavailable.");
            }
        }

        private void CreatePipelineCache()
        {
            _vk.GetPhysicalDeviceProperties(_physicalDevice, out var properties);
            var directory = Path.Combine(AppContext.BaseDirectory, "pipeline-cache");
            _pipelineCachePath = Path.Combine(
                directory,
                $"vulkan-{properties.VendorID:X4}-{properties.DeviceID:X4}-{properties.DriverVersion:X8}.bin");

            byte[] initialData = [];
            try
            {
                if (File.Exists(_pipelineCachePath))
                {
                    initialData = File.ReadAllBytes(_pipelineCachePath);
                }
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache could not be read: {exception.Message}");
            }

            fixed (byte* initialDataPointer = initialData)
            {
                var createInfo = new PipelineCacheCreateInfo
                {
                    SType = StructureType.PipelineCacheCreateInfo,
                    InitialDataSize = (nuint)initialData.Length,
                    PInitialData = initialData.Length == 0 ? null : initialDataPointer,
                };
                var result = _vk.CreatePipelineCache(
                    _device,
                    &createInfo,
                    null,
                    out _pipelineCache);
                if (result != Result.Success && initialData.Length != 0)
                {
                    createInfo.InitialDataSize = 0;
                    createInfo.PInitialData = null;
                    Check(
                        _vk.CreatePipelineCache(
                            _device,
                            &createInfo,
                            null,
                            out _pipelineCache),
                        "vkCreatePipelineCache(empty)");
                    initialData = [];
                }
                else
                {
                    Check(result, "vkCreatePipelineCache");
                }
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan pipeline cache: " +
                $"{(initialData.Length == 0 ? "new" : $"loaded {initialData.Length} bytes")}");
        }

        private void SavePipelineCache()
        {
            if (_pipelineCache.Handle == 0 || string.IsNullOrWhiteSpace(_pipelineCachePath))
            {
                return;
            }

            try
            {
                nuint size = 0;
                Check(
                    _vk.GetPipelineCacheData(_device, _pipelineCache, &size, null),
                    "vkGetPipelineCacheData(size)");
                if (size == 0 || size > int.MaxValue)
                {
                    return;
                }

                var data = new byte[(int)size];
                fixed (byte* dataPointer = data)
                {
                    Check(
                        _vk.GetPipelineCacheData(
                            _device,
                            _pipelineCache,
                            &size,
                            dataPointer),
                        "vkGetPipelineCacheData");
                }

                var directory = Path.GetDirectoryName(_pipelineCachePath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(_pipelineCachePath, data.AsSpan(0, (int)size).ToArray());
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan pipeline cache could not be saved: {exception.Message}");
            }
        }

        private void CreateSwapchain()
        {
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");

            uint formatCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR");
            var formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* formatPointer = formats)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, &formatCount, formatPointer),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR");
            }

            var surfaceFormat = ChooseSurfaceFormat(formats);
            _swapchainFormat = surfaceFormat.Format;
            _extent = ChooseExtent(capabilities);
            uint presentModeCount = 0;
            Check(
                _surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                    _physicalDevice,
                    _surface,
                    &presentModeCount,
                    null),
                "vkGetPhysicalDeviceSurfacePresentModesKHR");
            var presentModes = new PresentModeKHR[presentModeCount];
            fixed (PresentModeKHR* presentModePointer = presentModes)
            {
                Check(
                    _surfaceApi.GetPhysicalDeviceSurfacePresentModes(
                        _physicalDevice,
                        _surface,
                        &presentModeCount,
                        presentModePointer),
                    "vkGetPhysicalDeviceSurfacePresentModesKHR");
            }

            var presentMode = presentModes.Contains(PresentModeKHR.MailboxKhr)
                ? PresentModeKHR.MailboxKhr
                : PresentModeKHR.FifoKhr;
            Console.Error.WriteLine($"[LOADER][INFO] Vulkan present mode: {presentMode}");
            var imageCount = capabilities.MinImageCount + 1;
            if (capabilities.MaxImageCount != 0)
            {
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            }

            var compositeAlpha = ChooseCompositeAlpha(capabilities.SupportedCompositeAlpha);
            var createInfo = new SwapchainCreateInfoKHR
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = _surface,
                MinImageCount = imageCount,
                ImageFormat = surfaceFormat.Format,
                ImageColorSpace = surfaceFormat.ColorSpace,
                ImageExtent = _extent,
                ImageArrayLayers = 1,
                ImageUsage =
                    ImageUsageFlags.TransferDstBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.ColorAttachmentBit,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = compositeAlpha,
                PresentMode = presentMode,
                Clipped = true,
            };

            Check(_swapchainApi.CreateSwapchain(_device, &createInfo, null, out _swapchain), "vkCreateSwapchainKHR");

            uint swapchainImageCount = 0;
            Check(
                _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, null),
                "vkGetSwapchainImagesKHR");
            _swapchainImages = new Image[swapchainImageCount];
            fixed (Image* imagePointer = _swapchainImages)
            {
                Check(
                    _swapchainApi.GetSwapchainImages(_device, _swapchain, &swapchainImageCount, imagePointer),
                    "vkGetSwapchainImagesKHR");
            }

            _imageInitialized = new bool[swapchainImageCount];
        }

        private void CreateCommandResources()
        {
            var poolInfo = new CommandPoolCreateInfo
            {
                SType = StructureType.CommandPoolCreateInfo,
                Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
                QueueFamilyIndex = _queueFamilyIndex,
            };
            Check(_vk.CreateCommandPool(_device, &poolInfo, null, out _commandPool), "vkCreateCommandPool");

            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            Check(_vk.AllocateCommandBuffers(_device, &allocateInfo, out _commandBuffer), "vkAllocateCommandBuffers");
            _presentationCommandBuffer = _commandBuffer;

            var semaphoreInfo = new SemaphoreCreateInfo
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _imageAvailable), "vkCreateSemaphore");
            Check(_vk.CreateSemaphore(_device, &semaphoreInfo, null, out _renderFinished), "vkCreateSemaphore");

            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out _presentationFence),
                "vkCreateFence(presentation)");

            CreateStagingBuffer((ulong)_extent.Width * _extent.Height * 4);
        }

        private void CompletePendingPresentation(bool wait)
        {
            if (!_presentationInFlight)
            {
                return;
            }

            var fence = _presentationFence;
            var result = wait
                ? _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue)
                : _vk.GetFenceStatus(_device, fence);
            if (!wait && result == Result.NotReady)
            {
                return;
            }

            Check(result, "vkWaitForFences(presentation)");
            _presentationInFlight = false;
            if (_pendingPresentationResources is not null)
            {
                MarkSampledImagesInitialized(_pendingPresentationResources);
                MarkStorageImagesInitialized(_pendingPresentationResources);
                DestroyTranslatedDrawResources(_pendingPresentationResources);
                _pendingPresentationResources = null;
            }
        }

        private CommandBuffer AllocateGuestCommandBuffer()
        {
            var allocateInfo = new CommandBufferAllocateInfo
            {
                SType = StructureType.CommandBufferAllocateInfo,
                CommandPool = _commandPool,
                Level = CommandBufferLevel.Primary,
                CommandBufferCount = 1,
            };
            CommandBuffer commandBuffer;
            Check(
                _vk.AllocateCommandBuffers(
                    _device,
                    &allocateInfo,
                    out commandBuffer),
                "vkAllocateCommandBuffers(guest)");
            return commandBuffer;
        }

        private void SubmitGuestCommandBuffer(
            CommandBuffer commandBuffer,
            TranslatedDrawResources resources,
            IReadOnlyList<GuestImageResource> traceImages)
        {
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Fence fence;
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "vkCreateFence(guest)");
            try
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, fence),
                    "vkQueueSubmit(guest)");
            }
            catch
            {
                _vk.DestroyFence(_device, fence, null);
                throw;
            }

            _pendingGuestSubmissions.Enqueue(
                new PendingGuestSubmission(
                    fence,
                    commandBuffer,
                    resources,
                    traceImages,
                    resources.DebugName));
        }

        private void SubmitGuestCommandBufferAndWait(CommandBuffer commandBuffer)
        {
            var fenceInfo = new FenceCreateInfo
            {
                SType = StructureType.FenceCreateInfo,
            };
            Fence fence;
            Check(
                _vk.CreateFence(_device, &fenceInfo, null, out fence),
                "vkCreateFence(guest chunk)");
            try
            {
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, fence),
                    "vkQueueSubmit(guest chunk)");
                Check(
                    _vk.WaitForFences(_device, 1, &fence, true, ulong.MaxValue),
                    "vkWaitForFences(guest chunk)");
            }
            finally
            {
                _vk.DestroyFence(_device, fence, null);
            }

            _vk.FreeCommandBuffers(_device, _commandPool, 1, &commandBuffer);
        }

        private void EnsureGuestSubmissionCapacity()
        {
            CollectCompletedGuestSubmissions(waitForOldest: false);
            if (_pendingGuestSubmissions.Count >= MaxInFlightGuestSubmissions)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void WaitForAllGuestSubmissions()
        {
            while (_pendingGuestSubmissions.Count != 0)
            {
                CollectCompletedGuestSubmissions(waitForOldest: true);
            }
        }

        private void CollectCompletedGuestSubmissions(bool waitForOldest)
        {
            if (waitForOldest && _pendingGuestSubmissions.TryPeek(out var oldest))
            {
                var fence = oldest.Fence;
                var result = _vk.WaitForFences(
                    _device,
                    1,
                    &fence,
                    true,
                    ulong.MaxValue);
                Check(result, $"vkWaitForFences(guest: {oldest.DebugName})");
            }

            while (_pendingGuestSubmissions.TryPeek(out var submission))
            {
                var status = _vk.GetFenceStatus(_device, submission.Fence);
                if (status == Result.NotReady)
                {
                    break;
                }

                Check(status, $"vkGetFenceStatus(guest: {submission.DebugName})");
                _pendingGuestSubmissions.Dequeue();

                foreach (var image in submission.TraceImages)
                {
                    TraceGuestImageContents(image);
                }

                DestroyTranslatedDrawResources(submission.Resources);
                var commandBuffer = submission.CommandBuffer;
                _vk.FreeCommandBuffers(
                    _device,
                    _commandPool,
                    1,
                    &commandBuffer);
                _vk.DestroyFence(_device, submission.Fence, null);
            }
        }

        private IReadOnlyList<GuestImageResource> GetTraceImages(
            TranslatedDrawResources resources,
            IReadOnlyList<GuestImageResource>? renderTargets = null)
        {
            var images = new HashSet<GuestImageResource>();
            foreach (var renderTarget in renderTargets ?? [])
            {
                if (ShouldTraceGuestImageContents(renderTarget))
                {
                    images.Add(renderTarget);
                }
            }

            foreach (var texture in resources.Textures)
            {
                if (texture.IsStorage &&
                    texture.GuestImage is { } image &&
                    ShouldTraceGuestImageContents(image))
                {
                    images.Add(image);
                }
            }

            return images.ToArray();
        }

        private void CreateGuestDrawResources()
        {
            var colorAttachment = new AttachmentDescription
            {
                Format = _swapchainFormat,
                Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear,
                StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare,
                StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined,
                FinalLayout = ImageLayout.PresentSrcKhr,
            };
            var colorReference = new AttachmentReference
            {
                Attachment = 0,
                Layout = ImageLayout.ColorAttachmentOptimal,
            };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = 1,
                PColorAttachments = &colorReference,
            };
            var dependency = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal,
                DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = 1,
                PAttachments = &colorAttachment,
                SubpassCount = 1,
                PSubpasses = &subpass,
                DependencyCount = 1,
                PDependencies = &dependency,
            };
            Check(_vk.CreateRenderPass(_device, &renderPassInfo, null, out _renderPass), "vkCreateRenderPass");

            _swapchainImageViews = new ImageView[_swapchainImages.Length];
            _framebuffers = new Framebuffer[_swapchainImages.Length];
            for (var index = 0; index < _swapchainImages.Length; index++)
            {
                var viewInfo = new ImageViewCreateInfo
                {
                    SType = StructureType.ImageViewCreateInfo,
                    Image = _swapchainImages[index],
                    ViewType = ImageViewType.Type2D,
                    Format = _swapchainFormat,
                    Components = new ComponentMapping(
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity,
                        ComponentSwizzle.Identity),
                    SubresourceRange = ColorSubresourceRange(),
                };
                Check(
                    _vk.CreateImageView(_device, &viewInfo, null, out _swapchainImageViews[index]),
                    "vkCreateImageView");

                var imageView = _swapchainImageViews[index];
                var framebufferInfo = new FramebufferCreateInfo
                {
                    SType = StructureType.FramebufferCreateInfo,
                    RenderPass = _renderPass,
                    AttachmentCount = 1,
                    PAttachments = &imageView,
                    Width = _extent.Width,
                    Height = _extent.Height,
                    Layers = 1,
                };
                Check(
                    _vk.CreateFramebuffer(_device, &framebufferInfo, null, out _framebuffers[index]),
                    "vkCreateFramebuffer");
            }

            var layoutInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            Check(
                _vk.CreatePipelineLayout(_device, &layoutInfo, null, out _pipelineLayout),
                "vkCreatePipelineLayout");
            CreateBarycentricPipeline();
        }

        private void CreateBarycentricPipeline()
        {
            var vertexBytes = Convert.FromBase64String(FullscreenBarycentricVertexSpirv);
            var fragmentBytes = Convert.FromBase64String(FullscreenBarycentricFragmentSpirv);
            var vertexModule = CreateShaderModule(vertexBytes);
            var fragmentModule = CreateShaderModule(fragmentBytes);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexInput = new PipelineVertexInputStateCreateInfo
                {
                    SType = StructureType.PipelineVertexInputStateCreateInfo,
                };
                var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                {
                    SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                    Topology = PrimitiveTopology.TriangleList,
                };
                var viewport = new Viewport(0, 0, _extent.Width, _extent.Height, 0, 1);
                var scissor = new Rect2D(new Offset2D(0, 0), _extent);
                var viewportState = new PipelineViewportStateCreateInfo
                {
                    SType = StructureType.PipelineViewportStateCreateInfo,
                    ViewportCount = 1,
                    PViewports = &viewport,
                    ScissorCount = 1,
                    PScissors = &scissor,
                };
                var rasterization = new PipelineRasterizationStateCreateInfo
                {
                    SType = StructureType.PipelineRasterizationStateCreateInfo,
                    PolygonMode = PolygonMode.Fill,
                    CullMode = CullModeFlags.None,
                    FrontFace = FrontFace.CounterClockwise,
                    LineWidth = 1,
                };
                var multisample = new PipelineMultisampleStateCreateInfo
                {
                    SType = StructureType.PipelineMultisampleStateCreateInfo,
                    RasterizationSamples = SampleCountFlags.Count1Bit,
                };
                var colorBlendAttachment = new PipelineColorBlendAttachmentState
                {
                    ColorWriteMask =
                        ColorComponentFlags.RBit |
                        ColorComponentFlags.GBit |
                        ColorComponentFlags.BBit |
                        ColorComponentFlags.ABit,
                };
                var colorBlend = new PipelineColorBlendStateCreateInfo
                {
                    SType = StructureType.PipelineColorBlendStateCreateInfo,
                    AttachmentCount = 1,
                    PAttachments = &colorBlendAttachment,
                };
                var pipelineInfo = new GraphicsPipelineCreateInfo
                {
                    SType = StructureType.GraphicsPipelineCreateInfo,
                    StageCount = 2,
                    PStages = shaderStages,
                    PVertexInputState = &vertexInput,
                    PInputAssemblyState = &inputAssembly,
                    PViewportState = &viewportState,
                    PRasterizationState = &rasterization,
                    PMultisampleState = &multisample,
                    PColorBlendState = &colorBlend,
                    Layout = _pipelineLayout,
                    RenderPass = _renderPass,
                    Subpass = 0,
                };
                Check(
                    _vk.CreateGraphicsPipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out _barycentricPipeline),
                    "vkCreateGraphicsPipelines");
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private ShaderModule CreateShaderModule(byte[] code)
        {
            fixed (byte* codePointer = code)
            {
                var createInfo = new ShaderModuleCreateInfo
                {
                    SType = StructureType.ShaderModuleCreateInfo,
                    CodeSize = (nuint)code.Length,
                    PCode = (uint*)codePointer,
                };
                Check(
                    _vk.CreateShaderModule(_device, &createInfo, null, out var module),
                    "vkCreateShaderModule");
                return module;
            }
        }

        private TranslatedDrawResources CreateTranslatedDrawResources(
            VulkanTranslatedGuestDraw draw,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent)
        {
            var vertexSpirv = draw.VertexSpirv;
            if (draw.RenderState.Blends.Count != renderTargetFormats.Count)
            {
                throw new InvalidOperationException(
                    "color attachment formats and blend states must have matching counts");
            }

            if (vertexSpirv.Length == 0 &&
                !TryCompileFullscreenVertexShader(
                    draw.AttributeCount,
                    out vertexSpirv,
                    out var vertexError))
            {
                throw new InvalidOperationException($"translated vertex shader failed: {vertexError}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = "SharpEmu draw",
                Textures = new TextureResource[draw.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[draw.GlobalMemoryBuffers.Count],
                VertexBuffers = new VertexBufferResource[draw.VertexBuffers.Count],
                VertexCount = GetDrawVertexCount(draw.PrimitiveType, draw.VertexCount, draw.IndexBuffer),
                InstanceCount = Math.Max(draw.InstanceCount, 1),
                Topology = GetPrimitiveTopology(draw.PrimitiveType),
                Blends = draw.RenderState.Blends.ToArray(),
                Scissor = draw.RenderState.Scissor,
                Viewport = draw.RenderState.Viewport,
            };

            try
            {
                foreach (var texture in draw.Textures)
                {
                    if (texture.IsStorage)
                    {
                        _ = ResolveStorageGuestImage(texture);
                    }
                }

                for (var index = 0; index < draw.Textures.Count; index++)
                {
                    resources.Textures[index] = ResolveTextureResource(draw.Textures[index]);
                }

                for (var index = 0; index < draw.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(draw.GlobalMemoryBuffers[index]);
                }

                for (var index = 0; index < draw.VertexBuffers.Count; index++)
                {
                    resources.VertexBuffers[index] =
                        CreateVertexBufferResource(draw.VertexBuffers[index]);
                }

                if (draw.IndexBuffer is { Data.Length: > 0 } indexBuffer)
                {
                    resources.IndexBuffer = CreateHostBuffer(
                        indexBuffer.Data,
                        BufferUsageFlags.IndexBufferBit,
                        out resources.IndexMemory);
                    resources.Index32Bit = indexBuffer.Is32Bit;
                }

                CreateTranslatedDescriptorResources(
                    resources,
                    ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit);
                CreateTranslatedPipeline(
                    resources,
                    vertexSpirv,
                    draw.PixelSpirv,
                    renderPass,
                    renderTargetFormats,
                    extent);
                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TranslatedDrawResources CreateComputeDispatchResources(
            VulkanComputeGuestDispatch dispatch)
        {
            var traceResources = dispatch.Textures.Count >= 8;
            if (traceResources)
            {
                TraceVulkanShader(
                    $"vk.compute_resources begin groups={dispatch.GroupCountX}x" +
                    $"{dispatch.GroupCountY}x{dispatch.GroupCountZ} textures={dispatch.Textures.Count}");
            }

            var resources = new TranslatedDrawResources
            {
                DebugName = BuildComputeDebugName(dispatch),
                Textures = new TextureResource[dispatch.Textures.Count],
                GlobalMemoryBuffers =
                    new GlobalBufferResource[dispatch.GlobalMemoryBuffers.Count],
            };

            try
            {
                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    var texture = dispatch.Textures[index];
                    if (texture.IsStorage)
                    {
                        if (traceResources)
                        {
                            TraceVulkanShader(
                                $"vk.compute_resources storage[{index}] begin " +
                                $"addr=0x{texture.Address:X16} fmt={texture.Format} " +
                                $"size={texture.Width}x{texture.Height} " +
                                $"mips={texture.MipLevels} level={texture.MipLevel}");
                        }

                        _ = ResolveStorageImageResource(texture);
                        if (traceResources)
                        {
                            TraceVulkanShader($"vk.compute_resources storage[{index}] ready");
                        }
                    }
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve begin");
                }

                for (var index = 0; index < dispatch.Textures.Count; index++)
                {
                    resources.Textures[index] =
                        ResolveTextureResource(dispatch.Textures[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources resolve ready");
                }

                for (var index = 0; index < dispatch.GlobalMemoryBuffers.Count; index++)
                {
                    resources.GlobalMemoryBuffers[index] =
                        CreateGlobalBufferResource(dispatch.GlobalMemoryBuffers[index]);
                }

                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors begin");
                }

                CreateTranslatedDescriptorResources(resources, ShaderStageFlags.ComputeBit);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources descriptors ready");
                }

                if (traceResources)
                {
                    TraceVulkanShader(
                        $"vk.compute_resources pipeline begin " +
                        $"cs=0x{dispatch.ShaderAddress:X16} " +
                        $"spirv={dispatch.ComputeSpirv.Length} " +
                        $"textures={resources.Textures.Length} " +
                        $"globals={resources.GlobalMemoryBuffers.Length}");
                }

                CreateComputePipeline(resources, dispatch.ComputeSpirv);
                if (traceResources)
                {
                    TraceVulkanShader("vk.compute_resources pipeline ready");
                }

                return resources;
            }
            catch
            {
                DestroyTranslatedDrawResources(resources);
                throw;
            }
        }

        private static bool TryCompileFullscreenVertexShader(
            uint attributeCount,
            out byte[] spirv,
            out string error)
        {
            spirv = [];
            error = string.Empty;
            if (attributeCount > 32)
            {
                error = $"too many interpolated attributes: {attributeCount}";
                return false;
            }

            spirv = SpirvFixedShaders.CreateFullscreenVertex(attributeCount);
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateTranslatedDescriptorResources(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags)
        {
            var textureCount = resources.Textures.Length;
            var sampledImageCount = resources.Textures.Count(texture => !texture.IsStorage);
            var storageImageCount = textureCount - sampledImageCount;
            var globalBufferCount = resources.GlobalMemoryBuffers.Length;
            var bindingCount = textureCount + (globalBufferCount == 0 ? 0 : 1);
            var layout = GetOrCreateDescriptorLayout(resources, stageFlags, bindingCount);
            resources.DescriptorSetLayout = layout.DescriptorSetLayout;
            resources.PipelineLayout = layout.PipelineLayout;
            resources.DescriptorLayoutCached = true;
            if (bindingCount == 0)
            {
                return;
            }

            var setLayout = layout.DescriptorSetLayout;

            var poolSizes = new DescriptorPoolSize[
                (sampledImageCount == 0 ? 0 : 1) +
                (storageImageCount == 0 ? 0 : 1) +
                (globalBufferCount == 0 ? 0 : 1)];
            var poolSizeIndex = 0;
            if (sampledImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.CombinedImageSampler,
                    DescriptorCount = (uint)sampledImageCount,
                };
            }

            if (storageImageCount != 0)
            {
                poolSizes[poolSizeIndex++] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageImage,
                    DescriptorCount = (uint)storageImageCount,
                };
            }

            if (globalBufferCount != 0)
            {
                poolSizes[poolSizeIndex] = new DescriptorPoolSize
                {
                    Type = DescriptorType.StorageBuffer,
                    DescriptorCount = (uint)globalBufferCount,
                };
            }

            fixed (DescriptorPoolSize* poolSizePointer = poolSizes)
            {
                var poolInfo = new DescriptorPoolCreateInfo
                {
                    SType = StructureType.DescriptorPoolCreateInfo,
                    MaxSets = 1,
                    PoolSizeCount = (uint)poolSizes.Length,
                    PPoolSizes = poolSizePointer,
                };
                DescriptorPool descriptorPool;
                Check(
                    _vk.CreateDescriptorPool(
                        _device,
                        &poolInfo,
                        null,
                        out descriptorPool),
                    "vkCreateDescriptorPool");
                resources.DescriptorPool = descriptorPool;
            }

            var allocateInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = resources.DescriptorPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            DescriptorSet descriptorSet;
            Check(
                _vk.AllocateDescriptorSets(_device, &allocateInfo, out descriptorSet),
                "vkAllocateDescriptorSets");
            resources.DescriptorSet = descriptorSet;

            var imageInfos = new DescriptorImageInfo[textureCount];
            var bufferInfos = new DescriptorBufferInfo[globalBufferCount];
            var writes = new WriteDescriptorSet[bindingCount];
            fixed (DescriptorImageInfo* imageInfoPointer = imageInfos)
            fixed (DescriptorBufferInfo* bufferInfoPointer = bufferInfos)
            fixed (WriteDescriptorSet* writePointer = writes)
            {
                var writeIndex = 0;
                if (globalBufferCount != 0)
                {
                    for (var index = 0; index < globalBufferCount; index++)
                    {
                        bufferInfoPointer[index] = new DescriptorBufferInfo
                        {
                            Buffer = resources.GlobalMemoryBuffers[index].Buffer,
                            Offset = 0,
                            Range = resources.GlobalMemoryBuffers[index].Size,
                        };
                    }

                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = 0,
                        DescriptorCount = (uint)globalBufferCount,
                        DescriptorType = DescriptorType.StorageBuffer,
                        PBufferInfo = bufferInfoPointer,
                    };
                }

                for (var index = 0; index < textureCount; index++)
                {
                    var isStorage = resources.Textures[index].IsStorage;
                    if (!isStorage &&
                        resources.Textures[index].Sampler.Handle == 0)
                    {
                        resources.Textures[index].Sampler =
                            CreateSampler(resources.Textures[index].SamplerState);
                    }

                    imageInfoPointer[index] = new DescriptorImageInfo
                    {
                        Sampler = isStorage ? default : resources.Textures[index].Sampler,
                        ImageView = resources.Textures[index].View,
                        ImageLayout = isStorage ||
                            resources.Textures[index].GuestImage is { } guestImage &&
                            resources.Textures.Any(
                                texture =>
                                    texture.IsStorage &&
                                    texture.GuestImage == guestImage)
                                ? ImageLayout.General
                                : ImageLayout.ShaderReadOnlyOptimal,
                    };
                    writePointer[writeIndex++] = new WriteDescriptorSet
                    {
                        SType = StructureType.WriteDescriptorSet,
                        DstSet = resources.DescriptorSet,
                        DstBinding = (uint)(index + 1),
                        DescriptorCount = 1,
                        DescriptorType = isStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        PImageInfo = &imageInfoPointer[index],
                    };
                }

                _vk.UpdateDescriptorSets(
                    _device,
                    (uint)bindingCount,
                    writePointer,
                    0,
                    null);
            }
        }

        private void CreateTranslatedPipeline(
            TranslatedDrawResources resources,
            byte[] vertexSpirv,
            byte[] fragmentSpirv,
            RenderPass renderPass,
            IReadOnlyList<Format> renderTargetFormats,
            Extent2D extent)
        {
            var pipelineKey = new GraphicsPipelineKey(
                GetShaderDigest(vertexSpirv),
                GetShaderDigest(fragmentSpirv),
                string.Join(',', renderTargetFormats.Select(format => (uint)format)),
                resources.Topology,
                string.Join(';', resources.Blends.Select(blend =>
                    $"{(blend.Enable ? 1 : 0)}:{blend.ColorSrcFactor}:{blend.ColorDstFactor}:" +
                    $"{blend.ColorFunc}:{blend.AlphaSrcFactor}:{blend.AlphaDstFactor}:" +
                    $"{blend.AlphaFunc}:{(blend.SeparateAlphaBlend ? 1 : 0)}:{blend.WriteMask}")),
                GetResourceLayoutKey(resources),
                GetVertexLayoutKey(resources));
            if (_graphicsPipelines.TryGetValue(pipelineKey, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var vertexModule = CreateShaderModule(vertexSpirv);
            var fragmentModule = CreateShaderModule(fragmentSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var shaderStages = stackalloc PipelineShaderStageCreateInfo[2];
                shaderStages[0] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.VertexBit,
                    Module = vertexModule,
                    PName = entryPoint,
                };
                shaderStages[1] = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.FragmentBit,
                    Module = fragmentModule,
                    PName = entryPoint,
                };

                var vertexBindingDescriptions =
                    new VertexInputBindingDescription[resources.VertexBuffers.Length];
                var vertexAttributeDescriptions =
                    new VertexInputAttributeDescription[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    var vertexBuffer = resources.VertexBuffers[index];
                    vertexBindingDescriptions[index] = new VertexInputBindingDescription
                    {
                        Binding = (uint)index,
                        Stride = vertexBuffer.Stride == 0
                            ? Math.Max(vertexBuffer.ComponentCount, 1) * sizeof(float)
                            : vertexBuffer.Stride,
                        InputRate = VertexInputRate.Vertex,
                    };
                    vertexAttributeDescriptions[index] = new VertexInputAttributeDescription
                    {
                        Location = vertexBuffer.Location,
                        Binding = (uint)index,
                        Format = ToVkVertexFormat(
                            vertexBuffer.DataFormat,
                            vertexBuffer.NumberFormat,
                            vertexBuffer.ComponentCount),
                        Offset = 0,
                    };
                }

                fixed (VertexInputBindingDescription* vertexBindingPointerBase = vertexBindingDescriptions)
                fixed (VertexInputAttributeDescription* vertexAttributePointerBase = vertexAttributeDescriptions)
                {
                    var vertexInput = new PipelineVertexInputStateCreateInfo
                    {
                        SType = StructureType.PipelineVertexInputStateCreateInfo,
                        VertexBindingDescriptionCount = (uint)vertexBindingDescriptions.Length,
                        PVertexBindingDescriptions = vertexBindingDescriptions.Length == 0
                            ? null
                            : vertexBindingPointerBase,
                        VertexAttributeDescriptionCount = (uint)vertexAttributeDescriptions.Length,
                        PVertexAttributeDescriptions = vertexAttributeDescriptions.Length == 0
                            ? null
                            : vertexAttributePointerBase,
                    };
                    var inputAssembly = new PipelineInputAssemblyStateCreateInfo
                    {
                        SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                        Topology = resources.Topology,
                    };
                    var viewport = new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
                    var scissor = new Rect2D(new Offset2D(0, 0), extent);
                    var viewportState = new PipelineViewportStateCreateInfo
                    {
                        SType = StructureType.PipelineViewportStateCreateInfo,
                        ViewportCount = 1,
                        PViewports = &viewport,
                        ScissorCount = 1,
                        PScissors = &scissor,
                    };
                    var rasterization = new PipelineRasterizationStateCreateInfo
                    {
                        SType = StructureType.PipelineRasterizationStateCreateInfo,
                        PolygonMode = PolygonMode.Fill,
                        CullMode = CullModeFlags.None,
                        FrontFace = FrontFace.CounterClockwise,
                        LineWidth = 1,
                    };
                    var multisample = new PipelineMultisampleStateCreateInfo
                    {
                        SType = StructureType.PipelineMultisampleStateCreateInfo,
                        RasterizationSamples = SampleCountFlags.Count1Bit,
                    };
                    var colorBlendAttachments = stackalloc PipelineColorBlendAttachmentState[resources.Blends.Length];
                    for (var index = 0; index < resources.Blends.Length; index++)
                    {
                        var blend = resources.Blends[index];
                        colorBlendAttachments[index] = new PipelineColorBlendAttachmentState
                        {
                            BlendEnable = blend.Enable,
                            SrcColorBlendFactor = ToVkBlendFactor(blend.ColorSrcFactor),
                            DstColorBlendFactor = ToVkBlendFactor(blend.ColorDstFactor),
                            ColorBlendOp = ToVkBlendOp(blend.ColorFunc),
                            SrcAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaSrcFactor)
                                : ToVkBlendFactor(blend.ColorSrcFactor),
                            DstAlphaBlendFactor = blend.SeparateAlphaBlend
                                ? ToVkBlendFactor(blend.AlphaDstFactor)
                                : ToVkBlendFactor(blend.ColorDstFactor),
                            AlphaBlendOp = blend.SeparateAlphaBlend
                                ? ToVkBlendOp(blend.AlphaFunc)
                                : ToVkBlendOp(blend.ColorFunc),
                            ColorWriteMask = ToVkColorWriteMask(blend.WriteMask),
                        };
                    }
                    var colorBlend = new PipelineColorBlendStateCreateInfo
                    {
                        SType = StructureType.PipelineColorBlendStateCreateInfo,
                        AttachmentCount = (uint)resources.Blends.Length,
                        PAttachments = colorBlendAttachments,
                    };
                    var dynamicStateValues = stackalloc DynamicState[2];
                    dynamicStateValues[0] = DynamicState.Viewport;
                    dynamicStateValues[1] = DynamicState.Scissor;
                    var dynamicState = new PipelineDynamicStateCreateInfo
                    {
                        SType = StructureType.PipelineDynamicStateCreateInfo,
                        DynamicStateCount = 2,
                        PDynamicStates = dynamicStateValues,
                    };
                    var pipelineInfo = new GraphicsPipelineCreateInfo
                    {
                        SType = StructureType.GraphicsPipelineCreateInfo,
                        StageCount = 2,
                        PStages = shaderStages,
                        PVertexInputState = &vertexInput,
                        PInputAssemblyState = &inputAssembly,
                        PViewportState = &viewportState,
                        PRasterizationState = &rasterization,
                        PMultisampleState = &multisample,
                        PColorBlendState = &colorBlend,
                        PDynamicState = &dynamicState,
                        Layout = resources.PipelineLayout,
                        RenderPass = renderPass,
                        Subpass = 0,
                    };
                    Pipeline pipeline;
                    Check(
                        _vk.CreateGraphicsPipelines(
                            _device,
                            _pipelineCache,
                            1,
                            &pipelineInfo,
                            null,
                            out pipeline),
                        "vkCreateGraphicsPipelines(translated)");
                    resources.Pipeline = pipeline;
                    resources.PipelineCached = true;
                    _graphicsPipelines.Add(pipelineKey, pipeline);
                    SetDebugName(
                        ObjectType.Pipeline,
                        pipeline.Handle,
                        $"SharpEmu graphics ps={fragmentSpirv.Length}b attrs={resources.Textures.Length}");
                }
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, fragmentModule, null);
                _vk.DestroyShaderModule(_device, vertexModule, null);
            }
        }

        private DescriptorLayoutBundle GetOrCreateDescriptorLayout(
            TranslatedDrawResources resources,
            ShaderStageFlags stageFlags,
            int bindingCount)
        {
            var key = new DescriptorLayoutKey(stageFlags, GetResourceLayoutKey(resources));
            if (_descriptorLayouts.TryGetValue(key, out var cached))
            {
                return cached;
            }

            DescriptorSetLayout descriptorSetLayout = default;
            if (bindingCount != 0)
            {
                var bindings = new DescriptorSetLayoutBinding[bindingCount];
                var bindingOffset = 0;
                if (resources.GlobalMemoryBuffers.Length != 0)
                {
                    bindings[bindingOffset++] = new DescriptorSetLayoutBinding
                    {
                        Binding = 0,
                        DescriptorType = DescriptorType.StorageBuffer,
                        DescriptorCount = (uint)resources.GlobalMemoryBuffers.Length,
                        StageFlags = stageFlags,
                    };
                }

                for (var index = 0; index < resources.Textures.Length; index++)
                {
                    bindings[bindingOffset + index] = new DescriptorSetLayoutBinding
                    {
                        Binding = (uint)(index + 1),
                        DescriptorType = resources.Textures[index].IsStorage
                            ? DescriptorType.StorageImage
                            : DescriptorType.CombinedImageSampler,
                        DescriptorCount = 1,
                        StageFlags = stageFlags,
                    };
                }

                fixed (DescriptorSetLayoutBinding* bindingPointer = bindings)
                {
                    var descriptorInfo = new DescriptorSetLayoutCreateInfo
                    {
                        SType = StructureType.DescriptorSetLayoutCreateInfo,
                        BindingCount = (uint)bindings.Length,
                        PBindings = bindingPointer,
                    };
                    Check(
                        _vk.CreateDescriptorSetLayout(
                            _device,
                            &descriptorInfo,
                            null,
                            out descriptorSetLayout),
                        "vkCreateDescriptorSetLayout");
                }
            }

            var pipelineInfo = new PipelineLayoutCreateInfo
            {
                SType = StructureType.PipelineLayoutCreateInfo,
            };
            if (descriptorSetLayout.Handle != 0)
            {
                pipelineInfo.SetLayoutCount = 1;
                pipelineInfo.PSetLayouts = &descriptorSetLayout;
            }

            PipelineLayout pipelineLayout;
            Check(
                _vk.CreatePipelineLayout(
                    _device,
                    &pipelineInfo,
                    null,
                    out pipelineLayout),
                "vkCreatePipelineLayout");
            var created = new DescriptorLayoutBundle(descriptorSetLayout, pipelineLayout);
            _descriptorLayouts.Add(key, created);
            return created;
        }

        private string GetShaderDigest(byte[] spirv)
        {
            if (_shaderDigests.TryGetValue(spirv, out var digest))
            {
                return digest;
            }

            digest = Convert.ToHexString(SHA256.HashData(spirv));
            _shaderDigests.Add(spirv, digest);
            return digest;
        }

        private static string GetResourceLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            key.Append(resources.GlobalMemoryBuffers.Length).Append(':');
            foreach (var texture in resources.Textures)
            {
                key.Append(texture.IsStorage ? 'S' : 'T');
            }

            return key.ToString();
        }

        private static string GetVertexLayoutKey(TranslatedDrawResources resources)
        {
            var key = new StringBuilder();
            foreach (var buffer in resources.VertexBuffers)
            {
                key.Append(buffer.Location).Append(',')
                    .Append(buffer.ComponentCount).Append(',')
                    .Append(buffer.DataFormat).Append(',')
                    .Append(buffer.NumberFormat).Append(',')
                    .Append(buffer.Stride == 0
                        ? Math.Max(buffer.ComponentCount, 1) * sizeof(float)
                        : buffer.Stride)
                    .Append(';');
            }

            return key.ToString();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void CreateComputePipeline(
            TranslatedDrawResources resources,
            byte[] computeSpirv)
        {
            if (_computePipelines.TryGetValue(computeSpirv, out var cachedPipeline))
            {
                resources.Pipeline = cachedPipeline;
                resources.PipelineCached = true;
                return;
            }

            var computeModule = CreateShaderModule(computeSpirv);
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var stage = new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo,
                    Stage = ShaderStageFlags.ComputeBit,
                    Module = computeModule,
                    PName = entryPoint,
                };
                var pipelineInfo = new ComputePipelineCreateInfo
                {
                    SType = StructureType.ComputePipelineCreateInfo,
                    Flags = PipelineCreateFlags.CreateDispatchBaseBit,
                    Stage = stage,
                    Layout = resources.PipelineLayout,
                };
                Pipeline pipeline;
                Check(
                    _vk.CreateComputePipelines(
                        _device,
                        _pipelineCache,
                        1,
                        &pipelineInfo,
                        null,
                        out pipeline),
                    "vkCreateComputePipelines(translated)");
                resources.Pipeline = pipeline;
                resources.PipelineCached = true;
                SetDebugName(
                    ObjectType.Pipeline,
                    pipeline.Handle,
                    $"SharpEmu compute cs={computeSpirv.Length}b");
                _computePipelines.Add(computeSpirv, pipeline);
            }
            finally
            {
                SilkMarshal.Free((nint)entryPoint);
                _vk.DestroyShaderModule(_device, computeModule, null);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveTextureResource(GuestDrawTexture texture)
        {
            if (texture.IsStorage)
            {
                return ResolveStorageImageResource(texture);
            }

            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            if (texture.Address != 0 &&
                _guestImages.TryGetValue(texture.Address, out var guestImage) &&
                IsCompatibleGuestImageAlias(texture, guestImage) &&
                IsCompatibleViewFormat(guestImage.Format, vkFormat) &&
                TryGetOrCreateGuestImageView(
                    guestImage,
                    vkFormat,
                    mipLevel: 0,
                    levelCount: guestImage.MipLevels,
                    dstSelect: texture.DstSelect,
                    out var view))
            {
                if (ShouldTraceVulkanResources() &&
                    _tracedTextureCacheHits.Add(
                        (texture.Address, texture.Width, texture.Height, vkFormat)))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.texture_cache_hit addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} " +
                        $"image_format={guestImage.Format} view_format={vkFormat}");
                }

                if (guestImage.Width != texture.Width ||
                    guestImage.Height != texture.Height)
                {
                    TraceVulkanShader(
                        $"vk.texture_cache_alias addr=0x{texture.Address:X16} " +
                        $"texture={texture.Width}x{texture.Height} " +
                        $"image={guestImage.Width}x{guestImage.Height} " +
                        $"tile={texture.TileMode} format={vkFormat}");
                }

                if (string.Equals(
                        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES"),
                        "alias",
                        StringComparison.OrdinalIgnoreCase) &&
                    _tracedGuestImageContents.Add(guestImage.Address))
                {
                    // Deferred: reading back here would clobber the command
                    // buffer mid-recording; drained after the next present.
                    _pendingAliasImageDumps.Enqueue(guestImage);
                }

                if (TryCreateCpuTextureRefreshResource(texture, guestImage, view, out var refresh))
                {
                    return refresh;
                }

                return new TextureResource
                {
                    Address = texture.Address,
                    Image = guestImage.Image,
                    View = view,
                    Width = guestImage.Width,
                    Height = guestImage.Height,
                    RowLength = guestImage.Width,
                    DstSelect = texture.DstSelect,
                    SamplerState = texture.Sampler,
                    GuestImage = guestImage,
                };
            }

            return CreateTextureResource(texture);
        }

        private bool TryCreateCpuTextureRefreshResource(
            GuestDrawTexture texture,
            GuestImageResource guestImage,
            ImageView view,
            out TextureResource resource)
        {
            resource = default!;
            if (!guestImage.IsCpuBacked ||
                guestImage.Width != texture.Width ||
                guestImage.Height != texture.Height ||
                guestImage.MipLevels != 1 ||
                texture.RgbaPixels.Length == 0)
            {
                return false;
            }

            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, texture.Width)
                : texture.Width;
            var expectedSize = GetTextureByteCount(texture.Format, rowLength, texture.Height);
            if (expectedSize == 0 || expectedSize > int.MaxValue)
            {
                return false;
            }

            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, texture.Height, expectedSize);
            var fingerprint = ComputeTextureContentFingerprint(pixels);
            if ((guestImage.Initialized || guestImage.InitialUploadPending) &&
                guestImage.CpuContentFingerprint == fingerprint)
            {
                return false;
            }

            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var debugName = TextureDebugName(texture, guestImage.Format);
            var (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                uploadPixels,
                $"{debugName} refresh staging");
            TraceVulkanShader(
                $"vk.texture_refresh addr=0x{texture.Address:X16} " +
                $"size={texture.Width}x{texture.Height} bytes={uploadPixels.Length}");
            resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = guestImage.Image,
                View = view,
                Width = guestImage.Width,
                Height = guestImage.Height,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
                CpuContentFingerprint = fingerprint,
                UpdatesCpuContent = true,
            };
            return true;
        }

        private static bool IsCompatibleGuestImageAlias(
            GuestDrawTexture texture,
            GuestImageResource guestImage)
        {
            if (guestImage.Width == texture.Width &&
                guestImage.Height == texture.Height)
            {
                return true;
            }

            if (texture.TileMode == 0 ||
                texture.Width == 0 ||
                texture.Height == 0)
            {
                return false;
            }

            return texture.Width <= guestImage.Width &&
                texture.Height <= guestImage.Height;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private TextureResource ResolveStorageImageResource(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                return CreateStorageScratchResource(texture);
            }

            var guestImage = ResolveStorageGuestImage(texture);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var view = GetOrCreateGuestImageView(
                guestImage,
                vkFormat,
                texture.MipLevel,
                levelCount: 1);
            var resource = new TextureResource
            {
                Address = texture.Address,
                Image = guestImage.Image,
                View = view,
                Width = guestImage.Width,
                Height = guestImage.Height,
                RowLength = guestImage.Width,
                DstSelect = texture.DstSelect,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };

            if (!guestImage.Initialized &&
                !guestImage.InitialUploadPending &&
                texture.MipLevel == 0)
            {
                var expectedSize = GetTextureByteCount(
                    texture.Format,
                    texture.Width,
                    texture.Height);
                if ((ulong)texture.RgbaPixels.Length == expectedSize &&
                    texture.RgbaPixels.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
                {
                    resource.StagingBuffer = CreateBuffer(
                        expectedSize,
                        BufferUsageFlags.TransferSrcBit,
                        MemoryPropertyFlags.HostVisibleBit |
                        MemoryPropertyFlags.HostCoherentBit,
                        out resource.StagingMemory);

                    void* mapped;
                    Check(
                        _vk.MapMemory(
                            _device,
                            resource.StagingMemory,
                            0,
                            expectedSize,
                            0,
                            &mapped),
                        "vkMapMemory(storage texture)");
                    fixed (byte* source = texture.RgbaPixels)
                    {
                        System.Buffer.MemoryCopy(
                            source,
                            mapped,
                            texture.RgbaPixels.Length,
                            texture.RgbaPixels.Length);
                    }

                    _vk.UnmapMemory(_device, resource.StagingMemory);
                    resource.NeedsUpload = true;
                    guestImage.InitialUploadPending = true;
                    TraceVulkanShader(
                        $"vk.storage_upload addr=0x{texture.Address:X16} " +
                        $"size={texture.Width}x{texture.Height} bytes={expectedSize}");
                }
            }

            return resource;
        }

        private TextureResource CreateStorageScratchResource(GuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(
                _vk.CreateImage(_device, &imageInfo, null, out var image),
                "vkCreateImage(storage scratch)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &allocationInfo, null, out var memory),
                "vkAllocateMemory(storage scratch)");
            Check(
                _vk.BindImageMemory(_device, image, memory, 0),
                "vkBindImageMemory(storage scratch)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = vkFormat,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(storage scratch)");
            SetDebugName(ObjectType.Image, image.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat}");
            SetDebugName(ObjectType.ImageView, view.Handle, $"SharpEmu scratch storage {width}x{height} {vkFormat} view");

            var guestImage = new GuestImageResource
            {
                Address = 0,
                Width = width,
                Height = height,
                MipLevels = 1,
                Format = vkFormat,
                Image = image,
                Memory = memory,
                View = view,
            };

            return new TextureResource
            {
                Address = 0,
                Image = image,
                ImageMemory = memory,
                View = view,
                Width = width,
                Height = height,
                RowLength = width,
                DstSelect = texture.DstSelect,
                OwnsStorage = true,
                IsStorage = true,
                SamplerState = texture.Sampler,
                GuestImage = guestImage,
            };
        }

        private GuestImageResource ResolveStorageGuestImage(GuestDrawTexture texture)
        {
            if (texture.Address == 0)
            {
                throw new InvalidOperationException("Storage image has no guest address.");
            }

            var format = GetTextureFormat(texture.Format, texture.NumberType);
            var guestImage = GetOrCreateGuestImage(
                new GuestRenderTarget(
                    texture.Address,
                    texture.Width,
                    texture.Height,
                    texture.Format,
                    texture.NumberType,
                    texture.MipLevels),
                format);
            if (texture.MipLevel >= guestImage.MipLevels)
            {
                throw new InvalidOperationException(
                    $"Storage mip {texture.MipLevel} exceeds image mip count {guestImage.MipLevels}.");
            }

            return guestImage;
        }

        private TextureResource CreateTextureResource(GuestDrawTexture texture)
        {
            var width = Math.Max(texture.Width, 1);
            var height = Math.Max(texture.Height, 1);
            var rowLength = texture.TileMode == 0
                ? Math.Max(texture.Pitch, width)
                : width;
            var vkFormat = GetTextureFormat(texture.Format, texture.NumberType);

            var expectedSize = GetTextureByteCount(texture.Format, rowLength, height);
            if (_tracedTextureUploads.Add((texture.Address, width, height, vkFormat)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.texture addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} vk={vkFormat} " +
                    $"size={width}x{height} row={rowLength} tile={texture.TileMode} " +
                    $"dst=0x{texture.DstSelect:X3} " +
                    $"bytes={texture.RgbaPixels.Length} expected={expectedSize}");
            }
            var pixels = texture.RgbaPixels.Length == (int)expectedSize
                ? texture.RgbaPixels
                : CreateFallbackTexturePixels(texture.Format, rowLength, height, expectedSize);
            DumpTextureUpload(texture, pixels, rowLength, width, height);
            var uploadPixels = texture.Format == 13
                ? ExpandRgb32Pixels(pixels)
                : pixels;
            var contentFingerprint = ComputeTextureContentFingerprint(pixels);

            var (stagingBuffer, stagingMemory) = CreateTextureStagingBuffer(
                uploadPixels,
                $"{TextureDebugName(texture, vkFormat)} staging");

            var supportsAttachmentUsage = !IsBlockCompressedFormat(vkFormat);
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags = supportsAttachmentUsage
                    ? ImageCreateFlags.CreateMutableFormatBit | ImageCreateFlags.CreateExtendedUsageBit
                    : 0,
                ImageType = ImageType.Type2D,
                Format = vkFormat,
                Extent = new Extent3D(width, height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage = supportsAttachmentUsage
                    ? ImageUsageFlags.TransferDstBit |
                      ImageUsageFlags.SampledBit |
                      ImageUsageFlags.ColorAttachmentBit |
                      ImageUsageFlags.StorageBit |
                      ImageUsageFlags.TransferSrcBit
                    : ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(texture)");
            _vk.GetImageMemoryRequirements(_device, image, out var imageRequirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = imageRequirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    imageRequirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out var imageMemory), "vkAllocateMemory(texture)");
            Check(_vk.BindImageMemory(_device, image, imageMemory, 0), "vkBindImageMemory(texture)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = vkFormat,
                Components = ToVkComponentMapping(texture.DstSelect),
                SubresourceRange = ColorSubresourceRange(),
            };
            Check(_vk.CreateImageView(_device, &viewInfo, null, out var view), "vkCreateImageView(texture)");
            var debugName = TextureDebugName(texture, vkFormat);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            var resource = new TextureResource
            {
                Address = texture.Address,
                StagingBuffer = stagingBuffer,
                StagingMemory = stagingMemory,
                Image = image,
                ImageMemory = imageMemory,
                View = view,
                Width = width,
                Height = height,
                RowLength = rowLength,
                DstSelect = texture.DstSelect,
                NeedsUpload = true,
                OwnsStorage = true,
                SamplerState = texture.Sampler,
                CpuContentFingerprint = contentFingerprint,
                UpdatesCpuContent = texture.Address != 0,
            };

            if (texture.Address != 0 &&
                !_guestImages.ContainsKey(texture.Address))
            {
                var guestImage = new GuestImageResource
                {
                    Address = texture.Address,
                    Width = width,
                    Height = height,
                    MipLevels = 1,
                    Format = vkFormat,
                    Image = image,
                    Memory = imageMemory,
                    View = view,
                    InitialUploadPending = true,
                    IsCpuBacked = true,
                    CpuContentFingerprint = contentFingerprint,
                };
                _guestImages.Add(texture.Address, guestImage);
                resource.OwnsStorage = false;
                resource.GuestImage = guestImage;
                lock (_gate)
                {
                    var guestFormat = VulkanVideoPresenter.GetGuestTextureFormat(
                        texture.Format,
                        texture.NumberType);
                    if (guestFormat != 0)
                    {
                        _availableGuestImages[texture.Address] = guestFormat;
                        _gpuGuestImages.Remove(texture.Address);
                    }
                }
            }

            return resource;
        }

        private (VkBuffer Buffer, DeviceMemory Memory) CreateTextureStagingBuffer(
            byte[] pixels,
            string debugName)
        {
            var size = (ulong)pixels.Length;
            var buffer = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory(texture)");
            fixed (byte* source = pixels)
            {
                System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
            }
            _vk.UnmapMemory(_device, memory);
            SetDebugName(ObjectType.Buffer, buffer.Handle, debugName);
            return (buffer, memory);
        }

        private static ulong ComputeTextureContentFingerprint(ReadOnlySpan<byte> pixels)
        {
            const ulong offsetBasis = 14695981039346656037;
            const ulong prime = 1099511628211;

            var h0 = offsetBasis ^ (ulong)pixels.Length;
            var h1 = offsetBasis;
            var h2 = offsetBasis;
            var h3 = offsetBasis;

            var words = MemoryMarshal.Cast<byte, ulong>(pixels);
            var index = 0;
            var blockEnd = words.Length - (words.Length & 3);
            for (; index < blockEnd; index += 4)
            {
                h0 = (h0 ^ words[index]) * prime;
                h1 = (h1 ^ words[index + 1]) * prime;
                h2 = (h2 ^ words[index + 2]) * prime;
                h3 = (h3 ^ words[index + 3]) * prime;
            }

            for (; index < words.Length; index++)
            {
                h0 = (h0 ^ words[index]) * prime;
            }

            var hash = h0;
            hash = (hash ^ h1) * prime;
            hash = (hash ^ h2) * prime;
            hash = (hash ^ h3) * prime;

            foreach (var value in pixels[(words.Length * sizeof(ulong))..])
            {
                hash = (hash ^ value) * prime;
            }

            return hash;
        }

        private void DumpTextureUpload(
            GuestDrawTexture texture,
            byte[] pixels,
            uint rowLength,
            uint width,
            uint height)
        {
            if (!_dumpTextures ||
                texture.IsFallback ||
                texture.IsStorage ||
                GetTextureBytesPerPixel(texture.Format) != 4 ||
                width == 0 ||
                height == 0 ||
                !_dumpedTextures.Add((texture.Address, width, height, texture.Format)))
            {
                return;
            }

            var rowBytes = checked((int)rowLength * 4);
            var visibleRowBytes = checked((int)width * 4);
            if (pixels.Length < checked(rowBytes * (int)height))
            {
                return;
            }

            var directory = Path.Combine(AppContext.BaseDirectory, "texture-dumps");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(
                directory,
                $"tex-{texture.Address:X16}-{width}x{height}-fmt{texture.Format}-row{rowLength}.bmp");
            WriteRgbaBmp(path, pixels, rowBytes, visibleRowBytes, (int)width, (int)height);
        }

        private static void WriteRgbaBmp(
            string path,
            byte[] rgba,
            int sourceRowBytes,
            int visibleRowBytes,
            int width,
            int height)
        {
            const int fileHeaderSize = 14;
            const int infoHeaderSize = 40;
            const int bytesPerPixel = 4;
            var pixelBytes = checked(width * height * bytesPerPixel);
            var fileSize = fileHeaderSize + infoHeaderSize + pixelBytes;
            var output = new byte[fileSize];

            output[0] = (byte)'B';
            output[1] = (byte)'M';
            WriteUInt32(output, 2, (uint)fileSize);
            WriteUInt32(output, 10, fileHeaderSize + infoHeaderSize);
            WriteUInt32(output, 14, infoHeaderSize);
            WriteInt32(output, 18, width);
            WriteInt32(output, 22, -height);
            WriteUInt16(output, 26, 1);
            WriteUInt16(output, 28, 32);
            WriteUInt32(output, 34, (uint)pixelBytes);

            var destinationOffset = fileHeaderSize + infoHeaderSize;
            for (var y = 0; y < height; y++)
            {
                var sourceOffset = y * sourceRowBytes;
                for (var x = 0; x < visibleRowBytes; x += bytesPerPixel)
                {
                    var destination = destinationOffset + y * visibleRowBytes + x;
                    output[destination + 0] = rgba[sourceOffset + x + 2];
                    output[destination + 1] = rgba[sourceOffset + x + 1];
                    output[destination + 2] = rgba[sourceOffset + x + 0];
                    output[destination + 3] = rgba[sourceOffset + x + 3];
                }
            }

            File.WriteAllBytes(path, output);
        }

        private static void WriteUInt16(byte[] output, int offset, ushort value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
        }

        private static void WriteUInt32(byte[] output, int offset, uint value)
        {
            output[offset + 0] = (byte)value;
            output[offset + 1] = (byte)(value >> 8);
            output[offset + 2] = (byte)(value >> 16);
            output[offset + 3] = (byte)(value >> 24);
        }

        private static void WriteInt32(byte[] output, int offset, int value) =>
            WriteUInt32(output, offset, unchecked((uint)value));

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
            if (dstSelect == 0)
            {
                dstSelect = 0xFAC;
            }

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

        private GlobalBufferResource CreateGlobalBufferResource(
            GuestMemoryBuffer guestBuffer)
        {
            var buffer = CreateHostBuffer(
                guestBuffer.Data,
                BufferUsageFlags.StorageBufferBit,
                out var memory);
            var size = (ulong)Math.Max(guestBuffer.Data.Length, sizeof(uint));

            if (ShouldTraceVulkanResources() &&
                _tracedGlobalBuffers.Add((guestBuffer.BaseAddress, guestBuffer.Data.Length)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.global_buffer base=0x{guestBuffer.BaseAddress:X16} " +
                    $"bytes={guestBuffer.Data.Length}");
            }
            SetDebugName(
                ObjectType.Buffer,
                buffer.Handle,
                $"SharpEmu global 0x{guestBuffer.BaseAddress:X16} {guestBuffer.Data.Length}b");

            return new GlobalBufferResource
            {
                Buffer = buffer,
                Memory = memory,
                Size = size,
            };
        }

        private VertexBufferResource CreateVertexBufferResource(
            GuestVertexBuffer guestBuffer)
        {
            var buffer = CreateHostBuffer(
                guestBuffer.Data,
                BufferUsageFlags.VertexBufferBit,
                out var memory);
            var size = (ulong)Math.Max(guestBuffer.Data.Length, sizeof(uint));
            SetDebugName(
                ObjectType.Buffer,
                buffer.Handle,
                $"SharpEmu vertex loc{guestBuffer.Location} " +
                $"0x{guestBuffer.BaseAddress:X16} {guestBuffer.Data.Length}b");
            if (_tracedVertexBufferCount++ < 64)
            {
                TraceVulkanShader(
                    $"vk.vertex_buffer loc={guestBuffer.Location} " +
                    $"base=0x{guestBuffer.BaseAddress:X16} stride={guestBuffer.Stride} " +
                    $"offset={guestBuffer.OffsetBytes} comps={guestBuffer.ComponentCount} " +
                    $"fmt={guestBuffer.DataFormat}/num={guestBuffer.NumberFormat} " +
                    $"bytes={guestBuffer.Data.Length}");
            }

            return new VertexBufferResource
            {
                Buffer = buffer,
                Memory = memory,
                Size = size,
                Location = guestBuffer.Location,
                ComponentCount = guestBuffer.ComponentCount,
                DataFormat = guestBuffer.DataFormat,
                NumberFormat = guestBuffer.NumberFormat,
                Stride = guestBuffer.Stride,
                OffsetBytes = guestBuffer.OffsetBytes,
            };
        }

        private VkBuffer CreateHostBuffer(
            ReadOnlySpan<byte> data,
            BufferUsageFlags usage,
            out DeviceMemory memory)
        {
            var size = (ulong)Math.Max(data.Length, sizeof(uint));
            var capacity = BitOperations.RoundUpToPowerOf2(size);
            var key = new HostBufferPoolKey(usage, capacity);
            if (!_hostBufferPool.TryGetValue(key, out var available))
            {
                available = new Stack<HostBufferAllocation>();
                _hostBufferPool.Add(key, available);
            }

            HostBufferAllocation allocation;
            if (available.TryPop(out var pooled))
            {
                allocation = pooled;
            }
            else
            {
                var buffer = CreateBuffer(
                    capacity,
                    usage,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                    out var allocatedMemory);
                allocation = new HostBufferAllocation(buffer, allocatedMemory, key);
                _hostBufferAllocations.Add(buffer.Handle, allocation);
            }

            memory = allocation.Memory;
            void* mapped;
            Check(_vk.MapMemory(_device, memory, 0, size, 0, &mapped), "vkMapMemory(host)");
            try
            {
                fixed (byte* source = data)
                {
                    System.Buffer.MemoryCopy(
                        source,
                        mapped,
                        checked((long)size),
                        data.Length);
                }
            }
            finally
            {
                _vk.UnmapMemory(_device, memory);
            }

            return allocation.Buffer;
        }

        private void RecycleHostBuffer(VkBuffer buffer, DeviceMemory memory)
        {
            if (buffer.Handle == 0)
            {
                return;
            }

            if (_hostBufferAllocations.TryGetValue(buffer.Handle, out var allocation) &&
                allocation.Memory.Handle == memory.Handle)
            {
                _hostBufferPool[allocation.Key].Push(allocation);
                return;
            }

            _vk.DestroyBuffer(_device, buffer, null);
            if (memory.Handle != 0)
            {
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private static PrimitiveTopology GetPrimitiveTopology(uint primitiveType) =>
            primitiveType switch
            {
                1 => PrimitiveTopology.PointList,
                2 => PrimitiveTopology.LineList,
                3 => PrimitiveTopology.LineStrip,
                5 => PrimitiveTopology.TriangleFan,
                6 => PrimitiveTopology.TriangleStrip,
                GuestPrimitiveRectList => PrimitiveTopology.TriangleStrip,
                _ => PrimitiveTopology.TriangleList,
            };

        private static Format ToVkVertexFormat(
            uint dataFormat,
            uint numberFormat,
            uint componentCount) =>
            (dataFormat, numberFormat) switch
            {
                (1, 0) => Format.R8Unorm,
                (1, 1) => Format.R8SNorm,
                (1, 4) => Format.R8Uint,
                (1, 5) => Format.R8Sint,
                (1, 9) => Format.R8Srgb,
                (2, 0) => Format.R16Unorm,
                (2, 1) => Format.R16SNorm,
                (2, 4) => Format.R16Uint,
                (2, 5) => Format.R16Sint,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (3, 1) => Format.R8G8SNorm,
                (3, 4) => Format.R8G8Uint,
                (3, 5) => Format.R8G8Sint,
                (3, 9) => Format.R8G8Srgb,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 1) => Format.R16G16SNorm,
                (5, 2) => Format.R16G16Uscaled,
                (5, 3) => Format.R16G16Sscaled,
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
                (9, 0) => Format.A2R10G10B10UnormPack32,
                (9, 1) => Format.A2R10G10B10SNormPack32,
                (9, 2) => Format.A2R10G10B10UscaledPack32,
                (9, 3) => Format.A2R10G10B10SscaledPack32,
                (9, 4) => Format.A2R10G10B10UintPack32,
                (9, 5) => Format.A2R10G10B10SintPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 1) => Format.R8G8B8A8SNorm,
                (10, 2) => Format.R8G8B8A8Uscaled,
                (10, 3) => Format.R8G8B8A8Sscaled,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
                (10, 9) => Format.R8G8B8A8Srgb,
                (11, 4) => Format.R32G32Uint,
                (11, 5) => Format.R32G32Sint,
                (11, 7) => Format.R32G32Sfloat,
                (12, 0) => Format.R16G16B16A16Unorm,
                (12, 1) => Format.R16G16B16A16SNorm,
                (12, 2) => Format.R16G16B16A16Uscaled,
                (12, 3) => Format.R16G16B16A16Sscaled,
                (12, 4) => Format.R16G16B16A16Uint,
                (12, 5) => Format.R16G16B16A16Sint,
                (12, 6) => Format.R16G16B16A16SNorm,
                (12, 7) => Format.R16G16B16A16Sfloat,
                (13, 4) => Format.R32G32B32Uint,
                (13, 5) => Format.R32G32B32Sint,
                (13, 7) => Format.R32G32B32Sfloat,
                (14, 4) => Format.R32G32B32A32Uint,
                (14, 5) => Format.R32G32B32A32Sint,
                (14, 7) => Format.R32G32B32A32Sfloat,
                (16, 0) => Format.B5G6R5UnormPack16,
                (17, 0) => Format.R5G5B5A1UnormPack16,
                (19, 0) => Format.R4G4B4A4UnormPack16,
                (34, 7) => Format.E5B9G9R9UfloatPack32,
                _ => ToVkFloatVertexFormat(componentCount),
            };

        private static Format ToVkFloatVertexFormat(uint componentCount) =>
            componentCount switch
            {
                1 => Format.R32Sfloat,
                2 => Format.R32G32Sfloat,
                3 => Format.R32G32B32Sfloat,
                4 => Format.R32G32B32A32Sfloat,
                _ => Format.R32Sfloat,
            };

        private static ulong GetVertexBindingOffset(VertexBufferResource vertexBuffer)
        {
            if (vertexBuffer.OffsetBytes < vertexBuffer.Size)
            {
                return vertexBuffer.OffsetBytes;
            }

            TraceVulkanShader(
                $"vk.vertex_offset_oob loc={vertexBuffer.Location} " +
                $"offset={vertexBuffer.OffsetBytes} size={vertexBuffer.Size}");
            return 0;
        }

        private static uint GetDrawVertexCount(
            uint primitiveType,
            uint vertexCount,
            GuestIndexBuffer? indexBuffer)
        {
            if (primitiveType == GuestPrimitiveRectList && indexBuffer is null)
            {
                return 4;
            }

            return vertexCount;
        }

        private static BlendFactor ToVkBlendFactor(uint factor) =>
            factor switch
            {
                0 => BlendFactor.Zero,
                1 => BlendFactor.One,
                2 => BlendFactor.SrcColor,
                3 => BlendFactor.OneMinusSrcColor,
                4 => BlendFactor.SrcAlpha,
                5 => BlendFactor.OneMinusSrcAlpha,
                6 => BlendFactor.DstAlpha,
                7 => BlendFactor.OneMinusDstAlpha,
                8 => BlendFactor.DstColor,
                9 => BlendFactor.OneMinusDstColor,
                10 => BlendFactor.SrcAlphaSaturate,
                13 => BlendFactor.ConstantColor,
                14 => BlendFactor.OneMinusConstantColor,
                15 => BlendFactor.Src1Color,
                16 => BlendFactor.OneMinusSrc1Color,
                17 => BlendFactor.Src1Alpha,
                18 => BlendFactor.OneMinusSrc1Alpha,
                19 => BlendFactor.ConstantAlpha,
                20 => BlendFactor.OneMinusConstantAlpha,
                _ => BlendFactor.One,
            };

        private static BlendOp ToVkBlendOp(uint function) =>
            function switch
            {
                0 => BlendOp.Add,
                1 => BlendOp.Subtract,
                2 => BlendOp.Min,
                3 => BlendOp.Max,
                4 => BlendOp.ReverseSubtract,
                _ => BlendOp.Add,
            };

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

        private static ColorComponentFlags ToVkColorWriteMask(uint mask)
        {
            var flags = default(ColorComponentFlags);
            if ((mask & 1u) != 0)
            {
                flags |= ColorComponentFlags.RBit;
            }

            if ((mask & 2u) != 0)
            {
                flags |= ColorComponentFlags.GBit;
            }

            if ((mask & 4u) != 0)
            {
                flags |= ColorComponentFlags.BBit;
            }

            if ((mask & 8u) != 0)
            {
                flags |= ColorComponentFlags.ABit;
            }

            return flags;
        }

        private static GuestRect ClampScissor(GuestRect? scissor, Extent2D extent)
        {
            if (scissor is not { } rect)
            {
                return new GuestRect(0, 0, extent.Width, extent.Height);
            }

            var left = Math.Clamp(rect.X, 0, checked((int)extent.Width));
            var top = Math.Clamp(rect.Y, 0, checked((int)extent.Height));
            var right = Math.Clamp(
                rect.X + checked((int)rect.Width),
                left,
                checked((int)extent.Width));
            var bottom = Math.Clamp(
                rect.Y + checked((int)rect.Height),
                top,
                checked((int)extent.Height));
            return new GuestRect(
                left,
                top,
                checked((uint)(right - left)),
                checked((uint)(bottom - top)));
        }

        private static readonly float ViewportDebugEpsilon = float.TryParse(
            Environment.GetEnvironmentVariable("SHARPEMU_VIEWPORT_EPSILON"),
            System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture,
            out var viewportEpsilon)
            ? viewportEpsilon
            : 0f;

        private static Viewport ClampViewport(GuestViewport? viewport, Extent2D extent)
        {
            if (viewport is not { } rect)
            {
                return new Viewport(0, 0, extent.Width, extent.Height, 0, 1);
            }

            // Do NOT trim the rectangle to the render target: Vulkan allows
            // viewports that extend beyond the framebuffer (rendering is
            // confined by the scissor), and trimming changes the guest's
            // scale and offset. That skews texel addressing on 1:1 draws -
            // source rows get skipped or duplicated - which shredded the
            // game's pre-composed tile surfaces. Only guard what the spec
            // requires: a positive width and hardware viewport bounds.
            const float bound = 32767f;
            var x = Math.Clamp(rect.X, -bound, bound);
            var y = Math.Clamp(rect.Y, -bound, bound);
            var width = Math.Clamp(rect.Width, 1e-3f, bound);
            var height = Math.Clamp(rect.Height, -bound, bound);
            if (height == 0f)
            {
                height = extent.Height;
            }

            var minDepth = Math.Clamp(rect.MinDepth, 0f, 1f);
            var maxDepth = Math.Clamp(rect.MaxDepth, minDepth, 1f);
            return new Viewport(x, y, width, height, minDepth, maxDepth);
        }

        private static byte[] CreateFallbackTexturePixels(uint format, uint width, uint height, ulong expectedSize)
        {
            if (format is 9 or 10 or 56 or 62 or 64)
            {
                return CreateBlackFrame(width, height);
            }

            return new byte[checked((int)expectedSize)];
        }

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
                20 => 4UL,
                22 => 8UL,
                29 => 4UL,
                36 => 1UL,
                49 => 1UL,
                56 => 4UL,
                62 => 4UL,
                64 => 4UL,
                71 => 8UL,
                _ => 4UL,
            };

        private static ulong GetTextureByteCount(uint format, uint width, uint height)
        {
            var blockBytes = format switch
            {
                169 or 170 => 8UL,
                171 or 172 or 173 or 174 or 175 or 176 or
                177 or 178 or 179 or 180 or 181 or 182 => 16UL,
                _ => 0UL,
            };
            return blockBytes == 0
                ? checked((ulong)width * height * GetTextureBytesPerPixel(format))
                : checked(((ulong)width + 3) / 4 * (((ulong)height + 3) / 4) * blockBytes);
        }

        private bool SupportsColorAttachment(Format format)
        {
            _vk.GetPhysicalDeviceFormatProperties(_physicalDevice, format, out var properties);
            return (properties.OptimalTilingFeatures & FormatFeatureFlags.ColorAttachmentBit) != 0;
        }

        private static Format GetTextureFormat(uint format, uint numberType) =>
            (format, numberType) switch
            {
                (9, _) => Format.A2R10G10B10UnormPack32,
                (GuestFormatR32Uint, _) => Format.R32Uint,
                (GuestFormatR32Sint, _) => Format.R32Sint,
                (GuestFormatR32Sfloat, _) => Format.R32Sfloat,
                (GuestFormatR16G16Uint, _) => Format.R16G16Uint,
                (GuestFormatR16G16Sint, _) => Format.R16G16Sint,
                (GuestFormatR16G16Sfloat, _) => Format.R16G16Sfloat,
                (GuestFormatR8G8B8A8Uint, _) => Format.R8G8B8A8Uint,
                (GuestFormatR8G8B8A8Sint, _) => Format.R8G8B8A8Sint,
                (GuestFormatR16G16B16A16Uint, _) => Format.R16G16B16A16Uint,
                (GuestFormatR16G16B16A16Sint, _) => Format.R16G16B16A16Sint,
                (1, 0) => Format.R8Unorm,
                (2, 7) => Format.R16Sfloat,
                (3, 0) => Format.R8G8Unorm,
                (4, 4) => Format.R32Uint,
                (4, 5) => Format.R32Sint,
                (4, 7) => Format.R32Sfloat,
                (5, 0) => Format.R16G16Unorm,
                (5, 4) => Format.R16G16Uint,
                (5, 5) => Format.R16G16Sint,
                (5, 7) => Format.R16G16Sfloat,
                (6, 7) => Format.B10G11R11UfloatPack32,
                (10, 0) => Format.R8G8B8A8Unorm,
                (10, 4) => Format.R8G8B8A8Uint,
                (10, 5) => Format.R8G8B8A8Sint,
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
                (20, _) => Format.R32Uint,
                (4, _) => Format.R32Sfloat,
                (5, _) => Format.R16G16Sfloat,
                (7, _) => Format.B10G11R11UfloatPack32,
                (14, _) => Format.R32G32B32A32Sfloat,
                (22, _) => Format.R16G16B16A16Sfloat,
                (29, _) => Format.R32Sfloat,
                (36, _) => Format.R8Unorm,
                (49, _) => Format.R8Uint,
                (56, _) => Format.R8G8B8A8Unorm,
                (62, _) => Format.R8G8B8A8Unorm,
                (64, _) => Format.R8G8B8A8Unorm,
                (71, _) => Format.R16G16B16A16Sfloat,
                (75, _) => Format.R32G32Sfloat,
                (169, _) => Format.BC1RgbaUnormBlock,
                (170, _) => Format.BC1RgbaSrgbBlock,
                (181, _) => Format.BC7UnormBlock,
                (182, _) => Format.BC7SrgbBlock,
                _ => Format.R8G8B8A8Unorm,
            };

        private static bool IsBlockCompressedFormat(Format format) =>
            format is Format.BC1RgbaUnormBlock or
                Format.BC1RgbaSrgbBlock or
                Format.BC7UnormBlock or
                Format.BC7SrgbBlock;

        private VkBuffer CreateBuffer(
            ulong size,
            BufferUsageFlags usage,
            MemoryPropertyFlags memoryFlags,
            out DeviceMemory memory)
        {
            var bufferInfo = new BufferCreateInfo
            {
                SType = StructureType.BufferCreateInfo,
                Size = size,
                Usage = usage,
                SharingMode = SharingMode.Exclusive,
            };
            Check(_vk.CreateBuffer(_device, &bufferInfo, null, out var buffer), "vkCreateBuffer");

            _vk.GetBufferMemoryRequirements(_device, buffer, out var requirements);
            var memoryInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(requirements.MemoryTypeBits, memoryFlags),
            };
            Check(_vk.AllocateMemory(_device, &memoryInfo, null, out memory), "vkAllocateMemory");
            Check(_vk.BindBufferMemory(_device, buffer, memory, 0), "vkBindBufferMemory");
            return buffer;
        }

        private void CreateStagingBuffer(ulong size)
        {
            _stagingBuffer = CreateBuffer(
                size,
                BufferUsageFlags.TransferSrcBit | BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _stagingMemory);
            _stagingSize = size;
        }

        private uint FindMemoryType(uint typeBits, MemoryPropertyFlags requiredFlags)
        {
            _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var properties);
            var memoryTypes = &properties.MemoryTypes.Element0;
            for (uint index = 0; index < properties.MemoryTypeCount; index++)
            {
                if ((typeBits & (1u << (int)index)) != 0 &&
                    (memoryTypes[index].PropertyFlags & requiredFlags) == requiredFlags)
                {
                    return index;
                }
            }

            throw new InvalidOperationException("No compatible Vulkan host-visible memory type was found.");
        }

        private const uint MaxComputeZSlicesPerSubmission = 8;

        private void ExecuteComputeDispatch(VulkanComputeGuestDispatch work)
        {
            if (_deviceLost)
            {
                return;
            }

            if (AddressListContains("SHARPEMU_SKIP_COMPUTE_CS", work.ShaderAddress))
            {
                TraceVulkanShader(
                    $"vk.compute_skip cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count}");
                return;
            }

            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            try
            {
                EnsureGuestSubmissionCapacity();
                resources = CreateComputeDispatchResources(work);

                var batchCount = Math.Max(
                    1u,
                    (uint)Math.Ceiling(work.GroupCountZ / (double)MaxComputeZSlicesPerSubmission));

                for (var batchIndex = 0u; batchIndex < batchCount; batchIndex++)
                {
                    var zStart = batchIndex * MaxComputeZSlicesPerSubmission;
                    var zCount = Math.Min(MaxComputeZSlicesPerSubmission, work.GroupCountZ - zStart);
                    var isFirstBatch = batchIndex == 0;
                    var isLastBatch = batchIndex == batchCount - 1;

                    commandBuffer = AllocateGuestCommandBuffer();
                    _commandBuffer = commandBuffer;
                    var beginInfo = new CommandBufferBeginInfo
                    {
                        SType = StructureType.CommandBufferBeginInfo,
                        Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                    };
                    Check(
                        _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                        "vkBeginCommandBuffer(compute)");

                    BeginDebugLabel(_commandBuffer, resources.DebugName);
                    if (isFirstBatch)
                    {
                        RecordTextureUploads(resources, PipelineStageFlags.ComputeShaderBit);
                        RecordStorageImagesForWrite(resources, PipelineStageFlags.ComputeShaderBit);
                    }

                    _vk.CmdBindPipeline(
                        _commandBuffer,
                        PipelineBindPoint.Compute,
                        resources.Pipeline);
                    if (resources.DescriptorSet.Handle != 0)
                    {
                        var descriptorSet = resources.DescriptorSet;
                        _vk.CmdBindDescriptorSets(
                            _commandBuffer,
                            PipelineBindPoint.Compute,
                            resources.PipelineLayout,
                            0,
                            1,
                            &descriptorSet,
                            0,
                            null);
                    }

                    RecordChunkedComputeDispatch(_commandBuffer, work, zStart, zCount);

                    if (isLastBatch)
                    {
                        RecordStorageImagesForRead(resources, PipelineStageFlags.ComputeShaderBit);
                    }

                    EndDebugLabel(_commandBuffer);
                    Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(compute)");

                    TraceVulkanShader(
                        $"vk.compute_submit cs=0x{work.ShaderAddress:X16} " +
                        $"batch={batchIndex}/{batchCount} z={zStart}..{zStart + zCount}");
                    if (isLastBatch)
                    {
                        SubmitGuestCommandBuffer(
                            commandBuffer,
                            resources,
                            GetTraceImages(resources));
                        submitted = true;
                    }
                    else
                    {
                        SubmitGuestCommandBufferAndWait(commandBuffer);
                        commandBuffer = default;
                    }
                }

                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);
                TraceVulkanShader(
                    $"vk.compute_dispatch groups={work.GroupCountX}x" +
                    $"{work.GroupCountY}x{work.GroupCountZ} " +
                    $"textures={work.Textures.Count} cs=0x{work.ShaderAddress:X16} " +
                    $"batches={batchCount}");
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan compute dispatch failed " +
                    $"cs=0x{work.ShaderAddress:X16}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted && commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                if (!submitted && resources is not null)
                {
                    DestroyTranslatedDrawResources(resources);
                }
            }
        }

        private void RecordChunkedComputeDispatch(
            CommandBuffer commandBuffer,
            VulkanComputeGuestDispatch work,
            uint zStart,
            uint zCount)
        {
            const uint maxWorkgroupsPerCommand = 4096;
            var yChunk = Math.Max(
                1u,
                Math.Min(
                    work.GroupCountY,
                    maxWorkgroupsPerCommand / Math.Max(work.GroupCountX, 1u)));
            var commandCount = 0u;

            for (var z = zStart; z < zStart + zCount; z++)
            {
                for (var y = 0u; y < work.GroupCountY; y += yChunk)
                {
                    var countY = Math.Min(yChunk, work.GroupCountY - y);
                    _vk.CmdDispatchBase(
                        commandBuffer,
                        0,
                        y,
                        z,
                        work.GroupCountX,
                        countY,
                        1);
                    commandCount++;
                }
            }

            if (commandCount > 1)
            {
                TraceVulkanShader(
                    $"vk.compute_chunked cs=0x{work.ShaderAddress:X16} " +
                    $"groups={work.GroupCountX}x{work.GroupCountY}x{work.GroupCountZ} " +
                    $"z_range={zStart}..{zStart + zCount} commands={commandCount} y_chunk={yChunk}");
            }
        }

        private void ExecuteOffscreenDraw(VulkanOffscreenGuestDraw work)
        {
            if (_deviceLost || work.Targets.Count == 0)
            {
                return;
            }

            if (work.Targets.Count > _maxColorAttachments)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped MRT draw requesting {work.Targets.Count} color attachments; " +
                    $"the selected device supports {_maxColorAttachments}.");
                return;
            }

            var targetFormats = new VulkanRenderTargetFormat[work.Targets.Count];
            for (var index = 0; index < targetFormats.Length; index++)
            {
                var target = work.Targets[index];
                if (!TryDecodeRenderTargetFormat(target.Format, target.NumberType, out targetFormats[index]) ||
                    !SupportsColorAttachment(targetFormats[index].Format))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Vulkan skipped MRT draw with unsupported color target " +
                        $"format={target.Format} number_type={target.NumberType}.");
                    return;
                }
            }

            if (work.Draw.RenderState.Blends.Count != targetFormats.Length)
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw with mismatched attachment/blend counts.");
                return;
            }

            if (targetFormats
                .Select((format, index) => format.IsInteger && work.Draw.RenderState.Blends[index].Enable)
                .Any(invalid => invalid))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw with blending enabled for an integer attachment.");
                return;
            }

            if (!_supportsIndependentBlend &&
                work.Draw.RenderState.Blends.Skip(1).Any(blend => blend != work.Draw.RenderState.Blends[0]))
            {
                Console.Error.WriteLine(
                    "[LOADER][WARN] Vulkan skipped MRT draw requiring unsupported independentBlend.");
                return;
            }

            var formats = targetFormats.Select(target => target.Format).ToArray();

            var targetAddresses = work.Targets
                .Where(target => target.Address != 0)
                .Select(target => target.Address)
                .ToHashSet();
            if (work.Draw.Textures.Any(texture =>
                    targetAddresses.Contains(texture.Address)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Vulkan skipped render-target feedback loop " +
                    $"targets={string.Join(',', targetAddresses.Select(address => $"0x{address:X16}"))}");
                return;
            }

            var targets = new GuestImageResource[work.Targets.Count];
            EnsureGuestSubmissionCapacity();
            for (var index = 0; index < targets.Length; index++)
            {
                targets[index] = GetOrCreateGuestImage(work.Targets[index], formats[index]);
            }

            var firstTarget = targets[0];
            TranslatedDrawResources? resources = null;
            CommandBuffer commandBuffer = default;
            var submitted = false;
            RenderPass transientRenderPass = default;
            Framebuffer transientFramebuffer = default;
            try
            {
                var extent = new Extent2D(firstTarget.Width, firstTarget.Height);
                var renderPass = firstTarget.RenderPass;
                var framebuffer = firstTarget.Framebuffer;
                if (targets.Length > 1)
                {
                    (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(
                        formats,
                        targets.Select(target => target.MipViews[0]).ToArray(),
                        firstTarget.Width,
                        firstTarget.Height);
                    transientRenderPass = renderPass;
                    transientFramebuffer = framebuffer;
                }

                resources = CreateTranslatedDrawResources(
                    work.Draw,
                    renderPass,
                    formats,
                    extent);
                resources.TransientRenderPass = transientRenderPass;
                resources.TransientFramebuffer = transientFramebuffer;
                transientRenderPass = default;
                transientFramebuffer = default;
                resources.DebugName =
                    $"SharpEmu offscreen mrt={targets.Length} " +
                    $"first=0x{work.Targets[0].Address:X16} " +
                    $"{firstTarget.Width}x{firstTarget.Height}";

                commandBuffer = AllocateGuestCommandBuffer();
                _commandBuffer = commandBuffer;
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(offscreen)");

                BeginDebugLabel(_commandBuffer, resources.DebugName);
                RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
                RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);

                var toColorAttachments = stackalloc ImageMemoryBarrier[targets.Length];
                var anyPriorContents = false;
                for (var index = 0; index < targets.Length; index++)
                {
                    var hasPriorContents = targets[index].Initialized || targets[index].InitialUploadPending;
                    anyPriorContents |= hasPriorContents;
                    toColorAttachments[index] = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = hasPriorContents ? AccessFlags.ShaderReadBit : 0,
                        DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
                        OldLayout = hasPriorContents
                            ? ImageLayout.ShaderReadOnlyOptimal
                            : ImageLayout.Undefined,
                        NewLayout = ImageLayout.ColorAttachmentOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = targets[index].Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                }
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    anyPriorContents
                        ? PipelineStageFlags.AllCommandsBit
                        : PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    (uint)targets.Length,
                    toColorAttachments);

                RecordTranslatedGraphicsPass(
                    resources,
                    renderPass,
                    framebuffer,
                    extent);
                RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);

                var toShaderRead = stackalloc ImageMemoryBarrier[targets.Length];
                for (var index = 0; index < targets.Length; index++)
                {
                    toShaderRead[index] = new ImageMemoryBarrier
                    {
                        SType = StructureType.ImageMemoryBarrier,
                        SrcAccessMask = AccessFlags.ColorAttachmentWriteBit,
                        DstAccessMask = AccessFlags.ShaderReadBit,
                        OldLayout = ImageLayout.ColorAttachmentOptimal,
                        NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                        SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                        Image = targets[index].Image,
                        SubresourceRange = ColorSubresourceRange(),
                    };
                }
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.ColorAttachmentOutputBit,
                    PipelineStageFlags.FragmentShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    (uint)targets.Length,
                    toShaderRead);
                EndDebugLabel(_commandBuffer);

                Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer(offscreen)");
                SubmitGuestCommandBuffer(
                    commandBuffer,
                    resources,
                    GetTraceImages(resources, targets));
                submitted = true;
                foreach (var target in targets)
                {
                    target.Initialized = true;
                }
                MarkSampledImagesInitialized(resources);
                MarkStorageImagesInitialized(resources, traceContents: false);

                if (work.PublishTarget)
                {
                    for (var index = 0; index < targets.Length; index++)
                    {
                        var guestTextureFormat = VulkanVideoPresenter.GetGuestTextureFormat(
                            work.Targets[index].Format,
                            work.Targets[index].NumberType);
                        if (guestTextureFormat == 0)
                        {
                            continue;
                        }

                        lock (_gate)
                        {
                            _availableGuestImages[targets[index].Address] = guestTextureFormat;
                            _gpuGuestImages[targets[index].Address] = guestTextureFormat;
                        }
                    }
                }
                foreach (var target in targets)
                {
                    var traceSmallWrites =
                        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_WRITES") == "small" &&
                        target.Width <= 512 && target.Height <= 256;
                    if (ShouldTraceGuestImageWriteForDiagnostics(target.Address) || traceSmallWrites)
                    {
                        var writeCount = _tracedGuestWriteCounts.TryGetValue(
                            target.Address,
                            out var previousCount)
                            ? previousCount + 1
                            : 1;
                        _tracedGuestWriteCounts[target.Address] = writeCount;
                        if (writeCount <= (traceSmallWrites ? 48 : 3))
                        {
                            _commandBuffer = _presentationCommandBuffer;
                            Check(
                                _vk.QueueWaitIdle(_queue),
                                "vkQueueWaitIdle(guest write trace)");
                            Console.Error.WriteLine(
                                $"[LOADER][TRACE] vk.guest_write_sample " +
                                $"addr=0x{target.Address:X16} write={writeCount} " +
                                $"ps_bytes={work.Draw.PixelSpirv.Length}");
                            TraceGuestImageContents(target);
                        }
                    }
                }
                TraceVulkanShader(
                    $"vk.offscreen_draw mrt={targets.Length} " +
                    $"size={firstTarget.Width}x{firstTarget.Height} " +
                    $"textures={work.Draw.Textures.Count}");
            }
            catch (Exception exception)
            {
                if (TryMarkDeviceLost(exception))
                {
                    return;
                }

                lock (_gate)
                {
                    foreach (var target in work.Targets)
                    {
                        if (!_guestImages.TryGetValue(target.Address, out var failedTarget) ||
                            !failedTarget.Initialized)
                        {
                            _availableGuestImages.Remove(target.Address);
                            _gpuGuestImages.Remove(target.Address);
                        }
                    }
                }

                Console.Error.WriteLine(
                    $"[LOADER][ERROR] Vulkan offscreen draw failed " +
                    $"mrt={work.Targets.Count}: {exception.Message}");
            }
            finally
            {
                _commandBuffer = _presentationCommandBuffer;
                if (!submitted && commandBuffer.Handle != 0)
                {
                    _vk.FreeCommandBuffers(
                        _device,
                        _commandPool,
                        1,
                        &commandBuffer);
                }

                if (!submitted && resources is not null)
                {
                    DestroyTranslatedDrawResources(resources);
                }

                if (transientFramebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, transientFramebuffer, null);
                }

                if (transientRenderPass.Handle != 0)
                {
                    _vk.DestroyRenderPass(_device, transientRenderPass, null);
                }
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private GuestImageResource GetOrCreateGuestImage(
            GuestRenderTarget target,
            Format format)
        {
            var mipLevels = ClampMipLevels(target.Width, target.Height, target.MipLevels);
            if (_guestImages.TryGetValue(target.Address, out var existing))
            {
                if (existing.Width == target.Width &&
                    existing.Height == target.Height &&
                    existing.MipLevels == mipLevels &&
                    existing.Format == format)
                {
                    existing.IsCpuBacked = false;
                    existing.CpuContentFingerprint = 0;
                    if (existing.RenderPass.Handle == 0)
                    {
                        var attachmentView = existing.MipViews.Length > 0
                            ? existing.MipViews[0]
                            : existing.View;
                        var (promotedRenderPass, promotedFramebuffer) = CreateRenderPassAndFramebuffer(
                            existing.Format,
                            attachmentView,
                            existing.Width,
                            existing.Height);
                        existing.RenderPass = promotedRenderPass;
                        existing.Framebuffer = promotedFramebuffer;
                        var promotedName = GuestImageDebugName(target, format);
                        SetDebugName(ObjectType.RenderPass, promotedRenderPass.Handle, $"{promotedName} renderpass");
                        SetDebugName(ObjectType.Framebuffer, promotedFramebuffer.Handle, $"{promotedName} framebuffer");
                    }

                    return existing;
                }

                CompletePendingPresentation(wait: true);
                WaitForAllGuestSubmissions();
                DestroyGuestImage(existing);
                _guestImages.Remove(target.Address);
                lock (_gate)
                {
                    _availableGuestImages.Remove(target.Address);
                    _gpuGuestImages.Remove(target.Address);
                }
            }

            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                Flags =
                    ImageCreateFlags.CreateMutableFormatBit |
                    ImageCreateFlags.CreateExtendedUsageBit,
                ImageType = ImageType.Type2D,
                Format = format,
                Extent = new Extent3D(target.Width, target.Height, 1),
                MipLevels = mipLevels,
                ArrayLayers = 1,
                Samples = SampleCountFlags.Count1Bit,
                Tiling = ImageTiling.Optimal,
                Usage =
                    ImageUsageFlags.ColorAttachmentBit |
                    ImageUsageFlags.SampledBit |
                    ImageUsageFlags.StorageBit |
                    ImageUsageFlags.TransferSrcBit |
                    ImageUsageFlags.TransferDstBit,
                SharingMode = SharingMode.Exclusive,
                InitialLayout = ImageLayout.Undefined,
            };
            Check(_vk.CreateImage(_device, &imageInfo, null, out var image), "vkCreateImage(offscreen)");
            _vk.GetImageMemoryRequirements(_device, image, out var requirements);
            var allocationInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = requirements.Size,
                MemoryTypeIndex = FindMemoryType(
                    requirements.MemoryTypeBits,
                    MemoryPropertyFlags.DeviceLocalBit),
            };
            Check(
                _vk.AllocateMemory(_device, &allocationInfo, null, out var memory),
                "vkAllocateMemory(offscreen)");
            Check(_vk.BindImageMemory(_device, image, memory, 0), "vkBindImageMemory(offscreen)");

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = new ComponentMapping(
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity,
                    ComponentSwizzle.Identity),
                SubresourceRange = ColorSubresourceRange(0, mipLevels),
            };
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out var view),
                "vkCreateImageView(offscreen)");

            var mipViews = new ImageView[mipLevels];
            for (uint mipLevel = 0; mipLevel < mipLevels; mipLevel++)
            {
                viewInfo.SubresourceRange = ColorSubresourceRange(mipLevel, 1);
                ImageView mipView;
                Check(
                    _vk.CreateImageView(
                        _device,
                        &viewInfo,
                        null,
                        out mipView),
                    "vkCreateImageView(offscreen mip)");
                mipViews[mipLevel] = mipView;
            }

            var (renderPass, framebuffer) = CreateRenderPassAndFramebuffer(
                format,
                mipViews[0],
                target.Width,
                target.Height);

            var resource = new GuestImageResource
            {
                Address = target.Address,
                Width = target.Width,
                Height = target.Height,
                MipLevels = mipLevels,
                Format = format,
                Image = image,
                Memory = memory,
                View = view,
                MipViews = mipViews,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
            };
            var debugName = GuestImageDebugName(target, format);
            SetDebugName(ObjectType.Image, image.Handle, $"{debugName} image");
            SetDebugName(ObjectType.ImageView, view.Handle, $"{debugName} view");
            for (var mipLevel = 0; mipLevel < mipViews.Length; mipLevel++)
            {
                SetDebugName(
                    ObjectType.ImageView,
                    mipViews[mipLevel].Handle,
                    $"{debugName} mip{mipLevel}");
            }
            SetDebugName(ObjectType.RenderPass, renderPass.Handle, $"{debugName} renderpass");
            SetDebugName(ObjectType.Framebuffer, framebuffer.Handle, $"{debugName} framebuffer");
            _guestImages.Add(target.Address, resource);
            return resource;
        }

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            Format format,
            ImageView attachmentView,
            uint width,
            uint height) =>
            CreateRenderPassAndFramebuffer([format], [attachmentView], width, height);

        private (RenderPass RenderPass, Framebuffer Framebuffer) CreateRenderPassAndFramebuffer(
            IReadOnlyList<Format> formats,
            IReadOnlyList<ImageView> attachmentViews,
            uint width,
            uint height)
        {
            if (formats.Count == 0 || formats.Count != attachmentViews.Count)
            {
                throw new InvalidOperationException("render target formats and views must have matching counts");
            }

            var colorAttachments = stackalloc AttachmentDescription[formats.Count];
            var colorReferences = stackalloc AttachmentReference[formats.Count];
            var views = stackalloc ImageView[formats.Count];
            for (var index = 0; index < formats.Count; index++)
            {
                colorAttachments[index] = new AttachmentDescription
                {
                    Format = formats[index],
                    Samples = SampleCountFlags.Count1Bit,
                    LoadOp = AttachmentLoadOp.Load,
                    StoreOp = AttachmentStoreOp.Store,
                    StencilLoadOp = AttachmentLoadOp.DontCare,
                    StencilStoreOp = AttachmentStoreOp.DontCare,
                    InitialLayout = ImageLayout.ColorAttachmentOptimal,
                    FinalLayout = ImageLayout.ColorAttachmentOptimal,
                };
                colorReferences[index] = new AttachmentReference
                {
                    Attachment = (uint)index,
                    Layout = ImageLayout.ColorAttachmentOptimal,
                };
                views[index] = attachmentViews[index];
            }

            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics,
                ColorAttachmentCount = (uint)formats.Count,
                PColorAttachments = colorReferences,
            };
            var renderPassInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo,
                AttachmentCount = (uint)formats.Count,
                PAttachments = colorAttachments,
                SubpassCount = 1,
                PSubpasses = &subpass,
            };
            Check(
                _vk.CreateRenderPass(_device, &renderPassInfo, null, out var renderPass),
                "vkCreateRenderPass(offscreen)");

            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = renderPass,
                AttachmentCount = (uint)formats.Count,
                PAttachments = views,
                Width = width,
                Height = height,
                Layers = 1,
            };
            Check(
                _vk.CreateFramebuffer(_device, &framebufferInfo, null, out var framebuffer),
                "vkCreateFramebuffer(offscreen)");

            return (renderPass, framebuffer);
        }

        private static uint ClampMipLevels(uint width, uint height, uint requestedMipLevels)
        {
            var largestDimension = Math.Max(width, height);
            uint maximumMipLevels = 1;
            while (largestDimension > 1)
            {
                largestDimension >>= 1;
                maximumMipLevels++;
            }

            return Math.Min(Math.Max(requestedMipLevels, 1u), maximumMipLevels);
        }

        private void DestroyGuestImage(GuestImageResource resource)
        {
            foreach (var view in resource.FormatViews.Values)
            {
                if (view.Handle != 0)
                {
                    _vk.DestroyImageView(_device, view, null);
                }
            }
            resource.FormatViews.Clear();

            if (resource.Framebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resource.Framebuffer, null);
            }

            if (resource.RenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resource.RenderPass, null);
            }

            if (resource.View.Handle != 0)
            {
                _vk.DestroyImageView(_device, resource.View, null);
            }

            foreach (var mipView in resource.MipViews)
            {
                if (mipView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, mipView, null);
                }
            }

            if (resource.Image.Handle != 0)
            {
                _vk.DestroyImage(_device, resource.Image, null);
            }

            if (resource.Memory.Handle != 0)
            {
                _vk.FreeMemory(_device, resource.Memory, null);
            }
        }

        private static uint GetGuestTextureFormat(Format format) =>
            format switch
            {
                Format.A2R10G10B10UnormPack32 => 9,
                Format.R8G8B8A8Unorm => 56,
                Format.R16G16Unorm => 5,
                Format.R16G16B16A16Unorm => 12,
                Format.R32Uint => GuestFormatR32Uint,
                Format.R32Sint => GuestFormatR32Sint,
                Format.R32Sfloat => GuestFormatR32Sfloat,
                Format.R16G16Uint => GuestFormatR16G16Uint,
                Format.R16G16Sint => GuestFormatR16G16Sint,
                Format.R16G16Sfloat => GuestFormatR16G16Sfloat,
                Format.R8G8B8A8Uint => GuestFormatR8G8B8A8Uint,
                Format.R8G8B8A8Sint => GuestFormatR8G8B8A8Sint,
                Format.R16G16B16A16Uint => GuestFormatR16G16B16A16Uint,
                Format.R16G16B16A16Sint => GuestFormatR16G16B16A16Sint,
                Format.R16G16B16A16Sfloat => 71,
                _ => 0,
            };

        private bool TryGetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect,
            out ImageView view)
        {
            try
            {
                view = GetOrCreateGuestImageView(resource, format, mipLevel, levelCount, dstSelect);
                return true;
            }
            catch (Exception exception)
            {
                view = default;
                TraceVulkanShader(
                    $"vk.texture_alias_view_failed addr=0x{resource.Address:X16} " +
                    $"image_format={resource.Format} view_format={format}: {exception.Message}");
                return false;
            }
        }

        private ImageView GetOrCreateGuestImageView(
            GuestImageResource resource,
            Format format,
            uint mipLevel,
            uint levelCount,
            uint dstSelect = 0xFAC)
        {
            if (mipLevel >= resource.MipLevels)
            {
                throw new InvalidOperationException(
                    $"View mip {mipLevel} exceeds image mip count {resource.MipLevels}.");
            }

            levelCount = Math.Max(levelCount, 1);
            levelCount = Math.Min(levelCount, resource.MipLevels - mipLevel);
            if (format == resource.Format && dstSelect == 0xFAC)
            {
                if (mipLevel == 0 && levelCount == resource.MipLevels)
                {
                    return resource.View;
                }

                if (levelCount == 1)
                {
                    return resource.MipViews[mipLevel];
                }
            }

            if (!IsCompatibleViewFormat(resource.Format, format))
            {
                throw new InvalidOperationException(
                    $"Incompatible image view format {format} for image {resource.Format}.");
            }

            var key = (format, mipLevel, levelCount, dstSelect);
            if (resource.FormatViews.TryGetValue(key, out var existing))
            {
                return existing;
            }

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = resource.Image,
                ViewType = ImageViewType.Type2D,
                Format = format,
                Components = ToVkComponentMapping(dstSelect),
                SubresourceRange = ColorSubresourceRange(mipLevel, levelCount),
            };
            ImageView view;
            Check(
                _vk.CreateImageView(_device, &viewInfo, null, out view),
                "vkCreateImageView(guest alias)");
            resource.FormatViews.Add(key, view);
            SetDebugName(
                ObjectType.ImageView,
                view.Handle,
                $"SharpEmu guest 0x{resource.Address:X16} alias {format} mip{mipLevel}+{levelCount}");
            TraceVulkanShader(
                $"vk.texture_alias_view addr=0x{resource.Address:X16} " +
                $"image_format={resource.Format} view_format={format} " +
                $"mip={mipLevel} levels={levelCount} dst=0x{dstSelect:X3}");
            return view;
        }

        private static bool IsCompatibleViewFormat(Format imageFormat, Format viewFormat)
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
                Format.R8Uint or
                Format.R8Sint => 8,
                Format.R16Sfloat => 16,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.R16G16Unorm or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Unorm or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.A2R10G10B10UnormPack32 or
                Format.B10G11R11UfloatPack32 => 32,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat or
                Format.R16G16B16A16Unorm or
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 64,
                Format.R32G32B32Sfloat => 96,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 128,
                _ => 0,
            };

        private void UpdatePerformanceHud()
        {
            if (!_performanceHudEnabled)
            {
                return;
            }

            var now = Stopwatch.GetTimestamp();
            if (_performanceHudLastTimestamp != 0 &&
                Stopwatch.GetElapsedTime(_performanceHudLastTimestamp, now).TotalSeconds <
                    PerformanceHudSampleSeconds)
            {
                return;
            }

            try
            {
                using var process = Process.GetCurrentProcess();
                var processCpu = process.TotalProcessorTime;
                var currentThreadCpu = new Dictionary<int, TimeSpan>();
                var currentThreadIds = new HashSet<int>();
                var hottestThreadId = 0;
                var hottestThreadCpuSeconds = 0.0;

                // Per-thread CPU times and thread names come from Windows-only
                // APIs; on POSIX the HUD reports process totals with an "idle"
                // hottest-thread slot.
                if (OperatingSystem.IsWindows())
                {
                    foreach (ProcessThread thread in process.Threads)
                    {
                        using (thread)
                        {
                            try
                            {
                                var threadId = thread.Id;
                                var cpu = thread.TotalProcessorTime;
                                currentThreadIds.Add(threadId);
                                currentThreadCpu[threadId] = cpu;
                                if (_performanceHudThreadCpu.TryGetValue(threadId, out var previousCpu))
                                {
                                    var deltaSeconds = Math.Max(0.0, (cpu - previousCpu).TotalSeconds);
                                    if (deltaSeconds > hottestThreadCpuSeconds)
                                    {
                                        hottestThreadCpuSeconds = deltaSeconds;
                                        hottestThreadId = threadId;
                                    }
                                }
                            }
                            catch (InvalidOperationException)
                            {
                            }
                        }
                    }
                }

                if (_performanceHudLastTimestamp != 0)
                {
                    var elapsedSeconds = Math.Max(
                        Stopwatch.GetElapsedTime(_performanceHudLastTimestamp, now).TotalSeconds,
                        0.001);
                    var processCpuPercent = Math.Max(
                        0.0,
                        (processCpu - _performanceHudLastProcessCpu).TotalSeconds /
                            elapsedSeconds /
                            Math.Max(Environment.ProcessorCount, 1) *
                            100.0);
                    var hottestThreadPercent = hottestThreadCpuSeconds / elapsedSeconds * 100.0;
                    var presentedFrames = _performanceHudPresentedFrames;
                    var fps = (presentedFrames - _performanceHudLastPresentedFrames) / elapsedSeconds;
                    var hotName = hottestThreadId == 0
                        ? "idle"
                        : GetPerformanceThreadName(hottestThreadId);
                    long guestBacklog;
                    int queuedGuestWork;
                    lock (_gate)
                    {
                        guestBacklog = Math.Max(
                            0,
                            _enqueuedGuestWorkSequence - _completedGuestWorkSequence);
                        queuedGuestWork = _pendingGuestWork.Count;
                    }

                    var gpuInFlight = _pendingGuestSubmissions.Count +
                        (_presentationInFlight ? 1 : 0);
                    var readCount = Interlocked.Read(
                        ref Gen5ShaderScalarEvaluator.GlobalMemoryReadCount);
                    var readBytes = Interlocked.Read(
                        ref Gen5ShaderScalarEvaluator.GlobalMemoryReadBytes);
                    var readHits = Interlocked.Read(
                        ref Gen5ShaderScalarEvaluator.GlobalMemoryReadCacheHits);
                    var readPvmBytes = Interlocked.Read(
                        ref Gen5ShaderScalarEvaluator.GlobalMemoryReadPvmBytes);
                    var readLibcBytes = Interlocked.Read(
                        ref Gen5ShaderScalarEvaluator.GlobalMemoryReadLibcBytes);
                    var readsPerSecond =
                        (readCount - _performanceHudLastReadCount) / elapsedSeconds;
                    var readMbPerSecond =
                        (readBytes - _performanceHudLastReadBytes) /
                        elapsedSeconds /
                        (1024.0 * 1024.0);
                    var readHitsPerSecond =
                        (readHits - _performanceHudLastReadHits) / elapsedSeconds;
                    var readPvmMbPerSecond =
                        (readPvmBytes - _performanceHudLastReadPvmBytes) /
                        elapsedSeconds /
                        (1024.0 * 1024.0);
                    var readLibcMbPerSecond =
                        (readLibcBytes - _performanceHudLastReadLibcBytes) /
                        elapsedSeconds /
                        (1024.0 * 1024.0);
                    _performanceHudLastReadCount = readCount;
                    _performanceHudLastReadBytes = readBytes;
                    _performanceHudLastReadHits = readHits;
                    _performanceHudLastReadPvmBytes = readPvmBytes;
                    _performanceHudLastReadLibcBytes = readLibcBytes;
                    _window.Title =
                        $"FPS {fps:0.0} CPU {processCpuPercent:0}% | " +
                        $"HOT {hotName}#{hottestThreadId} {hottestThreadPercent:0}% | " +
                        $"WORK {guestBacklog} (q{queuedGuestWork}/gpu{gpuInFlight}) | " +
                        $"RD {readsPerSecond:0}/s {readMbPerSecond:0}MB/s h{readHitsPerSecond:0}/s " +
                        $"P{readPvmMbPerSecond:0} L{readLibcMbPerSecond:0} | " +
                        VideoOutExports.GetWindowTitle();
                    _performanceHudLastPresentedFrames = presentedFrames;
                }

                _performanceHudThreadCpu.Clear();
                foreach (var (threadId, cpu) in currentThreadCpu)
                {
                    _performanceHudThreadCpu[threadId] = cpu;
                }

                foreach (var staleThreadId in _performanceHudThreadNames.Keys
                             .Where(threadId => !currentThreadIds.Contains(threadId))
                             .ToArray())
                {
                    _performanceHudThreadNames.Remove(staleThreadId);
                }

                _performanceHudLastProcessCpu = processCpu;
                _performanceHudLastTimestamp = now;
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                _performanceHudLastTimestamp = now;
            }
        }

        private string GetPerformanceThreadName(int threadId)
        {
            if (_performanceHudThreadNames.TryGetValue(threadId, out var cached))
            {
                return cached;
            }

            var name = "tid";
            var handle = OpenThread(ThreadQueryLimitedInformation, false, (uint)threadId);
            if (handle != 0)
            {
                try
                {
                    if (GetThreadDescription(handle, out var description) >= 0 && description != 0)
                    {
                        try
                        {
                            var described = Marshal.PtrToStringUni(description);
                            if (!string.IsNullOrWhiteSpace(described))
                            {
                                name = described.Length <= 28 ? described : described[..28];
                            }
                        }
                        finally
                        {
                            LocalFree(description);
                        }
                    }
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            _performanceHudThreadNames[threadId] = name;
            return name;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern nint OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

        [DllImport("kernel32.dll")]
        private static extern int GetThreadDescription(nint thread, out nint description);

        [DllImport("kernel32.dll")]
        private static extern nint LocalFree(nint memory);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);

        private void WaitForRenderWork()
        {
            var gpuWorkInFlight = _pendingGuestSubmissions.Count > 0 || _presentationInFlight;
            lock (_gate)
            {
                if (_closed ||
                    _pendingGuestWork.Count > 0 ||
                    (_latestPresentation is { } latest &&
                     latest.Sequence != _presentedSequence &&
                     latest.RequiredGuestWorkSequence <= _completedGuestWorkSequence))
                {
                    return;
                }

                System.Threading.Monitor.Wait(_gate, gpuWorkInFlight ? 1 : 8);
            }
        }

        private void Render(double _)
        {
            if (Volatile.Read(ref _presenterCloseRequested))
            {
                _window.Close();
                return;
            }

            if (!_vulkanReady)
            {
                return;
            }

            WaitForRenderWork();
            UpdatePerformanceHud();

            _commandBuffer = _presentationCommandBuffer;
            if (!_deviceLost)
            {
                CollectCompletedGuestSubmissions(waitForOldest: false);
            }

            var completedWork = 0;
            while (completedWork < MaxGuestWorkPerRender &&
                   TryTakeGuestWork(out var work))
            {
                try
                {
                    switch (work)
                    {
                        case VulkanOffscreenGuestDraw offscreenDraw:
                            ExecuteOffscreenDraw(offscreenDraw);
                            break;
                        case VulkanComputeGuestDispatch computeDispatch:
                            ExecuteComputeDispatch(computeDispatch);
                            break;
                    }
                }
                finally
                {
                    CompleteGuestWork();
                }

                completedWork++;
            }

            if (!TryTakePresentation(_presentedSequence, out var presentation))
            {
                return;
            }

            if (presentation.Pixels is null &&
                presentation.DrawKind != GuestDrawKind.FullscreenBarycentric &&
                presentation.TranslatedDraw is null &&
                presentation.GuestImageAddress == 0)
            {
                return;
            }

            CompletePendingPresentation(wait: true);

            byte[]? pixels = null;
            if (presentation.Pixels is { } sourcePixels)
            {
                pixels = presentation.Width == _extent.Width && presentation.Height == _extent.Height
                    ? sourcePixels
                    : ScaleBgra(
                        sourcePixels,
                        presentation.Width,
                        presentation.Height,
                        _extent.Width,
                        _extent.Height);
                if ((ulong)pixels.Length > _stagingSize)
                {
                    return;
                }
            }

            TranslatedDrawResources? translatedResources = null;
            GuestImageResource? presentedGuestImage = null;
            var tracePresentedGuestImage = false;
            if (presentation.GuestImageAddress != 0 &&
                (!_guestImages.TryGetValue(
                    presentation.GuestImageAddress,
                    out presentedGuestImage) ||
                 !presentedGuestImage.Initialized))
            {
                return;
            }
            if (presentedGuestImage is not null)
            {
                _directPresentationCount++;
                if (ShouldSamplePresentedGuestImageForDiagnostics(
                        _directPresentationCount))
                {
                    tracePresentedGuestImage = true;
                    Console.Error.WriteLine(
                        $"[LOADER][TRACE] vk.present_sample frame={_directPresentationCount} " +
                        $"addr=0x{presentedGuestImage.Address:X16}");
                }
            }

            if (presentation.TranslatedDraw is { } translatedDraw)
            {
                try
                {
                    translatedResources = CreateTranslatedDrawResources(
                        translatedDraw,
                        _renderPass,
                        [_swapchainFormat],
                        _extent);
                    if (ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                        !_firstGuestDrawPresented)
                    {
                        Console.Error.WriteLine(
                            $"[LOADER][TRACE] vk.translated_draw kind={presentation.DrawKind} " +
                            $"textures={translatedResources.Textures.Length}");
                        foreach (var boundTexture in translatedResources.Textures)
                        {
                            if (boundTexture.GuestImage is { } guestImage &&
                                _tracedGuestImageContents.Add(guestImage.Address))
                            {
                                TraceGuestImageContents(guestImage);
                            }
                        }
                    }
                }
                catch (Exception exception)
                {
                    _presentedSequence = presentation.Sequence;
                    Console.Error.WriteLine(
                        $"[LOADER][ERROR] Vulkan VideoOut translated draw setup failed: {exception.Message}");
                    return;
                }
            }

            uint imageIndex;
            var acquireResult = _swapchainApi.AcquireNextImage(
                _device,
                _swapchain,
                ulong.MaxValue,
                _imageAvailable,
                default,
                &imageIndex);
            if (acquireResult == Result.ErrorOutOfDateKhr)
            {
                RecreateSwapchainResources("vkAcquireNextImageKHR", acquireResult);
                if (translatedResources is not null)
                {
                    DestroyTranslatedDrawResources(translatedResources);
                }

                return;
            }

            CheckSwapchainResult(acquireResult, "vkAcquireNextImageKHR");
            var recreateAfterPresent = acquireResult == Result.SuboptimalKhr;

            if (pixels is not null)
            {
                void* mapped;
                Check(
                    _vk.MapMemory(_device, _stagingMemory, 0, (ulong)pixels.Length, 0, &mapped),
                    "vkMapMemory");
                fixed (byte* source = pixels)
                {
                    System.Buffer.MemoryCopy(source, mapped, pixels.Length, pixels.Length);
                }
                _vk.UnmapMemory(_device, _stagingMemory);
            }

            Check(_vk.ResetCommandBuffer(_commandBuffer, 0), "vkResetCommandBuffer");
            var beginInfo = new CommandBufferBeginInfo
            {
                SType = StructureType.CommandBufferBeginInfo,
                Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
            };
            Check(_vk.BeginCommandBuffer(_commandBuffer, &beginInfo), "vkBeginCommandBuffer");

            PipelineStageFlags waitStage;
            if (pixels is not null)
            {
                RecordUpload(imageIndex);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (presentation.DrawKind == GuestDrawKind.FullscreenBarycentric)
            {
                var clearValue = default(ClearValue);
                var renderPassInfo = new RenderPassBeginInfo
                {
                    SType = StructureType.RenderPassBeginInfo,
                    RenderPass = _renderPass,
                    Framebuffer = _framebuffers[imageIndex],
                    RenderArea = new Rect2D(new Offset2D(0, 0), _extent),
                    ClearValueCount = 1,
                    PClearValues = &clearValue,
                };
                _vk.CmdBeginRenderPass(
                    _commandBuffer,
                    &renderPassInfo,
                    SubpassContents.Inline);
                _vk.CmdBindPipeline(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    _barycentricPipeline);
                _vk.CmdDraw(_commandBuffer, 3, 1, 0, 0);
                _vk.CmdEndRenderPass(_commandBuffer);
                waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
            }
            else if (presentedGuestImage is not null)
            {
                RecordGuestImageBlit(imageIndex, presentedGuestImage);
                waitStage = PipelineStageFlags.TransferBit;
            }
            else if (translatedResources is not null)
            {
                RecordTranslatedDraw(imageIndex, translatedResources);
                waitStage = PipelineStageFlags.AllCommandsBit;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unsupported translated guest draw: {presentation.DrawKind}.");
            }

            Check(_vk.EndCommandBuffer(_commandBuffer), "vkEndCommandBuffer");

            var imageAvailable = _imageAvailable;
            var commandBuffer = _commandBuffer;
            var renderFinished = _renderFinished;
            var submitInfo = new SubmitInfo
            {
                SType = StructureType.SubmitInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &imageAvailable,
                PWaitDstStageMask = &waitStage,
                CommandBufferCount = 1,
                PCommandBuffers = &commandBuffer,
                SignalSemaphoreCount = 1,
                PSignalSemaphores = &renderFinished,
            };
            var presentationFence = _presentationFence;
            Check(
                _vk.ResetFences(_device, 1, &presentationFence),
                "vkResetFences(presentation)");
            Check(
                _vk.QueueSubmit(_queue, 1, &submitInfo, presentationFence),
                "vkQueueSubmit");
            _presentationInFlight = true;
            _pendingPresentationResources = translatedResources;
            translatedResources = null;

            var swapchain = _swapchain;
            var presentInfo = new PresentInfoKHR
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &renderFinished,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            var presentResult = _swapchainApi.QueuePresent(_queue, &presentInfo);
            if (presentResult == Result.ErrorOutOfDateKhr)
            {
                Check(_vk.QueueWaitIdle(_queue), "vkQueueWaitIdle");
                CompletePendingPresentation(wait: false);
                CollectCompletedGuestSubmissions(waitForOldest: false);
                if (translatedResources is not null)
                {
                    DestroyTranslatedDrawResources(translatedResources);
                }

                RecreateSwapchainResources("vkQueuePresentKHR", presentResult);
                return;
            }

            CheckSwapchainResult(presentResult, "vkQueuePresentKHR");
            recreateAfterPresent |= presentResult == Result.SuboptimalKhr;
            VideoOutExports.ReportPresentedFrame();
            _performanceHudPresentedFrames++;
            if (_swapchainReadbackPending)
            {
                CompletePendingPresentation(wait: true);
                TraceSwapchainReadback();
            }
            // Report the actual presented pixels before starting the larger
            // source-image readback.  If a guest draw wedges the GPU, the
            // source probe can block in vkQueueWaitIdle; doing it first used
            // to hide whether the swapchain itself was black and made the
            // diagnostic run stop immediately after vk.present_sample.
            if (tracePresentedGuestImage && presentedGuestImage is not null)
            {
                TraceGuestImageContents(presentedGuestImage);
            }
            while (_pendingAliasImageDumps.TryDequeue(out var aliasImage))
            {
                TraceGuestImageContents(aliasImage);
            }
            CollectCompletedGuestSubmissions(waitForOldest: false);

            _imageInitialized[imageIndex] = true;
            _presentedSequence = presentation.Sequence;
            if (presentation.IsSplash && !_splashPresented)
            {
                _splashPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented splash: " +
                    $"{presentation.Width}x{presentation.Height}");
            }
            else if (!presentation.IsSplash && !_firstFramePresented)
            {
                _firstFramePresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented first frame: " +
                    $"{presentation.Width}x{presentation.Height}");
            }

            if (pixels is null && !_firstGuestDrawPresented)
            {
                _firstGuestDrawPresented = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan VideoOut presented guest frame: " +
                    (presentedGuestImage is not null
                        ? $"image=0x{presentedGuestImage.Address:X16} " +
                          $"{presentedGuestImage.Width}x{presentedGuestImage.Height}"
                        : presentation.TranslatedDraw is null
                        ? $"{presentation.DrawKind}"
                        : $"shader textures={presentation.TranslatedDraw.Textures.Count}"));
            }

            if (recreateAfterPresent)
            {
                RecreateSwapchainResources("present suboptimal", Result.SuboptimalKhr);
            }
        }

        private void TraceGuestImageContents(GuestImageResource image)
        {
            var bytesPerPixel = GetReadbackBytesPerPixel(image.Format);
            if (bytesPerPixel == 0)
            {
                Console.Error.WriteLine(
                    "[LOADER][TRACE] " +
                    $"vk.guest_image addr=0x{image.Address:X16} " +
                    $"format={image.Format} readback=unsupported");
                return;
            }

            var byteCount = checked((ulong)image.Width * image.Height * bytesPerPixel);
            var buffer = CreateBuffer(
                byteCount,
                BufferUsageFlags.TransferDstBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out var memory);
            try
            {
                Check(
                    _vk.ResetCommandBuffer(_commandBuffer, 0),
                    "vkResetCommandBuffer(guest readback)");
                var beginInfo = new CommandBufferBeginInfo
                {
                    SType = StructureType.CommandBufferBeginInfo,
                    Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
                };
                Check(
                    _vk.BeginCommandBuffer(_commandBuffer, &beginInfo),
                    "vkBeginCommandBuffer(guest readback)");

                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.ShaderReadBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var region = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(image.Width, image.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    image.Image,
                    ImageLayout.TransferSrcOptimal,
                    buffer,
                    1,
                    &region);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferReadBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferSrcOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = image.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.FragmentShaderBit |
                    PipelineStageFlags.ComputeShaderBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);

                Check(
                    _vk.EndCommandBuffer(_commandBuffer),
                    "vkEndCommandBuffer(guest readback)");
                var commandBuffer = _commandBuffer;
                var submitInfo = new SubmitInfo
                {
                    SType = StructureType.SubmitInfo,
                    CommandBufferCount = 1,
                    PCommandBuffers = &commandBuffer,
                };
                Check(
                    _vk.QueueSubmit(_queue, 1, &submitInfo, default),
                    "vkQueueSubmit(guest readback)");
                Check(
                    _vk.QueueWaitIdle(_queue),
                    "vkQueueWaitIdle(guest readback)");

                void* mapped;
                Check(
                    _vk.MapMemory(_device, memory, 0, byteCount, 0, &mapped),
                    "vkMapMemory(guest readback)");
                try
                {
                    var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                    var nonzeroBytes = 0L;
                    ulong hash = 14695981039346656037UL;
                    foreach (var value in bytes)
                    {
                        nonzeroBytes += value == 0 ? 0 : 1;
                        hash = (hash ^ value) * 1099511628211UL;
                    }

                    var nonblackPixels = CountNonblackPixels(
                        bytes,
                        image.Format,
                        bytesPerPixel);
                    var centerOffset = checked(
                        ((int)(image.Height / 2) * (int)image.Width +
                         (int)(image.Width / 2)) *
                        (int)bytesPerPixel);
                    var center = Convert.ToHexString(
                        bytes.Slice(centerOffset, (int)bytesPerPixel));
                    Console.Error.WriteLine(
                        "[LOADER][TRACE] " +
                        $"vk.guest_image addr=0x{image.Address:X16} " +
                        $"size={image.Width}x{image.Height} format={image.Format} " +
                        $"nonzero_bytes={nonzeroBytes}/{byteCount} " +
                        $"nonblack_pixels={nonblackPixels}/{(ulong)image.Width * image.Height} " +
                        $"center={center} hash=0x{hash:X16}");
                    DumpGuestImageBytes(image, bytes);
                }
                finally
                {
                    _vk.UnmapMemory(_device, memory);
                }
            }
            finally
            {
                _vk.DestroyBuffer(_device, buffer, null);
                _vk.FreeMemory(_device, memory, null);
            }
        }

        private static void DumpGuestImageBytes(
            GuestImageResource image,
            ReadOnlySpan<byte> bytes)
        {
            var directory =
                Environment.GetEnvironmentVariable("SHARPEMU_GUEST_IMAGE_DUMP_DIR");
            if (string.IsNullOrWhiteSpace(directory))
            {
                return;
            }

            Directory.CreateDirectory(directory);
            var sequence = Interlocked.Increment(ref _guestImageDumpSequence);
            var path = Path.Combine(
                directory,
                $"{sequence:D4}-0x{image.Address:X16}-{image.Width}x{image.Height}-{image.Format}.rgba");
            File.WriteAllBytes(path, bytes.ToArray());
        }

        private static uint GetReadbackBytesPerPixel(Format format) =>
            format switch
            {
                Format.R8Unorm or
                Format.R8Uint or
                Format.R8Sint => 1,
                Format.R32Uint or
                Format.R32Sint or
                Format.R32Sfloat or
                Format.R16G16Uint or
                Format.R16G16Sint or
                Format.R16G16Sfloat or
                Format.R8G8B8A8Uint or
                Format.R8G8B8A8Sint or
                Format.R8G8B8A8Unorm or
                Format.A2R10G10B10UnormPack32 => 4,
                Format.R16G16B16A16Uint or
                Format.R16G16B16A16Sint or
                Format.R16G16B16A16Sfloat => 8,
                Format.R32G32Uint or
                Format.R32G32Sint or
                Format.R32G32Sfloat => 8,
                Format.R32G32B32A32Uint or
                Format.R32G32B32A32Sint or
                Format.R32G32B32A32Sfloat => 16,
                _ => 0,
            };

        private static long CountNonblackPixels(
            ReadOnlySpan<byte> bytes,
            Format format,
            uint bytesPerPixel)
        {
            var count = 0L;
            for (var offset = 0; offset < bytes.Length; offset += (int)bytesPerPixel)
            {
                var pixel = bytes.Slice(offset, (int)bytesPerPixel);
                var hasColor = format switch
                {
                    Format.A2R10G10B10UnormPack32 =>
                        (BitConverter.ToUInt32(pixel) & 0x3FFFFFFFu) != 0,
                    Format.R8G8B8A8Uint or
                    Format.R8G8B8A8Sint or
                    Format.R8G8B8A8Unorm =>
                        pixel[0] != 0 || pixel[1] != 0 || pixel[2] != 0,
                    Format.R16G16B16A16Uint or
                    Format.R16G16B16A16Sint or
                    Format.R16G16B16A16Sfloat =>
                        pixel[..6].IndexOfAnyExcept((byte)0) >= 0,
                    _ => pixel.IndexOfAnyExcept((byte)0) >= 0,
                };
                count += hasColor ? 1 : 0;
            }

            return count;
        }

        private void RecordTranslatedDraw(uint imageIndex, TranslatedDrawResources resources)
        {
            BeginDebugLabel(_commandBuffer, "SharpEmu swapchain draw");
            RecordTextureUploads(resources, PipelineStageFlags.FragmentShaderBit);
            RecordStorageImagesForWrite(resources, PipelineStageFlags.FragmentShaderBit);
            RecordTranslatedGraphicsPass(
                resources,
                _renderPass,
                _framebuffers[imageIndex],
                _extent);
            RecordStorageImagesForRead(resources, PipelineStageFlags.FragmentShaderBit);
            EndDebugLabel(_commandBuffer);
        }

        private void RecordTextureUploads(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            foreach (var texture in resources.Textures)
            {
                if (!texture.NeedsUpload)
                {
                    continue;
                }

                // InitialUploadPending means this upload still has to perform
                // the image's first layout transition.  Treating it as prior
                // contents records ShaderReadOnlyOptimal as oldLayout even
                // though a freshly created image is still Undefined.  Linux
                // validation reports VUID-vkCmdDraw-None-09600 and NVIDIA
                // samples the uninitialized (black) image in that case.
                var hasPriorContents =
                    texture.GuestImage is { Initialized: true };
                var toTransfer = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = hasPriorContents
                        ? AccessFlags.ShaderReadBit
                        : 0,
                    DstAccessMask = AccessFlags.TransferWriteBit,
                    OldLayout = hasPriorContents
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.TransferDstOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    hasPriorContents
                        ? PipelineStageFlags.AllCommandsBit
                        : PipelineStageFlags.TopOfPipeBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toTransfer);

                var copyRegion = new BufferImageCopy
                {
                    BufferRowLength = texture.RowLength > texture.Width
                        ? texture.RowLength
                        : 0,
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(texture.Width, texture.Height, 1),
                };
                _vk.CmdCopyBufferToImage(
                    _commandBuffer,
                    texture.StagingBuffer,
                    texture.Image,
                    ImageLayout.TransferDstOptimal,
                    1,
                    &copyRegion);

                var toShaderRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = texture.Image,
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &toShaderRead);
            }
        }

        private void RecordStorageImagesForWrite(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? AccessFlags.ShaderReadBit
                        : 0,
                    DstAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    OldLayout =
                        guestImage.Initialized || guestImage.InitialUploadPending
                        ? ImageLayout.ShaderReadOnlyOptimal
                        : ImageLayout.Undefined,
                    NewLayout = ImageLayout.General,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    guestImage.Initialized || guestImage.InitialUploadPending
                        ? shaderStage
                        : PipelineStageFlags.TopOfPipeBit,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void RecordStorageImagesForRead(
            TranslatedDrawResources resources,
            PipelineStageFlags shaderStage)
        {
            var transitioned = new HashSet<GuestImageResource>();
            foreach (var texture in resources.Textures)
            {
                if (!texture.IsStorage ||
                    texture.GuestImage is not { } guestImage ||
                    !transitioned.Add(guestImage))
                {
                    continue;
                }

                var barrier = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask =
                        AccessFlags.ShaderReadBit |
                        AccessFlags.ShaderWriteBit,
                    DstAccessMask = AccessFlags.ShaderReadBit,
                    OldLayout = ImageLayout.General,
                    NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = guestImage.Image,
                    SubresourceRange = ColorSubresourceRange(0, guestImage.MipLevels),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    shaderStage,
                    shaderStage,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &barrier);
            }
        }

        private void MarkStorageImagesInitialized(
            TranslatedDrawResources resources,
            bool traceContents = true)
        {
            List<GuestImageResource>? traceImages = null;
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                    var format = GetGuestTextureFormat(guestImage.Format);
                    if (format != 0)
                    {
                        _availableGuestImages[texture.Address] = format;
                        _gpuGuestImages[texture.Address] = format;
                    }

                    if (traceContents &&
                        ShouldTraceGuestImageContents(guestImage))
                    {
                        traceImages ??= [];
                        traceImages.Add(guestImage);
                    }
                }
            }

            if (traceImages is null)
            {
                return;
            }

            foreach (var image in traceImages)
            {
                TraceGuestImageContents(image);
            }
        }

        private static void MarkSampledImagesInitialized(
            TranslatedDrawResources resources)
        {
            lock (_gate)
            {
                foreach (var texture in resources.Textures)
                {
                    if (!texture.NeedsUpload ||
                        texture.IsStorage ||
                        texture.Address == 0 ||
                        texture.GuestImage is not { } guestImage)
                    {
                        continue;
                    }

                    guestImage.Initialized = true;
                    guestImage.InitialUploadPending = false;
                    if (texture.UpdatesCpuContent)
                    {
                        guestImage.CpuContentFingerprint = texture.CpuContentFingerprint;
                    }
                }
            }
        }

        private bool ShouldTraceGuestImageContents(GuestImageResource image)
        {
            if (image.Address == 0)
            {
                return false;
            }

            var addressMatched = ShouldTraceGuestImageAddressForDiagnostics(image.Address);
            var broadTrace =
                ShouldTraceGuestImageContentsForDiagnostics() &&
                image.Width >= 1280 &&
                image.Height >= 720;
            return (addressMatched || broadTrace) &&
                   _tracedGuestImageContents.Add(image.Address);
        }

        private static bool ShouldTraceGuestImageContentsForDiagnostics() =>
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_TRACE_GUEST_IMAGES"),
                "1",
                StringComparison.Ordinal);

        private static bool ShouldTraceGuestImageAddressForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_IMAGE_ADDRS",
                address);
        }

        private static bool ShouldTraceGuestImageWriteForDiagnostics(ulong address)
        {
            return AddressListContains(
                "SHARPEMU_TRACE_GUEST_WRITES",
                address);
        }

        private static bool AddressListContains(
            string environmentVariable,
            ulong address)
        {
            var addresses = Environment.GetEnvironmentVariable(environmentVariable);
            if (string.IsNullOrWhiteSpace(addresses))
            {
                return false;
            }

            foreach (var token in addresses.Split(
                         [',', ';', ' ', '\t'],
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (token == "*")
                {
                    return true;
                }

                var span = token.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(
                        span,
                        System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed) &&
                    parsed == address)
                {
                    return true;
                }
            }

            return false;
        }

        private static bool ShouldTraceVulkanResources() =>
            string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_LOG_VK_RESOURCES"),
                "1",
                StringComparison.Ordinal);

        private void RecordTranslatedGraphicsPass(
            TranslatedDrawResources resources,
            RenderPass renderPass,
            Framebuffer framebuffer,
            Extent2D extent)
        {
            var clearValue = default(ClearValue);
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = renderPass,
                Framebuffer = framebuffer,
                RenderArea = new Rect2D(new Offset2D(0, 0), extent),
                ClearValueCount = 1,
                PClearValues = &clearValue,
            };
            _vk.CmdBeginRenderPass(
                _commandBuffer,
                &renderPassInfo,
                SubpassContents.Inline);
            _vk.CmdBindPipeline(
                _commandBuffer,
                PipelineBindPoint.Graphics,
                resources.Pipeline);
            if (resources.DescriptorSet.Handle != 0)
            {
                var descriptorSet = resources.DescriptorSet;
                _vk.CmdBindDescriptorSets(
                    _commandBuffer,
                    PipelineBindPoint.Graphics,
                    resources.PipelineLayout,
                    0,
                    1,
                    &descriptorSet,
                    0,
                    null);
            }

            var drawScissor = ClampScissor(resources.Scissor, extent);
            if (drawScissor.Width == 0 || drawScissor.Height == 0)
            {
                _vk.CmdEndRenderPass(_commandBuffer);
                return;
            }

            var drawViewport = ClampViewport(resources.Viewport, extent);
            if (ViewportDebugEpsilon != 0f)
            {
                drawViewport.X += ViewportDebugEpsilon;
                drawViewport.Y += ViewportDebugEpsilon;
            }
            _vk.CmdSetViewport(_commandBuffer, 0, 1, &drawViewport);
            if (resources.VertexBuffers.Length != 0)
            {
                var buffers = stackalloc VkBuffer[resources.VertexBuffers.Length];
                var offsets = stackalloc ulong[resources.VertexBuffers.Length];
                for (var index = 0; index < resources.VertexBuffers.Length; index++)
                {
                    buffers[index] = resources.VertexBuffers[index].Buffer;
                    offsets[index] = GetVertexBindingOffset(resources.VertexBuffers[index]);
                }

                _vk.CmdBindVertexBuffers(
                    _commandBuffer,
                    0,
                    (uint)resources.VertexBuffers.Length,
                    buffers,
                    offsets);
            }

            var scissor = new Rect2D(
                new Offset2D(drawScissor.X, drawScissor.Y),
                new Extent2D(drawScissor.Width, drawScissor.Height));
            _vk.CmdSetScissor(_commandBuffer, 0, 1, &scissor);

            if (resources.IndexBuffer.Handle != 0)
            {
                _vk.CmdBindIndexBuffer(
                    _commandBuffer,
                    resources.IndexBuffer,
                    0,
                    resources.Index32Bit ? IndexType.Uint32 : IndexType.Uint16);
                _vk.CmdDrawIndexed(
                    _commandBuffer,
                    resources.VertexCount,
                    resources.InstanceCount,
                    0,
                    0,
                    0);
            }
            else
            {
                _vk.CmdDraw(
                    _commandBuffer,
                    resources.VertexCount,
                    resources.InstanceCount,
                    0,
                    0);
            }
            _vk.CmdEndRenderPass(_commandBuffer);
        }

        private void DestroyTranslatedDrawResources(TranslatedDrawResources resources)
        {
            if (resources.TransientFramebuffer.Handle != 0)
            {
                _vk.DestroyFramebuffer(_device, resources.TransientFramebuffer, null);
            }

            if (resources.TransientRenderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, resources.TransientRenderPass, null);
            }

            foreach (var texture in resources.Textures)
            {
                if (texture is null)
                {
                    continue;
                }

                if (texture.OwnsStorage && texture.View.Handle != 0)
                {
                    _vk.DestroyImageView(_device, texture.View, null);
                }

                if (texture.OwnsStorage && texture.Image.Handle != 0)
                {
                    _vk.DestroyImage(_device, texture.Image, null);
                }

                if (texture.OwnsStorage && texture.ImageMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.ImageMemory, null);
                }

                if (texture.StagingBuffer.Handle != 0)
                {
                    _vk.DestroyBuffer(_device, texture.StagingBuffer, null);
                }

                if (texture.StagingMemory.Handle != 0)
                {
                    _vk.FreeMemory(_device, texture.StagingMemory, null);
                }

                if (texture.NeedsUpload &&
                    texture.GuestImage is { Initialized: false } guestImage)
                {
                    guestImage.InitialUploadPending = false;
                }
            }

            foreach (var globalBuffer in resources.GlobalMemoryBuffers)
            {
                if (globalBuffer is null)
                {
                    continue;
                }

                RecycleHostBuffer(globalBuffer.Buffer, globalBuffer.Memory);
            }

            foreach (var vertexBuffer in resources.VertexBuffers)
            {
                if (vertexBuffer is null)
                {
                    continue;
                }

                RecycleHostBuffer(vertexBuffer.Buffer, vertexBuffer.Memory);
            }

            RecycleHostBuffer(resources.IndexBuffer, resources.IndexMemory);

            if (!resources.PipelineCached && resources.Pipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, resources.Pipeline, null);
            }

            if (resources.DescriptorPool.Handle != 0)
            {
                _vk.DestroyDescriptorPool(_device, resources.DescriptorPool, null);
            }

            if (!resources.DescriptorLayoutCached &&
                resources.PipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, resources.PipelineLayout, null);
            }

            if (!resources.DescriptorLayoutCached &&
                resources.DescriptorSetLayout.Handle != 0)
            {
                _vk.DestroyDescriptorSetLayout(_device, resources.DescriptorSetLayout, null);
            }
        }

        private void RecordUpload(uint imageIndex)
        {
            var oldLayout = _imageInitialized[imageIndex]
                ? ImageLayout.PresentSrcKhr
                : ImageLayout.Undefined;
            var toTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex] ? AccessFlags.MemoryReadBit : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = oldLayout,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                _imageInitialized[imageIndex]
                    ? PipelineStageFlags.BottomOfPipeBit
                    : PipelineStageFlags.TopOfPipeBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toTransfer);

            var copyRegion = new BufferImageCopy
            {
                ImageSubresource = new ImageSubresourceLayers
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    LayerCount = 1,
                },
                ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
            };
            _vk.CmdCopyBufferToImage(
                _commandBuffer,
                _stagingBuffer,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &copyRegion);

            var toPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.BottomOfPipeBit,
                0,
                0,
                null,
                0,
                null,
                1,
                &toPresent);
        }

        private void RecordGuestImageBlit(
            uint imageIndex,
            GuestImageResource source)
        {
            var traceDestination =
                ShouldTracePresentedGuestImageContentsForDiagnostics() &&
                !_tracedPresentedSwapchain;
            _tracedPresentedSwapchain |= traceDestination;
            BeginDebugLabel(
                _commandBuffer,
                $"SharpEmu present image 0x{source.Address:X16}");

            var sourceToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                // An offscreen target is last written as a color attachment,
                // then put in ShaderReadOnlyOptimal for later sampling.  A
                // layout-only handoff to ShaderRead does not make that write
                // visible to this transfer when no shader sample occurs in
                // between.  NVIDIA's Linux driver exposed the resulting stale
                // (usually black) image while Windows drivers happened to
                // tolerate it.  Include all preceding writes before blitting
                // the image into the swapchain.
                SrcAccessMask = AccessFlags.MemoryWriteBit | AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferReadBit,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferSrcOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToTransfer = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = _imageInitialized[imageIndex]
                    ? AccessFlags.MemoryReadBit
                    : 0,
                DstAccessMask = AccessFlags.TransferWriteBit,
                OldLayout = _imageInitialized[imageIndex]
                    ? ImageLayout.PresentSrcKhr
                    : ImageLayout.Undefined,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            var barriers = stackalloc ImageMemoryBarrier[2];
            barriers[0] = sourceToTransfer;
            barriers[1] = destinationToTransfer;
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.AllCommandsBit,
                PipelineStageFlags.TransferBit,
                0,
                0,
                null,
                0,
                null,
                2,
                barriers);

            var sourceOffsets = new ImageBlit.SrcOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)source.Width),
                    checked((int)source.Height),
                    1),
            };
            var destinationOffsets = new ImageBlit.DstOffsetsBuffer
            {
                Element0 = new Offset3D(0, 0, 0),
                Element1 = new Offset3D(
                    checked((int)_extent.Width),
                    checked((int)_extent.Height),
                    1),
            };
            var region = new ImageBlit
            {
                SrcSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                SrcOffsets = sourceOffsets,
                DstSubresource = new ImageSubresourceLayers(
                    ImageAspectFlags.ColorBit,
                    0,
                    0,
                    1),
                DstOffsets = destinationOffsets,
            };
            // Nearest keeps integer upscales pixel-crisp, but any fractional
            // scale (e.g. a 3840x2160 guest frame into a 2560x1440 swapchain)
            // must blend neighbours or it silently drops every Nth source
            // row/column, which shreds 1-2px features in the guest frame.
            var isIntegerUpscale =
                source.Width != 0 && source.Height != 0 &&
                _extent.Width >= source.Width && _extent.Height >= source.Height &&
                _extent.Width % source.Width == 0 && _extent.Height % source.Height == 0;
            _vk.CmdBlitImage(
                _commandBuffer,
                source.Image,
                ImageLayout.TransferSrcOptimal,
                _swapchainImages[imageIndex],
                ImageLayout.TransferDstOptimal,
                1,
                &region,
                isIntegerUpscale ? Filter.Nearest : Filter.Linear);

            if (traceDestination)
            {
                var destinationToReadback = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier,
                    SrcAccessMask = AccessFlags.TransferWriteBit,
                    DstAccessMask = AccessFlags.TransferReadBit,
                    OldLayout = ImageLayout.TransferDstOptimal,
                    NewLayout = ImageLayout.TransferSrcOptimal,
                    SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    Image = _swapchainImages[imageIndex],
                    SubresourceRange = ColorSubresourceRange(),
                };
                _vk.CmdPipelineBarrier(
                    _commandBuffer,
                    PipelineStageFlags.TransferBit,
                    PipelineStageFlags.TransferBit,
                    0,
                    0,
                    null,
                    0,
                    null,
                    1,
                    &destinationToReadback);

                var copyRegion = new BufferImageCopy
                {
                    ImageSubresource = new ImageSubresourceLayers
                    {
                        AspectMask = ImageAspectFlags.ColorBit,
                        LayerCount = 1,
                    },
                    ImageExtent = new Extent3D(_extent.Width, _extent.Height, 1),
                };
                _vk.CmdCopyImageToBuffer(
                    _commandBuffer,
                    _swapchainImages[imageIndex],
                    ImageLayout.TransferSrcOptimal,
                    _stagingBuffer,
                    1,
                    &copyRegion);
                _swapchainReadbackPending = true;
            }

            var sourceToShaderRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = AccessFlags.TransferReadBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
                OldLayout = ImageLayout.TransferSrcOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = source.Image,
                SubresourceRange = ColorSubresourceRange(),
            };
            var destinationToPresent = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                SrcAccessMask = traceDestination
                    ? AccessFlags.TransferReadBit
                    : AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.MemoryReadBit,
                OldLayout = traceDestination
                    ? ImageLayout.TransferSrcOptimal
                    : ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.PresentSrcKhr,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _swapchainImages[imageIndex],
                SubresourceRange = ColorSubresourceRange(),
            };
            barriers[0] = sourceToShaderRead;
            barriers[1] = destinationToPresent;
            _vk.CmdPipelineBarrier(
                _commandBuffer,
                PipelineStageFlags.TransferBit,
                PipelineStageFlags.AllCommandsBit,
                0,
                0,
                null,
                0,
                null,
                2,
                barriers);
            EndDebugLabel(_commandBuffer);
        }

        private void TraceSwapchainReadback()
        {
            _swapchainReadbackPending = false;
            var byteCount = checked((ulong)_extent.Width * _extent.Height * 4);
            void* mapped;
            Check(
                _vk.MapMemory(_device, _stagingMemory, 0, byteCount, 0, &mapped),
                "vkMapMemory(swapchain readback)");
            try
            {
                var bytes = new ReadOnlySpan<byte>(mapped, checked((int)byteCount));
                var nonzeroBytes = 0L;
                var nonblackPixels = 0L;
                ulong hash = 14695981039346656037UL;
                for (var offset = 0; offset < bytes.Length; offset += 4)
                {
                    var b0 = bytes[offset];
                    var b1 = bytes[offset + 1];
                    var b2 = bytes[offset + 2];
                    var b3 = bytes[offset + 3];
                    nonzeroBytes += b0 == 0 ? 0 : 1;
                    nonzeroBytes += b1 == 0 ? 0 : 1;
                    nonzeroBytes += b2 == 0 ? 0 : 1;
                    nonzeroBytes += b3 == 0 ? 0 : 1;
                    nonblackPixels += b0 != 0 || b1 != 0 || b2 != 0 ? 1 : 0;
                    hash = (hash ^ b0) * 1099511628211UL;
                    hash = (hash ^ b1) * 1099511628211UL;
                    hash = (hash ^ b2) * 1099511628211UL;
                    hash = (hash ^ b3) * 1099511628211UL;
                }

                Console.Error.WriteLine(
                    $"[LOADER][TRACE] vk.swapchain_image size={_extent.Width}x{_extent.Height} " +
                    $"format={_swapchainFormat} nonzero_bytes={nonzeroBytes}/{byteCount} " +
                    $"nonblack_pixels={nonblackPixels}/{(ulong)_extent.Width * _extent.Height} " +
                    $"hash=0x{hash:X16}");
            }
            finally
            {
                _vk.UnmapMemory(_device, _stagingMemory);
            }
        }

        private Extent2D ChooseExtent(SurfaceCapabilitiesKHR capabilities)
        {
            if (capabilities.CurrentExtent.Width != uint.MaxValue)
            {
                var fallbackWidth = _extent.Width != 0
                    ? _extent.Width
                    : DefaultWindowWidth;
                var fallbackHeight = _extent.Height != 0
                    ? _extent.Height
                    : DefaultWindowHeight;
                return new Extent2D(
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Width,
                        fallbackWidth,
                        capabilities.MinImageExtent.Width,
                        capabilities.MaxImageExtent.Width),
                    ClampSurfaceExtent(
                        capabilities.CurrentExtent.Height,
                        fallbackHeight,
                        capabilities.MinImageExtent.Height,
                        capabilities.MaxImageExtent.Height));
            }

            var size = _window.FramebufferSize;
            return new Extent2D(
                ClampSurfaceExtent(
                    (uint)Math.Max(size.X, 1),
                    DefaultWindowWidth,
                    capabilities.MinImageExtent.Width,
                    capabilities.MaxImageExtent.Width),
                ClampSurfaceExtent(
                    (uint)Math.Max(size.Y, 1),
                    DefaultWindowHeight,
                    capabilities.MinImageExtent.Height,
                    capabilities.MaxImageExtent.Height));
        }

        private static uint ClampSurfaceExtent(
            uint value,
            uint fallback,
            uint minimum,
            uint maximum)
        {
            value = value <= 1 && fallback > 1 ? fallback : value;
            minimum = Math.Max(minimum, 1u);
            maximum = Math.Max(maximum, minimum);
            return Math.Clamp(value, minimum, maximum);
        }

        private static SurfaceFormatKHR ChooseSurfaceFormat(IReadOnlyList<SurfaceFormatKHR> formats)
        {
            foreach (var format in formats)
            {
                if (format.Format is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm &&
                    format.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                {
                    return format;
                }
            }

            return formats.Count > 0
                ? formats[0]
                : throw new InvalidOperationException("The Vulkan surface exposes no pixel formats.");
        }

        private static CompositeAlphaFlagsKHR ChooseCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            foreach (var candidate in new[]
                     {
                         CompositeAlphaFlagsKHR.OpaqueBitKhr,
                         CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                         CompositeAlphaFlagsKHR.InheritBitKhr,
                     })
            {
                if ((supported & candidate) != 0)
                {
                    return candidate;
                }
            }

            throw new InvalidOperationException("The Vulkan surface exposes no composite alpha mode.");
        }

        private static ImageSubresourceRange ColorSubresourceRange(
            uint baseMipLevel = 0,
            uint levelCount = 1) =>
            new()
            {
                AspectMask = ImageAspectFlags.ColorBit,
                BaseMipLevel = baseMipLevel,
                LevelCount = levelCount,
                LayerCount = 1,
            };

        private static byte[] ScaleBgra(byte[] source, uint sourceWidth, uint sourceHeight, uint width, uint height)
        {
            var destination = new byte[checked((int)(width * height * 4))];
            for (uint y = 0; y < height; y++)
            {
                var sourceY = (uint)(((ulong)y * sourceHeight) / height);
                for (uint x = 0; x < width; x++)
                {
                    var sourceX = (uint)(((ulong)x * sourceWidth) / width);
                    var sourceOffset = checked((int)(((ulong)sourceY * sourceWidth + sourceX) * 4));
                    var destinationOffset = checked((int)(((ulong)y * width + x) * 4));
                    source.AsSpan(sourceOffset, 4).CopyTo(destination.AsSpan(destinationOffset, 4));
                }
            }

            return destination;
        }

        private void DisposeVulkan()
        {
            if (!_vulkanReady)
            {
                return;
            }

            if (_debugUtils is not null && _debugMessenger.Handle != 0)
            {
                _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            }
            _vulkanReady = false;
            _vk.DeviceWaitIdle(_device);
            CompletePendingPresentation(wait: false);
            CollectCompletedGuestSubmissions(waitForOldest: false);
            SavePipelineCache();
            foreach (var pipeline in _computePipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _computePipelines.Clear();
            foreach (var pipeline in _graphicsPipelines.Values)
            {
                _vk.DestroyPipeline(_device, pipeline, null);
            }
            _graphicsPipelines.Clear();
            if (_pipelineCache.Handle != 0)
            {
                _vk.DestroyPipelineCache(_device, _pipelineCache, null);
                _pipelineCache = default;
            }
            foreach (var layout in _descriptorLayouts.Values)
            {
                _vk.DestroyPipelineLayout(_device, layout.PipelineLayout, null);
                if (layout.DescriptorSetLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(
                        _device,
                        layout.DescriptorSetLayout,
                        null);
                }
            }
            _descriptorLayouts.Clear();
            foreach (var sampler in _samplers.Values)
            {
                _vk.DestroySampler(_device, sampler, null);
            }
            _samplers.Clear();
            _shaderDigests.Clear();
            foreach (var allocation in _hostBufferAllocations.Values)
            {
                _vk.DestroyBuffer(_device, allocation.Buffer, null);
                _vk.FreeMemory(_device, allocation.Memory, null);
            }
            _hostBufferAllocations.Clear();
            _hostBufferPool.Clear();
            foreach (var guestImage in _guestImages.Values)
            {
                DestroyGuestImage(guestImage);
            }
            _guestImages.Clear();
            lock (_gate)
            {
                _availableGuestImages.Clear();
                _gpuGuestImages.Clear();
            }
            DestroySwapchainResources();
            if (_device.Handle != 0)
            {
                _vk.DestroyDevice(_device, null);
                _device = default;
            }
            if (_surface.Handle != 0)
            {
                _surfaceApi.DestroySurface(_instance, _surface, null);
                _surface = default;
            }
            if (_instance.Handle != 0)
            {
                _vk.DestroyInstance(_instance, null);
                _instance = default;
            }
        }

        private void RecreateSwapchainResources(string operation, Result result)
        {
            if (_device.Handle == 0)
            {
                return;
            }

            Check(
                _surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    _physicalDevice,
                    _surface,
                    out var capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            var framebufferSize = _window.FramebufferSize;
            var hasFixedExtent = capabilities.CurrentExtent.Width != uint.MaxValue;
            var surfaceWidth = hasFixedExtent
                ? capabilities.CurrentExtent.Width
                : (uint)Math.Max(framebufferSize.X, 0);
            var surfaceHeight = hasFixedExtent
                ? capabilities.CurrentExtent.Height
                : (uint)Math.Max(framebufferSize.Y, 0);
            if (surfaceWidth <= 1 || surfaceHeight <= 1)
            {
                if (!_swapchainRecreateDeferred)
                {
                    _swapchainRecreateDeferred = true;
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Vulkan VideoOut deferred swapchain recreation: " +
                        $"surface={surfaceWidth}x{surfaceHeight}");
                }

                return;
            }

            _swapchainRecreateDeferred = false;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreating swapchain after {operation}: {result}");
            _vk.DeviceWaitIdle(_device);
            CompletePendingPresentation(wait: false);
            CollectCompletedGuestSubmissions(waitForOldest: false);
            DestroySwapchainResources();
            CreateSwapchain();
            CreateCommandResources();
            CreateGuestDrawResources();
            Console.Error.WriteLine(
                $"[LOADER][INFO] Vulkan VideoOut recreated swapchain: " +
                $"{_extent.Width}x{_extent.Height}, format={_swapchainFormat}");
        }

        private void DestroySwapchainResources()
        {
            if (_stagingBuffer.Handle != 0)
            {
                _vk.DestroyBuffer(_device, _stagingBuffer, null);
                _stagingBuffer = default;
            }
            if (_stagingMemory.Handle != 0)
            {
                _vk.FreeMemory(_device, _stagingMemory, null);
                _stagingMemory = default;
                _stagingSize = 0;
            }
            if (_imageAvailable.Handle != 0)
            {
                _vk.DestroySemaphore(_device, _imageAvailable, null);
                _imageAvailable = default;
            }
            if (_renderFinished.Handle != 0)
            {
                _vk.DestroySemaphore(_device, _renderFinished, null);
                _renderFinished = default;
            }
            if (_presentationFence.Handle != 0)
            {
                _vk.DestroyFence(_device, _presentationFence, null);
                _presentationFence = default;
            }
            if (_barycentricPipeline.Handle != 0)
            {
                _vk.DestroyPipeline(_device, _barycentricPipeline, null);
                _barycentricPipeline = default;
            }
            if (_pipelineLayout.Handle != 0)
            {
                _vk.DestroyPipelineLayout(_device, _pipelineLayout, null);
                _pipelineLayout = default;
            }
            foreach (var framebuffer in _framebuffers)
            {
                if (framebuffer.Handle != 0)
                {
                    _vk.DestroyFramebuffer(_device, framebuffer, null);
                }
            }
            if (_renderPass.Handle != 0)
            {
                _vk.DestroyRenderPass(_device, _renderPass, null);
                _renderPass = default;
            }
            foreach (var imageView in _swapchainImageViews)
            {
                if (imageView.Handle != 0)
                {
                    _vk.DestroyImageView(_device, imageView, null);
                }
            }
            if (_commandPool.Handle != 0)
            {
                _vk.DestroyCommandPool(_device, _commandPool, null);
                _commandPool = default;
                _commandBuffer = default;
                _presentationCommandBuffer = default;
            }
            if (_swapchain.Handle != 0)
            {
                _swapchainApi.DestroySwapchain(_device, _swapchain, null);
                _swapchain = default;
            }

            _swapchainImages = [];
            _swapchainImageViews = [];
            _framebuffers = [];
            _imageInitialized = [];
        }

        private static void CheckSwapchainResult(Result result, string operation)
        {
            if (result is Result.Success or Result.SuboptimalKhr)
            {
                return;
            }

            throw new InvalidOperationException($"{operation} failed with {result}.");
        }

        private static void Check(Result result, string operation)
        {
            if (result != Result.Success)
            {
                throw new InvalidOperationException($"{operation} failed with {result}.");
            }
        }

        private bool TryMarkDeviceLost(Exception exception)
        {
            if (!exception.Message.Contains(nameof(Result.ErrorDeviceLost), StringComparison.Ordinal))
            {
                return false;
            }

            _deviceLost = true;
            if (!_deviceLostLogged)
            {
                _deviceLostLogged = true;
                Console.Error.WriteLine(
                    "[LOADER][ERROR] Vulkan device lost; dropping subsequent guest GPU work. " +
                    exception.Message);
            }

            return true;
        }

        private static void TraceVulkanShader(string message)
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC"), "1", StringComparison.Ordinal) &&
                !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AGC_SHADER"), "1", StringComparison.Ordinal))
            {
                return;
            }

            Console.Error.WriteLine($"[LOADER][TRACE] {message}");
        }
    }
}
