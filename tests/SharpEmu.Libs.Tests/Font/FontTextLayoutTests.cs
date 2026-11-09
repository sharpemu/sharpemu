// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

[Collection("GuestThreadFlowProfile")]
public sealed class FontTextLayoutTests
{
    private const ulong Base = 0x19_0000_0000;
    private const ulong Source = Base + 0x100;
    private const ulong Text = Base + 0x200;
    private const ulong StringOutput = Base + 0x300;
    private const ulong Scratch = Base + 0x400;
    private readonly FakeCpuMemory _memory = new(Base, 0x10000);
    private readonly CpuContext _ctx;

    public FontTextLayoutTests() => _ctx = new CpuContext(_memory, Generation.Gen5);

    [Fact]
    public void TombRaiderTextLayoutImports_AreRegistered()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        (string Nid, string Name)[] expected =
        [
            ("FXP359ygujs", "DestroyLibrary"),
            ("h6hIgxXEiEc", "MemoryTerm"),
            ("OqQKX0h5COw", "TextSourceSetWritingForm"),
            ("eCRMCSk96NU", "TextSourceSetDefaultFont"),
            ("hq5LffQjz-s", "StringRefersRenderCharacters"),
            ("fD5rqhEXKYQ", "WritingInit"),
            ("W-2WOXEHGck", "WritingRefersRenderStep"),
            ("f4Onl7efPEY", "WritingRefersRenderStepCharacter"),
            ("mxgmMj-Mq-o", "CharacterGetTextOrder"),
            ("fljdejMcG1c", "WritingGetRenderMetrics"),
            ("o1vIEHeb6tw", "StringGetWritingForm"),
            ("SaRlqtqaCew", "CharacterLooksWhiteSpace"),
            ("zN3+nuA0SFQ", "CharacterGetTextFontCode"),
            ("Avv7OApgCJk", "StringRefersTextCharacters"),
            ("6DFUkCwQLa8", "CharacterGetBidiLevel"),
            ("coCrV6IWplE", "CharacterGetSyllableStringState"),
            ("BkjBP+YC19w", "CharacterRefersTextNext"),
            ("+B-xlbiWDJ4", "StringGetTerminateOrder"),
            ("7rogx92EEyc", "CreateWritingLine"),
            ("wyKFUOWdu3Q", "WritingLineWritesOrder"),
            ("JQKWIsS9joE", "WritingLineGetOrderingSpace"),
            ("1+DgKL0haWQ", "WritingLineClear"),
            ("+FYcYefsVX0", "WritingLineRefersRenderStep"),
            ("nlU2VnfpqTM", "WritingLineGetRenderMetrics"),
            ("PEjv7CVDRYs", "DestroyWritingLine"),
            ("MO24vDhmS4E", "CreateString"),
            ("ObkDGDBsVtw", "StringGetTerminateCode"),
            ("SSCaczu2aMQ", "DestroyString"),
        ];
        foreach (var (nid, name) in expected)
        {
            Assert.True(manager.TryGetExport(nid, out var export), $"Missing font NID {nid}");
            Assert.Equal("sceFont" + name, export.Name);
            Assert.Equal("libSceFont", export.LibraryName);
        }
        Assert.True(manager.TryGetExport("uZrOwuNJX-M", out var marker));
        Assert.Equal("sceRazorCpuPushMarkerStatic", marker.Name);
        _ctx[CpuRegister.Rax] = ulong.MaxValue;
        Assert.Equal(0, marker.Function(_ctx));
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void CreateString_DecodesUnicodeScalarsAndPreservesByteOrders()
    {
        const string text = "Ayarlar İğ😀 ";
        const ulong font = 0x6000_0000_5E80;
        var handle = CreateString(text, font);
        _ctx[CpuRegister.Rdi] = handle;
        FontExports.StringGetWritingForm(_ctx);
        Assert.Equal(0x10UL, _ctx[CpuRegister.Rax]);
        var first = FirstCharacter(handle, out var count);
        var expected = text.EnumerateRunes().ToArray();
        Assert.Equal((uint)expected.Length, count);
        Assert.True(_memory.CanRead(first, 0x28));

        var character = first;
        var byteOffset = 0UL;
        foreach (var rune in expected)
        {
            _ctx[CpuRegister.Rdi] = character;
            _ctx[CpuRegister.Rsi] = Scratch;
            _ctx[CpuRegister.Rdx] = Scratch + 8;
            Assert.Equal(0, FontExports.CharacterGetTextFontCode(_ctx));
            Assert.Equal(font, Read64(Scratch));
            Assert.Equal((uint)rune.Value, Read32(Scratch + 8));
            _ctx[CpuRegister.Rsi] = Scratch;
            Assert.Equal(0, FontExports.CharacterGetTextOrder(_ctx));
            Assert.Equal(Text + byteOffset, Read64(Scratch));
            FontExports.CharacterLooksWhiteSpace(_ctx);
            Assert.Equal(Rune.IsWhiteSpace(rune) ? 1UL : 0UL, _ctx[CpuRegister.Rax]);
            FontExports.CharacterRefersTextNext(_ctx);
            character = _ctx[CpuRegister.Rax];
            byteOffset += (ulong)rune.Utf8SequenceLength;
        }
        Assert.Equal(0UL, character);
        Assert.Equal(Text + byteOffset, Read64(Source + 0x18));
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = first;
        _ctx[CpuRegister.Rdx] = first + 2 * 0x28;
        _ctx[CpuRegister.Rcx] = Scratch;
        FontExports.StringRefersRenderCharacters(_ctx);
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(3U, Read32(Scratch));
        _ctx[CpuRegister.Rdx] = first;
        FontExports.StringRefersRenderCharacters(_ctx);
        var renderCharacter = _ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, renderCharacter);
        Assert.Equal(1U, Read32(Scratch));
        _ctx[CpuRegister.Rdi] = Base + 0x500;
        _ctx[CpuRegister.Rsi] = handle;
        _ctx[CpuRegister.Rdx] = renderCharacter;
        Assert.Equal(0, FontExports.WritingInit(_ctx));
        _ctx[CpuRegister.Rsi] = Scratch;
        FontExports.WritingRefersRenderStepCharacter(_ctx);
        Assert.Equal(first, _ctx[CpuRegister.Rax]);

