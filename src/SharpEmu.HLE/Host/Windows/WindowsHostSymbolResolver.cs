// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.HLE.Host.Windows;

internal sealed class WindowsHostSymbolResolver : IHostSymbolResolver
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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern nint GetProcAddress(nint hModule, string procName);
}
