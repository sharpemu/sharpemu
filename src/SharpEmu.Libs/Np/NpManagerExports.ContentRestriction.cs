// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Np;

public static partial class NpManagerExports
{
    private const int NpErrorInvalidSize = unchecked((int)0x80550011);

    [SysAbiExport(
        Nid = "A2CQ3kgSopQ",
        ExportName = "sceNpSetContentRestriction",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNpManager")]
    public static int NpSetContentRestriction(CpuContext context)
    {
        var address = context[CpuRegister.Rdi];
        Span<byte> header = stackalloc byte[24];
        if (address == 0 || !context.Memory.TryRead(address, header[..8]))
            return SetReturn(context, NpErrorInvalidArgument);
        if (BinaryPrimitives.ReadUInt64LittleEndian(header) != (ulong)header.Length)
            return SetReturn(context, NpErrorInvalidSize);
        if (!context.Memory.TryRead(address, header))
            return SetReturn(context, NpErrorInvalidArgument);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var entries = BinaryPrimitives.ReadUInt64LittleEndian(header[16..]);
        if ((sbyte)header[8] < 0 || count is < 0 or > 256 || (count != 0 && entries == 0))
            return SetReturn(context, NpErrorInvalidArgument);
        if (count != 0)
        {
            Span<byte> restrictions = stackalloc byte[count * 8];
            if (entries > ulong.MaxValue - (ulong)restrictions.Length ||
                !context.Memory.TryRead(entries, restrictions))
                return SetReturn(context, NpErrorInvalidArgument);
        }
        return SetReturn(context, 0);
    }
}
