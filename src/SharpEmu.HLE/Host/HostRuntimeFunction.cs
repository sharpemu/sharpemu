// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Host functions whose addresses the execution engine bakes into emitted
/// stubs (spin-waits, worker run loops, TLS reads). Enum-keyed rather than a
/// free-form name lookup: each platform's emitters need their own specific
/// functions, and this set is exactly what the current emitters consume.
/// </summary>
public enum HostRuntimeFunction
{
    TlsGetValue,
    QueryPerformanceCounter,
    SwitchToThread,
    Sleep,
    WaitForSingleObject,
    SetEvent,
    ExitThread,
}
