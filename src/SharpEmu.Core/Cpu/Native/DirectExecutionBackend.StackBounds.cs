// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
    private const int HostStackStateBytes = 3 * sizeof(ulong);

    private static unsafe bool TryGetGuestStackBounds(ulong stackPointer, out ulong bottom, out ulong top)
    {
        bottom = top = 0;
        if (VirtualQuery((void*)stackPointer, out var region, (nuint)sizeof(MEMORY_BASIC_INFORMATION64)) == 0 ||
            region.State != 0x1000 || region.RegionSize == 0 || region.RegionSize > ulong.MaxValue - region.BaseAddress)
        {
            return false;
        }

        bottom = region.BaseAddress;
        top = region.BaseAddress + region.RegionSize;
        return stackPointer >= bottom && stackPointer < top;
    }

    private static unsafe void EmitStackBound(byte* code, ref int offset, byte register, uint field, bool store)
    {
        EmitByte(code, ref offset, 0x65);
        EmitByte(code, ref offset, (byte)(0x48 | (register >= 8 ? 4 : 0)));
        EmitByte(code, ref offset, store ? (byte)0x89 : (byte)0x8B);
        EmitByte(code, ref offset, (byte)(0x04 | ((register & 7) << 3)));
        EmitByte(code, ref offset, 0x25);
        EmitUInt32(code, ref offset, field);
    }

    // R10 points to the entry state. R11 is scratch; RAX remains unchanged.
    private static unsafe void EmitHostStackBounds(byte* code, ref int offset, bool save)
    {
        if (!OperatingSystem.IsWindows()) return;
        for (byte field = 8; field <= 16; field += 8)
        {
            if (save) EmitStackBound(code, ref offset, 11, field, store: false);
            EmitByte(code, ref offset, 0x4D);
            EmitByte(code, ref offset, save ? (byte)0x89 : (byte)0x8B);
            EmitByte(code, ref offset, 0x5A);
            EmitByte(code, ref offset, field);
            if (!save) EmitStackBound(code, ref offset, 11, field, store: true);
        }
    }

    private static unsafe void EmitGuestStackBounds(byte* code, ref int offset, ulong stackPointer)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!TryGetGuestStackBounds(stackPointer, out var bottom, out var top))
        {
            throw new InvalidOperationException("Guest stack has no committed host mapping.");
        }

        EmitHostStackBounds(code, ref offset, save: true);
        for (uint field = 8; field <= 16; field += 8)
        {
            EmitByte(code, ref offset, 0x49);
            EmitByte(code, ref offset, 0xBB);
            *(ulong*)(code + offset) = field == 8 ? top : bottom;
            offset += sizeof(ulong);
            EmitStackBound(code, ref offset, 11, field, store: true);
        }
    }

    private static unsafe void EmitSavedStackBounds(byte* code, ref int offset, bool restore)
    {
        if (!OperatingSystem.IsWindows()) return;
        EmitStackBound(code, ref offset, 14, 8, restore);
        EmitStackBound(code, ref offset, 15, 16, restore);
    }

    private unsafe void EmitExceptionHostStackBounds(byte* code, ref int offset)
    {
        EmitSavedStackBounds(code, ref offset, restore: false);
        ReadOnlySpan<byte> reserve = [0x48, 0x83, 0xEC, 0x28, 0xB9];
        foreach (var value in reserve) EmitByte(code, ref offset, value);
        EmitUInt32(code, ref offset, _hostRspSlotTlsIndex);
        EmitByte(code, ref offset, 0x48); EmitByte(code, ref offset, 0xB8);
        *(nint*)(code + offset) = _tlsGetValueAddress;
        offset += sizeof(nint);
        ReadOnlySpan<byte> lookup = [0xFF, 0xD0, 0x48, 0x83, 0xC4, 0x28, 0x48, 0x85, 0xC0, 0x0F, 0x84];
        foreach (var value in lookup) EmitByte(code, ref offset, value);
        var missingState = offset;
        EmitUInt32(code, ref offset, 0);
        // The state is zeroed at allocation; bounds the entry stub has not saved yet stay untouched.
        ReadOnlySpan<byte> unsaved = [0x48, 0x83, 0x78, 0x08, 0x00, 0x0F, 0x84];
        foreach (var value in unsaved) EmitByte(code, ref offset, value);
        var missingBounds = offset;
        EmitUInt32(code, ref offset, 0);
        EmitByte(code, ref offset, 0x49); EmitByte(code, ref offset, 0x89); EmitByte(code, ref offset, 0xC2);
        // A host fault already has live bounds. Do not replace a grown limit with an old one.
        ReadOnlySpan<byte> sameStack = [0x4C, 0x3B, 0x70, 0x08, 0x0F, 0x85];
        foreach (var value in sameStack) EmitByte(code, ref offset, value);
        var guestStack = offset;
        EmitUInt32(code, ref offset, 0);
        EmitHostStackBounds(code, ref offset, save: true);
        EmitByte(code, ref offset, 0xE9);
        var hostStack = offset;
        EmitUInt32(code, ref offset, 0);
        *(int*)(code + guestStack) = offset - guestStack - sizeof(int);
        EmitHostStackBounds(code, ref offset, save: false);
        *(int*)(code + hostStack) = offset - hostStack - sizeof(int);
        *(int*)(code + missingState) = offset - missingState - sizeof(int);
        *(int*)(code + missingBounds) = offset - missingBounds - sizeof(int);
    }

    [UnmanagedCallersOnly]
    private static unsafe int SelectExceptionStackBounds(void* exceptionInfo, int disposition, ulong* bounds)
    {
        if (disposition != -1) return disposition;
        var context = ((EXCEPTION_POINTERS*)exceptionInfo)->ContextRecord;
        if (context == null) return 0;
        var stackPointer = ReadCtxU64(context, 152);
        if (stackPointer >= bounds[1] && stackPointer < bounds[0]) return disposition;

        GetCurrentThreadStackLimits(out var nativeBottom, out var nativeTop);
        if (stackPointer >= nativeBottom && stackPointer < nativeTop)
        {
            bounds[0] = nativeTop;
            bounds[1] = nativeBottom;
            return disposition;
        }

        if (!TryGetGuestStackBounds(stackPointer, out var bottom, out var top)) return 0;
        bounds[0] = top;
        bounds[1] = bottom;
        return disposition;
    }

    private static unsafe void EmitExceptionStackSelection(byte* code, ref int offset)
    {
        // The caller reserves 72 bytes: ABI home space and two stack bounds.
        ReadOnlySpan<byte> arguments = [0x4C, 0x89, 0x74, 0x24, 0x28, 0x4C, 0x89, 0x7C, 0x24, 0x30,
            0x89, 0xC2, 0x4C, 0x89, 0xE9, 0x4C, 0x8D, 0x44, 0x24, 0x28, 0x48, 0xB8];
        foreach (var value in arguments) EmitByte(code, ref offset, value);
        *(nint*)(code + offset) = (nint)(delegate* unmanaged<void*, int, ulong*, int>)&SelectExceptionStackBounds;
        offset += sizeof(nint);
        ReadOnlySpan<byte> result = [0xFF, 0xD0, 0x4C, 0x8B, 0x74, 0x24, 0x28, 0x4C, 0x8B, 0x7C, 0x24, 0x30];
        foreach (var value in result) EmitByte(code, ref offset, value);
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void GetCurrentThreadStackLimits(out ulong lowLimit, out ulong highLimit);
}
