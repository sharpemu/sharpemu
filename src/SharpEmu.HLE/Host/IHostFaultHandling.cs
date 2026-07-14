// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host;

/// <summary>
/// Installation mechanics for the process-wide fault interception the execution
/// engine relies on to catch guest faults. Deliberately thin: the managed
/// handlers keep receiving the platform's raw exception data, and the emitted
/// pre-filter thunk is an opaque per-platform unit. Implementations live next
/// to the execution backend (SharpEmu.Core), not behind HostPlatform.Current.
/// </summary>
public interface IHostFaultHandling
{
    /// <summary>
    /// Emits the native thunk that wraps a managed fault handler: it pre-filters
    /// exception codes that must never enter managed code and, when the fault
    /// happened on a guest stack, switches to the host stack saved in
    /// <paramref name="hostRspSwitchTlsSlot"/> before the call. Returns 0 on failure.
    /// </summary>
    nint CreateHandlerThunk(nint managedCallback, uint hostRspSwitchTlsSlot, nint tlsGetValueAddress);

    void FreeThunk(nint thunk);

    /// <summary>Installs a first-chance handler ahead of existing ones; returns a removal handle (0 on failure).</summary>
    nint AddFirstChanceHandler(nint thunk);

    void RemoveHandler(nint handle);

    /// <summary>Installs the last-resort filter; pass 0 to clear.</summary>
    void SetUnhandledFilter(nint thunk);
}
