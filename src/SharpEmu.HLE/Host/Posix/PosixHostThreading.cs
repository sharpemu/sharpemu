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

    // Native guest workers currently emit a Win32 event loop and are disabled
    // on POSIX. These members keep the platform contract explicit until that
    // loop has a pthread/eventfd implementation.
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
        _ = entry;
        _ = parameter;
        _ = stackReserveBytes;
        threadId = 0;
        return 0;
    }

    public bool WaitForThreadExit(nint threadHandle, uint timeoutMilliseconds)
    {
        _ = threadHandle;
        _ = timeoutMilliseconds;
        return false;
    }

    public void CloseThreadHandle(nint threadHandle)
    {
        _ = threadHandle;
    }

    public bool TryCaptureThreadRegisters(uint threadId, out HostCapturedRegisters registers)
    {
        _ = threadId;
        registers = default;
        return false;
    }
}
