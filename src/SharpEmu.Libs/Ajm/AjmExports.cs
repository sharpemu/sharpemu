// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Ajm;

public static class AjmExports
{
    private const int OrbisAjmErrorInvalidParameter = unchecked((int)0x80930005);
    private static int _nextContextId;

    [SysAbiExport(
        Nid = "dl+4eHSzUu4",
        ExportName = "sceAjmInitialize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAjm")]
    public static int Initialize(CpuContext ctx)
    {
        var reserved = ctx[CpuRegister.Rdi];
        var outContextAddress = ctx[CpuRegister.Rsi];

        // The public AJM ABI requires reserved to be zero.  In particular, do
        // not report success without initializing the context: audio engines
        // then start worker threads around a nonexistent AJM backend.
        if (reserved != 0 || outContextAddress == 0)
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        if (!ctx.TryWriteUInt32(outContextAddress, contextId))
        {
            return ctx.SetReturn(OrbisAjmErrorInvalidParameter);
        }

        return ctx.SetReturn(0);
    }
}
