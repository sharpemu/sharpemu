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
        HostRuntimeFunction.WaitForSingleObject => PosixHostStubs.WaitForSingleObjectStubAddress,
        HostRuntimeFunction.SetEvent => PosixHostStubs.SetEventStubAddress,
        HostRuntimeFunction.ExitThread => PosixHostStubs.ExitThreadStubAddress,
        _ => throw new ArgumentOutOfRangeException(nameof(function), function, null),
    };
}
