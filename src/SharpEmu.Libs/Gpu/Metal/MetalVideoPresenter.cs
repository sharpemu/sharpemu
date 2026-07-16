// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// The Metal presenter: an AppKit window hosting a CAMetalLayer, driven by a manually
/// pumped NSApplication event loop so its structure matches the Vulkan presenter's
/// poll-and-render loop. Everything AppKit runs on the process main thread via
/// <see cref="HostMainThread"/> (AppKit traps off-main), which the CLI parks for us.
///
/// This phase presents CPU-produced BGRA frames and the splash; guest draws, guest
/// images, and compute arrive in later phases.
/// </summary>
internal static class MetalVideoPresenter
{
    private const uint DefaultWindowWidth = 1280;
    private const uint DefaultWindowHeight = 720;

    // NSWindow style: Titled | Closable | Miniaturizable — fixed border like the
    // Vulkan presenter's window.
    private const nuint WindowStyleMask = 1 | 2 | 4;
    private const nuint BackingStoreBuffered = 2;
    private const nuint PixelFormatBgra8Unorm = (nuint)MtlPixelFormat.Bgra8Unorm;
    private const nuint LoadActionClear = 2;
    private const nuint StoreActionStore = 1;
    private const nuint PrimitiveTypeTriangle = 3;
    private const nuint SamplerMinMagFilterLinear = 1;

    private sealed record Presentation(
        byte[]? Pixels,
        uint Width,
        uint Height,
        long Sequence,
        bool IsSplash);

