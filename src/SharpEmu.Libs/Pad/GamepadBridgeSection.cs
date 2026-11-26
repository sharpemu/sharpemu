// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpEmu.Libs.Pad;

/// <summary>
/// The unnamed pagefile-backed section behind the gamepad bridge. It has no name on purpose: a named kernel object of
/// the launcher lands in another namespace for an AppContainer child, so the child gets the section as an inherited
/// handle (passed in its handle list) and the handle's value in <see cref="GamepadBridgeClient.EnvironmentVariable"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed unsafe class GamepadBridgeSection : IDisposable
{
    private const uint PageReadWrite = 0x04;
    private const uint FileMapAllAccess = 0xF001F;
    private static readonly nint InvalidHandleValue = -1;

    private nint _handle;
    private byte* _view;

    private GamepadBridgeSection(nint handle, byte* view, bool ownsHandle)
    {
        _handle = ownsHandle ? handle : 0;
        Handle = handle;
        _view = view;
        Memory = new GamepadBridgeMemory(view);
    }

    public nint Handle { get; }

    public GamepadBridgeMemory Memory { get; }

    /// <summary>A zeroed, initialized section whose handle the child can inherit.</summary>
    public static GamepadBridgeSection Create()
    {
        var attributes = new SecurityAttributes { Size = sizeof(SecurityAttributes), InheritHandle = 1 };
        var handle = CreateFileMappingW(InvalidHandleValue, ref attributes, PageReadWrite, 0, GamepadBridgeMemory.Size, null);
        if (handle == 0)
        {
            throw new InvalidOperationException($"Could not create the gamepad bridge section (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        var view = (byte*)MapViewOfFile(handle, FileMapAllAccess, 0, 0, GamepadBridgeMemory.Size);
        if (view is null)
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(handle);
            throw new InvalidOperationException($"Could not map the gamepad bridge section (Win32 error {error}).");
        }

        var section = new GamepadBridgeSection(handle, view, ownsHandle: true);
        section.Memory.Initialize();
        return section;
    }

    /// <summary>Maps the section behind an inherited handle; the handle stays the process's own and is not closed.</summary>
    public static GamepadBridgeSection? Open(nint handle)
    {
        var view = (byte*)MapViewOfFile(handle, FileMapAllAccess, 0, 0, GamepadBridgeMemory.Size);
        if (view is null)
        {
            return null;
        }

        var section = new GamepadBridgeSection(handle, view, ownsHandle: false);
        if (!section.Memory.IsValid)
        {
            section.Dispose();
            return null;
        }

        return section;
    }

    public void Dispose()
    {
        if (_view is not null)
        {
            UnmapViewOfFile((nint)_view);
            _view = null;
        }

        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Size;
        public nint Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateFileMappingW(
        nint file, ref SecurityAttributes attributes, uint protect, uint maximumSizeHigh, uint maximumSizeLow, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnmapViewOfFile(nint baseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
