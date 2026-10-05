// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Font;
using SkiaSharp;
using Xunit;

namespace SharpEmu.Libs.Tests.Font;

public sealed class FontExportsTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong LayoutAddress = Base + 0x100;

    private readonly FakeCpuMemory _memory = new(Base, 0x1000);
    private readonly CpuContext _ctx;

    public FontExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void TextSourceInit_WritesParserAndTextRangeWithoutOverwritingNextObject()
    {
        const ulong text = Base + 0x300;
        const ulong parser = Base + 0x400;
        const ulong parserObject = Base + 0x500;
        var bytes = new byte[0x68];
        Array.Fill(bytes, (byte)0xcc);
        Assert.True(_memory.TryWrite(LayoutAddress, bytes));
        _ctx[CpuRegister.Rdi] = LayoutAddress;
        _ctx[CpuRegister.Rsi] = text;
        _ctx[CpuRegister.Rdx] = 9;
        _ctx[CpuRegister.Rcx] = parser;
        _ctx[CpuRegister.R8] = parserObject;

        Assert.Equal(0, FontExports.TextSourceInit(_ctx));
        Assert.True(_memory.TryRead(LayoutAddress, bytes));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes));
        Assert.Equal(text, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x08)));
        Assert.Equal(text + 9, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x10)));
        Assert.Equal(text, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x18)));
        Assert.Equal(parser, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x20)));
        Assert.Equal(parserObject, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x28)));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x30)));
        Assert.Equal(0x10UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x38)));
        Assert.Equal(9UL, BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(0x40)));
        Assert.All(bytes[0x48..0x60], value => Assert.Equal((byte)0, value));
        Assert.All(bytes[0x60..], value => Assert.Equal((byte)0xcc, value));
    }

    // SceFontHorizontalLayout is three floats; the sentinel directly after
    // them must survive the call.
    [Fact]
    public void GetHorizontalLayout_WritesExactlyThreeFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetHorizontalLayout(_ctx));

        Span<byte> layout = stackalloc byte[16];
        Assert.True(_ctx.Memory.TryRead(LayoutAddress, layout));
        Assert.Equal(12.0f, BinaryPrimitives.ReadSingleLittleEndian(layout));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[4..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[8..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(layout[12..]));
    }

    [Fact]
    public void GetVerticalLayout_WritesExactlyThreeFloats()
    {
        const uint Sentinel = 0xDEADBEEF;
        Span<byte> sentinelBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(sentinelBytes, Sentinel);
        Assert.True(_ctx.Memory.TryWrite(LayoutAddress + 12, sentinelBytes));

        _ctx[CpuRegister.Rsi] = LayoutAddress;
        Assert.Equal(0, FontExports.GetVerticalLayout(_ctx));

        Span<byte> layout = stackalloc byte[16];
        Assert.True(_ctx.Memory.TryRead(LayoutAddress, layout));
        Assert.Equal(8.0f, BinaryPrimitives.ReadSingleLittleEndian(layout));
        Assert.Equal(16.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[4..]));
        Assert.Equal(0.0f, BinaryPrimitives.ReadSingleLittleEndian(layout[8..]));
        Assert.Equal(Sentinel, BinaryPrimitives.ReadUInt32LittleEndian(layout[12..]));
    }

    [Fact]
    public void GetVerticalLayout_NullBuffer_ReturnsInvalidArgument()
    {
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            FontExports.GetVerticalLayout(_ctx));
    }

    [Fact]
    public void DrawGlyph_WritesOnlySetPixelsInsideScissor()
    {
        const ulong surface = Base + 0x200;
        const ulong pixels = Base + 0x300;
        Span<byte> descriptor = stackalloc byte[0x28];
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor, pixels);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[8..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[12..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[16..], 8);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[20..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[24..], 2);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[28..], 0);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[32..], 6);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor[36..], 1);
        Assert.True(_ctx.Memory.TryWrite(surface, descriptor));

        byte[] coverage = [255, 255, 255, 128, 0, 255, 255, 255];
        Assert.True(FontExports.DrawGlyph(_ctx, surface, 0, 0, 8, 1, coverage));

        Span<byte> result = stackalloc byte[8];
        Assert.True(_ctx.Memory.TryRead(pixels, result));
        Assert.Equal(new byte[] { 0, 0, 255, 128, 0, 255, 0, 0 }, result.ToArray());
    }

    [Fact]
    public void SystemFont_RendersUnicodeAcrossScriptsInsteadOfQuestionMarks()
    {
        const ulong surface = Base + 0x200;
        const ulong pixels = Base + 0x300;
        _ctx[CpuRegister.R8] = Base + 0x80;
        Assert.Equal(0, FontExports.OpenFontSet(_ctx));
        Assert.True(_ctx.TryReadUInt64(Base + 0x80, out var font));
        _ctx[CpuRegister.Rdi] = surface;
        _ctx[CpuRegister.Rsi] = pixels;
        _ctx[CpuRegister.Rdx] = 32;
        _ctx[CpuRegister.Rcx] = 1;
        _ctx[CpuRegister.R8] = 32;
        _ctx[CpuRegister.R9] = 32;
        Assert.Equal(0, FontExports.RenderSurfaceInit(_ctx));
        _ctx[CpuRegister.Rdi] = font;
        _ctx[CpuRegister.Rdx] = surface;
        _ctx[CpuRegister.Rcx] = Base + 0x100;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(4), 0);
        _ctx.SetXmmRegister(1, BitConverter.SingleToUInt32Bits(24), 0);
        var bitmaps = new List<byte[]>();
        try
        {
            // Latin extended, Greek, Cyrillic, and characters needing a fallback face.
            foreach (var code in new uint[] { '?', 0x0130, 0x011E, 0x0141, 0x03A9, 0x0416, 0x05D0, 0x0628 })
            {
                Assert.True(_memory.TryWrite(pixels, new byte[1024]));
                _ctx[CpuRegister.Rsi] = code;
                _ctx[CpuRegister.R8] = 0;
                Assert.Equal(0, FontExports.RenderCharGlyphImageHorizontal(_ctx));
                var bitmap = new byte[1024];
                Assert.True(_memory.TryRead(pixels, bitmap));
                Assert.Contains(bitmap, pixel => pixel != 0);
                Assert.DoesNotContain(bitmaps, previous => previous.SequenceEqual(bitmap));
                bitmaps.Add(bitmap);
                _ctx[CpuRegister.Rdx] = Base + 0x180;
                Assert.Equal(0, FontExports.GetCharGlyphMetrics(_ctx));
                _ctx[CpuRegister.Rdx] = surface;
                var renderedMetrics = new byte[32];
                var queriedMetrics = new byte[32];
                Assert.True(_memory.TryRead(Base + 0x100, renderedMetrics));
                Assert.True(_memory.TryRead(Base + 0x180, queriedMetrics));
                Assert.Equal(renderedMetrics, queriedMetrics);
            }
            _ctx[CpuRegister.Rsi] = 0xD800; // A surrogate is not a Unicode scalar.
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, FontExports.RenderCharGlyphImageHorizontal(_ctx));
        }
        finally
        {
            Assert.Equal(0, FontExports.CloseFont(_ctx));
        }
    }

    [Fact]
    public void OpenFontMemory_UsesGuestFontAndPreservesItInInstances()
    {
        using var typeface = SKTypeface.FromFamilyName(null);
        using var stream = typeface.OpenStream(out var index);
        var bytes = new byte[stream.Length];
        Assert.Equal(bytes.Length, stream.Read(bytes, bytes.Length));
        var memory = new FakeCpuMemory(Base, bytes.Length + 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(Base + 0x1000, bytes));
        Assert.True(ctx.TryWriteUInt32(Base + 8, (uint)index));
        ctx[CpuRegister.Rsi] = Base + 0x1000;
        ctx[CpuRegister.Rdx] = (uint)bytes.Length;
        ctx[CpuRegister.Rcx] = Base;
        ctx[CpuRegister.R8] = Base + 0x100;
        Assert.Equal(0, FontExports.OpenFontMemory(ctx));
        Assert.True(ctx.TryReadUInt64(Base + 0x100, out var source));
        ctx[CpuRegister.Rdi] = source;
        ctx[CpuRegister.Rsi] = Base + 0x500;
        ctx[CpuRegister.Rdx] = Base + 0x108;
        Assert.Equal(0, FontExports.OpenFontInstance(ctx));
        Assert.True(ctx.TryReadUInt64(Base + 0x108, out var instance));
        Assert.Equal(Base + 0x500, instance);
        Assert.Equal(0, FontExports.CloseFont(ctx));
        // The instance still owns the same face after the source handle is closed.
        ctx[CpuRegister.Rdi] = instance;
        ctx[CpuRegister.Rsi] = 0x0130;
        ctx[CpuRegister.Rdx] = Base + 0x200;
        Assert.Equal(0, FontExports.GetCharGlyphMetrics(ctx));
        var metrics = new byte[32];
        Assert.True(memory.TryRead(Base + 0x200, metrics));
        using var font = new SKFont(typeface, 16);
        Assert.Equal(font.MeasureText("\u0130"), BinaryPrimitives.ReadSingleLittleEndian(metrics.AsSpan(16)));
        Assert.Equal(0, FontExports.CloseFont(ctx));
    }
}
