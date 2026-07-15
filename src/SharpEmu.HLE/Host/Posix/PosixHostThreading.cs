// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.HLE.Host.Posix;

internal sealed class PosixHostThreading : IHostThreading
{
    public uint AllocateTlsSlot() => PosixHostStubs.TlsAlloc();

    public bool FreeTlsSlot(uint slot) => PosixHostStubs.TlsFree(slot);

    public bool SetTlsValue(uint slot, nint value) => PosixHostStubs.TlsSetValue(slot, value);

    public nint GetTlsValue(uint slot) => PosixHostStubs.TlsGetValue(slot);

    public uint CurrentThreadId => PosixHostStubs.GetCurrentThreadId();

    public void RequestTimerResolution()
    {
        // POSIX sleep primitives are already high-resolution; there is no
        // timeBeginPeriod equivalent to request.
    }

    // Thread affinity is advisory on POSIX hosts (macOS offers no
    // pthread-level affinity API); callers treat false as "not applied".
    public bool TrySetCurrentThreadAffinity(nuint affinityMask)
    {
        _ = affinityMask;
        return false;
    }

    public nint CreateNativeThread(
        nint entry,
        nint parameter,
        nuint stackReserveBytes,
        out uint threadId)
    {
        return PosixHostStubs.CreateWorkerThread(entry, parameter, stackReserveBytes, out threadId);
    }

    public bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds)
    {
        return PosixHostStubs.WaitForWorkerThreadExit(threadHandle, timeoutMilliseconds);
    }

    public void CloseThreadHandle(nint threadHandle)
    {
        PosixHostStubs.CloseWorkerThreadHandle(threadHandle);
    }

    public bool TryCaptureThreadRegisters(uint threadId, out HostCapturedRegisters registers)
    {
        _ = threadId;
        registers = default;
        return false;
    }
}
