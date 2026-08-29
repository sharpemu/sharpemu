// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using SharpEmu.HLE;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Gpu;
using SharpEmu.ShaderCompiler;
using Silk.NET.Vulkan;

internal sealed record VulkanOrderedGuestFlip(
    long Version,
    int VideoOutHandle,
    int DisplayBufferIndex,
    ulong Address,
    uint Width,
    uint Height,
    uint PitchInPixel);

internal sealed record VulkanOrderedGuestFlipWait(
    long Version,
    int VideoOutHandle,
    int DisplayBufferIndex);

internal static class VulkanGuestFlipSourcePolicy
{
    public static bool CanCapture(
        bool registered,
        bool materialized,
        bool hasQueuedWriter) =>
        registered && (materialized || hasQueuedWriter);
}

internal sealed class VulkanGuestFlipCompletionTracker
{
    private sealed class BufferState
    {
        public long CompletedThrough;
        public SortedDictionary<long, bool> Pending { get; } = new();
    }

    private readonly Lock _gate = new();
    private readonly Dictionary<(int Handle, int BufferIndex), BufferState> _states = new();

    public void Register(int handle, int bufferIndex, long version)
    {
        lock (_gate)
        {
            var state = GetOrCreateState(handle, bufferIndex);
            state.Pending.TryAdd(version, false);
        }
    }

    public bool IsSafe(int handle, int bufferIndex, long version)
    {
        if (version == 0)
        {
            return true;
        }

        lock (_gate)
        {
            return _states.TryGetValue((handle, bufferIndex), out var state) &&
                (version <= state.CompletedThrough ||
                 state.Pending.GetValueOrDefault(version));
        }
    }

    public void MarkSafe(int handle, int bufferIndex, long version)
    {
        lock (_gate)
        {
            var state = GetOrCreateState(handle, bufferIndex);
            state.Pending[version] = true;
            while (state.Pending.Count != 0)
            {
                var first = state.Pending.First();
                if (!first.Value)
                {
                    break;
                }

                state.CompletedThrough = first.Key;
                state.Pending.Remove(first.Key);
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _states.Clear();
        }
    }

    private BufferState GetOrCreateState(int handle, int bufferIndex)
    {
        var key = (handle, bufferIndex);
        if (!_states.TryGetValue(key, out var state))
        {
            state = new BufferState();
            _states.Add(key, state);
        }

        return state;
    }
}

internal static unsafe partial class VulkanVideoPresenter
{
    // This partial owns guest presentation scheduling and presenter thread lifecycle.

    private const uint DefaultWindowWidth = 1920;
    private const uint DefaultWindowHeight = 1080;

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
    private static readonly Dictionary<ulong, long> _guestImageWorkSequences = new();
    private static readonly Dictionary<(int Handle, int BufferIndex), long>
        _lastOrderedGuestFlipVersions = new();
    private static readonly VulkanGuestFlipCompletionTracker _guestFlipCompletion = new();
    private static long _orderedGuestFlipVersionSequence;

