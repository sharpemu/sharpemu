// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Messenger;

/// <summary>Small Unity/libc compatibility shims exercised by The Messenger.</summary>
public static class MessengerCompatExports
{
    [SysAbiExport(Nid = "wLlFkwG9UcQ", ExportName = "time", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libc")]
    public static int Time(CpuContext ctx)
    {
        var seconds = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var output = ctx[CpuRegister.Rdi];
        if (output != 0 && !ctx.TryWriteUInt64(output, unchecked((ulong)seconds)))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)seconds);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "M4YYbSFfJ8g", ExportName = "setenv", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libc")]
    public static int Setenv(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "-P6FNMzk2Kc", ExportName = "cosf", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libc")]
    public static int Cosf(CpuContext ctx)
    {
        // AMD64 passes a scalar float in XMM0 and returns it in XMM0. RDI is
        // unrelated caller state and must not be interpreted as the argument.
        ctx.GetXmmRegister(0, out var low, out var high);
        var value = BitConverter.Int32BitsToSingle(unchecked((int)low));
        var resultBits = unchecked((uint)BitConverter.SingleToInt32Bits(MathF.Cos(value)));
        ctx.SetXmmRegister(0, (low & 0xFFFFFFFF00000000UL) | resultBits, high);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "YQ0navp+YIc", ExportName = "puts", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libc")]
    public static int Puts(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "KuOuD58hqn4", ExportName = "malloc_stats_fast", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libc")]
    public static int MallocStatsFast(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "-pnj3-7a6QA", ExportName = "unity_mono_set_user_malloc_mutex", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libunity")]
    public static int UnityMonoSetMallocMutex(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "35NoyMOtYpE", ExportName = "SetDataFolder", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libunity")]
    public static int SetDataFolder(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

#pragma warning disable SHEM006
    [SysAbiExport(Nid = "cJ2Y4E-t258", ExportName = "il2cpp_api_register_symbol", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libil2cpp")]
    public static int Il2CppRegisterSymbol(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
#pragma warning restore SHEM006
