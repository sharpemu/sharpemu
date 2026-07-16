// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// The Metal presenter: an AppKit window hosting a CAMetalLayer. A CADisplayLink
/// requested from the content view drives <see cref="RenderFrame"/> in sync with the
/// display refresh, on the main run loop — the loop whose Core Animation observer
/// commits presented drawables to the window server, so it must be a real running
/// run loop (a hand-pumped event drain never fires that observer and the window
/// stays black). Everything AppKit runs on the process main thread via
/// <see cref="HostMainThread"/> (AppKit traps off-main), which the CLI parks for us.
/// </summary>
internal static partial class MetalVideoPresenter
{
    private const uint DefaultWindowWidth = 1280;
    private const uint DefaultWindowHeight = 720;

    // NSWindow style: Titled | Closable | Miniaturizable — fixed border like the
    // Vulkan presenter's window.
    private const nuint WindowStyleMask = 1 | 2 | 4;
    private const nuint BackingStoreBuffered = 2;
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

    // Presenter objects and per-frame present state, shared between window setup
    // and the display-link RenderFrame callback (both on the main thread).
    private static nint _device;
    private static nint _commandQueue;
    private static nint _metalLayer;
    private static nint _presentPipeline;
    private static nint _presentSampler;
    private static nint _window;
    private static nint _application;
    private static nint _renderTimer;
    private static nint _renderTimerTarget;
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
    private static bool _userClosed;
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

        // Fixed window like the Vulkan presenter: guest frames letterbox into
        // it. Sizing the window from the guest's display mode (4K) exceeds the
        // screen — macOS clamps the window while the layer keeps the requested
        // geometry, leaving the visible region showing nothing but clear.
        const uint width = DefaultWindowWidth;
        const uint height = DefaultWindowHeight;

        var setupPool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            _application = MetalNative.Send(
                MetalNative.Class("NSApplication"), MetalNative.Selector("sharedApplication"));
            // NSApplicationActivationPolicyRegular: dock icon + key window like any app.
            MetalNative.Send(_application, MetalNative.Selector("setActivationPolicy:"), 0);
            MetalNative.SendVoid(_application, MetalNative.Selector("finishLaunching"));

            _window = CreateWindow(width, height);
            _metalLayer = CreateLayer(_device, _window, out _drawableWidth, out _drawableHeight);
            _commandQueue = MetalNative.Send(_device, MetalNative.Selector("newCommandQueue"));
            if (!TryCreatePresentPipeline(_device, out _presentPipeline, out var pipelineError))
            {
                Console.Error.WriteLine($"[LOADER][ERROR] Metal present pipeline failed: {pipelineError}");
                return;
            }

            _presentSampler = CreateLinearSampler(_device);

            MetalNative.SendVoid(_window, MetalNative.Selector("makeKeyAndOrderFront:"), 0);
            MetalNative.SendVoidBool(
                _application, MetalNative.Selector("activateIgnoringOtherApps:"), true);

            // A repeating NSTimer on this (main) run loop fires onFrame: at the
            // display rate. CADisplayLink (NSView.displayLinkWithTarget:selector:)
            // is the natural choice but its callback never fires under the x86-64
            // Rosetta process this emulator runs as — proven in isolation against
            // a bare AppKit harness, where a timer fires and composites and the
            // display link does not. nextDrawable still throttles presentation to
            // the display, so the timer only needs to keep up, not pace precisely.
            _renderTimerTarget = CreateRenderTimerTarget();
            _renderTimer = MetalNative.Send(
                MetalNative.SendTimer(
                    MetalNative.Class("NSTimer"),
                    MetalNative.Selector("scheduledTimerWithTimeInterval:target:selector:userInfo:repeats:"),
                    1.0 / 60.0,
                    _renderTimerTarget,
                    MetalNative.Selector("onFrame:"),
                    0,
                    repeats: true),
                MetalNative.Selector("retain"));
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(setupPool);
        }

        Console.Error.WriteLine("[LOADER][INFO] Metal VideoOut presenter started.");

        // [NSApp run] runs the main run loop (its Core Animation observer commits
        // presented drawables to the window server) AND fully activates the app,
        // which a bare CFRunLoopRun does not — the CADisplayLink is only serviced
        // once the app is running, and NSApp dispatches window events itself.
        // Returns once RenderFrame stops it.
        MetalNative.SendVoid(_application, MetalNative.Selector("run"));

        var closePool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            MetalNative.SendVoid(_window, MetalNative.Selector("close"));
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(closePool);
        }

        if (_userClosed)
        {
            Console.Error.WriteLine(
                "[LOADER][WARN] Metal VideoOut window closed; requesting emulator shutdown.");
            VideoOutExports.NotifyPresentationWindowClosed();
        }
    }

    private static unsafe nint CreateRenderTimerTarget()
    {
        // A minimal NSObject subclass whose onFrame: is our unmanaged callback —
        // the dependency-free way to hand a target/selector to NSTimer without a
        // binding library. Registered once per process.
        var cls = MetalNative.objc_allocateClassPair(
            MetalNative.Class("NSObject"), "SharpEmuRenderTimerTarget", 0);
        if (cls != 0)
        {
            var imp = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnRenderTimer;
            // "v@:@": void return, self, _cmd, one object argument (the timer).
            MetalNative.class_addMethod(cls, MetalNative.Selector("onFrame:"), imp, "v@:@");
            MetalNative.objc_registerClassPair(cls);
        }
        else
        {
            cls = MetalNative.Class("SharpEmuRenderTimerTarget");
        }

        return MetalNative.Send(
            MetalNative.Send(cls, MetalNative.Selector("alloc")), MetalNative.Selector("init"));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnRenderTimer(nint self, nint cmd, nint timer)
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
            // NSApp.run dispatches window events itself, so there is no manual
            // event drain here.
            var visible = MetalNative.SendBool(_window, MetalNative.Selector("isVisible"));
            if (Volatile.Read(ref _closeRequested) || !visible)
            {
                _userClosed = !visible && !Volatile.Read(ref _closeRequested);
                MetalNative.SendVoid(_renderTimer, MetalNative.Selector("invalidate"));
                // Stop both the AppKit loop and the underlying CFRunLoop so
                // [NSApp run] returns.
                MetalNative.SendVoid(_application, MetalNative.Selector("stop:"), 0);
                MetalNative.CFRunLoopStop(MetalNative.CFRunLoopGetMain());
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

            MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("presentDrawable:"), drawable);
            MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
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
        // makeKeyAndOrderFront happens after the metal layer is attached.
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

        const uint width = DefaultWindowWidth;
        const uint height = DefaultWindowHeight;
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

        // A manually created layer defaults to a zero-size frame, and a hosted
        // layer's geometry is the caller's job: without this the presenter
        // happily presents every drawable into a layer with no on-screen
        // extent — a permanently black window.
        MetalNative.SendVoidRect(
            layer,
            MetalNative.Selector("setFrame:"),
            new CGRect { X = 0, Y = 0, Width = width, Height = height });

        // wantsLayer FIRST, then the layer: that makes the metal layer the
        // view's AppKit-managed BACKING layer (geometry and window-server
        // commits handled by AppKit) — the SDL/GLFW pattern. The reverse order
        // creates a layer-hosting view whose tree the app must commit itself,
        // which never composites under a manually pumped run loop.
        MetalNative.SendVoidBool(contentView, MetalNative.Selector("setWantsLayer:"), true);
        MetalNative.SendVoid(contentView, MetalNative.Selector("setLayer:"), layer);
        MetalNative.SendVoid(MetalNative.Class("CATransaction"), MetalNative.Selector("flush"));
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