    private static Thread? _thread;
    private static HostVideoOptions _videoOptions = HostVideoOptions.Default;
    private static Presentation? _latestPresentation;
    private static byte[]? _copyFragmentSpirv;
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
                    RequiredGuestWorkSequence: 0,
                    IsSplash: false)
                : hasSplash
                ? new Presentation(
                    splashPixels,
                    splashWidth,
                    splashHeight,
                    1,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: 0,
                    IsSplash: true)
                : new Presentation(
                    null,
                    width,
                    height,
                    0,
                    GuestDrawKind.None,
                    TranslatedDraw: null,
                    RequiredGuestWorkSequence: 0,
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
        _pendingGuestWorkByQueue.Clear();
        _pendingGuestQueueSchedule.Clear();
        _pendingGuestQueueCursor = 0;
        _pendingGuestWorkCount = 0;
        _pendingPayloadGuestWorkCount = 0;
        _pendingSyncGuestWorkCount = 0;
        _pendingGuestWorkBytes = 0;
        _pendingGuestImagePresentations.Clear();
        _pendingVideoPresentations.Clear();
        _guestImageWorkSequences.Clear();
        _availableGuestImages.Clear();
        _cpuBackedUploadGenerations.Clear();
        _untrackedGuestImageContentProbes.Clear();
        _lastOrderedGuestFlipVersions.Clear();
        _guestFlipCompletion.Reset();
        _orderedGuestFlipVersionSequence = 0;
        _pendingGuestImageUploads.Clear();
        _pendingGuestImageInitialData.Clear();
        _pendingGuestImageBufferClears.Clear();
        _guestImageExtents.Clear();
        _enqueuedGuestWorkSequence = 0;
        _completedGuestWorkSequence = 0;
        _completedGuestWorkOutOfOrder.Clear();
        _lastEnqueuedGuestWorkByQueue.Clear();
        _requiredGpuLabelDependenciesByGuestQueue.Clear();
        Volatile.Write(ref _gpuLabelTimelineAvailable, false);
        _executingGuestWorkSequence = 0;
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
                RequiredGuestWorkSequence: 0,
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
                RequiredGuestWorkSequence: 0,
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
                RequiredGuestWorkSequence: CurrentSubmittingQueueTailLocked(),
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
                RequiredGuestWorkSequence: CurrentSubmittingQueueTailLocked(),
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

    public static bool TrySubmitGuestImage(
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        var traceSubmission = false;
        lock (_gate)
        {
            if (_closed ||
                !_availableGuestImages.ContainsKey(address))
            {
                return false;
            }

            traceSubmission =
                _tracedGuestImageSubmissions.Add((address, width, height));
            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            var requiredWorkSequence = _guestImageWorkSequences.TryGetValue(
                address,
                out var imageWorkSequence)
                ? imageWorkSequence
                : _completedGuestWorkSequence;
            var presentation = new Presentation(
                null,
                width,
                height,
                sequence,
                GuestDrawKind.None,
                TranslatedDraw: null,
                // Wait only for the work that last wrote this image, not for
                // every later command the guest has already queued. Requiring
                // the global tail makes a fast guest permanently outrun the
                // renderer and turns every flip into a dropped black frame.
                RequiredGuestWorkSequence: requiredWorkSequence,
                IsSplash: false,
                GuestImageAddress: address,
                IsHdr: VideoOutExports.IsHdrOutputRequested);
            _latestPresentation = presentation;
            _pendingGuestImagePresentations.Enqueue(presentation);
            while (_pendingGuestImagePresentations.Count > MaxPendingGuestFlipVersions)
            {
                _pendingGuestImagePresentations.Dequeue();
            }
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

    /// <summary>
    /// Enqueues an AGC flip at its exact position in the logical guest queue.
    /// The presenter captures the named image into an immutable Vulkan image
    /// before it executes later work from the same queue. Presentation then
    /// consumes that captured generation rather than the mutable render target.
    /// </summary>
    public static bool TrySubmitOrderedGuestImageFlip(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        lock (_gate)
        {
            if (_closed ||
                _thread is null ||
                !VulkanGuestFlipSourcePolicy.CanCapture(
                    _availableGuestImages.ContainsKey(address),
                    _guestImageExtents.ContainsKey(address),
                    _guestImageWorkSequences.ContainsKey(address)))
            {
                return false;
            }

            var version = ++_orderedGuestFlipVersionSequence;
            _lastOrderedGuestFlipVersions[(videoOutHandle, displayBufferIndex)] = version;
            var enqueued = EnqueueGuestWorkLocked(
                new VulkanOrderedGuestFlip(
                    version,
                    videoOutHandle,
                    displayBufferIndex,
                    address,
                    width,
                    height,
                    pitchInPixel)) > 0;
            if (enqueued)
            {
                _guestFlipCompletion.Register(videoOutHandle, displayBufferIndex, version);
            }
            SharpEmu.Libs.Diagnostics.LoadProgressDiagnostics.TraceOrderedFlipEnqueue(
                videoOutHandle,
                displayBufferIndex,
                address,
                version,
                enqueued);
            return enqueued;
        }
    }

    /// <summary>
    /// Preserves sceAgcDcbWaitUntilSafeForRendering in queue order. Because an
    /// ordered flip first copies the mutable render target into an immutable
    /// generation on the same Vulkan queue, reaching this marker proves later
    /// rendering cannot change the frame selected by that flip. No CPU wait or
    /// event-loop stall is required.
    /// </summary>
    public static long SubmitOrderedGuestFlipWait(
        int videoOutHandle,
        int displayBufferIndex)
    {
        lock (_gate)
        {
            var version = _lastOrderedGuestFlipVersions.TryGetValue(
                (videoOutHandle, displayBufferIndex),
                out var lastVersion)
                    ? lastVersion
                    : 0;
            return _closed || _thread is null
                ? 0
                : EnqueueGuestWorkLocked(
                    new VulkanOrderedGuestFlipWait(
                        version,
                        videoOutHandle,
                        displayBufferIndex));
        }
    }

    // Maps a UNORM swapchain format to the sRGB view of the same bit layout,
    // or Undefined when no counterpart exists. Used to encode linear-float
    // guest flips on their way into a UNORM swapchain.
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

    // A copy between the sRGB and UNORM views of the same byte layout keeps
    // the encoded bytes unchanged. A blit performs format conversion instead
    // and decodes the sRGB source to linear values, which makes an SDR frame
    // too dark when those values are then presented through a UNORM swapchain.
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
            // Guest flips are retained in submission order. The renderer is
            // deliberately allowed to lag a frame or two behind the guest
            // while it drains expensive work, so use the first completed flip
            // rather than repeatedly asking only for the newest one.
            while (_pendingGuestImagePresentations.Count > 0 &&
                   _pendingGuestImagePresentations.Peek().Sequence <= presentedSequence)
            {
                _pendingGuestImagePresentations.Dequeue();
            }

            if (_pendingGuestImagePresentations.Count > 0)
            {
                var pending = _pendingGuestImagePresentations.Peek();
                if (IsGuestWorkCompletedLocked(pending.RequiredGuestWorkSequence))
                {
                    presentation = _pendingGuestImagePresentations.Dequeue();
                    TryReplaceWithHostMovieFrame(ref presentation);
                    return true;
                }

                presentation = default;
                return false;
            }

            // Video's RequiredGuestWorkSequence is always 0, so this never blocks like the guest-image queue can.
            while (_pendingVideoPresentations.Count > 0 &&
                   _pendingVideoPresentations.Peek().Sequence <= presentedSequence)
            {
                _pendingVideoPresentations.Dequeue();
            }

            if (_pendingVideoPresentations.Count > 0)
            {
                presentation = _pendingVideoPresentations.Dequeue();
                return true;
            }

            if (_latestPresentation is not { } latest ||
                latest.Sequence == presentedSequence ||
                !IsGuestWorkCompletedLocked(latest.RequiredGuestWorkSequence))
            {
                if (_latestPresentation is { } rej &&
                    rej.GuestImageAddress != 0 &&
					rej.Sequence != presentedSequence &&
                    _tracedGuestImagePresentRejections.Add(rej.Sequence))
                {
                    var reason = rej.Sequence == presentedSequence
                        ? "already-presented(seq==presented)"
                        : !IsGuestWorkCompletedLocked(rej.RequiredGuestWorkSequence)
                            ? $"work-not-done(req={rej.RequiredGuestWorkSequence}>" +
                              $"contiguous_done={_completedGuestWorkSequence})"
                            : "unknown";
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] vk.guest_present_rejected addr=0x{rej.GuestImageAddress:X16} " +
                        $"seq={rej.Sequence} presentedSeq={presentedSequence} reason={reason}");
                }

                presentation = default;
                return false;
            }

            presentation = latest;
            TryReplaceWithHostMovieFrame(ref presentation);
            return true;
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
            presentation.RequiredGuestWorkSequence,
            IsSplash: false);
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
            RequiredGuestWorkSequence: 0,
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
        long RequiredGuestWorkSequence,
        bool IsSplash,
        ulong GuestImageAddress = 0,
        long GuestImageVersion = 0,
        bool IsHdr = false);
}