    private static readonly object _gate = new();
    private static Thread? _thread;
    private static bool _closed;
    private static bool _splashHidden;
    private static bool _closeRequested;
    private static Presentation? _latestPresentation;
    private static uint _windowWidth;
    private static uint _windowHeight;

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
            }
        }
    }

    private static void RunWindowLoop()
    {
        MetalNative.EnsureFrameworksLoaded();

        var device = MetalNative.MTLCreateSystemDefaultDevice();
        if (device == 0)
        {
            Console.Error.WriteLine("[LOADER][ERROR] No Metal device available.");
            return;
        }

        uint width, height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? DefaultWindowWidth : _windowWidth;
            height = _windowHeight == 0 ? DefaultWindowHeight : _windowHeight;
        }

        var selNextDrawable = MetalNative.Selector("nextDrawable");
        var selTexture = MetalNative.Selector("texture");
        var selCommandBuffer = MetalNative.Selector("commandBuffer");
        var selRenderEncoder = MetalNative.Selector("renderCommandEncoderWithDescriptor:");
        var selEndEncoding = MetalNative.Selector("endEncoding");
        var selPresentDrawable = MetalNative.Selector("presentDrawable:");
        var selCommit = MetalNative.Selector("commit");
        var selSendEvent = MetalNative.Selector("sendEvent:");
        var selDistantPast = MetalNative.Selector("distantPast");
        var selIsVisible = MetalNative.Selector("isVisible");
        var nsDateClass = MetalNative.Class("NSDate");

        var setupPool = MetalNative.objc_autoreleasePoolPush();
        nint application, window, layer, queue, pipeline, sampler;
        nint runLoopMode;
        double drawableWidth, drawableHeight;
        try
        {
            application = MetalNative.Send(
                MetalNative.Class("NSApplication"), MetalNative.Selector("sharedApplication"));
            // NSApplicationActivationPolicyRegular: dock icon + key window like any app.
            MetalNative.Send(application, MetalNative.Selector("setActivationPolicy:"), 0);
            MetalNative.SendVoid(application, MetalNative.Selector("finishLaunching"));

            window = CreateWindow(width, height);
            layer = CreateLayer(device, window, out drawableWidth, out drawableHeight);
            queue = MetalNative.Send(device, MetalNative.Selector("newCommandQueue"));
            if (!TryCreatePresentPipeline(device, out pipeline, out var pipelineError))
            {
                Console.Error.WriteLine($"[LOADER][ERROR] Metal present pipeline failed: {pipelineError}");
                return;
            }

            sampler = CreateLinearSampler(device);

            MetalNative.SendVoidBool(
                application, MetalNative.Selector("activateIgnoringOtherApps:"), true);

            // The pump's run-loop mode string outlives the setup pool.
            runLoopMode = MetalNative.Send(
                MetalNative.NsString("kCFRunLoopDefaultMode"), MetalNative.Selector("retain"));
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(setupPool);
        }

        Console.Error.WriteLine("[LOADER][INFO] Metal VideoOut presenter started.");

        nint frameTexture = 0;
        uint frameTextureWidth = 0, frameTextureHeight = 0;
        long presentedSequence = -1;
        var userClosed = false;

        while (true)
        {
            var pool = MetalNative.objc_autoreleasePoolPush();
            try
            {
                // Drain pending AppKit events, non-blocking (untilDate: distantPast).
                var distantPast = MetalNative.Send(nsDateClass, selDistantPast);
                while (true)
                {
                    var nsEvent = MetalNative.SendNextEvent(
                        application,
                        MetalNative.Selector("nextEventMatchingMask:untilDate:inMode:dequeue:"),
                        ulong.MaxValue,
                        distantPast,
                        runLoopMode,
                        dequeue: true);
                    if (nsEvent == 0)
                    {
                        break;
                    }

                    MetalNative.SendVoid(application, selSendEvent, nsEvent);
                }

                if (Volatile.Read(ref _closeRequested))
                {
                    break;
                }

                if (!MetalNative.SendBool(window, selIsVisible))
                {
                    userClosed = true;
                    break;
                }

                Presentation? presentation;
                lock (_gate)
                {
                    presentation = _latestPresentation;
                }

                if (presentation is { Pixels: not null } && presentation.Sequence != presentedSequence)
                {
                    UploadFrame(
                        device,
                        presentation,
                        ref frameTexture,
                        ref frameTextureWidth,
                        ref frameTextureHeight);
                    presentedSequence = presentation.Sequence;
                }

                // nextDrawable blocks until the layer has a free drawable, which paces
                // the loop at presentation rate.
                var drawable = MetalNative.Send(layer, selNextDrawable);
                if (drawable == 0)
                {
                    Thread.Sleep(8);
                    continue;
                }

                var drawableTexture = MetalNative.Send(drawable, selTexture);
                var commandBuffer = MetalNative.Send(queue, selCommandBuffer);
                var pass = CreateClearPass(drawableTexture);
                var encoder = MetalNative.Send(commandBuffer, selRenderEncoder, pass);
                if (frameTexture != 0)
                {
                    EncodePresent(
                        encoder,
                        pipeline,
                        sampler,
                        frameTexture,
                        frameTextureWidth,
                        frameTextureHeight,
                        drawableWidth,
                        drawableHeight);
                }

                MetalNative.SendVoid(encoder, selEndEncoding);
                MetalNative.SendVoid(commandBuffer, selPresentDrawable, drawable);
                MetalNative.SendVoid(commandBuffer, selCommit);
            }
            finally
            {
                MetalNative.objc_autoreleasePoolPop(pool);
            }
        }

        var closePool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            MetalNative.SendVoid(window, MetalNative.Selector("close"));
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(closePool);
        }

        if (userClosed)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Metal VideoOut window closed; requesting emulator shutdown.");
            VideoOutExports.NotifyPresentationWindowClosed();
        }
    }

    private static nint CreateWindow(uint width, uint height)
    {
        var window = MetalNative.SendInitWindow(
            MetalNative.Send(MetalNative.Class("NSWindow"), MetalNative.Selector("alloc")),
            MetalNative.Selector("initWithContentRect:styleMask:backing:defer:"),
            new CGRect { X = 0, Y = 0, Width = width, Height = height },
            WindowStyleMask,
            BackingStoreBuffered,
            defer: false);
        // The presenter owns the handle; AppKit must not free it on user close.
        MetalNative.SendVoidBool(window, MetalNative.Selector("setReleasedWhenClosed:"), false);
        MetalNative.SendVoid(
            window,
            MetalNative.Selector("setTitle:"),
            MetalNative.NsString(VideoOutExports.GetWindowTitle()));
        MetalNative.SendVoid(window, MetalNative.Selector("center"));
        MetalNative.SendVoid(window, MetalNative.Selector("makeKeyAndOrderFront:"), 0);
        return window;
    }

    private static nint CreateLayer(nint device, nint window, out double drawableWidth, out double drawableHeight)
    {
        var contentView = MetalNative.Send(window, MetalNative.Selector("contentView"));
        var scale = MetalNative.SendDouble(window, MetalNative.Selector("backingScaleFactor"));
        if (scale <= 0)
        {
            scale = 1;
        }

        uint width, height;
        lock (_gate)
        {
            width = _windowWidth == 0 ? DefaultWindowWidth : _windowWidth;
            height = _windowHeight == 0 ? DefaultWindowHeight : _windowHeight;
        }

        drawableWidth = width * scale;
        drawableHeight = height * scale;

        var layer = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("CAMetalLayer"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        MetalNative.SendVoid(layer, MetalNative.Selector("setDevice:"), device);
        MetalNative.Send(layer, MetalNative.Selector("setPixelFormat:"), (nint)PixelFormatBgra8Unorm);
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setFramebufferOnly:"), true);
        MetalNative.SendVoidDouble(layer, MetalNative.Selector("setContentsScale:"), scale);
        MetalNative.SendVoidSize(
            layer,
            MetalNative.Selector("setDrawableSize:"),
            new CGSize { Width = drawableWidth, Height = drawableHeight });

        // Layer-hosting view: assign the layer before enabling wantsLayer.
        MetalNative.SendVoid(contentView, MetalNative.Selector("setLayer:"), layer);
        MetalNative.SendVoidBool(contentView, MetalNative.Selector("setWantsLayer:"), true);
        return layer;
    }

    private static bool TryCreatePresentPipeline(nint device, out nint pipeline, out string error)
    {
        pipeline = 0;
        if (!TryCompileLibrary(device, MslFixedShaders.CreateFullscreenVertex(1), out var vertexLibrary, out error) ||
            !TryCompileLibrary(device, MslFixedShaders.CreatePresentFragment(), out var fragmentLibrary, out error))
        {
            return false;
        }

        var selNewFunction = MetalNative.Selector("newFunctionWithName:");
        var vertexFunction = MetalNative.Send(vertexLibrary, selNewFunction, MetalNative.NsString("fullscreen_vs"));
        var fragmentFunction = MetalNative.Send(fragmentLibrary, selNewFunction, MetalNative.NsString("present_fs"));
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
            frameTexture = MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
            textureWidth = presentation.Width;
            textureHeight = presentation.Height;
        }

        unsafe
        {
            fixed (byte* pixels = presentation.Pixels)
            {
                MetalNative.SendReplaceRegion(
                    frameTexture,
                    MetalNative.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                    new MtlRegion
                    {
                        X = 0,
                        Y = 0,
                        Z = 0,
                        Width = presentation.Width,
                        Height = presentation.Height,
                        Depth = 1,
                    },
                    0,
                    (nint)pixels,
                    presentation.Width * 4);
            }
        }
    }

    private static nint CreateClearPass(nint drawableTexture)
    {
        var pass = MetalNative.Send(
            MetalNative.Class("MTLRenderPassDescriptor"),
            MetalNative.Selector("renderPassDescriptor"));
        var colorAttachment = MetalNative.SendAtIndex(
            MetalNative.Send(pass, MetalNative.Selector("colorAttachments")),
            MetalNative.Selector("objectAtIndexedSubscript:"),
            0);
        MetalNative.SendVoid(colorAttachment, MetalNative.Selector("setTexture:"), drawableTexture);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setLoadAction:"), (nint)LoadActionClear);
        MetalNative.Send(colorAttachment, MetalNative.Selector("setStoreAction:"), (nint)StoreActionStore);
        MetalNative.SendVoidClearColor(
            colorAttachment,
            MetalNative.Selector("setClearColor:"),
            new MtlClearColor { Red = 0, Green = 0, Blue = 0, Alpha = 1 });
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

        // Aspect-fit letterbox: scale the frame into the drawable via the viewport.
        var scale = Math.Min(drawableWidth / frameWidth, drawableHeight / frameHeight);
        var viewportWidth = frameWidth * scale;
        var viewportHeight = frameHeight * scale;
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
