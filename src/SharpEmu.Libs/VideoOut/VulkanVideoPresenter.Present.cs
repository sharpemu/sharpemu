// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns guest presentation scheduling and presenter thread lifecycle.


    // A captured 4K flip can consume tens of MiB of device-local memory.
    // Retain only a short presentation queue while always preserving the
    // newest generation; older immutable versions are retired immediately.
    private const int MaxPendingGuestFlipVersions = 4;

    // A flip names an image that was rendered earlier in the command stream.
    // Keep a small FIFO of those flips instead of replacing an incomplete one
    // with the next frame: the guest can enqueue the next frame before the
    // render thread reaches the previous image, which otherwise starves
    // presentation indefinitely.
    private static readonly Queue<Presentation> _pendingGuestImagePresentations = new();
    // Same fix as _pendingGuestImagePresentations above, for Submit()'s decoded video
    // frames: a single "latest wins" slot dropped frames the render loop didn't poll in time.
    private static readonly Queue<Presentation> _pendingVideoPresentations = new();
    private static long _guestFlipVersionSequence;

    private static Thread? _thread;
    private static HostVideoOptions _videoOptions = HostVideoOptions.Default;
    private static Presentation? _latestPresentation;
    private static uint _windowWidth;
    private static uint _windowHeight;
    private static bool _closed;
    private static bool _presenterCloseRequested;

    private static bool _splashHidden;

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
                    IsSplash: false)
                : hasSplash
                ? new Presentation(
                    splashPixels,
                    splashWidth,
                    splashHeight,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    IsSplash: true)
                : new Presentation(
                    null,
                    width,
                    height,
                    0,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    IsSplash: false);
            StartPresenterLocked();
        }
    }

    public static bool TryConfigureVideo(HostVideoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_thread is not null)
            {
                return false;
            }

            _videoOptions = options.Normalize();
            return true;
        }
    }

    private static void ResetHostSessionStateLocked()
    {
        _latestPresentation = null;
        _splashHidden = false;
        _pendingGuestImagePresentations.Clear();
        _pendingVideoPresentations.Clear();
        _knownDisplayBuffers.Clear();
        _guestFlipVersionSequence = 0;
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
                IsSplash: false);
            Console.Error.WriteLine("[LOADER][INFO] Vulkan VideoOut hid splash");
        }
    }

    public static void Submit(byte[] bgraFrame, uint width, uint height)
    {
        if (bgraFrame.Length != checked((int)(width * height * 4)))
        {
            return;
        }

        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            var presentation = new Presentation(
                bgraFrame,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                IsSplash: false);

            // Also dual-written to _latestPresentation as a fallback once the queue drains.
            _pendingVideoPresentations.Enqueue(presentation);
            while (_pendingVideoPresentations.Count > MaxPendingGuestFlipVersions)
            {
                _pendingVideoPresentations.Dequeue();
            }

            _latestPresentation = presentation;
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
                IsSplash: false);
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
                IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    // Select the sRGB format with the same byte layout as the UNORM target.
    internal static Format GetSrgbCounterpart(Format format) => format switch
    {
        Format.B8G8R8A8Unorm => Format.B8G8R8A8Srgb,
        Format.R8G8B8A8Unorm => Format.R8G8B8A8Srgb,
        _ => Format.Undefined,
    };

    // Float VideoOut flip buffers hold linear scRGB light; presenting them
    // requires a linear->sRGB encode that a plain blit does not perform.
    internal static bool IsLinearFloatPresentSource(Format format) =>
        format is Format.R16G16B16A16Sfloat or Format.R32G32B32A32Sfloat;

    // Keep encoded display bytes in UNORM snapshots so scaling does not decode them.
    internal static Format GetPresentationSnapshotFormat(Format sourceFormat) => sourceFormat switch
    {
        Format.R8G8B8A8Srgb => Format.R8G8B8A8Unorm,
        Format.B8G8R8A8Srgb => Format.B8G8R8A8Unorm,
        _ => sourceFormat,
    };

    // A raw copy preserves encoded colors when the source and target byte layouts match.
    internal static bool CanCopyEncodedSrgbPresentSource(
        Format sourceFormat,
        Format swapchainFormat) =>
        (sourceFormat, swapchainFormat) switch
        {
            (Format.B8G8R8A8Srgb, Format.B8G8R8A8Unorm) => true,
            (Format.R8G8B8A8Srgb, Format.R8G8B8A8Unorm) => true,
            _ => false,
        };

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
            // AppKit windowing traps when touched off the process
            // main thread on macOS, so hand the whole window loop to the
            // main-thread pump the CLI parked for us. _thread only marks the
            // presenter as running; Run() clears it on exit either way.
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

    private static void Run()
    {
        uint width;
        uint height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? _latestPresentation?.Width ?? 1280 : _windowWidth;
            height = _windowHeight == 0 ? _latestPresentation?.Height ?? 720 : _windowHeight;
        }

        try
        {
            using var presenter = new Presenter(width, height);
            lock (_gate)
            {
                _activePresenter = presenter;
            }

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
                _activePresenter = null;
                System.Threading.Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>
    /// AvPlayer titles whose guest texture allocators reject the decoded movie
    /// surface have no sampled image to draw, so the movie would never become
    /// visible.  In that case the AvPlayer HLE keeps a host-decoded BGRA frame
    /// available; substitute it for the guest image the title is flipping.
    /// </summary>
    private static void TryReplaceWithHostMovieFrame(ref Presentation presentation)
    {
        if (!TryTakeHostMovieFrame(out var pixels, out var width, out var height))
        {
            return;
        }

        presentation = new Presentation(
            pixels,
            width,
            height,
            presentation.Sequence,
            GuestDrawKind.None,
            TranslatedDraw: null,
            IsSplash: false,
            RequiredTick: presentation.RequiredTick,
            FlipRequestId: presentation.FlipRequestId);
    }

    /// <summary>
    /// The movie is decoded on the host clock, so it must not be limited to the
    /// title's flip rate: emulated flips are far slower than 59.94 Hz, which
    /// would turn the intro into a slideshow.  The render loop uses this on the
    /// ticks where the guest produced no new flip, keeping the same presented
    /// sequence so guest presentation bookkeeping is untouched.
    /// </summary>
    private static bool TryTakeHostMovieOnlyPresentation(
        long presentedSequence,
        out Presentation presentation)
    {
        if (!TryTakeHostMovieFrame(out var pixels, out var width, out var height))
        {
            presentation = default;
            return false;
        }

        presentation = new Presentation(
            pixels,
            width,
            height,
            presentedSequence,
            GuestDrawKind.None,
            TranslatedDraw: null,
            IsSplash: false);
        return true;
    }

    private static bool TryTakeHostMovieFrame(
        out byte[] pixels,
        out uint width,
        out uint height)
    {
        if (!AvPlayerExports.TryGetFallbackPresentationFrame(
                out pixels,
                out width,
                out height,
                out var serial))
        {
            return false;
        }

        if (Interlocked.Exchange(
                ref _tracedAvPlayerFallbackPresentationSerial,
                serial) != serial)
        {
            var frameCount = Interlocked.Increment(
                ref _avPlayerFallbackPresentationCount);
            if (frameCount <= 4 || frameCount % 30 == 0)
            {
                Console.Error.WriteLine(
                    "[VIDEOOUT][INFO] AvPlayer host fallback frame presented: " +
                    $"frame={frameCount} serial={serial} size={width}x{height}.");
            }
        }

        return true;
    }

    private static long _tracedAvPlayerFallbackPresentationSerial;
    private static long _avPlayerFallbackPresentationCount;
    private static readonly HashSet<long> _tracedGuestImagePresentRejections = new();

	private static bool HasPendingGuestPresentation(long presentedSequence)
	{
		lock (_gate)
		{
			return _pendingGuestImagePresentations.Count > 0 ||
				_latestPresentation is { } latest && latest.Sequence > presentedSequence;
		}
	}

    private readonly record struct Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        GuestDrawKind DrawKind,
        VulkanTranslatedGuestDraw? TranslatedDraw,
        bool IsSplash,
        ulong GuestImageAddress = 0,
        long GuestImageVersion = 0,
        bool IsHdr = false,
        ulong RequiredTick = 0,
        ulong FlipRequestId = 0);
}
