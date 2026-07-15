// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

internal sealed class PosixHostSymbolResolver : IHostSymbolResolver
{
    public nint GetAddress(HostRuntimeFunction function) => function switch
    {
        HostRuntimeFunction.TlsGetValue => PosixHostStubs.TlsGetValueStubAddress,
        HostRuntimeFunction.QueryPerformanceCounter => PosixHostStubs.QueryPerformanceCounterStubAddress,
        HostRuntimeFunction.SwitchToThread => PosixHostStubs.SwitchToThreadStubAddress,
        HostRuntimeFunction.Sleep => PosixHostStubs.SleepStubAddress,
        // Native guest workers are currently Windows-only, so their wait-loop
        // exports deliberately remain unavailable on POSIX.
        HostRuntimeFunction.WaitForSingleObject => 0,
        HostRuntimeFunction.SetEvent => 0,
        HostRuntimeFunction.ExitThread => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };
}
