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

    // NSWindow style: Titled | Closable | Miniaturizable | Resizable. Resizable
    // both lets the user drag the window edges and turns the green zoom button
    // into the full-screen toggle (paired with the collection behavior below).
    private const nuint WindowStyleMask = 1 | 2 | 4 | 8;

    // NSWindowCollectionBehaviorFullScreenPrimary: opt this window into native
    // full-screen, so the green button enters full-screen rather than zooming.
    private const nuint FullScreenPrimaryBehavior = 1 << 7;
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
    private static int _titleRefreshCounter;
    private static string? _lastWindowTitle;

    // CPU-rasterized perf HUD (F1), blitted over the frame like the Vulkan
    // presenter does; the panel texture lives for the window's lifetime.
    private static nint _overlayTexture;
    private static readonly byte[] _overlayPixels =
        new byte[PerfOverlay.PanelWidth * PerfOverlay.PanelHeight * 4];

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

        // Mirror the Vulkan presenter: fold the selected GPU's name into the
        // window title. Without this the Metal title never gains the "· <GPU>"
        // suffix the Vulkan path shows.
        var deviceName = MetalNative.ReadNsString(
            MetalNative.Send(_device, MetalNative.Selector("name")));
        if (!string.IsNullOrEmpty(deviceName))
        {
            VideoOutExports.SetSelectedGpuName(deviceName);
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

            // Swap in the key-capturing view before the metal layer attaches so
            // the layer lands on the input-aware content view.
            var keyView = MetalNative.SendInitFrame(
                MetalNative.Send(CreateKeyViewClass(), MetalNative.Selector("alloc")),
                MetalNative.Selector("initWithFrame:"),
                new CGRect { X = 0, Y = 0, Width = width, Height = height });
            MetalNative.SendVoid(_window, MetalNative.Selector("setContentView:"), keyView);

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
            MetalNative.SendVoid(_window, MetalNative.Selector("makeFirstResponder:"), keyView);
            MetalHostInput.Attach();

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

    /// <summary>
    /// An NSView subclass that records key events for pad emulation. Overriding
    /// keyDown:/keyUp: (instead of an event monitor) needs no ObjC blocks, and
    /// swallowing the events also silences the system alert beep AppKit plays
    /// for unhandled keys. Registered once per process.
    /// </summary>
    private static unsafe nint CreateKeyViewClass()
    {
        var cls = MetalNative.objc_allocateClassPair(
            MetalNative.Class("NSView"), "SharpEmuMetalView", 0);
        if (cls == 0)
        {
            return MetalNative.Class("SharpEmuMetalView");
        }

        var keyDown = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnKeyDown;
        MetalNative.class_addMethod(cls, MetalNative.Selector("keyDown:"), keyDown, "v@:@");
        var keyUp = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnKeyUp;
        MetalNative.class_addMethod(cls, MetalNative.Selector("keyUp:"), keyUp, "v@:@");
        // Command-modified keys never reach keyDown: — AppKit routes them through
        // performKeyEquivalent:, so Cmd+F1 (Metal Performance HUD) hooks in here.
        var keyEquivalent = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, byte>)&OnPerformKeyEquivalent;
        MetalNative.class_addMethod(
            cls, MetalNative.Selector("performKeyEquivalent:"), keyEquivalent, "c@:@");
        // First responder status is what routes key events to this view.
        var accepts = (nint)(delegate* unmanaged[Cdecl]<nint, nint, byte>)&AcceptsFirstResponder;
        MetalNative.class_addMethod(
            cls, MetalNative.Selector("acceptsFirstResponder"), accepts, "c@:");
        MetalNative.objc_registerClassPair(cls);
        return cls;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnKeyDown(nint self, nint cmd, nint nsEvent)
    {
        try
        {
            var keyCode = (ushort)(MetalNative.Send(nsEvent, MetalNative.Selector("keyCode")) & 0xFFFF);
            var isRepeat = MetalNative.SendBool(nsEvent, MetalNative.Selector("isARepeat"));

            // Function keys can arrive here even with Command held (AppKit only
            // reroutes some chords through the key-equivalent path), so catch
            // Cmd+F1 in both places — and keep it away from MetalHostInput so it
            // never toggles the plain-F1 perf overlay.
            var modifiers = (ulong)MetalNative.Send(nsEvent, MetalNative.Selector("modifierFlags"));
            if (keyCode == KeyCodeF1 && (modifiers & NsEventModifierFlagCommand) != 0)
            {
                if (!isRepeat)
                {
                    ToggleMetalPerformanceHud();
                }

                return;
            }

            MetalHostInput.KeyDown(keyCode, isRepeat);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Metal key-down handler failed: {exception.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnKeyUp(nint self, nint cmd, nint nsEvent)
    {
        try
        {
            var keyCode = (ushort)(MetalNative.Send(nsEvent, MetalNative.Selector("keyCode")) & 0xFFFF);
            MetalHostInput.KeyUp(keyCode);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Metal key-up handler failed: {exception.Message}");
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte AcceptsFirstResponder(nint self, nint cmd) => 1;

    private const ushort KeyCodeF1 = 0x7A;
    private const ulong NsEventModifierFlagCommand = 1UL << 20;
    private static bool _metalHudVisible;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static byte OnPerformKeyEquivalent(nint self, nint cmd, nint nsEvent)
    {
        try
        {
            var keyCode = (ushort)(MetalNative.Send(nsEvent, MetalNative.Selector("keyCode")) & 0xFFFF);
            var modifiers = (ulong)MetalNative.Send(nsEvent, MetalNative.Selector("modifierFlags"));
            if (keyCode == KeyCodeF1 && (modifiers & NsEventModifierFlagCommand) != 0)
            {
                if (!MetalNative.SendBool(nsEvent, MetalNative.Selector("isARepeat")))
                {
                    ToggleMetalPerformanceHud();
                }

                return 1; // handled: no system beep, no further routing
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Metal key-equivalent handler failed: {exception.Message}");
        }

        return 0;
    }

    /// <summary>
    /// Cmd+F1: Apple's Metal Performance HUD on the CAMetalLayer (plain F1 keeps
    /// the built-in CPU-rasterized perf overlay). Configured per Apple's
    /// "Customizing Metal Performance HUD": developerHUDProperties takes
    /// mode=default|disabled and logging=default, plus any MTL_HUD_* environment
    /// keys directly in the dictionary — all three HUD flags (enabled, per-frame
    /// logging, shader-compile logging) ride in one property set. Runs on the
    /// AppKit main thread (the key-equivalent path), same thread as the render loop.
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
            var wakeImp = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnGuestWorkWake;
            MetalNative.class_addMethod(cls, MetalNative.Selector("onGuestWork:"), wakeImp, "v@:@");
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

    /// <summary>Set while an onGuestWork: wake is scheduled on the main run
    /// loop; coalesces enqueue-side wake requests to one in-flight message.</summary>
    private static int _guestWorkWakeScheduled;

    /// <summary>Wakes the main run loop to drain guest work now instead of at
    /// the next render tick. Guest submit→wait round-trips (release-mem labels,
    /// CPU-visible write-backs) otherwise cost a full frame interval each —
    /// games that chain several per frame crawl at a fraction of the display
    /// rate. Safe from any thread; no-op until the presenter starts.</summary>
    internal static void ScheduleGuestWorkDrain()
    {
        if (Interlocked.CompareExchange(ref _guestWorkWakeScheduled, 1, 0) != 0)
        {
            return;
        }

        var target = _renderTimerTarget;
        if (target == 0)
        {
            Volatile.Write(ref _guestWorkWakeScheduled, 0);
            return;
        }

        MetalNative.SendVoidPerformSelector(
            target,
            MetalNative.Selector("performSelectorOnMainThread:withObject:waitUntilDone:"),
            MetalNative.Selector("onGuestWork:"),
            0,
            waitUntilDone: false);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnGuestWorkWake(nint self, nint cmd, nint argument)
    {
        Volatile.Write(ref _guestWorkWakeScheduled, 0);
        if (_device == 0 || _commandQueue == 0 || Volatile.Read(ref _closeRequested))
        {
            return;
        }

        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            DrainGuestWork(_device, _commandQueue);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[LOADER][ERROR] Metal guest work wake failed: {exception}");
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    private static void RenderFrame()
    {
        MetalHostInput.PumpAutoKeys();
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

            // The window title reflects late guest state (the game registers its
            // application name after boot) plus the GPU suffix; the Vulkan
            // presenter re-reads it, so refresh periodically here for parity.
            if ((++_titleRefreshCounter & 0x3F) == 0)
            {
                var title = VideoOutExports.GetWindowTitle();
                if (!string.Equals(title, _lastWindowTitle, StringComparison.Ordinal))
                {
                    _lastWindowTitle = title;
                    MetalNative.SendVoid(
                        _window, MetalNative.Selector("setTitle:"), MetalNative.NsString(title));
                }
            }

            // The window is resizable, so the backing layer's bounds follow the
            // window while its drawable size does not — match them before asking
            // for a drawable, or nextDrawable keeps handing back the original
            // resolution and Core Animation stretches it (blurry, mis-scaled
            // overlay). No-op when the size is unchanged, i.e. almost every tick.
            SyncDrawableSizeToLayer();

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

            if (PerfOverlay.Enabled)
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

    /// <summary>Draws the CPU-rasterized perf panel over the frame's top-left
    /// corner, reusing the present pipeline with a panel-sized viewport.</summary>
    private static void EncodeOverlay(nint encoder)
    {
        if (_overlayTexture == 0)
        {
            var descriptor = MetalNative.SendTextureDescriptor(
                MetalNative.Class("MTLTextureDescriptor"),
                MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
                PixelFormatBgra8Unorm,
                PerfOverlay.PanelWidth,
                PerfOverlay.PanelHeight,
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
            PerfOverlay.PanelWidth,
            PerfOverlay.PanelHeight,
            _overlayPixels,
            PerfOverlay.PanelWidth,
            bytesPerPixel: 4);

        const double margin = 16;
        var panelWidth = Math.Min(PerfOverlay.PanelWidth, _drawableWidth - margin);
        var panelHeight = Math.Min(PerfOverlay.PanelHeight, _drawableHeight - margin);
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
                OriginX = margin,
                OriginY = margin,
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
        MetalNative.Send(
            window, MetalNative.Selector("setCollectionBehavior:"), (nint)FullScreenPrimaryBehavior);
        MetalNative.SendVoid(
            window,
            MetalNative.Selector("setTitle:"),
            MetalNative.NsString(VideoOutExports.GetWindowTitle()));
        MetalNative.SendVoid(window, MetalNative.Selector("center"));
        // makeKeyAndOrderFront happens after the metal layer is attached.
        return window;
    }

    /// <summary>Keeps the CAMetalLayer's drawable size (pixels) matched to its
    /// current bounds (points) × scale as the window resizes or moves between
    /// displays. CAMetalLayer never updates drawableSize on its own, even as a
    /// view's backing layer, so the render loop drives it.</summary>
    private static void SyncDrawableSizeToLayer()
    {
        MetalNative.SendStretRect(out var bounds, _metalLayer, MetalNative.Selector("bounds"));
        var scale = MetalNative.SendDouble(_metalLayer, MetalNative.Selector("contentsScale"));
        if (scale <= 0)
        {
            scale = 1;
        }

        var width = Math.Max(1, Math.Round(bounds.Width * scale));
        var height = Math.Max(1, Math.Round(bounds.Height * scale));
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
        // A Core Animation layer composites with its alpha channel by default, so
        // a presented frame whose guest alpha is zero would show through as the
        // window background (black). The presenter output is a finished opaque
        // frame; mark the layer opaque so alpha never reaches the compositor.
        MetalNative.SendVoidBool(layer, MetalNative.Selector("setOpaque:"), true);
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
