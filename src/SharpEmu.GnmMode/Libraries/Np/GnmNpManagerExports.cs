// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;

namespace SharpEmu.GnmMode.Libraries.Np;

public static class GnmNpManagerExports
{
    public static int GetNpId(CpuContext context)
    {
        var address = context[CpuRegister.Rsi];
        if (unchecked((int)context[CpuRegister.Rdi]) == -1 || address == 0)
            return context.SetReturn(unchecked((int)0x80550003));
        Span<byte> npId = stackalloc byte[36];
        npId.Clear();
        if (!context.Memory.TryWrite(address, npId))
            return context.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        return NpManagerExports.NpGetOnlineId(context);
    }
}
