// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;

namespace SharpEmu.GnmMode.Libraries.Gnm;

public static partial class GnmDriverExports
{
    public static int DriverCaptureInProgress(CpuContext context) => context.SetReturn(0);

    public static int ValidateCommandBuffers(CpuContext context) =>
        context.SetReturn(unchecked((int)0x80D13FFF)); // Retail GNM has no developer validation service.

    public static int GetTessellationFactorRingBufferBaseAddress(CpuContext context)
    {
        if (!AgcExports.TryGetTessellationFactorRingAddress(context.Memory, out var address))
        {
            context[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }
        context[CpuRegister.Rax] = address;
        return 0;
    }
}
