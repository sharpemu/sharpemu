// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using SharpEmu.Libs.VideoOut;
using System.Runtime.InteropServices;

namespace SharpEmu.GUI;

/// <summary>
/// Native child surface owned by Avalonia. The isolated emulator process uses
/// its platform handle to create the Vulkan presentation surface, keeping the
/// guest address space out of the GUI process.
/// </summary>
public sealed class GameSurfaceHost : NativeControlHost
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private const uint SwpHideWindow = 0x0080;
    private const uint WsChild = 0x40000000;
    private const uint WsVisible = 0x10000000;
    private const uint WsClipSiblings = 0x04000000;
    private const uint WsClipChildren = 0x02000000;
    private const uint CsOwnDc = 0x0020;
    private const uint WmSetCursor = 0x0020;
    private const uint WmMouseMove = 0x0200;
    private const int IdcArrow = 32512;
    private const int CursorHideDelayMs = 2500;

    private VulkanHostSurface? _surface;
    private nint _windowHandle;
    private nint _x11Display;
    private string? _win32ClassName;
    private WindowProcedure? _windowProcedure;
    private nint _metalLayer;
    private bool _presentationVisible = true;
    private DispatcherTimer? _cursorIdleTimer;
    private bool _cursorAutoHide;
    private bool _cursorHidden;
    private long _lastPointerActivity;

    public GameSurfaceHost()
    {
        PropertyChanged += (_, change) =>
        {
            if (change.Property == BoundsProperty)
            {
                UpdateSurfaceSize();
            }
        };
        LayoutUpdated += (_, _) =>
        {
            // Fullscreen can change a monitor's DPI scale without changing
            // the logical Bounds. Refresh the native child from physical size.
            UpdateSurfaceSize();

            // NativeControlHost may make its HWND visible again as part of a
            // later arrange pass. Keep a loading surface hidden until its
            // child process reports a real first frame.
            if (!_presentationVisible)
            {
                ApplyPresentationVisibility();
            }
        };
    }

    public event EventHandler<VulkanHostSurface>? SurfaceAvailable;

    public event EventHandler<VulkanHostSurface>? SurfaceDestroyed;

    public VulkanHostSurface? Surface => _surface;

    public void RefreshSurfaceSize() => UpdateSurfaceSize();

    /// <summary>
    /// Hides the platform child without detaching the Vulkan surface. This
    /// allows the launcher to return to its library while guest teardown is
    /// still finishing on the render thread.
    /// </summary>
    public void SetPresentationVisible(bool visible)
    {
        _presentationVisible = visible;
        ApplyPresentationVisibility();
    }

    /// <summary>
    /// Auto-hides the mouse cursor over the game surface after a short idle
    /// period; any pointer movement brings it back. Enabling (again) restarts
    /// the idle countdown, so both "first frame presented" and "entered
    /// fullscreen" can arm it. Windows-only; a no-op elsewhere.
    /// </summary>
    public void SetCursorAutoHide(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        _cursorAutoHide = enabled;
        _lastPointerActivity = System.Diagnostics.Stopwatch.GetTimestamp();
        if (enabled)
        {
            _cursorIdleTimer ??= CreateCursorIdleTimer();
            _cursorIdleTimer.Start();
            return;
        }

        _cursorIdleTimer?.Stop();
        ShowCursorNow();
    }

    private DispatcherTimer CreateCursorIdleTimer()
    {
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        timer.Tick += (_, _) => HideCursorWhenIdle();
        return timer;
    }

    private void HideCursorWhenIdle()
    {
        if (!_cursorAutoHide || _cursorHidden || _windowHandle == 0)
        {
            return;
        }

        var idleMs = (System.Diagnostics.Stopwatch.GetTimestamp() - _lastPointerActivity) *
            1000 / System.Diagnostics.Stopwatch.Frequency;
        if (idleMs < CursorHideDelayMs)
        {
            return;
        }

        // Only swallow the cursor while it is actually over the game surface;
        // hovering launcher chrome (console, toolbar) must keep the arrow.
        if (!GetCursorPos(out var point) || WindowFromPoint(point) != _windowHandle)
        {
            return;
        }

        _cursorHidden = true;
        _ = SetCursor(0);
    }

    private void ShowCursorNow()
    {
        if (!_cursorHidden)
        {
            return;
        }

        _cursorHidden = false;
        _ = SetCursor(LoadCursorW(0, IdcArrow));
    }

    private void ApplyPresentationVisibility()
    {
        if (_windowHandle == 0)
        {
            return;
        }

        var visible = _presentationVisible;
        if (OperatingSystem.IsWindows())
        {
            // SW_HIDE can be ignored for a window's initial show state. Force
            // the state through SetWindowPos so an old child swapchain cannot
            // remain composed while the next game is loading.
            var flags = SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate |
                (visible ? SwpShowWindow : SwpHideWindow);
            _ = SetWindowPos(_windowHandle, 0, 0, 0, 0, 0, flags);
        }
        else if (OperatingSystem.IsLinux() && _x11Display != 0)
        {
            _ = visible
                ? XMapWindow(_x11Display, _windowHandle)
                : XUnmapWindow(_x11Display, _windowHandle);
            _ = XFlush(_x11Display);
        }
        else if (OperatingSystem.IsMacOS())
        {
            SendBool(_windowHandle, "setHidden:", !visible);
        }
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle control)
    {
        PlatformHandle handle;
        if (OperatingSystem.IsWindows())
        {
            handle = CreateWin32(control);
        }
        else if (OperatingSystem.IsLinux())
        {
            handle = CreateX11(control);
        }
        else if (OperatingSystem.IsMacOS())
        {
            handle = CreateMacOS();
        }
        else
        {
            throw new PlatformNotSupportedException("SharpEmu's embedded Vulkan surface is unsupported on this platform.");
        }

        UpdateSurfaceSize();
        if (_surface is { } surface)
        {
            SurfaceAvailable?.Invoke(this, surface);
        }

        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (OperatingSystem.IsWindows())
        {
            SetCursorAutoHide(false);
        }

        var surface = _surface;
        _surface = null;

        if (OperatingSystem.IsWindows())
        {
            DestroyWin32();
        }
        else if (OperatingSystem.IsLinux())
        {
            DestroyX11();
        }
        else if (OperatingSystem.IsMacOS())
        {
            DestroyMacOS();
        }

        if (surface is not null)
        {
            SurfaceDestroyed?.Invoke(this, surface);
        }
    }

    private PlatformHandle CreateWin32(IPlatformHandle control)
    {
        _win32ClassName = $"SharpEmuGameSurface-{Guid.NewGuid():N}";
        _windowProcedure = WindowProcedureImpl;
        var classInfo = new WndClassEx
        {
            Size = (uint)Marshal.SizeOf<WndClassEx>(),
            Style = CsOwnDc,
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(_windowProcedure),
            Instance = GetModuleHandleW(null),
            ClassName = _win32ClassName,
        };

        if (RegisterClassExW(ref classInfo) == 0)
        {
            throw new InvalidOperationException($"Could not register the embedded game window class (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        _windowHandle = CreateWindowExW(
            0,
            _win32ClassName,
            "SharpEmu Game Surface",
            WsChild | (_presentationVisible ? WsVisible : 0) | WsClipSiblings | WsClipChildren,
            0,
            0,
            1,
            1,
            control.Handle,
            0,
            classInfo.Instance,
            0);
        if (_windowHandle == 0)
        {
            var error = Marshal.GetLastWin32Error();
            _ = UnregisterClassW(_win32ClassName, classInfo.Instance);
            throw new InvalidOperationException($"Could not create the embedded game window (Win32 error {error}).");
        }

        _surface = new VulkanHostSurface(
            VulkanHostSurfaceKind.Win32,
            _windowHandle,
            classInfo.Instance);
        return new PlatformHandle(_windowHandle, "HWND");
    }

    private PlatformHandle CreateX11(IPlatformHandle control)
    {
        _x11Display = XOpenDisplay(0);
        if (_x11Display == 0)
        {
            throw new InvalidOperationException("Could not connect to the X11 server for the embedded game surface.");
        }

        _windowHandle = XCreateSimpleWindow(
            _x11Display,
            control.Handle,
            0,
            0,
            1,
            1,
            0,
            0,
            0);
        if (_windowHandle == 0)
        {
            XCloseDisplay(_x11Display);
            _x11Display = 0;
            throw new InvalidOperationException("Could not create the X11 embedded game surface.");
        }

        if (_presentationVisible)
        {
            _ = XMapWindow(_x11Display, _windowHandle);
        }
        _ = XFlush(_x11Display);
        _surface = new VulkanHostSurface(VulkanHostSurfaceKind.Xlib, _windowHandle, _x11Display);
        return new PlatformHandle(_windowHandle, "X11");
    }

    private PlatformHandle CreateMacOS()
    {
        _metalLayer = CreateObjectiveCObject("CAMetalLayer");
        _windowHandle = CreateObjectiveCObject("NSView");
        SendBool(_windowHandle, "setWantsLayer:", true);
        SendPointer(_windowHandle, "setLayer:", _metalLayer);
        SendBool(_windowHandle, "setHidden:", !_presentationVisible);

        _surface = new VulkanHostSurface(VulkanHostSurfaceKind.Metal, _windowHandle, metalLayerHandle: _metalLayer);
        return new PlatformHandle(_windowHandle, "NSView");
    }

    private void UpdateSurfaceSize()
    {
        if (_surface is null)
        {
            return;
        }

        var renderScale = (VisualRoot as TopLevel)?.RenderScaling ?? 1.0;
        var width = Math.Max(1, (int)Math.Round(Bounds.Width * renderScale));
        var height = Math.Max(1, (int)Math.Round(Bounds.Height * renderScale));
        var sizeChanged = _surface.PixelWidth != width || _surface.PixelHeight != height;
        if (Environment.GetEnvironmentVariable("SHARPEMU_TRACE_SURFACE_SIZE") == "1")
        {
            Console.Error.WriteLine(
                $"[GUI][TRACE] GameSurfaceHost.UpdateSurfaceSize bounds={Bounds.Width}x{Bounds.Height} " +
                $"scale={renderScale} computed={width}x{height} changed={sizeChanged} " +
                $"prevSurface={_surface.PixelWidth}x{_surface.PixelHeight}");
        }
        _surface.UpdatePixelSize(width, height);

        if (!sizeChanged)
        {
            return;
        }

        if (OperatingSystem.IsWindows() && _windowHandle != 0)
        {
            _ = SetWindowPos(
                _windowHandle,
                0,
                0,
                0,
                width,
                height,
                SwpNoMove | SwpNoZOrder | SwpNoActivate);
        }
        else if (OperatingSystem.IsLinux() && _x11Display != 0 && _windowHandle != 0)
        {
            _ = XResizeWindow(_x11Display, _windowHandle, (uint)width, (uint)height);
            _ = XFlush(_x11Display);
        }
        else if (OperatingSystem.IsMacOS() && _metalLayer != 0)
        {
            SendDouble(_metalLayer, "setContentsScale:", renderScale);
        }
    }

    private void DestroyWin32()
    {
        if (_windowHandle != 0)
        {
            _ = DestroyWindow(_windowHandle);
            _windowHandle = 0;
        }

        if (!string.IsNullOrWhiteSpace(_win32ClassName))
        {
            _ = UnregisterClassW(_win32ClassName, GetModuleHandleW(null));
            _win32ClassName = null;
        }

        _windowProcedure = null;
    }

    private void DestroyX11()
    {
        if (_x11Display != 0 && _windowHandle != 0)
        {
            _ = XDestroyWindow(_x11Display, _windowHandle);
        }
        if (_x11Display != 0)
        {
            _ = XCloseDisplay(_x11Display);
        }

        _windowHandle = 0;
        _x11Display = 0;
    }

    private void DestroyMacOS()
    {
        if (_windowHandle != 0)
        {
            SendVoid(_windowHandle, "release");
        }
        if (_metalLayer != 0)
        {
            SendVoid(_metalLayer, "release");
        }

        _windowHandle = 0;
        _metalLayer = 0;
    }

    private nint WindowProcedureImpl(nint window, uint message, nint wParam, nint lParam)
    {
        if (message == WmMouseMove)
        {
            _lastPointerActivity = System.Diagnostics.Stopwatch.GetTimestamp();
            ShowCursorNow();
        }
        else if (message == WmSetCursor && _cursorHidden)
        {
            // Win32 re-resolves the cursor on every mouse message; returning
            // TRUE here keeps the parent chain from restoring the arrow.
            _ = SetCursor(0);
            return 1;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    private static nint CreateObjectiveCObject(string className)
    {
        var classHandle = objc_getClass(className);
        if (classHandle == 0)
        {
            throw new InvalidOperationException($"Objective-C class '{className}' is unavailable.");
        }

        var instance = objc_msgSend_id(classHandle, sel_registerName("alloc"));
        instance = objc_msgSend_id(instance, sel_registerName("init"));
        if (instance == 0)
        {
            throw new InvalidOperationException($"Could not create Objective-C '{className}'.");
        }

        return instance;
    }

    private static void SendVoid(nint receiver, string selector) =>
        objc_msgSend_void(receiver, sel_registerName(selector));

    private static void SendBool(nint receiver, string selector, bool value) =>
        objc_msgSend_bool(receiver, sel_registerName(selector), value ? (byte)1 : (byte)0);

    private static void SendPointer(nint receiver, string selector, nint value) =>
        objc_msgSend_pointer(receiver, sel_registerName(selector), value);

    private static void SendDouble(nint receiver, string selector, double value) =>
        objc_msgSend_double(receiver, sel_registerName(selector), value);

    private delegate nint WindowProcedure(nint window, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint Size;
        public uint Style;
        public nint WindowProcedure;
        public int ClassExtra;
        public int WindowExtra;
        public nint Instance;
        public nint Icon;
        public nint Cursor;
        public nint Background;
        public string? MenuName;
        public string? ClassName;
        public nint IconSmall;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? moduleName);

    [DllImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WndClassEx classInfo);

    [DllImport("user32.dll", EntryPoint = "UnregisterClassW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClassW(string className, nint instance);

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateWindowExW(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [DllImport("user32.dll", EntryPoint = "DestroyWindow", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll", EntryPoint = "SetWindowPos", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        nint window,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW", CharSet = CharSet.Unicode)]
    private static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetCursor")]
    private static extern nint SetCursor(nint cursor);

    [DllImport("user32.dll", EntryPoint = "LoadCursorW", CharSet = CharSet.Unicode)]
    private static extern nint LoadCursorW(nint instance, nint cursorName);

    [DllImport("user32.dll", EntryPoint = "GetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static extern nint WindowFromPoint(NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("libX11.so.6", EntryPoint = "XOpenDisplay")]
    private static extern nint XOpenDisplay(nint displayName);

    [DllImport("libX11.so.6", EntryPoint = "XCreateSimpleWindow")]
    private static extern nint XCreateSimpleWindow(
        nint display,
        nint parent,
        int x,
        int y,
        uint width,
        uint height,
        uint borderWidth,
        ulong border,
        ulong background);

    [DllImport("libX11.so.6", EntryPoint = "XMapWindow")]
    private static extern int XMapWindow(nint display, nint window);

    [DllImport("libX11.so.6", EntryPoint = "XUnmapWindow")]
    private static extern int XUnmapWindow(nint display, nint window);

    [DllImport("libX11.so.6", EntryPoint = "XResizeWindow")]
    private static extern int XResizeWindow(nint display, nint window, uint width, uint height);

    [DllImport("libX11.so.6", EntryPoint = "XDestroyWindow")]
    private static extern int XDestroyWindow(nint display, nint window);

    [DllImport("libX11.so.6", EntryPoint = "XCloseDisplay")]
    private static extern int XCloseDisplay(nint display);

    [DllImport("libX11.so.6", EntryPoint = "XFlush")]
    private static extern int XFlush(nint display);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint objc_getClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern nint sel_registerName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint objc_msgSend_id(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_void(nint receiver, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_bool(nint receiver, nint selector, byte value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_pointer(nint receiver, nint selector, nint value);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void objc_msgSend_double(nint receiver, nint selector, double value);
}
