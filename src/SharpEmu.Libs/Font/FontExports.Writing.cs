// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Font;

public static partial class FontExports
{
    private const ulong WritingMagic = 0x0F05;
    private const int WritingStepOffset = 0x30;
    private const int WritingLetterOffset = 0x80;
    private const int WritingMetricsOffset = 0xC0;
    private const int WritingEndOffset = 0xD8;
    private const int WritingCountOffset = 0xDC;
    private static readonly ConcurrentDictionary<(ICpuMemory Memory, ulong Handle), WritingLineState> WritingLines = new();

    private readonly record struct WritingMetrics(float AdvanceX, float AdvanceY, float Top, float Bottom, float Left, float Right)
    {
        public void Write(Span<byte> bytes)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes, AdvanceX);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], AdvanceY);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], Top);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], Bottom);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[16..], Left);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[20..], Right);
        }

        public static WritingMetrics Read(ReadOnlySpan<byte> bytes) => new(
            BinaryPrimitives.ReadSingleLittleEndian(bytes),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[4..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[8..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[12..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[16..]),
            BinaryPrimitives.ReadSingleLittleEndian(bytes[20..]));
    }

    private sealed class WritingLineState
    {
        public readonly List<ulong> Steps = [];
        public int Cursor;
        public WritingMetrics Metrics;
    }

    [SysAbiExport(Nid = "fD5rqhEXKYQ", ExportName = "sceFontWritingInit",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingInit(CpuContext ctx)
    {
        var writing = ctx[CpuRegister.Rdi];
        if (writing == 0 || writing > ulong.MaxValue - 0x100 ||
            !TextStrings.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rsi]), out var text))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        TextCharacterState? character = null;
        if (ctx[CpuRegister.Rdx] != 0)
        {
            if (!TryGetTextCharacter(ctx, ctx[CpuRegister.Rdx], out character) || character.String != text)
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            }
        }
        else if (text.Characters.Length != 0)
        {
            character = text.Characters[0];
        }

        // Keep all returned structures inside the caller's 0x100-byte writing object.
        Span<byte> bytes = stackalloc byte[0x100];
        bytes.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, WritingMagic);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], writing + WritingStepOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], character?.Address ?? 0);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], writing + WritingLetterOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[32..], writing + WritingMetricsOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], character?.Address ?? 0);
        if (character != null)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes[WritingEndOffset..], (uint)character.RenderEndIndex);
            FillWritingStep(bytes, character, 0, 0);
        }

        return ctx.Memory.TryWrite(writing, bytes)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "W-2WOXEHGck", ExportName = "sceFontWritingRefersRenderStep",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingRefersRenderStep(CpuContext ctx)
    {
        var writing = ctx[CpuRegister.Rdi];
        ctx[CpuRegister.Rax] = 0;
        Span<byte> bytes = stackalloc byte[0x100];
        if (!IsWriting(ctx, writing) || !ctx.Memory.TryRead(writing, bytes)) return 0;
        var nextAddress = BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]);
        var endIndex = BinaryPrimitives.ReadUInt32LittleEndian(bytes[WritingEndOffset..]);
        if (!TryGetTextCharacter(ctx, nextAddress, out var character) || character.Index >= endIndex) return 0;

        var previous = WritingMetrics.Read(bytes[WritingMetricsOffset..]);
        var metrics = FillWritingStep(bytes, character, previous.AdvanceX, previous.AdvanceY);
        var count = BinaryPrimitives.ReadUInt32LittleEndian(bytes[WritingCountOffset..]);
        new WritingMetrics(previous.AdvanceX + metrics.AdvanceX, previous.AdvanceY + metrics.AdvanceY,
            count == 0 ? metrics.Top : MathF.Min(previous.Top, metrics.Top),
            count == 0 ? metrics.Bottom : MathF.Max(previous.Bottom, metrics.Bottom),
            count == 0 ? metrics.Left : MathF.Min(previous.Left, metrics.Left),
            count == 0 ? metrics.Right : MathF.Max(previous.Right, metrics.Right))
            .Write(bytes[WritingMetricsOffset..]);
        var next = NextTextCharacter(character);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], character.Address);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[40..], next != null && next.Index < endIndex ? next.Address : 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[WritingCountOffset..], count + 1);
        if (ctx.Memory.TryWrite(writing, bytes)) ctx[CpuRegister.Rax] = writing + WritingStepOffset;
        return 0;
    }

    private static WritingMetrics FillWritingStep(Span<byte> bytes, TextCharacterState character, float x, float y)
    {
        var glyph = GetGlyphMetrics(character.Font, character.Code);
        var scale = GetFontScale(character.Font);
        var step = bytes.Slice(WritingStepOffset, 0x48);
        step.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(step, x);
        BinaryPrimitives.WriteSingleLittleEndian(step[4..], y);
        BinaryPrimitives.WriteSingleLittleEndian(step[8..], glyph.Advance);
        BinaryPrimitives.WriteUInt64LittleEndian(step[16..], character.Font);
        BinaryPrimitives.WriteUInt32LittleEndian(step[24..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(step[28..], character.Code);
        BinaryPrimitives.WriteSingleLittleEndian(step[0x20..], x);
        BinaryPrimitives.WriteSingleLittleEndian(step[0x24..], y);
        ReadOnlySpan<float> glyphMetrics = [glyph.Width, glyph.Height, glyph.BearingX, glyph.BearingY, glyph.Advance, 0, 0, scale.Height];
        for (var i = 0; i < glyphMetrics.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(step[(0x28 + i * 4)..], glyphMetrics[i]);

        var letter = bytes.Slice(WritingLetterOffset, 0x40);
        letter.Clear();
        BinaryPrimitives.WriteSingleLittleEndian(letter, x);
        BinaryPrimitives.WriteSingleLittleEndian(letter[4..], y);
        BinaryPrimitives.WriteSingleLittleEndian(letter[8..], glyph.Advance);
        BinaryPrimitives.WriteUInt32LittleEndian(letter[16..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(letter[20..], (uint)character.Index);
        BinaryPrimitives.WriteUInt32LittleEndian(letter[24..], 1);
        letter[32] = 1;
        letter[34] = 1;
        letter[37] = 1;
        letter[38] = 1;
        return new(glyph.Advance, 0, y - glyph.BearingY, y + glyph.Height - glyph.BearingY,
            x + glyph.BearingX, x + glyph.BearingX + glyph.Width);
    }

    [SysAbiExport(Nid = "f4Onl7efPEY", ExportName = "sceFontWritingRefersRenderStepCharacter",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingRefersRenderStepCharacter(CpuContext ctx)
    {
        var writing = ctx[CpuRegister.Rdi];
        var valid = IsWriting(ctx, writing);
        if (ctx[CpuRegister.Rsi] != 0 &&
            !ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], valid ? writing + WritingLetterOffset : 0))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        ctx[CpuRegister.Rax] = valid && ctx.TryReadUInt64(writing + 16, out var character) ? character : 0;
        return 0;
    }

    [SysAbiExport(Nid = "fljdejMcG1c", ExportName = "sceFontWritingGetRenderMetrics",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingGetRenderMetrics(CpuContext ctx)
    {
        var writing = ctx[CpuRegister.Rdi];
        if (!IsWriting(ctx, writing) || ctx[CpuRegister.Rsi] == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        Span<byte> metrics = stackalloc byte[24];
        return ctx.Memory.TryRead(writing + WritingMetricsOffset, metrics) && ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], metrics)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static bool IsWriting(CpuContext ctx, ulong writing) =>
        writing != 0 && writing <= ulong.MaxValue - 0x100 &&
        ctx.TryReadUInt64(writing, out var magic) && magic == WritingMagic;

    [SysAbiExport(Nid = "7rogx92EEyc", ExportName = "sceFontCreateWritingLine",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CreateWritingLine(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rcx];
        if (output == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        if (!TryAllocateOpaque(ctx, 0x10, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (!ctx.TryWriteUInt64(output, handle))
        {
            ((IGuestMemoryAllocator)ctx.Memory).TryFreeGuestMemory(handle);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        WritingLines[(GetTextMemory(ctx), handle)] = new();
        return SetSuccess(ctx);
    }

    [SysAbiExport(Nid = "PEjv7CVDRYs", ExportName = "sceFontDestroyWritingLine",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int DestroyWritingLine(CpuContext ctx)
    {
        var output = ctx[CpuRegister.Rdi];
        if (output == 0 || !ctx.TryReadUInt64(output, out var handle))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (handle != 0 && !WritingLines.ContainsKey((GetTextMemory(ctx), handle)))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        if (!ctx.TryWriteUInt64(output, 0))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        if (WritingLines.TryRemove((GetTextMemory(ctx), handle), out var line))
        {
            lock (line)
            {
                ClearWritingLine(ctx, line);
                ((IGuestMemoryAllocator)ctx.Memory).TryFreeGuestMemory(handle);
            }
        }
        return SetSuccess(ctx);
    }

    [SysAbiExport(Nid = "1+DgKL0haWQ", ExportName = "sceFontWritingLineClear",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingLineClear(CpuContext ctx)
    {
        if (!WritingLines.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var line))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        lock (line) ClearWritingLine(ctx, line);
        return SetSuccess(ctx);
    }

    private static void ClearWritingLine(CpuContext ctx, WritingLineState line)
    {
        foreach (var step in line.Steps)
        {
            ((IGuestMemoryAllocator)ctx.Memory).TryFreeGuestMemory(step);
        }
        line.Steps.Clear();
        line.Cursor = 0;
        line.Metrics = default;
    }

    [SysAbiExport(Nid = "wyKFUOWdu3Q", ExportName = "sceFontWritingLineWritesOrder",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingLineWritesOrder(CpuContext ctx)
    {
        if (!WritingLines.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var line))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        Span<byte> bytes = stackalloc byte[0x40];
        bytes.Clear();
        WritingMetrics metrics = new(8, 0, -12, 4, 0, 8);
        if (ctx[CpuRegister.Rdx] != 0)
        {
            if (!ctx.Memory.TryRead(ctx[CpuRegister.Rdx], bytes[..24]))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            metrics = WritingMetrics.Read(bytes);
            bytes.Clear();
        }
        lock (line)
        {
            var old = line.Metrics;
            BinaryPrimitives.WriteSingleLittleEndian(bytes, old.AdvanceX);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[4..], old.AdvanceY);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[8..], metrics.AdvanceX);
            BinaryPrimitives.WriteSingleLittleEndian(bytes[12..], metrics.AdvanceY);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes[24..], ctx[CpuRegister.Rcx]);
            metrics.Write(bytes[40..]);
            if (!TryAllocateOpaque(ctx, bytes.Length, out var step))
            {
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            if (!ctx.Memory.TryWrite(step, bytes))
            {
                ((IGuestMemoryAllocator)ctx.Memory).TryFreeGuestMemory(step);
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            }
            var first = line.Steps.Count == 0;
            line.Metrics = new(old.AdvanceX + metrics.AdvanceX, old.AdvanceY + metrics.AdvanceY,
                first ? old.AdvanceY + metrics.Top : MathF.Min(old.Top, old.AdvanceY + metrics.Top),
                first ? old.AdvanceY + metrics.Bottom : MathF.Max(old.Bottom, old.AdvanceY + metrics.Bottom),
                first ? old.AdvanceX + metrics.Left : MathF.Min(old.Left, old.AdvanceX + metrics.Left),
                first ? old.AdvanceX + metrics.Right : MathF.Max(old.Right, old.AdvanceX + metrics.Right));
            line.Steps.Add(step);
            line.Cursor = 0;
        }
        return SetSuccess(ctx);
    }

    [SysAbiExport(Nid = "JQKWIsS9joE", ExportName = "sceFontWritingLineGetOrderingSpace",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingLineGetOrderingSpace(CpuContext ctx)
    {
        if (!WritingLines.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var line))
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        lock (line)
        {
            CpuRegister[] outputs = [CpuRegister.Rsi, CpuRegister.Rdx, CpuRegister.Rcx, CpuRegister.R8];
            for (var i = 0; i < outputs.Length; i++)
            {
                if (ctx[outputs[i]] != 0 && !TryWriteUInt32(ctx, ctx[outputs[i]],
                    BitConverter.SingleToUInt32Bits(i is 0 or 2 ? line.Metrics.AdvanceX : 0)))
                {
                    return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
                }
            }
        }
        return SetSuccess(ctx);
    }

    [SysAbiExport(Nid = "+FYcYefsVX0", ExportName = "sceFontWritingLineRefersRenderStep",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingLineRefersRenderStep(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        if (WritingLines.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var line))
        {
            lock (line)
            {
                if (line.Cursor < line.Steps.Count) ctx[CpuRegister.Rax] = line.Steps[line.Cursor++];
            }
        }
        return 0;
    }

    [SysAbiExport(Nid = "nlU2VnfpqTM", ExportName = "sceFontWritingLineGetRenderMetrics",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int WritingLineGetRenderMetrics(CpuContext ctx)
    {
        if (!WritingLines.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var line) || ctx[CpuRegister.Rsi] == 0)
        {
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }
        Span<byte> bytes = stackalloc byte[24];
        lock (line) line.Metrics.Write(bytes);
        return ctx.Memory.TryWrite(ctx[CpuRegister.Rsi], bytes)
            ? SetSuccess(ctx)
            : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }
}