        DestroyString();
        _ctx[CpuRegister.Rdi] = first;
        FontExports.CharacterRefersTextNext(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void EmptyString_HasNoCharactersAndCanBeDestroyedTwice()
    {
        var handle = CreateString("");
        Assert.Equal(0UL, FirstCharacter(handle, out var count));
        Assert.Equal(0U, count);
        _ctx[CpuRegister.Rdi] = handle;
        FontExports.StringGetTerminateCode(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        FontExports.StringGetTerminateOrder(_ctx);
        Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        DestroyString();
        DestroyString();
    }

    [Fact]
    public void CreateString_InvalidSourceRangeReturnsErrorWithoutPublishingHandle()
    {
        InitSource("A");
        Assert.True(_ctx.TryWriteUInt64(Source + 0x10, Text - 1));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = Source;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = StringOutput;
        Assert.NotEqual(0, FontExports.CreateString(_ctx));
        Assert.Equal(0UL, Read64(StringOutput));
    }

    [Theory]
    [InlineData("C328")]
    [InlineData("E282")]
    public void CreateString_MalformedOrTruncatedUtf8DoesNotPublishString(string hex)
    {
        var bytes = Convert.FromHexString(hex);
        InitSource("XX");
        Assert.True(_memory.TryWrite(Text, bytes));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = Source;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = StringOutput;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, FontExports.CreateString(_ctx));
        Assert.Equal(0UL, Read64(StringOutput));
        Assert.Equal(Text, Read64(Source + 0x18));
    }

    [Fact]
    public void TextHandles_AreSharedByContextsWrappingTheSameGuestMemory()
    {
        InitSource("İğ");
        var producer = new CpuContext(new TrackedCpuMemory(_memory), Generation.Gen5);
        var consumer = new CpuContext(new TrackedCpuMemory(_memory), Generation.Gen5);
        producer[CpuRegister.Rsi] = Source;
        producer[CpuRegister.Rcx] = StringOutput;
        Assert.Equal(0, FontExports.CreateString(producer));
        var handle = Read64(StringOutput);
        consumer[CpuRegister.Rdi] = handle;
        consumer[CpuRegister.Rsi] = Scratch;
        FontExports.StringRefersTextCharacters(consumer);
        var first = consumer[CpuRegister.Rax];
        Assert.NotEqual(0UL, first);
        Assert.Equal(2U, Read32(Scratch));
        consumer[CpuRegister.Rdi] = first;
        consumer[CpuRegister.Rsi] = 0;
        consumer[CpuRegister.Rdx] = Scratch;
        Assert.Equal(0, FontExports.CharacterGetTextFontCode(consumer));
        Assert.Equal(0x0130U, Read32(Scratch));
        FontExports.CharacterRefersTextNext(consumer);
        Assert.NotEqual(0UL, consumer[CpuRegister.Rax]);

        consumer[CpuRegister.Rdi] = StringOutput;
        Assert.Equal(0, FontExports.DestroyString(consumer));
        Assert.Equal(0UL, Read64(StringOutput));
        producer[CpuRegister.Rdi] = first;
        FontExports.CharacterRefersTextNext(producer);
        Assert.Equal(0UL, producer[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void TextSourceInit_UnboundedParserSourceHasNullEnd(int textSize)
    {
        _ctx[CpuRegister.Rdi] = Source;
        _ctx[CpuRegister.Rsi] = Text;
        _ctx[CpuRegister.Rdx] = unchecked((uint)textSize);
        _ctx[CpuRegister.Rcx] = Base + 0x9000;

        Assert.Equal(0, FontExports.TextSourceInit(_ctx));
        Assert.Equal(0UL, Read64(Source + 0x10));
        Assert.Equal(Text, Read64(Source + 0x18));
    }

    [Fact]
    public void CreateString_ZeroSizeUtf8SourceStopsAtNullTerminator()
    {
        const string text = "İğ😀";
        _memory.WriteCString(Text, text);
        _ctx[CpuRegister.Rdi] = Source;
        _ctx[CpuRegister.Rsi] = Text;
        Assert.Equal(0, FontExports.TextSourceInit(_ctx));
        Assert.Equal(0UL, Read64(Source + 0x10));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = Source;
        _ctx[CpuRegister.Rcx] = StringOutput;
        Assert.Equal(0, FontExports.CreateString(_ctx));
        var character = FirstCharacter(Read64(StringOutput), out var count);
        Assert.Equal(3U, count);
        foreach (var rune in text.EnumerateRunes())
        {
            _ctx[CpuRegister.Rdi] = character;
            _ctx[CpuRegister.Rsi] = 0;
            _ctx[CpuRegister.Rdx] = Scratch;
            Assert.Equal(0, FontExports.CharacterGetTextFontCode(_ctx));
            Assert.Equal((uint)rune.Value, Read32(Scratch));
            FontExports.CharacterRefersTextNext(_ctx);
            character = _ctx[CpuRegister.Rax];
        }
        Assert.Equal(0UL, character);
        Assert.Equal(Text + (ulong)Encoding.UTF8.GetByteCount(text), Read64(Source + 0x18));
        DestroyString();
    }

    [Fact]
    public void CreateString_CallsGuestParserAndResumesAfterLineTermination()
    {
        const ulong parser = Base + 0x9000;
        const ulong defaultFont = 0x6000_0000_5E80;
        const ulong alternateFont = defaultFont + 0x100;
        InitSource("A\nB\n");
        _ctx[CpuRegister.Rdi] = Source;
        _ctx[CpuRegister.Rsi] = Text;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = parser;
        Assert.Equal(0, FontExports.TextSourceInit(_ctx));
        Assert.Equal(0UL, Read64(Source + 0x10));
        Assert.True(_ctx.TryWriteUInt64(Source + 0x30, defaultFont));
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, ParserScheduler>();
        var proxy = (ParserScheduler)(object)scheduler;
        var calls = 0;
        proxy.Call = args =>
        {
            var ctx = (CpuContext)args[0]!;
            Assert.Same(_ctx, ctx);
            Assert.Equal(parser, (ulong)args[1]!);
            Assert.Equal(Source, (ulong)args[2]!);
            var order = (ulong)args[3]!;
            var result = (ulong)args[4]!;
            Assert.True(_memory.CanRead(result, 0x20));
            Assert.Equal(Text + (ulong)calls, Read64(Source + 0x18));
            Assert.True(ctx.TryWriteUInt64(order, Text + (ulong)calls));
            if ((calls & 1) == 0)
            {
                Assert.True(ctx.TryWriteUInt64(result, calls == 0 ? 0 : alternateFont));
                Assert.True(ctx.TryWriteUInt32(result + 8, calls == 0 ? 0x0130U : 0x011EU));
                args[^2] = 1UL;
            }
            else
            {
                Assert.True(ctx.TryWriteUInt32(result + 8, calls == 1 ? 0xD000AU : 0U));
                args[^2] = 0UL;
            }
            Assert.True(ctx.TryWriteUInt64(Source + 0x18, Text + (ulong)++calls));
            args[^1] = null;
        };
        var previous = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            for (var line = 0; line < 2; line++)
            {
                _ctx[CpuRegister.Rdi] = 0;
                _ctx[CpuRegister.Rsi] = Source;
                _ctx[CpuRegister.Rdx] = 0;
                _ctx[CpuRegister.Rcx] = StringOutput;
                Assert.Equal(0, FontExports.CreateString(_ctx));
                var handle = Read64(StringOutput);
                var character = FirstCharacter(handle, out var count);
                Assert.Equal(1U, count);
                _ctx[CpuRegister.Rdi] = character;
                _ctx[CpuRegister.Rsi] = Scratch;
                _ctx[CpuRegister.Rdx] = Scratch + 8;
                Assert.Equal(0, FontExports.CharacterGetTextFontCode(_ctx));
                Assert.Equal(line == 0 ? defaultFont : alternateFont, Read64(Scratch));
                Assert.Equal(line == 0 ? 0x0130U : 0x011EU, Read32(Scratch + 8));
                _ctx[CpuRegister.Rsi] = Scratch;
                Assert.Equal(0, FontExports.CharacterGetTextOrder(_ctx));
                Assert.Equal(Text + (ulong)(line * 2), Read64(Scratch));
                _ctx[CpuRegister.Rdi] = handle;
                FontExports.StringGetTerminateCode(_ctx);
                Assert.Equal(line == 0 ? 0xD000AUL : 0UL, _ctx[CpuRegister.Rax]);
                FontExports.StringGetTerminateOrder(_ctx);
                Assert.Equal(Text + (ulong)(line * 2 + 1), _ctx[CpuRegister.Rax]);
                DestroyString();
            }
            Assert.Equal(4, calls);
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void CreateString_PropagatesGuestParserErrors()
    {
        const uint error = 0x80550010;
        InitSource("A");
        Assert.True(_ctx.TryWriteUInt64(Source + 0x20, Base + 0x9000));
        var scheduler = DispatchProxy.Create<IGuestThreadScheduler, ParserScheduler>();
        ((ParserScheduler)(object)scheduler).Call = args =>
        {
            Assert.True(_ctx.TryWriteUInt32((ulong)args[4]! + 8, error));
            args[^2] = unchecked((ulong)-1L);
            args[^1] = null;
        };
        var previous = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            _ctx[CpuRegister.Rdi] = 0;
            _ctx[CpuRegister.Rsi] = Source;
            _ctx[CpuRegister.Rdx] = 0;
            _ctx[CpuRegister.Rcx] = StringOutput;
            Assert.Equal(unchecked((int)error), FontExports.CreateString(_ctx));
            Assert.Equal(0UL, Read64(StringOutput));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previous;
        }
    }

    [Fact]
    public void WritingStep_UsesGuestPointersAndMatchesGlyphAdvance()
    {
        const ulong writing = Base + 0x500;
        var handle = CreateString("Wi");
        var character = FirstCharacter(handle, out _);
        _ctx[CpuRegister.Rdi] = writing;
        _ctx[CpuRegister.Rsi] = handle;
        _ctx[CpuRegister.Rdx] = character;
        Assert.Equal(0, FontExports.WritingInit(_ctx));
        FontExports.WritingRefersRenderStep(_ctx);
        var step = _ctx[CpuRegister.Rax];
        Assert.True(_memory.CanRead(step, 0x48));
        var bytes = new byte[0x48];
        Assert.True(_memory.TryRead(step, bytes));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x10)));
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x18)) & 0xff);
        Assert.Equal((uint)'W', BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0x1c)));
        var advance = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(8));
        Assert.True(advance > 0);
        Assert.Equal(advance, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(0x38)));

        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 'W';
        _ctx[CpuRegister.Rdx] = Scratch;
        Assert.Equal(0, FontExports.GetCharGlyphMetrics(_ctx));
        Assert.Equal(ReadFloat(Scratch + 16), advance);
        _ctx[CpuRegister.Rdi] = writing;
        _ctx[CpuRegister.Rsi] = Scratch;
        Assert.Equal(0, FontExports.WritingGetRenderMetrics(_ctx));
        Assert.Equal(advance, ReadFloat(Scratch));
        _ctx[CpuRegister.Rsi] = Scratch;
        FontExports.WritingRefersRenderStepCharacter(_ctx);
        Assert.Equal(character, _ctx[CpuRegister.Rax]);
        Assert.True(_memory.CanRead(Read64(Scratch), 0x40));
        DestroyString();
    }

    private void InitSource(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.True(_memory.TryWrite(Text, bytes));
        _ctx[CpuRegister.Rdi] = Source;
        _ctx[CpuRegister.Rsi] = Text;
        _ctx[CpuRegister.Rdx] = (ulong)bytes.Length;
        _ctx[CpuRegister.Rcx] = 0;
        _ctx[CpuRegister.R8] = 0;
        Assert.Equal(0, FontExports.TextSourceInit(_ctx));
    }

    private ulong CreateString(string text, ulong font = 0)
    {
        InitSource(text);
        _ctx[CpuRegister.Rdi] = Source;
        _ctx[CpuRegister.Rsi] = 0x10;
        Assert.Equal(0, FontExports.TextSourceSetWritingForm(_ctx));
        _ctx[CpuRegister.Rsi] = font;
        Assert.Equal(0, FontExports.TextSourceSetDefaultFont(_ctx));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = Source;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = StringOutput;
        Assert.Equal(0, FontExports.CreateString(_ctx));
        var handle = Read64(StringOutput);
        Assert.NotEqual(0UL, handle);
        return handle;
    }

    private ulong FirstCharacter(ulong handle, out uint count)
    {
        _ctx[CpuRegister.Rdi] = handle;
        _ctx[CpuRegister.Rsi] = Scratch;
        FontExports.StringRefersTextCharacters(_ctx);
        count = Read32(Scratch);
        return _ctx[CpuRegister.Rax];
    }

    private void DestroyString()
    {
        _ctx[CpuRegister.Rdi] = StringOutput;
        Assert.Equal(0, FontExports.DestroyString(_ctx));
        Assert.Equal(0UL, Read64(StringOutput));
    }

    private ulong Read64(ulong address)
    {
        Assert.True(_ctx.TryReadUInt64(address, out var value));
        return value;
    }

    private uint Read32(ulong address)
    {
        Assert.True(_ctx.TryReadUInt32(address, out var value));
        return value;
    }

    private float ReadFloat(ulong address) => BitConverter.UInt32BitsToSingle(Read32(address));

    public class ParserScheduler : DispatchProxy
    {
        public Action<object?[]>? Call { get; set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IGuestThreadScheduler.TryCallGuestFunction), targetMethod?.Name);
            Call!(args!);
            return true;
        }
    }
}
