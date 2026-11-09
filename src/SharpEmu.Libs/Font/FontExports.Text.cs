// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Font;

public static partial class FontExports
{
    private const int MaxTextCharacters = 65536;
    private static readonly ConcurrentDictionary<(ICpuMemory Memory, ulong Handle), TextStringState> TextStrings = new();
    private static readonly ConcurrentDictionary<(ICpuMemory Memory, ulong Handle), TextCharacterState> TextCharacters = new();
    private static long _nextRenderCharacter;

    private sealed class TextStringState
    {
        public required ICpuMemory Memory;
        public int WritingForm;
        public uint TerminateCode;
        public ulong TerminateOrder;
        public ulong CharactersAddress;
        public required TextCharacterState[] Characters;
        public readonly List<ulong> RenderHandles = [];
    }

    private sealed class TextCharacterState
    {
        public required TextStringState String;
        public int Index;
        public ulong Font;
        public uint Code;
        public ulong Order;
        public int RenderEndIndex;
        public ulong Address => String.CharactersAddress + (ulong)(Index * 0x28);
    }

    private static ICpuMemory GetTextMemory(CpuContext ctx)
    {
        var memory = ctx.Memory;
        while (memory is ICpuMemoryWrapper wrapper) memory = wrapper.Inner;
        return memory;
    }

    private static bool TryGetTextCharacter(CpuContext ctx, ulong address, out TextCharacterState character) =>
        TextCharacters.TryGetValue((GetTextMemory(ctx), address), out character!);

    private static TextCharacterState? NextTextCharacter(TextCharacterState character) =>
        character.Index + 1 < character.String.Characters.Length ? character.String.Characters[character.Index + 1] : null;

