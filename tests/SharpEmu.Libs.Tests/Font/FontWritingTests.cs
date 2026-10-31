// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

public sealed class FontWritingTests
{
    private const ulong Base = 0x3_0000_0000;

    [Fact]
    public void Writing_RenderingAtStepPositioningPlacesSuccessiveGlyphsApart()
    {
        var memory = new TrackingMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var fontString = CreateText(ctx, memory, "WW");
        const ulong writing = Base + 0x400;
        const ulong surface = Base + 0x700;
        const ulong pixels = Base + 0x1000;
        const int width = 96;
        const int height = 64;
        ctx[CpuRegister.Rdi] = surface;
        ctx[CpuRegister.Rsi] = pixels;
        ctx[CpuRegister.Rdx] = width;
        ctx[CpuRegister.Rcx] = 1;
        ctx[CpuRegister.R8] = width;
        ctx[CpuRegister.R9] = height;
        Assert.Equal(0, FontExports.RenderSurfaceInit(ctx));
        ctx[CpuRegister.Rdi] = writing;
        ctx[CpuRegister.Rsi] = fontString;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, FontExports.WritingInit(ctx));

        var rightEdges = new List<int>();
        for (var index = 0; index < 2; index++)
        {
            ctx[CpuRegister.Rdi] = writing;
            Assert.Equal(0, FontExports.WritingRefersRenderStep(ctx));
            var step = Read(memory, ctx[CpuRegister.Rax], 0x48);
            ctx[CpuRegister.Rdi] = BinaryPrimitives.ReadUInt64LittleEndian(step.AsSpan(0x10));
            ctx[CpuRegister.Rsi] = BinaryPrimitives.ReadUInt32LittleEndian(step.AsSpan(0x1c));
            ctx[CpuRegister.Rdx] = surface;
            ctx[CpuRegister.Rcx] = 0;
            ctx[CpuRegister.R8] = 0;
            // TRL takes the raster origin from positioning, independently of step.x/y.
            ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(4 + BinaryPrimitives.ReadSingleLittleEndian(step.AsSpan(0x20))), 0);
            ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(30 + BinaryPrimitives.ReadSingleLittleEndian(step.AsSpan(0x24))), 0);
            Assert.Equal(0, FontExports.RenderCharGlyphImageHorizontal(ctx));
            var bitmap = Read(memory, pixels, width * height);
            var occupiedColumns = Enumerable.Range(0, width)
                .Where(x => Enumerable.Range(0, height).Any(y => bitmap[y * width + x] != 0)).ToArray();
            Assert.NotEmpty(occupiedColumns);
            rightEdges.Add(occupiedColumns[^1]);
        }
        Assert.True(rightEdges[1] > rightEdges[0], "The second glyph must extend the text instead of overlapping the first.");
        ctx[CpuRegister.Rdi] = Base + 0x80;
        Assert.Equal(0, FontExports.DestroyString(ctx));
    }

    [Fact]
    public void Writing_IteratesOnceInitializedAndAccumulatesGlyphMetricsWithoutAllocating()
    {
        var memory = new TrackingMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var writing = Base + 0x400;
        var output = Base + 0x80;
        var fontString = CreateText(ctx, memory, "iW");
        ctx[CpuRegister.Rdi] = fontString;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, FontExports.StringRefersTextCharacters(ctx));
        var character = ctx[CpuRegister.Rax];
        var allocationCount = memory.Live.Count;
        var advances = new List<float>();
        var total = 0f;
        ctx[CpuRegister.Rdi] = writing;
        ctx[CpuRegister.Rsi] = fontString;
        ctx[CpuRegister.Rdx] = character;
        Assert.Equal(0, FontExports.WritingInit(ctx));
        foreach (var code in new uint[] { 'i', 'W' })
        {
            ctx[CpuRegister.Rdi] = writing;
            Assert.Equal(0, FontExports.WritingRefersRenderStep(ctx));
            var stepAddress = ctx[CpuRegister.Rax];
            Assert.InRange(stepAddress, writing, writing + 0x100 - 0x48);
            var step = Read(memory, stepAddress, 0x48);
            Assert.Equal(code, BinaryPrimitives.ReadUInt32LittleEndian(step.AsSpan(0x1C)));
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(step.AsSpan(0x18)));
            Assert.Equal(total, BinaryPrimitives.ReadSingleLittleEndian(step));
            var advance = BinaryPrimitives.ReadSingleLittleEndian(step.AsSpan(8));
            advances.Add(advance);
            total += advance;
            ctx[CpuRegister.Rsi] = Base + 0x98;
            Assert.Equal(0, FontExports.WritingRefersRenderStepCharacter(ctx));
            Assert.Equal(character, ctx[CpuRegister.Rax]);
            Assert.True(ctx.TryReadUInt64(Base + 0x98, out var letter));
            Assert.InRange(letter, writing, writing + 0x100 - 0x40);
            var letterBytes = Read(memory, letter, 0x40);
            Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(letterBytes.AsSpan(0x10)));
            Assert.Equal(advance, BinaryPrimitives.ReadSingleLittleEndian(letterBytes.AsSpan(8)));
            ctx[CpuRegister.Rsi] = Base + 0x600;
            Assert.Equal(0, FontExports.WritingGetRenderMetrics(ctx));
            Assert.Equal(total, BinaryPrimitives.ReadSingleLittleEndian(Read(memory, Base + 0x600, 24)));
            ctx[CpuRegister.Rdi] = BinaryPrimitives.ReadUInt64LittleEndian(step.AsSpan(0x10));
            ctx[CpuRegister.Rsi] = code;
            ctx[CpuRegister.Rdx] = Base + 0x640;
            Assert.Equal(0, FontExports.GetCharGlyphMetrics(ctx));
            Assert.Equal(advance, BinaryPrimitives.ReadSingleLittleEndian(Read(memory, Base + 0x650, 4)));
            Assert.Equal(allocationCount, memory.Live.Count);
            ctx[CpuRegister.Rdi] = character;
            Assert.Equal(0, FontExports.CharacterRefersTextNext(ctx));
            character = ctx[CpuRegister.Rax];
        }
        Assert.True(advances[1] > advances[0]);
        Assert.Equal(0UL, character);
        ctx[CpuRegister.Rdi] = writing;
        Assert.Equal(0, FontExports.WritingRefersRenderStep(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rsi] = fontString;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, FontExports.WritingInit(ctx));
        Assert.Equal(0, FontExports.WritingRefersRenderStep(ctx));
        var restarted = Read(memory, ctx[CpuRegister.Rax], 0x48);
        Assert.Equal(0, BinaryPrimitives.ReadSingleLittleEndian(restarted));
        Assert.Equal((uint)'i', BinaryPrimitives.ReadUInt32LittleEndian(restarted.AsSpan(0x1C)));
        Assert.Equal(allocationCount, memory.Live.Count);
        ctx[CpuRegister.Rdi] = output;
        Assert.Equal(0, FontExports.DestroyString(ctx));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    public void Writing_StopsAtInclusiveRenderRangeEnd(int firstIndex, int lastIndex)
    {
        var memory = new TrackingMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var text = "iWm";
        var fontString = CreateText(ctx, memory, text);
        ctx[CpuRegister.Rdi] = fontString;
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, FontExports.StringRefersTextCharacters(ctx));
        var characters = new List<ulong>();
        while (ctx[CpuRegister.Rax] != 0)
        {
            characters.Add(ctx[CpuRegister.Rax]);
            ctx[CpuRegister.Rdi] = ctx[CpuRegister.Rax];
            Assert.Equal(0, FontExports.CharacterRefersTextNext(ctx));
        }
        ctx[CpuRegister.Rdi] = fontString;
        ctx[CpuRegister.Rsi] = characters[firstIndex];
        ctx[CpuRegister.Rdx] = characters[lastIndex];
        ctx[CpuRegister.Rcx] = Base + 0x90;
        Assert.Equal(0, FontExports.StringRefersRenderCharacters(ctx));
        var renderCharacter = ctx[CpuRegister.Rax];
        Assert.True(ctx.TryReadUInt32(Base + 0x90, out var count));
        Assert.Equal((uint)(lastIndex - firstIndex + 1), count);
        ctx[CpuRegister.Rdi] = Base + 0x400;
        ctx[CpuRegister.Rsi] = fontString;
        ctx[CpuRegister.Rdx] = renderCharacter;
        Assert.Equal(0, FontExports.WritingInit(ctx));
        var total = 0f;
        for (var i = firstIndex; i <= lastIndex; i++)
        {
            Assert.Equal(0, FontExports.WritingRefersRenderStep(ctx));
            Assert.NotEqual(0UL, ctx[CpuRegister.Rax]);
            var step = Read(memory, ctx[CpuRegister.Rax], 0x48);
            Assert.Equal((uint)text[i], BinaryPrimitives.ReadUInt32LittleEndian(step.AsSpan(0x1C)));
            Assert.Equal(total, BinaryPrimitives.ReadSingleLittleEndian(step));
            total += BinaryPrimitives.ReadSingleLittleEndian(step.AsSpan(8));
            ctx[CpuRegister.Rsi] = 0;
            Assert.Equal(0, FontExports.WritingRefersRenderStepCharacter(ctx));
            Assert.Equal(characters[i], ctx[CpuRegister.Rax]);
        }
        Assert.Equal(0, FontExports.WritingRefersRenderStep(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rsi] = Base + 0x600;
        Assert.Equal(0, FontExports.WritingGetRenderMetrics(ctx));
        Assert.Equal(total, BinaryPrimitives.ReadSingleLittleEndian(Read(memory, Base + 0x600, 24)));
        ctx[CpuRegister.Rdi] = Base + 0x80;
        Assert.Equal(0, FontExports.DestroyString(ctx));
    }

    [Fact]
    public void WritingLine_AggregatesPositionedBoundsAndKeepsMutableStepsUntilClear()
    {
        var memory = new TrackingMemory();
        var ctx = new CpuContext(memory, Generation.Gen5);
        var output = Base + 0x80;
        ctx[CpuRegister.Rsi] = 0x10;
        ctx[CpuRegister.Rcx] = output;
        Assert.Equal(0, FontExports.CreateWritingLine(ctx));
        Assert.True(ctx.TryReadUInt64(output, out var line));
        var metrics = Base + 0x100;
        WriteMetrics(memory, metrics, [10, 0, -8, 2, -1, 9]);
        ctx[CpuRegister.Rdi] = line;
        ctx[CpuRegister.Rdx] = metrics;
        ctx[CpuRegister.Rcx] = Base + 0x500;
        Assert.Equal(0, FontExports.WritingLineWritesOrder(ctx));
        WriteMetrics(memory, metrics, [6, 0, -12, 3, 0, 8]);
        ctx[CpuRegister.Rcx] = Base + 0x600;
        Assert.Equal(0, FontExports.WritingLineWritesOrder(ctx));
        ctx[CpuRegister.Rsi] = Base + 0x200;
        Assert.Equal(0, FontExports.WritingLineGetRenderMetrics(ctx));
        var aggregate = Read(memory, Base + 0x200, 24);
        Assert.Equal(new float[] { 16, 0, -12, 3, -1, 18 },
            Enumerable.Range(0, 6).Select(i => BinaryPrimitives.ReadSingleLittleEndian(aggregate.AsSpan(i * 4))));
        Assert.Equal(0, FontExports.WritingLineRefersRenderStep(ctx));
        var first = ctx[CpuRegister.Rax];
        Assert.True(ctx.TryWriteUInt32(first + 0x20, BitConverter.SingleToUInt32Bits(2.5f)));
        Assert.Equal(0, FontExports.WritingLineRefersRenderStep(ctx));
        var second = ctx[CpuRegister.Rax];
        Assert.NotEqual(first, second);
        var secondBytes = Read(memory, second, 0x40);
        Assert.Equal(10, BinaryPrimitives.ReadSingleLittleEndian(secondBytes));
        Assert.Equal(Base + 0x600, BinaryPrimitives.ReadUInt64LittleEndian(secondBytes.AsSpan(0x18)));
        Assert.Equal(2.5f, BinaryPrimitives.ReadSingleLittleEndian(Read(memory, first + 0x20, 4)));
        Assert.Equal(0, FontExports.WritingLineRefersRenderStep(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal(3, memory.Live.Count);
        Assert.Equal(0, FontExports.WritingLineClear(ctx));
        Assert.Single(memory.Live);
        Assert.Equal(0, FontExports.WritingLineRefersRenderStep(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        ctx[CpuRegister.Rdi] = output;
        Assert.Equal(0, FontExports.DestroyWritingLine(ctx));
        Assert.Empty(memory.Live);
        Assert.True(ctx.TryReadUInt64(output, out var destroyed));
        Assert.Equal(0UL, destroyed);
    }

    private static byte[] Read(ICpuMemory memory, ulong address, int size)
    {
        var bytes = new byte[size];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
    }

    private static ulong CreateText(CpuContext ctx, ICpuMemory memory, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Assert.True(memory.TryWrite(Base + 0x200, bytes));
        ctx[CpuRegister.Rdi] = Base + 0x100;
        ctx[CpuRegister.Rsi] = Base + 0x200;
        ctx[CpuRegister.Rdx] = (ulong)bytes.Length;
        ctx[CpuRegister.Rcx] = 0;
        ctx[CpuRegister.R8] = 0;
        Assert.Equal(0, FontExports.TextSourceInit(ctx));
        ctx[CpuRegister.Rsi] = Base + 0x100;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = Base + 0x80;
        Assert.Equal(0, FontExports.CreateString(ctx));
        Assert.True(ctx.TryReadUInt64(Base + 0x80, out var fontString));
        return fontString;
    }

    private static void WriteMetrics(ICpuMemory memory, ulong address, float[] values)
    {
        var bytes = new byte[24];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private sealed class TrackingMemory : ICpuMemory, IGuestMemoryAllocator
    {
        private readonly FakeCpuMemory _memory = new(Base, 0x10000);
        public HashSet<ulong> Live { get; } = [];
        public bool TryRead(ulong address, Span<byte> bytes) => _memory.TryRead(address, bytes);
        public bool TryWrite(ulong address, ReadOnlySpan<byte> bytes) => _memory.TryWrite(address, bytes);
        public bool CanRead(ulong address, ulong size) => _memory.CanRead(address, size);
        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            if (!_memory.TryAllocateGuestMemory(size, alignment, out address)) return false;
            return Live.Add(address);
        }
        public bool TryFreeGuestMemory(ulong address) => Live.Remove(address);
    }
}
