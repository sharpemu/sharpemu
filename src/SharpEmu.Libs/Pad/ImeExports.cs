// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Pad;

public static class ImeExports
{
    private const int PrimaryUserId = 0x10000000;
    private const int ImeErrorInvalidAddress = unchecked((int)0x80BC0001);
    private const int ImeErrorInvalidUserId = unchecked((int)0x80BC0010);
    private const int ImeErrorNotOpened = unchecked((int)0x80BC0005);

    private static bool _keyboardOpen;
    public static int ImeKeyboardOpen(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var parameterAddress = ctx[CpuRegister.Rsi];
        if (parameterAddress == 0)
        {
            return SetReturn(ctx, ImeErrorInvalidAddress);
        }

        if (userId != PrimaryUserId)
        {
            return SetReturn(ctx, ImeErrorInvalidUserId);
        }

        _keyboardOpen = true;
        return SetReturn(ctx, 0);
    }
    public static int ImeUpdate(CpuContext ctx) =>
        SetReturn(ctx, _keyboardOpen ? 0 : ImeErrorNotOpened);

    private static int SetReturn(CpuContext ctx, int result)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)result);
        return result;
    }
}
