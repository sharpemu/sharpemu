// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    [SysAbiExport(Nid = "BPE9s9vQQXo", ExportName = "mmap", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixMmap(CpuContext ctx)
    {
        var requested = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        var protection = (int)ctx[CpuRegister.Rdx];
        var flags = (uint)ctx[CpuRegister.Rcx];
        var descriptor = (int)ctx[CpuRegister.R8];
        var offset = ctx[CpuRegister.R9];
        if (length == 0 || length > ulong.MaxValue - (OrbisPageSize - 1) ||
            (flags & 3) != 2 || (flags & 0x1000) == 0 || (flags & ~0xFF001012u) != 0 ||
            descriptor != -1 || offset != 0 || !TryDecodeMappedProtection(protection, out var mode))
            return PosixFailure(ctx, MemoryInvalidArgument, Einval);
        length = (length + OrbisPageSize - 1) & ~(OrbisPageSize - 1);
        var shift = flags >> 24;
        if (shift != 0 && shift is < 14 or > 31)
            return PosixFailure(ctx, MemoryInvalidArgument, Einval);
        var alignment = shift == 0 ? OrbisPageSize : 1UL << (int)shift;
        if ((flags & OrbisKernelMapFixed) != 0 &&
            (requested == 0 || !IsAligned(requested, alignment) || requested > ulong.MaxValue - length))
            return PosixFailure(ctx, MemoryInvalidArgument, Einval);
        var address = 0UL;
        var result = RunMappingTransaction(() =>
        {
            lock (_memoryGate)
                return MapFlexibleRangeLocked(ctx, requested, length, protection,
                    flags & ~0x1002u, alignment, mode, out address);
        });
        if (result != 0)
            return PosixFailure(ctx, result, result & 0xFFFF);
        ctx[CpuRegister.Rax] = address;
        return 0;
    }
}
