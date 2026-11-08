// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.GnmMode.Libraries.Np;

public static class GnmMatching2Exports
{
    private const int AlreadyInitialized = unchecked((int)0x80550C02);
    private const int NotInitialized = unchecked((int)0x80550C03);
    private const int InvalidArgument = unchecked((int)0x80550C0A);
    private static int _initialized;

    internal static void Reset() => Volatile.Write(ref _initialized, 0);

    public static int Initialize(CpuContext context)
    {
        if (Volatile.Read(ref _initialized) != 0)
            return context.SetReturn(AlreadyInitialized);

        var address = context[CpuRegister.Rdi];
        Span<byte> parameter = stackalloc byte[0x30];
        if (address == 0 || !context.Memory.TryRead(address, parameter[..0x28]))
            return context.SetReturn(InvalidArgument);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(parameter[0x20..]);
        if (size is not (0x28 or 0x30) || size == 0x30 && !context.Memory.TryRead(address, parameter))
            return context.SetReturn(InvalidArgument);

        return context.SetReturn(Interlocked.CompareExchange(ref _initialized, 1, 0) == 0
            ? 0 : AlreadyInitialized);
    }

    public static int Terminate(CpuContext context) =>
        context.SetReturn(Interlocked.Exchange(ref _initialized, 0) != 0 ? 0 : NotInitialized);
}
