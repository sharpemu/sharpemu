// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

internal static class Il2CppApiLookupAbi
{
    internal static OrbisGen2Result SetResult(CpuContext context, bool resolved, ulong address)
    {
        // il2cpp_api_lookup_symbol is a normal pointer-returning function:
        // the result is in RAX and RSI remains caller-owned state.
        context[CpuRegister.Rax] = resolved ? address : 0;
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
