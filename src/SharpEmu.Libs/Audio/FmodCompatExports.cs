// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Audio;

public static class FmodCompatExports
{
    private static readonly ConcurrentDictionary<ulong, int> SetOutputCalls = new();

    [SysAbiExport(
        Nid = "uPLTdl3psGk",
        Target = Generation.Gen5,
        LibraryName = "libfmod")]
    public static int FmodSystemSetOutput(CpuContext ctx)
    {
        var system = ctx[CpuRegister.Rdi];
        var output = unchecked((int)ctx[CpuRegister.Rsi]);
        var callCount = SetOutputCalls.AddOrUpdate(system, 1, static (_, count) => count + 1);
        var resetPrematureInit = callCount <= 2;
        if (resetPrematureInit && system != 0)
        {
            Span<byte> zeroByte = stackalloc byte[1];
            Span<byte> outputBytes = stackalloc byte[sizeof(int)];
            Span<byte> zeroInt = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(outputBytes, output);
            _ = ctx.Memory.TryWrite(system + 0x08, zeroByte);
            _ = ctx.Memory.TryWrite(system + 0x116D0, outputBytes);
            _ = ctx.Memory.TryWrite(system + 0x116D4, zeroInt);
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_FMOD"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] fmod.system_set_output system=0x{system:X16} output={output} call={callCount} reset_premature_init={resetPrematureInit} -> 0");
        }

        // The PS5 FMOD object arrives with its initialized byte set before Unity has
        // applied the startup output configuration. Preserve those settings while
        // clearing the premature marker so Studio can execute the real core init.
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }
}
