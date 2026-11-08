// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using LibAtrac9;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    [SysAbiExport(
        Nid = "3pCNbVM11UA",
        ExportName = "sceNgs2CalcWaveformBlock",
        Target = Generation.Gen5,
        LibraryName = "libSceNgs2")]
    public static int Ngs2CalcWaveformBlock(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rcx];
        if (output == 0) return SetReturn(ctx, OrbisNgs2ErrorInvalidParseOutAddress);
        Span<byte> format = stackalloc byte[Ngs2WaveformParser.FormatSize];
        if (ctx[CpuRegister.Rdi] == 0 || !ctx.Memory.TryRead(ctx[CpuRegister.Rdi], format))
            return SetReturn(ctx, InvalidWaveformFormat);
        var type = U32(format, 0);
        var channels = U32(format, 4);
        var rate = U32(format, 8);
        if (channels is < 1 or > 8 || rate is < 1 or > 192000)
            return SetReturn(ctx, InvalidWaveformFormat);

        var frameBytes = (ulong)PcmSampleBytes(type) * channels;
        ulong frameSamples = 1;
        if (type == 0x1c)
        {
            frameBytes = 16 * channels;
            frameSamples = 28;
        }
        else if (type == 0x40)
        {
            var config = ReadAtrac9Config(format.Slice(12, 4));
            if (config is null || config.ChannelCount != channels || config.SampleRate != rate)
                return SetReturn(ctx, InvalidWaveformFormat);
            frameBytes = (ulong)config.SuperframeBytes;
            frameSamples = (ulong)config.SuperframeSamples;
        }
        else if (frameBytes == 0)
        {
            return SetReturn(ctx, unchecked((int)0x804A8432));
        }

        // shortcut: reject non-contiguous framing until the sampler supports frame padding and offsets.
        if (U32(format, 16) != 0 || U32(format, 20) != 0)
            return SetReturn(ctx, InvalidWaveformFormat);
        var position = (uint)ctx[CpuRegister.Rsi];
        var count = (uint)ctx[CpuRegister.Rdx];
        var skip = count == 0 ? 0 : position % frameSamples;
        var frames = (skip + count + frameSamples - 1) / frameSamples;
        Span<byte> block = stackalloc byte[Ngs2WaveformParser.BlockSize];
        block.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(block, position / frameSamples * frameBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(block[8..], frames * frameBytes);
        BinaryPrimitives.WriteUInt32LittleEndian(block[20..], (uint)skip);
        BinaryPrimitives.WriteUInt32LittleEndian(block[24..], count);
        return SetReturn(ctx, ctx.Memory.TryWrite(output, block) ? 0 : OrbisNgs2ErrorInvalidParseOutAddress);
    }

    private static Atrac9Config? ReadAtrac9Config(ReadOnlySpan<byte> data)
    {
        // Accept a packed config word and the raw four-byte config returned by our RIFF parser.
        var config = data.ToArray();
        if (config[0] is not (0xfe or 0x30)) Array.Reverse(config);
        try { return new Atrac9Config(config); }
        catch (InvalidDataException) { return null; }
    }
}
