// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Core.Runtime;

/// <summary>
/// Resolves the allocator-replacement initializer a title advertises through the
/// SceProcParam -> sceLibcParam -> _sceLibcMallocReplace chain. On hardware,
/// libSceLibcInternal reads this chain while libc itself initializes — before the
/// application image's own initializers run — and calls the game's user_malloc_init
/// so the replacement allocator exists before any guest static initializer allocates.
/// Titles that ship a replace table (e.g. UE4 titles) never construct their global
/// allocator without this call, and their static-init cascade dereferences a null
/// allocator singleton.
/// All fields are read from guest memory and are untrusted: every read is
/// bounds-checked through <see cref="ICpuMemory"/> and every size/magic validated
/// before the resolved entry point is considered callable.
/// </summary>
public static class LibcParamBootstrap
{
    private const uint ProcParamMagic = 0x4942524F; // "ORBI"
    private const int ProcParamLibcParamOffset = 0x38;
    private const ulong ProcParamMinimumSize = ProcParamLibcParamOffset + sizeof(ulong);
    private const int LibcParamMallocReplaceOffset = 0x30;
    private const ulong LibcParamMinimumSize = LibcParamMallocReplaceOffset + sizeof(ulong);
    private const int MallocReplaceInitOffset = 0x10;
    private const ulong MallocReplaceMinimumSize = MallocReplaceInitOffset + sizeof(ulong);
    private const ulong MallocReplaceMagicGen4 = 1; // PS4-era table (size 0x70)
    private const ulong MallocReplaceMagicGen5 = 2; // PS5 table (size 0x78)
    private const ulong MaxPlausibleStructSize = 0x1000;

    public static bool TryResolveUserMallocInit(
        ICpuMemory memory,
        ulong procParamAddress,
        out ulong userMallocInit,
        out string detail)
    {
        userMallocInit = 0;

        if (procParamAddress == 0)
        {
            detail = "no SceProcParam segment";
            return false;
        }

        if (!TryReadUInt64(memory, procParamAddress, out var procParamSize) ||
            !TryReadUInt32(memory, procParamAddress + 8, out var procParamMagic))
        {
            detail = $"SceProcParam header unreadable at 0x{procParamAddress:X16}";
            return false;
        }

        if (procParamMagic != ProcParamMagic)
        {
            detail = $"SceProcParam magic mismatch (0x{procParamMagic:X8})";
            return false;
        }

        if (procParamSize < ProcParamMinimumSize || procParamSize > MaxPlausibleStructSize)
        {
            detail = $"SceProcParam size 0x{procParamSize:X} out of range";
            return false;
        }

        if (!TryReadUInt64(memory, procParamAddress + ProcParamLibcParamOffset, out var libcParamAddress))
        {
            detail = "sceLibcParam slot unreadable";
            return false;
        }

        if (libcParamAddress == 0)
        {
            detail = "no sceLibcParam block";
            return false;
        }

        if (!TryReadUInt64(memory, libcParamAddress, out var libcParamSize))
        {
            detail = $"sceLibcParam header unreadable at 0x{libcParamAddress:X16}";
            return false;
        }

        if (libcParamSize < LibcParamMinimumSize || libcParamSize > MaxPlausibleStructSize)
        {
            detail = $"sceLibcParam size 0x{libcParamSize:X} out of range";
            return false;
        }

        if (!TryReadUInt64(memory, libcParamAddress + LibcParamMallocReplaceOffset, out var mallocReplaceAddress))
        {
            detail = "_sceLibcMallocReplace slot unreadable";
            return false;
        }

        if (mallocReplaceAddress == 0)
        {
            detail = "no _sceLibcMallocReplace table";
            return false;
        }

        if (!TryReadUInt64(memory, mallocReplaceAddress, out var mallocReplaceSize) ||
            !TryReadUInt64(memory, mallocReplaceAddress + 8, out var mallocReplaceMagic))
        {
            detail = $"_sceLibcMallocReplace header unreadable at 0x{mallocReplaceAddress:X16}";
            return false;
        }

        if (mallocReplaceMagic != MallocReplaceMagicGen4 && mallocReplaceMagic != MallocReplaceMagicGen5)
        {
            detail = $"_sceLibcMallocReplace magic {mallocReplaceMagic} unsupported";
            return false;
        }

        if (mallocReplaceSize < MallocReplaceMinimumSize || mallocReplaceSize > MaxPlausibleStructSize)
        {
            detail = $"_sceLibcMallocReplace size 0x{mallocReplaceSize:X} out of range";
            return false;
        }

        if (!TryReadUInt64(memory, mallocReplaceAddress + MallocReplaceInitOffset, out var initAddress))
        {
            detail = "user_malloc_init slot unreadable";
            return false;
        }

        if (initAddress == 0)
        {
            detail = "user_malloc_init entry absent";
            return false;
        }

        // The entry must point at mapped guest memory; anything else is a corrupt
        // table and must not be dispatched as guest code.
        Span<byte> probe = stackalloc byte[1];
        if (!memory.TryRead(initAddress, probe))
        {
            detail = $"user_malloc_init 0x{initAddress:X16} is not mapped";
            return false;
        }

        userMallocInit = initAddress;
        detail =
            $"proc_param=0x{procParamAddress:X16} libc_param=0x{libcParamAddress:X16} " +
            $"malloc_replace=0x{mallocReplaceAddress:X16} (size=0x{mallocReplaceSize:X}, magic={mallocReplaceMagic}) " +
            $"user_malloc_init=0x{initAddress:X16}";
        return true;
    }

    private static bool TryReadUInt64(ICpuMemory memory, ulong address, out ulong value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(ulong)];
        if (!memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
        return true;
    }

    private static bool TryReadUInt32(ICpuMemory memory, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }
}
