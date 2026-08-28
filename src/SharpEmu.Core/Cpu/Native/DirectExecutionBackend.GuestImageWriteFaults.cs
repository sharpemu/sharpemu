// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
    private nint _guestImageWriteFaultHandler;
    private nint _guestImageWriteFaultHandlerStub;

    private unsafe nint CreateGuestImageWriteFaultHandlerStub()
    {
        var control = GuestImageWriteTracker.NativeWindowsFaultControlAddress;
        var kernel32 = GetModuleHandle("kernel32.dll");
        var virtualProtect = kernel32 != 0
            ? GetProcAddress(kernel32, "VirtualProtect")
            : 0;
        if (control == 0 || virtualProtect == 0)
        {
            return 0;
        }

        const uint stubSize = 512;
        var allocation = VirtualAlloc(null, stubSize, 12288u, 64u);
        if (allocation is null)
        {
            return 0;
        }

        var code = (byte*)allocation;
        var offset = 0;
        Span<int> missJumps = stackalloc int[12];
        var missJumpCount = 0;

        // Accept only a Windows write access violation with two parameters.
        EmitBytes(code, ref offset, [0x48, 0x8B, 0x01]);
        EmitBytes(code, ref offset, [0x48, 0x85, 0xC0]);
        EmitNearJump(code, ref offset, 0x84, missJumps, ref missJumpCount);
        EmitBytes(code, ref offset, [0x81, 0x38]);
        EmitUInt32(code, ref offset, 0xC0000005u);
        EmitNearJump(code, ref offset, 0x85, missJumps, ref missJumpCount);
        EmitBytes(code, ref offset, [0x83, 0x78, 0x18, 0x02]);
        EmitNearJump(code, ref offset, 0x82, missJumps, ref missJumpCount);
        EmitBytes(code, ref offset, [0x48, 0x83, 0x78, 0x20, 0x01]);
        EmitNearJump(code, ref offset, 0x85, missJumps, ref missJumpCount);

        // Resolve the sparse native page entry from the fault address.
        EmitBytes(code, ref offset, [0x48, 0x8B, 0x50, 0x28]);
        EmitBytes(code, ref offset, [0x49, 0xBA]);
        *(nint*)(code + offset) = control;
        offset += sizeof(nint);
        EmitBytes(code, ref offset, [0x4D, 0x8B, 0x1A]);
        EmitBytes(code, ref offset, [0x4D, 0x85, 0xDB]);
        Span<int> tableMissJumps = stackalloc int[8];
        var tableMissJumpCount = 0;
        EmitNearJump(code, ref offset, 0x84, tableMissJumps, ref tableMissJumpCount);
        EmitBytes(code, ref offset, [0x48, 0x89, 0xD0]);
        EmitBytes(code, ref offset, [0x48, 0xC1, 0xE8, 0x22]);
        EmitBytes(code, ref offset, [0x48, 0x3D]);
        EmitUInt32(code, ref offset, GuestImageWriteTracker.NativeWindowsLevelOneEntryCount);
        EmitNearJump(code, ref offset, 0x83, tableMissJumps, ref tableMissJumpCount);
        EmitBytes(code, ref offset, [0x4D, 0x8B, 0x1C, 0xC3]);
        EmitBytes(code, ref offset, [0x4D, 0x85, 0xDB]);
        EmitNearJump(code, ref offset, 0x84, tableMissJumps, ref tableMissJumpCount);

        EmitBytes(code, ref offset, [0x48, 0x89, 0xD0]);
        EmitBytes(code, ref offset, [0x48, 0xC1, 0xE8, 0x16]);
        EmitByte(code, ref offset, 0x25);
        EmitUInt32(code, ref offset, 0xFFFu);
        EmitBytes(code, ref offset, [0x4D, 0x8B, 0x1C, 0xC3]);
        EmitBytes(code, ref offset, [0x4D, 0x85, 0xDB]);
        EmitNearJump(code, ref offset, 0x84, tableMissJumps, ref tableMissJumpCount);

        EmitBytes(code, ref offset, [0x48, 0x89, 0xD0]);
        EmitBytes(code, ref offset, [0x48, 0xC1, 0xE8, 0x0C]);
        EmitByte(code, ref offset, 0x25);
        EmitUInt32(code, ref offset, 0x3FFu);
        EmitBytes(code, ref offset, [0x48, 0xC1, 0xE0, 0x05]);
        EmitBytes(code, ref offset, [0x49, 0x01, 0xC3]);

        // Claim Armed. Dirty means another fault already won.
        EmitByte(code, ref offset, 0xB9);
        EmitUInt32(code, ref offset, GuestImageWriteTracker.NativeWindowsPageStateDirty);
        EmitByte(code, ref offset, 0xB8);
        EmitUInt32(code, ref offset, GuestImageWriteTracker.NativeWindowsPageStateArmed);
        EmitBytes(code, ref offset, [0xF0, 0x41, 0x0F, 0xB1, 0x0B]);
        Span<int> claimedJumps = stackalloc int[2];
        var claimedJumpCount = 0;
        EmitNearJump(code, ref offset, 0x84, claimedJumps, ref claimedJumpCount);
        EmitBytes(code, ref offset, [0x83, 0xF8, 0x02]);
        EmitNearJump(code, ref offset, 0x85, tableMissJumps, ref tableMissJumpCount);

        var claimedOffset = offset;
        PatchNearJumps(code, claimedJumps, claimedJumpCount, claimedOffset);

        // Save volatile state, then restore write access for this page.
        EmitBytes(code, ref offset, [0x48, 0x83, 0xEC, 0x48]);
        EmitBytes(code, ref offset, [0x89, 0x44, 0x24, 0x20]);
        EmitBytes(code, ref offset, [0x48, 0x89, 0x54, 0x24, 0x28]);
        EmitBytes(code, ref offset, [0x4C, 0x89, 0x54, 0x24, 0x30]);
        EmitBytes(code, ref offset, [0x4C, 0x89, 0x5C, 0x24, 0x38]);
        EmitBytes(code, ref offset, [0x48, 0x89, 0xD1]);
        EmitBytes(code, ref offset, [0x48, 0x81, 0xE1]);
        EmitUInt32(code, ref offset, 0xFFFFF000u);
        EmitByte(code, ref offset, 0xBA);
        EmitUInt32(code, ref offset, 0x1000u);
        EmitBytes(code, ref offset, [0x41, 0xB8]);
        EmitUInt32(code, ref offset, 0x04u);
        EmitBytes(code, ref offset, [0x41, 0x83, 0x7B, 0x04, 0x00]);
        EmitBytes(code, ref offset, [0x74, 0x06]);
        EmitBytes(code, ref offset, [0x41, 0xB8]);
        EmitUInt32(code, ref offset, 0x40u);
        EmitBytes(code, ref offset, [0x4C, 0x8D, 0x4C, 0x24, 0x40]);
        EmitBytes(code, ref offset, [0x48, 0xB8]);
        *(nint*)(code + offset) = virtualProtect;
        offset += sizeof(nint);
        EmitBytes(code, ref offset, [0xFF, 0xD0]);
        EmitBytes(code, ref offset, [0x85, 0xC0]);
        var protectFailureJump = offset;
        EmitBytes(code, ref offset, [0x0F, 0x84]);
        EmitUInt32(code, ref offset, 0u);

        EmitBytes(code, ref offset, [0x4C, 0x8B, 0x5C, 0x24, 0x38]);
        EmitBytes(code, ref offset, [0x4C, 0x8B, 0x54, 0x24, 0x30]);
        EmitBytes(code, ref offset, [0x48, 0x8B, 0x54, 0x24, 0x28]);

        // Only the first fault that changes this page to Dirty publishes it.
        // Later threads can still restore write access, but must not link the
        // same intrusive node twice and create a cycle in the dirty stack.
        EmitBytes(code, ref offset, [0x8B, 0x44, 0x24, 0x20]);
        EmitBytes(code, ref offset, [0x83, 0xF8, 0x02]);
        var skipDirtyPushJump = offset;
        EmitBytes(code, ref offset, [0x0F, 0x84]);
        EmitUInt32(code, ref offset, 0u);

        var dirtyPushLoopOffset = offset;
        EmitBytes(code, ref offset, [0x49, 0x8B, 0x42, 0x30]);
        EmitBytes(code, ref offset, [0x49, 0x89, 0x43, 0x18]);
        EmitBytes(code, ref offset, [0xF0, 0x4D, 0x0F, 0xB1, 0x5A, 0x30]);
        var dirtyPushRetryJump = offset;
        EmitBytes(code, ref offset, [0x0F, 0x85]);
        EmitUInt32(code, ref offset, 0u);
        *(int*)(code + dirtyPushRetryJump + 2) =
            dirtyPushLoopOffset - (dirtyPushRetryJump + 6);
        EmitBytes(code, ref offset, [0xF0, 0x49, 0xFF, 0x42, 0x38]);

        var skipDirtyPushOffset = offset;
        *(int*)(code + skipDirtyPushJump + 2) =
            skipDirtyPushOffset - (skipDirtyPushJump + 6);

        EmitBytes(code, ref offset, [0xF0, 0x41, 0xFF, 0x43, 0x08]);
        EmitBytes(code, ref offset, [0xF0, 0x49, 0xFF, 0x42, 0x08]);
        EmitBytes(code, ref offset, [0x49, 0x89, 0x52, 0x20]);
        EmitBytes(code, ref offset, [0x8B, 0x44, 0x24, 0x20]);
        EmitBytes(code, ref offset, [0x41, 0x89, 0x42, 0x28]);
        EmitBytes(code, ref offset, [0x48, 0x83, 0xC4, 0x48]);
        EmitByte(code, ref offset, 0xB8);
        EmitUInt32(code, ref offset, unchecked((uint)-1));
        EmitByte(code, ref offset, 0xC3);

        var protectFailureOffset = offset;
        *(int*)(code + protectFailureJump + 2) =
            protectFailureOffset - (protectFailureJump + 6);
        EmitBytes(code, ref offset, [0x4C, 0x8B, 0x54, 0x24, 0x30]);
        EmitBytes(code, ref offset, [0xF0, 0x49, 0xFF, 0x42, 0x10]);
        EmitBytes(code, ref offset, [0x48, 0x83, 0xC4, 0x48]);
        EmitBytes(code, ref offset, [0x31, 0xC0, 0xC3]);

        var tableMissOffset = offset;
        PatchNearJumps(code, tableMissJumps, tableMissJumpCount, tableMissOffset);
        EmitBytes(code, ref offset, [0xF0, 0x49, 0xFF, 0x42, 0x18]);

        var missOffset = offset;
        PatchNearJumps(code, missJumps, missJumpCount, missOffset);
        EmitBytes(code, ref offset, [0x31, 0xC0, 0xC3]);

        if (offset > stubSize)
        {
            VirtualFree(allocation, 0u, 32768u);
            return 0;
        }

        uint oldProtect = 0;
        if (!VirtualProtect(allocation, stubSize, 32u, &oldProtect))
        {
            VirtualFree(allocation, 0u, 32768u);
            return 0;
        }

        FlushInstructionCache(GetCurrentProcess(), allocation, (nuint)offset);
        return (nint)allocation;
    }

    private unsafe static void EmitBytes(
        byte* code,
        ref int offset,
        ReadOnlySpan<byte> bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            EmitByte(code, ref offset, bytes[index]);
        }
    }

    private unsafe static void EmitNearJump(
        byte* code,
        ref int offset,
        byte condition,
        Span<int> jumps,
        ref int jumpCount)
    {
        EmitBytes(code, ref offset, [0x0F, condition]);
        jumps[jumpCount++] = offset;
        EmitUInt32(code, ref offset, 0u);
    }

    private unsafe static void PatchNearJumps(
        byte* code,
        Span<int> jumps,
        int jumpCount,
        int target)
    {
        for (var index = 0; index < jumpCount; index++)
        {
            var displacement = jumps[index];
            *(int*)(code + displacement) = target - (displacement + sizeof(int));
        }
    }
}
