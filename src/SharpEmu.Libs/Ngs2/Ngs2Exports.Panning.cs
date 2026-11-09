// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Ngs2;

public static partial class Ngs2Exports
{
    private static int InitializePanner(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var angles = ctx[CpuRegister.Rsi];
        var count = ctx[CpuRegister.Rdx];
        ctx.GetXmmRegister(0, out var bits, out _);
        var unit = BitConverter.UInt32BitsToSingle((uint)bits);
        if (address == 0) return unchecked((int)0x804A8453);
        if (!float.IsFinite(unit) || unit <= 0) return unchecked((int)0x804A8450);
        if (count is not (2 or 4 or 5 or 7)) return unchecked((int)0x804A8451);
        Span<byte> work = stackalloc byte[40];
        work.Clear();
        if (angles != 0)
        {
            if (!ctx.Memory.TryRead(angles, work[..((int)count * 4)])) return unchecked((int)0x804A8451);
        }
        else
        {
            ReadOnlySpan<float> defaults = count switch
            {
                2 => [-30f, 30f],
                4 => [-45f, 45f, -135f, 135f],
                5 => [-30f, 30f, 0f, -110f, 110f],
                _ => [-30f, 30f, 0f, -150f, 150f, -90f, 90f],
            };
            for (var i = 0; i < defaults.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(work[(i * 4)..], defaults[i] / 360 * unit);
        }
        for (var i = 0; i < (int)count; i++)
            if (!float.IsFinite(BinaryPrimitives.ReadSingleLittleEndian(work[(i * 4)..]))) return unchecked((int)0x804A8451);
        BinaryPrimitives.WriteSingleLittleEndian(work[32..], unit);
        BinaryPrimitives.WriteUInt32LittleEndian(work[36..], (uint)count);
        return ctx.Memory.TryWrite(address, work) ? 0 : unchecked((int)0x804A8453);
    }

    [SysAbiExport(Nid = "gbMKV+8Enuo", ExportName = "sceNgs2PanGetVolumeMatrix",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libSceNgs2")]
    public static int Ngs2PanGetVolumeMatrix(CpuContext ctx)
    {
        Span<byte> work = stackalloc byte[40];
        if (!ctx.Memory.TryRead(ctx[CpuRegister.Rdi], work)) return SetReturn(ctx, unchecked((int)0x804A8453));
        var unit = BinaryPrimitives.ReadSingleLittleEndian(work[32..]);
        var speakers = BinaryPrimitives.ReadUInt32LittleEndian(work[36..]);
        var count = ctx[CpuRegister.Rdx];
        var channels = ctx[CpuRegister.Rcx];
        if (channels is not (1 or 2 or 6 or 8)) return SetReturn(ctx, unchecked((int)0x804A8452));
        if (speakers is not (2 or 4 or 5 or 7) || !float.IsFinite(unit) || unit <= 0)
            return SetReturn(ctx, unchecked((int)0x804A8453));
        if (count is < 1 or > 8 || ctx[CpuRegister.R8] == 0) return SetReturn(ctx, unchecked((int)0x804A8454));
        Span<byte> parameters = stackalloc byte[(int)count * 16];
        Span<byte> output = stackalloc byte[(int)(count * channels) * 4];
        output.Clear();
        if (!ctx.Memory.TryRead(ctx[CpuRegister.Rsi], parameters)) return SetReturn(ctx, unchecked((int)0x804A8454));
        Span<float> angles = stackalloc float[8];
        Span<int> mapped = stackalloc int[8];
        var usable = 0;
        for (var i = 0; i < speakers; i++)
        {
            var channel = speakers == 4 ? i < 2 ? i : i + 2 : i < 3 ? i : i + 1;
            if (channel >= (int)channels) continue;
            var angle = BinaryPrimitives.ReadSingleLittleEndian(work[(i * 4)..]);
            if (!float.IsFinite(angle)) return SetReturn(ctx, unchecked((int)0x804A8453));
            angles[usable] = angle / unit * MathF.Tau;
            mapped[usable++] = channel;
        }
        Span<float> gains = stackalloc float[8];
        for (var row = 0; row < (int)count; row++)
        {
            var item = parameters[(row * 16)..];
            var angle = BinaryPrimitives.ReadSingleLittleEndian(item) / unit * MathF.Tau;
            var distance = BinaryPrimitives.ReadSingleLittleEndian(item[4..]);
            var fbw = BinaryPrimitives.ReadSingleLittleEndian(item[8..]);
            var lfe = BinaryPrimitives.ReadSingleLittleEndian(item[12..]);
            if (!float.IsFinite(angle) || !float.IsFinite(distance) || !float.IsFinite(fbw) || !float.IsFinite(lfe))
                return SetReturn(ctx, unchecked((int)0x804A8454));
            var levels = output.Slice(row * (int)channels * 4, (int)channels * 4);
            if (channels == 1) { BinaryPrimitives.WriteSingleLittleEndian(levels, fbw); continue; }
            if (usable == 0) return SetReturn(ctx, unchecked((int)0x804A8453));
            // shortcut: equal-power speaker pairs; native NGS spread curves are not calibrated yet.
            var before = -1;
            var after = -1;
            var backDistance = float.MaxValue;
            var forwardDistance = float.MaxValue;
            for (var i = 0; i < usable; i++)
            {
                var delta = (angles[i] - angle) % MathF.Tau;
                if (delta < 0) delta += MathF.Tau;
                if (delta < forwardDistance) { forwardDistance = delta; after = i; }
                var back = (MathF.Tau - delta) % MathF.Tau;
                if (back < backDistance) { backDistance = back; before = i; }
            }
            gains.Clear();
            if (before == after) gains[before] = 1;
            else
            {
                var fraction = backDistance / (backDistance + forwardDistance);
                gains[before] = MathF.Cos(fraction * MathF.PI / 2);
                gains[after] = MathF.Sin(fraction * MathF.PI / 2);
            }
            var spread = 1 - Math.Clamp(distance, 0, 1);
            var energy = 0f;
            for (var i = 0; i < usable; i++) { gains[i] = gains[i] * (1 - spread) + spread / MathF.Sqrt(usable); energy += gains[i] * gains[i]; }
            var scale = energy > 0 ? fbw / MathF.Sqrt(energy) : 0;
            for (var i = 0; i < usable; i++) BinaryPrimitives.WriteSingleLittleEndian(levels[(mapped[i] * 4)..], gains[i] * scale);
            if (channels >= 6) BinaryPrimitives.WriteSingleLittleEndian(levels[12..], lfe);
        }
        return SetReturn(ctx, ctx.Memory.TryWrite(ctx[CpuRegister.R8], output) ? 0 : unchecked((int)0x804A8454));
    }
}
