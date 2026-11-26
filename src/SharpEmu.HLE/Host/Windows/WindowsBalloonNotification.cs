// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>
/// A Windows notification raised by the launcher: a Shell_NotifyIcon balloon, which Windows 10 and 11 show as a toast
/// in the corner of the screen over the game window (and keep in the notification center). It needs no package
/// identity. A helper thread owns a hidden message-only window and a tray icon for <see cref="DisplaySeconds"/>
/// seconds, then removes both. Never throws: a failure is logged and the launch goes on.
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsBalloonNotification
{
    public const int DisplaySeconds = 10;

    private const int TitleLimit = 63;
    private const int BodyLimit = 255;
    private const uint NimAdd = 0;
    private const uint NimDelete = 2;
    private const uint NifIcon = 0x2;
    private const uint NifTip = 0x4;
    private const uint NifInfo = 0x10;
    private const uint NiifWarning = 0x2;
    private const uint QsAllInput = 0x04FF;
    private const uint PmRemove = 1;
    private const uint FlashAll = 0x3;
    private const uint FlashTimerNoForeground = 0xC;
    private const nint HwndMessage = -3;
    private const nint IconExclamation = 32515;

    /// <summary>Starts the notification on its own thread and returns at once; false when it could not start.</summary>
    public static bool TryShow(string title, string body)
    {
        try
        {
            var thread = new Thread(() => Run(title, body), 256 * 1024)
            {
                IsBackground = true,
                Name = "SharpEmu notification",
            };
            thread.Start();
            return true;
        }
        catch (Exception exception) when (exception is ThreadStartException or OutOfMemoryException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[SANDBOX][WARN] could not show the notification: {exception.Message}");
            return false;
        }
    }

    /// <summary>Flashes the taskbar button of a window until it is brought to the front; false when Windows refuses.</summary>
    public static bool FlashTaskbarButton(nint window)
    {
        try
        {
            var info = new FlashInfo
            {
                Size = (uint)Marshal.SizeOf<FlashInfo>(),
                Window = window,
                Flags = FlashAll | FlashTimerNoForeground,
            };
            return window != 0 && FlashWindowEx(ref info);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    internal static string Truncate(string text, int limit) =>
        text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit - 3), "...");

    private static void Run(string title, string body)
    {
        nint window = 0;
        var added = false;
        var data = default(NotifyIconData);
        try
        {
            window = CreateWindowExW(0, "STATIC", "SharpEmu notification", 0, 0, 0, 0, 0, HwndMessage, 0, 0, 0);
            if (window == 0)
            {
                Console.Error.WriteLine($"[SANDBOX][WARN] could not show the notification: window creation failed ({Marshal.GetLastWin32Error()})");
                return;
            }

            var icon = ExtractIconW(0, Environment.ProcessPath ?? string.Empty, 0);
            if (icon <= 1)
            {
                icon = LoadIconW(0, IconExclamation);
            }

            data = new NotifyIconData
            {
                Size = (uint)Marshal.SizeOf<NotifyIconData>(),
                Window = window,
                Id = 1,
                Flags = NifIcon | NifTip | NifInfo,
                Icon = icon,
                Tip = "SharpEmu",
                Info = Truncate(body, BodyLimit),
                InfoTitle = Truncate(title, TitleLimit),
                InfoFlags = NiifWarning,
            };
            if (!ShellNotifyIconW(NimAdd, ref data))
            {
                Console.Error.WriteLine("[SANDBOX][WARN] could not show the notification: Shell_NotifyIcon failed");
                return;
            }

            added = true;
            var deadline = Environment.TickCount64 + DisplaySeconds * 1000L;
            while (true)
            {
                var remaining = deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    break;
                }

                MsgWaitForMultipleObjects(0, 0, false, (uint)remaining, QsAllInput);
                while (PeekMessageW(out var message, 0, 0, 0, PmRemove))
                {
                    TranslateMessage(ref message);
                    DispatchMessageW(ref message);
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
            MarshalDirectiveException or ArgumentException or InvalidOperationException)
        {
            Console.Error.WriteLine($"[SANDBOX][WARN] could not show the notification: {exception.Message}");
        }
        finally
        {
            if (added)
            {
                _ = ShellNotifyIconW(NimDelete, ref data);
            }

            if (window != 0)
            {
                _ = DestroyWindow(window);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint CallbackMessage;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Info;
        public uint TimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string InfoTitle;
        public uint InfoFlags;
        public Guid Item;
        public nint BalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    private static extern bool ShellNotifyIconW(uint message, ref NotifyIconData data);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint ExtractIconW(nint instance, string fileName, uint iconIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindowExW(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FlashInfo info);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(nint window);

    [DllImport("user32.dll")]
    private static extern nint LoadIconW(nint instance, nint iconName);

    [DllImport("user32.dll")]
    private static extern uint MsgWaitForMultipleObjects(uint count, nint handles, bool waitAll, uint milliseconds, uint wakeMask);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PeekMessageW(out Message message, nint window, uint filterMin, uint filterMax, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint DispatchMessageW(ref Message message);
}
