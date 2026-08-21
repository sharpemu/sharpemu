// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static class KernelWriteThrottlingCompatExports
{
    internal const ulong WriteBandwidth64KiBUnits = 0x4B00;
    internal const int StatusSize = 32;

    [SysAbiExport(
        Nid = "YFC3dBBipj8",
        ExportName = "sceKernelWriteThrottlingStatus",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelWriteThrottlingStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdi];
        if (statusAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> status = stackalloc byte[StatusSize];
        status.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(status, WriteBandwidth64KiBUnits);

        return ctx.Memory.TryWrite(statusAddress, status)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }
}
