// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

internal sealed partial class WindowsHostSymbolResolver : IHostSymbolResolver
{
    public nint GetAddress(HostRuntimeFunction function)
    {
        var kernel32 = GetModuleHandle("kernel32.dll");
        if (kernel32 == 0)
        {
            return 0;
        }

        return GetProcAddress(kernel32, function switch
        {
            HostRuntimeFunction.TlsGetValue => "TlsGetValue",
            HostRuntimeFunction.QueryPerformanceCounter => "QueryPerformanceCounter",
            HostRuntimeFunction.SwitchToThread => "SwitchToThread",
            HostRuntimeFunction.Sleep => "Sleep",
            HostRuntimeFunction.WaitForSingleObject => "WaitForSingleObject",
            HostRuntimeFunction.SetEvent => "SetEvent",
            HostRuntimeFunction.ExitThread => "ExitThread",
            _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
        });
    }

    // Utf16 marshalling pins the managed string and passes its address directly
    // (no copy); Utf8 stack-allocates the transient buffer for these short
    // ASCII export names. LibraryImport is exact-spelling, hence the W entry point.
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string lpModuleName);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint hModule, string procName);
}
