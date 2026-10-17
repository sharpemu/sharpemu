// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// The Metal presenter uses SDL for its host window, events and input, then renders
/// into the CAMetalLayer owned by SDL's Metal view. Windowing and input therefore
/// share the Vulkan path while all Metal command encoding remains native.
/// </summary>
internal static partial class MetalVideoPresenter
{
    private const nuint PixelFormatBgra8Unorm = (nuint)MtlPixelFormat.Bgra8Unorm;
    private const nuint LoadActionLoad = 1;
    private const nuint LoadActionClear = 2;
    private const nuint StoreActionStore = 1;
    private const nuint PrimitiveTypeTriangle = 3;
    private const nuint SamplerMinMagFilterLinear = 1;

    private sealed record Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        bool IsSplash,
        ulong GuestImageAddress = 0,
        long GuestImageVersion = 0,
        uint GuestImagePitch = 0,
        long RequiredGuestWorkSequence = 0,
        TranslatedGuestDraw? TranslatedDraw = null,
        GuestDrawKind DrawKind = GuestDrawKind.None);

    private static readonly object _gate = new();
    private static Thread? _thread;
    private static bool _closed;
    private static bool _splashHidden;
    private static bool _closeRequested;
    private static Presentation? _latestPresentation;
    private static bool _loggedFirstPresentedFrame;
    private static int _titleRefreshCounter;
    private static string? _lastWindowTitle;

    // CPU-rasterized perf HUD (F1), blitted over the frame like the Vulkan
    // presenter does; the panel texture lives for the window's lifetime.
    private static nint _overlayTexture;
    private static readonly byte[] _overlayPixels =
        new byte[PerfOverlay.PixelBufferWidth * PerfOverlay.PixelBufferHeight * 4];

    // Presenter objects and per-frame present state, all used on the SDL host thread.
    private static nint _device;
    private static nint _commandQueue;
    private static nint _metalLayer;
    private static nint _presentPipeline;
    private static nint _presentSampler;
    private static SdlHostWindow? _hostWindow;
    private static HostVideoOptions _videoOptions = HostVideoOptions.Default;
    private static double _drawableWidth;
    private static double _drawableHeight;
    private static nint _frameTexture;
    private static uint _frameTextureWidth;
    private static uint _frameTextureHeight;
    private static nint _presentTexture;
    private static uint _presentTextureWidth;
    private static uint _presentTextureHeight;
    private static nint _ownedVersionTexture;
    private static ulong _presentGuestAddress;
    private static long _presentedSequence = -1;
    private static uint _windowWidth;
    private static uint _windowHeight;

    public static bool TryConfigureVideo(HostVideoOptions options)
    {
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
                ? new Presentation(CreateBlackFrame(width, height), width, height, 1, IsSplash: false)
                : hasSplash
                ? new Presentation(splashPixels, splashWidth, splashHeight, 1, IsSplash: true)
                : new Presentation(null, width, height, 0, IsSplash: false);
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

            _latestPresentation = new Presentation(
                CreateBlackFrame(latest.Width, latest.Height),
                latest.Width,
                latest.Height,
                latest.Sequence + 1,
                IsSplash: false);
            Console.Error.WriteLine("[LOADER][INFO] Metal VideoOut hid splash");
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
            _latestPresentation = new Presentation(bgraFrame, width, height, sequence, IsSplash: false);
            if (_thread is not null)
            {
                return;
            }

            _windowWidth = width;
            _windowHeight = height;
            StartPresenterLocked();
        }
    }

    /// <summary>Asks a running presenter loop to close its window and return.</summary>
    public static void RequestClose()
    {
        Volatile.Write(ref _closeRequested, true);
        Volatile.Read(ref _hostWindow)?.Close();
    }

    private static void StartPresenterLocked()
    {
        if (HostMainThread.IsAvailable)
        {
            _thread = Thread.CurrentThread;
            HostMainThread.SetShutdownRequestHandler(RequestClose);
            HostMainThread.Post(Run);
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "SharpEmu Metal VideoOut",
        };
        _thread.Start();
    }

    private static void Run()
    {
        try
        {
            RunWindowLoop();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Metal VideoOut presenter failed: {exception}");
        }
        finally
        {
            lock (_gate)
            {
                _closed = true;
                _thread = null;
                // Wake guest-work waiters and backpressured producers so close
                // never strands a blocked guest thread.
                Monitor.PulseAll(_gate);
            }
        }
    }

    private static void RunWindowLoop()
    {
        MetalNative.EnsureFrameworksLoaded();

        _device = MetalNative.MTLCreateSystemDefaultDevice();
        if (_device == 0)
        {
            Console.Error.WriteLine("[LOADER][ERROR] No Metal device available.");
            return;
        }

        // Mirror the Vulkan presenter: fold the selected GPU's name into the
        // window title. Without this the Metal title never gains the "· <GPU>"
        // suffix the Vulkan path shows.
        var deviceName = MetalNative.ReadNsString(
            MetalNative.Send(_device, MetalNative.Selector("name")));
        if (!string.IsNullOrEmpty(deviceName))
        {
            VideoOutExports.SetSelectedGpuName(deviceName);
        }

        HostVideoOptions videoOptions;
        lock (_gate)
        {
            videoOptions = _videoOptions;
        }

        using var hostWindow = new SdlHostWindow(
            VideoOutExports.GetWindowTitle(),
            videoOptions,
            SdlGraphicsApi.Metal,
            ToggleMetalPerformanceHud);
        Volatile.Write(ref _hostWindow, hostWindow);
        var setupPool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            _metalLayer = hostWindow.CreateMetalLayer();
            ConfigureMetalLayer(_device, _metalLayer);
            _commandQueue = MetalNative.Send(_device, MetalNative.Selector("newCommandQueue"));
            if (!TryCreatePresentPipeline(_device, out _presentPipeline, out var pipelineError))
            {
                Console.Error.WriteLine($"[LOADER][ERROR] Metal present pipeline failed: {pipelineError}");
                return;
            }

            _presentSampler = CreateLinearSampler(_device);
            SyncDrawableSizeToLayer();
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(setupPool);
        }

        Console.Error.WriteLine("[LOADER][INFO] Metal VideoOut presenter started.");
        try
        {
            hostWindow.Run(
                static () => { },
                static _ => RenderFrameSafely(),
                static () => { });
        }
        finally
        {
            Volatile.Write(ref _hostWindow, null);
            _metalLayer = 0;
            if (_overlayTexture != 0)
            {
                // Submitted command buffers retain the texture until their GPU work completes.
                MetalNative.SendVoid(_overlayTexture, MetalNative.Selector("release"));
                _overlayTexture = 0;
            }
        }

        if (hostWindow.ClosedByUser)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Metal VideoOut window closed; requesting emulator shutdown.");
            VideoOutExports.NotifyPresentationWindowClosed();
        }
    }

    private static bool _metalHudVisible;

    /// <summary>
    /// Cmd+F1: Apple's Metal Performance HUD on the CAMetalLayer (plain F1 keeps
    /// the built-in CPU-rasterized perf overlay). Configured per Apple's
    /// "Customizing Metal Performance HUD": developerHUDProperties takes
    /// mode=default|disabled and logging=default, plus any MTL_HUD_* environment
    /// keys directly in the dictionary — all three HUD flags (enabled, per-frame
    /// logging, shader-compile logging) ride in one property set. Runs on the
    /// SDL host thread, the same thread as the render loop.
    /// </summary>
    private static void ToggleMetalPerformanceHud()
    {
        var layer = _metalLayer;
        if (layer == 0)
        {
            return;
        }

        var setProperties = MetalNative.Selector("setDeveloperHUDProperties:");
        if (!MetalNative.SendBool(layer, MetalNative.Selector("respondsToSelector:"), setProperties))
        {
            Console.Error.WriteLine("[LOADER][WARN] Metal Performance HUD unavailable on this macOS.");
            return;
        }

        _metalHudVisible = !_metalHudVisible;
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            var properties = MetalNative.Send(
                MetalNative.Class("NSMutableDictionary"), MetalNative.Selector("dictionary"));
            var setObjectForKey = MetalNative.Selector("setObject:forKey:");
            if (_metalHudVisible)
            {
                var defaultValue = MetalNative.NsString("default");
                MetalNative.SendVoid(properties, setObjectForKey, defaultValue, MetalNative.NsString("mode"));
                MetalNative.SendVoid(properties, setObjectForKey, defaultValue, MetalNative.NsString("logging"));
                MetalNative.SendVoid(
                    properties,
                    setObjectForKey,
                    MetalNative.NsString("1"),
                    MetalNative.NsString("MTL_HUD_LOG_SHADER_ENABLED"));
            }
            else
            {
                MetalNative.SendVoid(
                    properties, setObjectForKey, MetalNative.NsString("disabled"), MetalNative.NsString("mode"));
            }

            MetalNative.SendVoid(layer, setProperties, properties);
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }

        Console.Error.WriteLine(
            $"[LOADER][INFO] Metal Performance HUD {(_metalHudVisible ? "shown" : "hidden")} (Cmd+F1).");
    }

    /// <summary>The SDL host loop drains guest work continuously. The method
    /// remains as the enqueue-side hook used by guest image synchronization.</summary>
    internal static void ScheduleGuestWorkDrain()
    {
    }

    private static void RenderFrameSafely()
    {
        try
        {
            RenderFrame();
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Metal render frame failed: {exception}");
        }
    }

    private static void RenderFrame()
    {
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            if (Volatile.Read(ref _closeRequested))
            {
                return;
            }

            DrainGuestWork(_device, _commandQueue);

            if (TryTakePresentation(_presentedSequence, out var presentation))
            {
                _presentedSequence = presentation.Sequence;
                if (presentation.Pixels is not null)
                {
                    UploadFrame(
                        _device,
                        presentation,
                        ref _frameTexture,
                        ref _frameTextureWidth,
                        ref _frameTextureHeight);
                    SwitchPresentSource(
                        _frameTexture,
                        _frameTextureWidth,
                        _frameTextureHeight,
                        ownsTexture: false,
                        ref _presentTexture,
                        ref _presentTextureWidth,
                        ref _presentTextureHeight,
                        ref _ownedVersionTexture);
                    _presentGuestAddress = 0;
                }
                else if (presentation.TranslatedDraw is not null ||
                         presentation.DrawKind != GuestDrawKind.None)
                {
                    var drawTarget = ExecutePresentationDraw(_device, _commandQueue, presentation);
                    if (drawTarget != 0)
                    {
                        // Transient targets are pooled by the presenter; the
                        // present source borrows them.
                        SwitchPresentSource(
                            drawTarget,
                            presentation.Width,
                            presentation.Height,
                            ownsTexture: false,
                            ref _presentTexture,
                            ref _presentTextureWidth,
                            ref _presentTextureHeight,
                            ref _ownedVersionTexture);
                        _presentGuestAddress = 0;
                    }
                }
                else if (TryResolveGuestPresentation(
                             _device,
                             presentation,
                             out var guestTexture,
                             out var guestWidth,
                             out var guestHeight,
                             out var ownsGuestTexture))
                {
                    // Captured versions are immutable and owned here; mutable
                    // address-keyed images are re-resolved at encode time so a
                    // write swapping the texture never leaves a stale handle.
                    SwitchPresentSource(
                        ownsGuestTexture ? guestTexture : 0,
                        guestWidth,
                        guestHeight,
                        ownsGuestTexture,
                        ref _presentTexture,
                        ref _presentTextureWidth,
                        ref _presentTextureHeight,
                        ref _ownedVersionTexture);
                    _presentGuestAddress = ownsGuestTexture ? 0 : presentation.GuestImageAddress;
                }
            }

            if (_presentGuestAddress != 0)
            {
                // Re-resolve every frame: a guest-image write swaps the texture
                // behind the address.
                _presentTexture = 0;
                lock (_gate)
                {
                    if (_guestImages.TryGetValue(_presentGuestAddress, out var borrowed) &&
                        borrowed.Initialized)
                    {
                        _presentTexture = borrowed.Texture;
                        _presentTextureWidth = borrowed.Width;
                        _presentTextureHeight = borrowed.Height;
                    }
                }
            }

            // The window title reflects late guest state (the game registers its
            // application name after boot) plus the GPU suffix; the Vulkan
            // presenter re-reads it, so refresh periodically here for parity.
            if ((++_titleRefreshCounter & 0x3F) == 0)
            {
                var title = VideoOutExports.GetWindowTitle();
                if (!string.Equals(title, _lastWindowTitle, StringComparison.Ordinal))
                {
                    _lastWindowTitle = title;
                    Volatile.Read(ref _hostWindow)?.SetTitle(title);
                }
            }

            // The window is resizable, so the backing layer's bounds follow the
            // window while its drawable size does not — match them before asking
            // for a drawable, or nextDrawable keeps handing back the original
            // resolution and Core Animation stretches it (blurry, mis-scaled
            // overlay). No-op when the size is unchanged, i.e. almost every tick.
            var hostWindow = Volatile.Read(ref _hostWindow);
            if (hostWindow?.ConsumeSurfaceRestore() == true)
            {
                _drawableWidth = 0;
                _drawableHeight = 0;
            }

            SyncDrawableSizeToLayer();

            if (hostWindow?.IsMinimized == true)
            {
                return;
            }

            var drawable = MetalNative.Send(_metalLayer, MetalNative.Selector("nextDrawable"));
            if (drawable == 0)
            {
                // No free drawable this tick; the next timer fire retries.
                return;
            }

            if (_presentTexture != 0 && !_loggedFirstPresentedFrame)
            {
                _loggedFirstPresentedFrame = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Metal VideoOut presenting {_presentTextureWidth}x{_presentTextureHeight}.");
            }

            var drawableTexture = MetalNative.Send(drawable, MetalNative.Selector("texture"));
            var commandBuffer = MetalNative.Send(_commandQueue, MetalNative.Selector("commandBuffer"));
            var pass = CreateClearPass(
                drawableTexture,
                new MtlClearColor { Red = 0, Green = 0, Blue = 0, Alpha = 1 });
            var encoder = MetalNative.Send(
                commandBuffer, MetalNative.Selector("renderCommandEncoderWithDescriptor:"), pass);
            if (_presentTexture != 0)
            {
                EncodePresent(
                    encoder,
                    _presentPipeline,
                    _presentSampler,
                    _presentTexture,
                    _presentTextureWidth,
                    _presentTextureHeight,
                    _drawableWidth,
                    _drawableHeight);
            }

            if (PerfOverlay.DrawOnScreen)
            {
                EncodeOverlay(encoder);
            }

            MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("presentDrawable:"), drawable);
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
            PerfOverlay.RecordPresent();
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    // Uses the present pipeline with a viewport at the selected corner.
    private static void EncodeOverlay(nint encoder)
    {
        if (_overlayTexture == 0)
        {
            var descriptor = MetalNative.SendTextureDescriptor(
                MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                PixelFormatBgra8Unorm,
                (nuint)PerfOverlay.DisplayWidth,
                (nuint)PerfOverlay.DisplayHeight,
                mipmapped: false);
            _overlayTexture = MetalNative.Send(
                _device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            if (_overlayTexture == 0)
            {
                return;
            }
        }

        int pendingWork;
        lock (_gate)
        {
            pendingWork = _pendingGuestWorkCount;
        }

        PerfOverlay.Fill(_overlayPixels, pendingWork, 0);
        ReplaceTextureContents(
            _overlayTexture,
            (uint)PerfOverlay.DisplayWidth,
            (uint)PerfOverlay.DisplayHeight,
            _overlayPixels,
            PerfOverlay.PixelBufferWidth,
            bytesPerPixel: 4);

        var rectangle = PerfOverlay.GetRectangle((int)_drawableWidth, (int)_drawableHeight);
        var panelWidth = rectangle.Width;
        var panelHeight = rectangle.Height;
        if (panelWidth <= 0 || panelHeight <= 0)
        {
            return;
        }

        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), _presentPipeline);
        MetalNative.SendVoidViewport(
            encoder,
            MetalNative.Selector("setViewport:"),
            new MtlViewport
            {
                OriginX = rectangle.Left,
                OriginY = rectangle.Top,
                Width = panelWidth,
                Height = panelHeight,
                ZNear = 0,
                ZFar = 1,
            });
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentTexture:atIndex:"), _overlayTexture, 0);
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentSamplerState:atIndex:"), _presentSampler, 0);
        MetalNative.SendDrawPrimitives(
            encoder,
            MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"),
            PrimitiveTypeTriangle,
            0,
            3);
    }

    private static void ConfigureMetalLayer(nint device, nint layer)
    {
        MetalNative.SendVoid(layer, MetalNative.Selector("setDevice:"), device);
        MetalNative.Send(layer, MetalNative.Selector("setPixelFormat:"), (nint)PixelFormatBgra8Unorm);
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setOpaque:"), true);
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setFramebufferOnly:"), true);

        var setDisplaySync = MetalNative.Selector("setDisplaySyncEnabled:");
        if (MetalNative.SendBool(layer, MetalNative.Selector("respondsToSelector:"), setDisplaySync))
        {
            MetalNative.SendVoidBool(layer, setDisplaySync, _videoOptions.VSync);
        }
    }

    /// <summary>Keeps the SDL-owned CAMetalLayer drawable in host pixels as the
    /// window resizes or moves between displays with different scale factors.</summary>
    private static void SyncDrawableSizeToLayer()
    {
        var hostWindow = Volatile.Read(ref _hostWindow);
        if (hostWindow is null || _metalLayer == 0)
        {
            return;
        }

        var pixelSize = hostWindow.PixelSize;
        var width = (double)pixelSize.Width;
        var height = (double)pixelSize.Height;

        // Retina hosts often present at 3840x2160 into a 1920x1080 window.
        // Cap is opt-in: defaulting it on silently drops present resolution for
        // every Metal title. SHARPEMU_METAL_CAP_DRAWABLE=1 enables the long-edge
        // cap; SHARPEMU_METAL_FULL_DRAWABLE=1 remains a no-op when the cap is off.
        if (string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_METAL_CAP_DRAWABLE"),
                "1",
                StringComparison.Ordinal) &&
            !string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_METAL_FULL_DRAWABLE"),
                "1",
                StringComparison.Ordinal))
        {
            const double maxLongEdge = 1920.0;
            var longEdge = Math.Max(width, height);
            if (longEdge > maxLongEdge)
            {
                var scale = maxLongEdge / longEdge;
                width = Math.Round(width * scale);
                height = Math.Round(height * scale);
            }
        }

        if (width == _drawableWidth && height == _drawableHeight)
        {
            return;
        }

        _drawableWidth = width;
        _drawableHeight = height;
        MetalNative.SendVoidSize(
            _metalLayer,
            MetalNative.Selector("setDrawableSize:"),
            new CGSize { Width = width, Height = height });
    }

    private static bool TryCreatePresentPipeline(nint device, out nint pipeline, out string error)
    {
        pipeline = 0;
        var dbg = Environment.GetEnvironmentVariable("SHARPEMU_METAL_DBG");
        var fragmentSource = dbg switch
        {
            "solid" => MslFixedShaders.CreateSolidFragment(0f, 1f, 0f, 1f),
            "uv" => MslFixedShaders.CreateAttributeFragment(0),
            _ => MslFixedShaders.CreatePresentFragment(),
        };
        if (!TryCompileLibrary(device, MslFixedShaders.CreateFullscreenVertex(1), out var vertexLibrary, out error) ||
            !TryCompileLibrary(device, fragmentSource, out var fragmentLibrary, out error))
        {
            return false;
        }

        var selNewFunction = MetalNative.Selector("newFunctionWithName:");
        var fragmentEntry = dbg switch { "solid" => "solid_fs", "uv" => "attribute_fs", _ => "present_fs" };
        var vertexFunction = MetalNative.Send(vertexLibrary, selNewFunction, MetalNative.NsString("fullscreen_vs"));
        var fragmentFunction = MetalNative.Send(fragmentLibrary, selNewFunction, MetalNative.NsString(fragmentEntry));
        if (vertexFunction == 0 || fragmentFunction == 0)
        {
            error = "present shader entry points missing from the compiled libraries";
            return false;
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLRenderPipelineDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setVertexFunction:"), vertexFunction);
        MetalNative.SendVoid(descriptor, MetalNative.Selector("setFragmentFunction:"), fragmentFunction);
        var colorAttachment = MetalNative.SendAtIndex(
            MetalNative.Send(descriptor, MetalNative.Selector("colorAttachments")),
            MetalNative.Selector("objectAtIndexedSubscript:"),
            0);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setPixelFormat:"), (nint)PixelFormatBgra8Unorm);

        nint nsError = 0;
        pipeline = MetalNative.Send(
            device,
            MetalNative.Selector("newRenderPipelineStateWithDescriptor:error:"),
            descriptor,
            ref nsError);
        if (pipeline == 0)
        {
            error = MetalNative.DescribeError(nsError);
            return false;
        }

        return true;
    }

    private static bool TryCompileLibrary(nint device, string source, out nint library, out string error)
    {
        error = string.Empty;

        // Fast-math off everywhere for parity with translated guest shaders,
        // whose GCN float semantics do not survive it.
        var options = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLCompileOptions"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoidBool(options, MetalNative.Selector("setFastMathEnabled:"), false);

        nint nsError = 0;
        library = MetalNative.Send(
            device,
            MetalNative.Selector("newLibraryWithSource:options:error:"),
            MetalNative.NsString(source),
            options,
            ref nsError);
        if (library == 0)
        {
            error = MetalNative.DescribeError(nsError);
            return false;
        }

        return true;
    }

    private static nint CreateLinearSampler(nint device)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLSamplerDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.Send(descriptor, MetalNative.Selector("setMinFilter:"), (nint)SamplerMinMagFilterLinear);
        MetalNative.Send(descriptor, MetalNative.Selector("setMagFilter:"), (nint)SamplerMinMagFilterLinear);
        return MetalNative.Send(device, MetalNative.Selector("newSamplerStateWithDescriptor:"), descriptor);
    }

    private static void UploadFrame(
        nint device,
        Presentation presentation,
        ref nint frameTexture,
        ref uint textureWidth,
        ref uint textureHeight)
    {
        if (frameTexture == 0 || textureWidth != presentation.Width || textureHeight != presentation.Height)
        {
            if (frameTexture != 0)
            {
                MetalNative.SendVoid(frameTexture, MetalNative.Selector("release"));
            }

            var descriptor = MetalNative.SendTextureDescriptor(
                MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                PixelFormatBgra8Unorm,
                presentation.Width,
                presentation.Height,
                mipmapped: false);
            // Shared (0): CPU-uploaded frame, GPU-sampled by the present pass;
            // the Managed default reads stale on unified memory.
            MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), (nint)0);
            frameTexture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            textureWidth = presentation.Width;
            textureHeight = presentation.Height;
        }

        ReplaceTextureContents(
            frameTexture,
            presentation.Width,
            presentation.Height,
            presentation.Pixels!,
            presentation.Width,
            bytesPerPixel: 4);
    }

    private static nint CreateClearPass(nint targetTexture, MtlClearColor clearColor)
    {
        var pass = MetalNative.Send(
            MetalNative.Class("MTLRenderPassDescriptor"),
            MetalNative.Selector("renderPassDescriptor"));
        var colorAttachment = MetalNative.SendAtIndex(
            MetalNative.Send(pass, MetalNative.Selector("colorAttachments")),
            MetalNative.Selector("objectAtIndexedSubscript:"),
            0);
        MetalNative.SendVoid(colorAttachment, MetalNative.Selector("setTexture:"), targetTexture);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setLoadAction:"), (nint)LoadActionClear);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setStoreAction:"), (nint)StoreActionStore);
        MetalNative.SendVoidClearColor(
            colorAttachment,
            MetalNative.Selector("setClearColor:"),
            clearColor);
        return pass;
    }

    private static void EncodePresent(
        nint encoder,
        nint pipeline,
        nint sampler,
        nint frameTexture,
        uint frameWidth,
        uint frameHeight,
        double drawableWidth,
        double drawableHeight)
    {
        MetalNative.SendVoid(encoder, MetalNative.Selector("setRenderPipelineState:"), pipeline);

        var scaleX = drawableWidth / frameWidth;
        var scaleY = drawableHeight / frameHeight;
        var viewportWidth = drawableWidth;
        var viewportHeight = drawableHeight;
        if (_videoOptions.ScalingMode != HostScalingMode.Stretch)
        {
            var scale = _videoOptions.ScalingMode == HostScalingMode.Cover
                ? Math.Max(scaleX, scaleY)
                : Math.Min(scaleX, scaleY);
            if (_videoOptions.ScalingMode == HostScalingMode.Integer && scale >= 1)
            {
                scale = Math.Floor(scale);
            }

            viewportWidth = frameWidth * scale;
            viewportHeight = frameHeight * scale;
        }

        MetalNative.SendVoidViewport(
            encoder,
            MetalNative.Selector("setViewport:"),
            new MtlViewport
            {
                OriginX = (drawableWidth - viewportWidth) * 0.5,
                OriginY = (drawableHeight - viewportHeight) * 0.5,
                Width = viewportWidth,
                Height = viewportHeight,
                ZNear = 0,
                ZFar = 1,
            });

        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentTexture:atIndex:"), frameTexture, 0);
        MetalNative.SendSetAtIndex(
            encoder, MetalNative.Selector("setFragmentSamplerState:atIndex:"), sampler, 0);
        MetalNative.SendDrawPrimitives(
            encoder,
            MetalNative.Selector("drawPrimitives:vertexStart:vertexCount:"),
            PrimitiveTypeTriangle,
            0,
            3);
    }

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
}
