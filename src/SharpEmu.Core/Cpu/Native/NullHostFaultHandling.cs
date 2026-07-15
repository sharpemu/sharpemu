// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Placeholder for hosts whose fault bridge is installed directly by the
/// execution backend. POSIX uses its sigaction bridge and never calls these
/// Windows-shaped registration methods.
/// </summary>
internal sealed class NullHostFaultHandling : IHostFaultHandling
{
    public static NullHostFaultHandling Instance { get; } = new();

    private NullHostFaultHandling()
    {
    }

    public nint CreateHandlerThunk(nint managedCallback, uint hostRspSwitchTlsSlot, nint tlsGetValueAddress)
    {
        _ = managedCallback;
        _ = hostRspSwitchTlsSlot;
        _ = tlsGetValueAddress;
        return 0;
    }

    public void FreeThunk(nint thunk)
    {
        _ = thunk;
    }

    public nint AddFirstChanceHandler(nint thunk)
    {
        _ = thunk;
        return 0;
    }

    public void RemoveHandler(nint handle)
    {
        _ = handle;
    }

    public void SetUnhandledFilter(nint thunk)
    {
        _ = thunk;
    }
}
