// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;

namespace SharpEmu.GnmMode.Libraries.Ajm;

public static partial class GnmAjmExports
{
    private static int InitializeRiff(CpuContext context, BatchScratch scratch, InstanceState state, List<Buffer> inputs)
    {
        Span<byte> signature = stackalloc byte[4];
        var signatureBytes = ReadPrefix(context.Memory, inputs, signature);
        if (signatureBytes < 0)
            return 4;
        if (signatureBytes < 4)
            return signatureBytes != 0 && "RIFF"u8[..signatureBytes].SequenceEqual(signature[..signatureBytes]) ? 8 : -1;
        if (!signature.SequenceEqual("RIFF"u8))
            return -1;
        Span<byte> header = stackalloc byte[8192];
        var length = ReadPrefix(context.Memory, inputs, header);
        if (length < 0)
            return 4;
        header = header[..length];
        if (length < 12)
            return 8;
        if (!header.Slice(8, 4).SequenceEqual("WAVE"u8))
            return 4;
        Span<byte> parameters = stackalloc byte[16];
        parameters.Clear();
        var hasFormat = false;
        var hasFact = false;
        var offset = 12;
        while (offset + 8 <= length)
        {
            var kind = header.Slice(offset, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header[(offset + 4)..]);
            offset += 8;
            if (kind.SequenceEqual("data"u8))
            {
                if (!hasFormat)
                    return 4;
                scratch.WriteBytes(0xF00, parameters);
                var flags = ControlReset | ControlInitialize | (hasFact ? AjmExports.AjmJobSidebandFlagGaplessDecode : 0);
                var address = scratch.Address(hasFact ? 0xF00 : 0xF08);
                var result = ExecuteControl(scratch.Context, scratch, state, flags, new Buffer(address, hasFact ? 16UL : 8UL), default);
                Trace($"riff_initialize config={Convert.ToHexString(parameters[8..12])} channels={state.Config?.ChannelCount} status=0x{result:X}");
                return result;
            }
            if (size > (uint)(length - offset))
                return length == 8192 ? 4 : 8;
            var chunk = header.Slice(offset, (int)size);
            if (kind.SequenceEqual("fmt "u8))
            {
                ReadOnlySpan<byte> guid = [0xD2, 0x42, 0xE1, 0x47, 0xBA, 0x36, 0x8D, 0x4D,
                    0x88, 0xFC, 0x61, 0x65, 0x4F, 0x8C, 0x83, 0x6C];
                if (size < 52 || BinaryPrimitives.ReadUInt16LittleEndian(chunk) != 0xFFFE || !chunk.Slice(24, 16).SequenceEqual(guid))
                    return 4;
                chunk.Slice(44, 4).CopyTo(parameters[8..]);
                hasFormat = true;
            }
            else if (kind.SequenceEqual("fact"u8))
            {
                if (size < 8 || BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) > ushort.MaxValue)
                    return 4;
                chunk[..4].CopyTo(parameters);
                BinaryPrimitives.WriteUInt16LittleEndian(parameters[4..], (ushort)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]));
                hasFact = true;
            }
            offset += (int)size + (int)(size & 1);
        }
        return length == 8192 ? 4 : 8;
    }

    private static int ReadPrefix(ICpuMemory memory, List<Buffer> inputs, Span<byte> destination)
    {
        var length = 0;
        foreach (var input in inputs)
        {
            var count = (int)Math.Min(input.Size, (ulong)(destination.Length - length));
            if (count != 0 && !memory.TryRead(input.Address, destination.Slice(length, count)))
                return -1;
            length += count;
            if (length == destination.Length)
                break;
        }
        return length;
    }
}