    [SysAbiExport(Nid = "OqQKX0h5COw", ExportName = "sceFontTextSourceSetWritingForm",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int TextSourceSetWritingForm(CpuContext ctx) => SetTextSourceField(ctx, 0x38);

    [SysAbiExport(Nid = "eCRMCSk96NU", ExportName = "sceFontTextSourceSetDefaultFont",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int TextSourceSetDefaultFont(CpuContext ctx) => SetTextSourceField(ctx, 0x30);

    private static int SetTextSourceField(CpuContext ctx, ulong offset)
    {
        var address = ctx[CpuRegister.Rdi];
        if (address == 0 || address > ulong.MaxValue - 0x60)
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return ctx.TryWriteUInt64(address + offset, ctx[CpuRegister.Rsi])
            ? SetSuccess(ctx) : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "MO24vDhmS4E", ExportName = "sceFontCreateString",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CreateString(CpuContext ctx)
    {
        var sourceAddress = ctx[CpuRegister.Rsi];
        var detailAddress = ctx[CpuRegister.Rdx];
        var outputAddress = ctx[CpuRegister.Rcx];
        if (sourceAddress == 0 || outputAddress == 0 || sourceAddress > ulong.MaxValue - 0x60)
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        Span<byte> source = stackalloc byte[0x60];
        if (!ctx.TryWriteUInt64(outputAddress, 0) || !ctx.Memory.TryRead(sourceAddress, source))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);

        var defaultFont = BinaryPrimitives.ReadUInt64LittleEndian(source[0x30..]);
        if (detailAddress != 0)
        {
            if (detailAddress > ulong.MaxValue - 0x20 || !ctx.TryReadUInt64(detailAddress + 8, out var detailFont))
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
            if (detailFont != 0) defaultFont = detailFont;
        }
        var state = new TextStringState
        {
            Memory = GetTextMemory(ctx),
            WritingForm = (int)BinaryPrimitives.ReadUInt64LittleEndian(source[0x38..]),
            Characters = [],
        };
        var characters = new List<TextCharacterState>();
        var parser = BinaryPrimitives.ReadUInt64LittleEndian(source[0x20..]);
        var result = parser == 0
            ? ParseTextBytes(ctx, sourceAddress, source, defaultFont, state, characters)
            : ParseTextCallback(ctx, sourceAddress, parser, defaultFont, state, characters);
        if (result != 0) return ctx.SetReturn(result);

        state.Characters = characters.ToArray();
        foreach (var character in state.Characters) character.RenderEndIndex = state.Characters.Length;
        // Keep the opaque string and its guest-readable character records in one owned allocation.
        var bytes = new byte[0x10 + state.Characters.Length * 0x28];
        if (ctx.Memory is not IGuestMemoryAllocator allocator ||
            !allocator.TryAllocateGuestMemory((ulong)bytes.Length, 0x10, out var handle))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        state.CharactersAddress = handle + 0x10;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, 0x0F04);
        for (var index = 0; index < state.Characters.Length; index++)
        {
            var character = state.Characters[index];
            var record = bytes.AsSpan(0x10 + index * 0x28, 0x28);
            BinaryPrimitives.WriteUInt64LittleEndian(record, handle);
            BinaryPrimitives.WriteInt32LittleEndian(record[8..], index);
            BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], character.Font);
            BinaryPrimitives.WriteUInt32LittleEndian(record[0x18..], character.Code);
            BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], character.Order);
        }
        if (!ctx.Memory.TryWrite(handle, bytes) || !ctx.TryWriteUInt64(outputAddress, handle))
        {
            allocator.TryFreeGuestMemory(handle);
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }
        TextStrings[(state.Memory, handle)] = state;
        foreach (var character in state.Characters) TextCharacters[(state.Memory, character.Address)] = character;
        return SetSuccess(ctx);
    }

    private static int ParseTextBytes(CpuContext ctx, ulong sourceAddress, ReadOnlySpan<byte> source,
        ulong font, TextStringState state, List<TextCharacterState> characters)
    {
        var current = BinaryPrimitives.ReadUInt64LittleEndian(source[0x18..]);
        var end = BinaryPrimitives.ReadUInt64LittleEndian(source[0x10..]);
        if (end == 0 && current != 0)
        {
            if (ctx.Memory.TryScanCString(current, 0, false, MaxTextCharacters * 4 + 1, out var terminator))
            {
                end = terminator;
            }
            else
            {
                end = current;
                for (var index = 0; index <= MaxTextCharacters * 4; index++)
                {
                    if (!ctx.TryReadByte(end, out var value)) return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    if (value == 0) break;
                    if (end == ulong.MaxValue || index == MaxTextCharacters * 4)
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                    end++;
                }
            }
        }
        if (end < current || end - current > MaxTextCharacters * 4 || (current == 0 && end != 0))
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        var bytes = new byte[(int)(end - current)];
        if (bytes.Length != 0 && !ctx.Memory.TryRead(current, bytes))
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (characters.Count == MaxTextCharacters ||
                Rune.DecodeFromUtf8(bytes.AsSpan(offset), out var rune, out var consumed) != OperationStatus.Done)
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            characters.Add(new() { String = state, Index = characters.Count, Font = font, Code = (uint)rune.Value, Order = current + (ulong)offset });
            offset += consumed;
        }
        return ctx.TryWriteUInt64(sourceAddress + 0x18, end) ? 0 : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    private static int ParseTextCallback(CpuContext ctx, ulong sourceAddress, ulong parser,
        ulong defaultFont, TextStringState state, List<TextCharacterState> characters)
    {
        if (GuestThreadExecution.Scheduler is not { } scheduler)
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED;
        if (!TryAllocateOpaque(ctx, 0x30, out var scratch))
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        try
        {
            Span<byte> parsed = stackalloc byte[0x30];
            for (var index = 0; index <= MaxTextCharacters; index++)
            {
                parsed.Clear();
                if (!ctx.Memory.TryWrite(scratch, parsed) ||
                    !scheduler.TryCallGuestFunction(ctx, parser, sourceAddress, scratch, scratch + 8,
                        0, 0, "font_text_parser", out var returned, out _) ||
                    !ctx.Memory.TryRead(scratch, parsed))
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                var code = BinaryPrimitives.ReadUInt32LittleEndian(parsed[0x10..]);
                var order = BinaryPrimitives.ReadUInt64LittleEndian(parsed);
                var font = BinaryPrimitives.ReadUInt64LittleEndian(parsed[8..]);
                switch (unchecked((int)returned))
                {
                    case 0:
                        state.TerminateCode = code;
                        state.TerminateOrder = order != 0 ? order : font;
                        return 0;
                    case 1 when index < MaxTextCharacters && IsValidCode(code):
                        characters.Add(new() { String = state, Index = index, Font = font != 0 ? font : defaultFont, Code = code, Order = order });
                        break;
                    case -1:
                        return code != 0 ? unchecked((int)code) : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                    default:
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }
            }
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
        finally
        {
            if (ctx.Memory is IGuestMemoryAllocator allocator) allocator.TryFreeGuestMemory(scratch);
        }
    }

    [SysAbiExport(Nid = "SSCaczu2aMQ", ExportName = "sceFontDestroyString",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int DestroyString(CpuContext ctx)
    {
        var pointer = ctx[CpuRegister.Rdi];
        if (pointer == 0) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        if (!ctx.TryReadUInt64(pointer, out var handle)) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        var key = (GetTextMemory(ctx), handle);
        if (handle != 0 && !TextStrings.ContainsKey(key)) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        if (!ctx.TryWriteUInt64(pointer, 0)) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        if (TextStrings.TryRemove(key, out var state))
        {
            foreach (var character in state.Characters) TextCharacters.TryRemove((state.Memory, character.Address), out _);
            lock (state.RenderHandles)
                foreach (var render in state.RenderHandles) TextCharacters.TryRemove((state.Memory, render), out _);
            if (ctx.Memory is IGuestMemoryAllocator allocator) allocator.TryFreeGuestMemory(handle);
        }
        return SetSuccess(ctx);
    }

    [SysAbiExport(Nid = "o1vIEHeb6tw", ExportName = "sceFontStringGetWritingForm",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int StringGetWritingForm(CpuContext ctx) =>
        ctx.SetReturn(TextStrings.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var state) ? state.WritingForm : 0x10);

    [SysAbiExport(Nid = "ObkDGDBsVtw", ExportName = "sceFontStringGetTerminateCode",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int StringGetTerminateCode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = TextStrings.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var state) ? state.TerminateCode : 0;
        return 0;
    }

    [SysAbiExport(Nid = "+B-xlbiWDJ4", ExportName = "sceFontStringGetTerminateOrder",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int StringGetTerminateOrder(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = TextStrings.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var state) ? state.TerminateOrder : 0;
        return 0;
    }

    [SysAbiExport(Nid = "Avv7OApgCJk", ExportName = "sceFontStringRefersTextCharacters",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int StringRefersTextCharacters(CpuContext ctx)
    {
        TextStrings.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var state);
        if (ctx[CpuRegister.Rsi] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rsi], (uint)(state?.Characters.Length ?? 0)))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        ctx[CpuRegister.Rax] = state is { Characters.Length: > 0 } ? state.CharactersAddress : 0;
        return 0;
    }

    [SysAbiExport(Nid = "hq5LffQjz-s", ExportName = "sceFontStringRefersRenderCharacters",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int StringRefersRenderCharacters(CpuContext ctx)
    {
        if (!TextStrings.TryGetValue((GetTextMemory(ctx), ctx[CpuRegister.Rdi]), out var state))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        var start = 0;
        var end = state.Characters.Length;
        if (ctx[CpuRegister.Rsi] != 0)
        {
            if (!TryGetTextCharacter(ctx, ctx[CpuRegister.Rsi], out var first) || first.String != state)
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            start = first.Index;
        }
        if (ctx[CpuRegister.Rdx] != 0)
        {
            if (!TryGetTextCharacter(ctx, ctx[CpuRegister.Rdx], out var last) || last.String != state)
                return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
            end = last.Index + 1;
        }
        if (end < start) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        if (ctx[CpuRegister.Rcx] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rcx], (uint)(end - start)))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        ctx[CpuRegister.Rax] = 0;
        if (end > start)
        {
            var first = state.Characters[start];
            // Render ranges are opaque handles so overlapping ranges keep independent end points.
            var render = unchecked((ulong)Interlocked.Decrement(ref _nextRenderCharacter));
            lock (state.RenderHandles)
            {
                state.RenderHandles.Add(render);
                TextCharacters[(state.Memory, render)] = new()
                {
                    String = state, Index = start, Font = first.Font, Code = first.Code,
                    Order = first.Order, RenderEndIndex = end,
                };
            }
            ctx[CpuRegister.Rax] = render;
        }
        return 0;
    }

    [SysAbiExport(Nid = "BkjBP+YC19w", ExportName = "sceFontCharacterRefersTextNext",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CharacterRefersTextNext(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = TryGetTextCharacter(ctx, ctx[CpuRegister.Rdi], out var character) ? NextTextCharacter(character)?.Address ?? 0 : 0;
        return 0;
    }

    [SysAbiExport(Nid = "mxgmMj-Mq-o", ExportName = "sceFontCharacterGetTextOrder",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CharacterGetTextOrder(CpuContext ctx)
    {
        if (!TryGetTextCharacter(ctx, ctx[CpuRegister.Rdi], out var character) || ctx[CpuRegister.Rsi] == 0)
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], character.Order) ? SetSuccess(ctx) : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "zN3+nuA0SFQ", ExportName = "sceFontCharacterGetTextFontCode",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CharacterGetTextFontCode(CpuContext ctx)
    {
        if (!TryGetTextCharacter(ctx, ctx[CpuRegister.Rdi], out var character))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        if ((ctx[CpuRegister.Rsi] != 0 && !ctx.TryWriteUInt64(ctx[CpuRegister.Rsi], character.Font)) ||
            (ctx[CpuRegister.Rdx] != 0 && !ctx.TryWriteUInt32(ctx[CpuRegister.Rdx], character.Code)))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        return SetSuccess(ctx);
    }

    [SysAbiExport(Nid = "SaRlqtqaCew", ExportName = "sceFontCharacterLooksWhiteSpace",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CharacterLooksWhiteSpace(CpuContext ctx) => ctx.SetReturn(
        TryGetTextCharacter(ctx, ctx[CpuRegister.Rdi], out var character) &&
        Rune.IsValid((int)character.Code) && Rune.IsWhiteSpace(new Rune((int)character.Code)) ? 1 : 0);

    // shortcut: one render character per code point and left-to-right levels; extend when a title needs shaping or bidi.
    [SysAbiExport(Nid = "6DFUkCwQLa8", ExportName = "sceFontCharacterGetBidiLevel",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CharacterGetBidiLevel(CpuContext ctx) => WriteCharacterState(ctx);

    [SysAbiExport(Nid = "coCrV6IWplE", ExportName = "sceFontCharacterGetSyllableStringState",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int CharacterGetSyllableStringState(CpuContext ctx) => WriteCharacterState(ctx);

    private static int WriteCharacterState(CpuContext ctx)
    {
        if (!TryGetTextCharacter(ctx, ctx[CpuRegister.Rdi], out _) || ctx[CpuRegister.Rsi] == 0)
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return ctx.TryWriteUInt32(ctx[CpuRegister.Rsi], 0) ? SetSuccess(ctx) : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    [SysAbiExport(Nid = "FXP359ygujs", ExportName = "sceFontDestroyLibrary",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int DestroyLibrary(CpuContext ctx)
    {
        var pointer = ctx[CpuRegister.Rdi];
        if (pointer == 0) return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        if (!ctx.TryReadUInt64(pointer, out var handle) || !ctx.TryWriteUInt64(pointer, 0))
            return SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        if (handle != 0 && ctx.Memory is IGuestMemoryAllocator allocator) allocator.TryFreeGuestMemory(handle);
        return SetSuccess(ctx);
    }

    [SysAbiExport(Nid = "h6hIgxXEiEc", ExportName = "sceFontMemoryTerm",
        Target = Generation.Gen5, LibraryName = "libSceFont")]
    public static int MemoryTerm(CpuContext ctx) => ctx[CpuRegister.Rdi] != 0 && ctx.TryWriteUInt16(ctx[CpuRegister.Rdi], 0)
        ? SetSuccess(ctx) : SetReturn(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
}
