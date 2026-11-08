// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Png;
using SkiaSharp;
using Xunit;

namespace SharpEmu.Libs.Tests.Png;

public sealed class PngEncExportsTests
{
    private const ulong CreateParameter = 0x1000;
    private const ulong WorkMemory = 0x1100;
    private const ulong HandleOutput = 0x1120;
    private const ulong EncodeParameter = 0x1200;
    private const ulong Source = 0x2000;
    private const ulong Destination = 0x3000;
    private const ulong OutputInfo = 0x5000;

    [Theory]
    [InlineData(0u, 3u)]
    [InlineData(1u, 3u)]
    [InlineData(0u, 19u)]
    [InlineData(1u, 19u)]
    public void EncodesPaddedRgbaAndBgraRows(uint pixelFormat, uint colorSpace)
    {
        var ctx = CreateEncoder();
        byte[] expected = [32, 64, 128, 128, 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 64];
        var padded = new byte[24];
        Array.Fill(padded, (byte)0xCD);
        for (var i = 0; i < 4; i++)
        {
            var texel = expected.AsSpan(i * 4, 4).ToArray();
            if (pixelFormat == 1)
                (texel[0], texel[2]) = (texel[2], texel[0]);
            texel.CopyTo(padded.AsSpan(i / 2 * 12 + i % 2 * 4));
        }
        Assert.True(ctx.Memory.TryWrite(Source, padded));
        Assert.True(ctx.TryWriteUInt16(EncodeParameter + 36, (ushort)pixelFormat));
        Assert.True(ctx.TryWriteUInt16(EncodeParameter + 38, (ushort)colorSpace));

        var length = PngEncExports.Encode(ctx);

        Assert.InRange(length, 33, 0x1000);
        Assert.Equal((ulong)length, ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt32(OutputInfo, out var written));
        Assert.Equal((uint)length, written);
        Assert.True(ctx.TryReadUInt32(OutputInfo + 4, out var height));
        Assert.Equal(2u, height);
        var png = new byte[length];
        Assert.True(ctx.Memory.TryRead(Destination, png));
        Assert.Equal(colorSpace == 3 ? 2 : 6, png[25]);
        using var codec = SKCodec.Create(new MemoryStream(png));
        using var decoded = new SKBitmap(2, 2, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        Assert.Equal(SKCodecResult.Success, codec.GetPixels(decoded.Info, decoded.GetPixels()));
        if (colorSpace == 3)
            for (var i = 3; i < expected.Length; i += 4)
                expected[i] = 255;
        Assert.Equal(expected, decoded.GetPixelSpan().ToArray());
    }

    [Fact]
    public void ShortOutputPreservesTheDestination()
    {
        var ctx = CreateEncoder();
        Assert.True(ctx.Memory.TryWrite(Source, new byte[24]));
        Assert.True(ctx.Memory.TryWrite(Destination, new byte[] { 0xCD, 0xCD, 0xCD, 0xCD }));
        Assert.True(ctx.TryWriteUInt32(EncodeParameter + 20, 4));

        Assert.Equal(unchecked((int)0x80690110), PngEncExports.Encode(ctx));

        var destination = new byte[4];
        Assert.True(ctx.Memory.TryRead(Destination, destination));
        Assert.Equal(new byte[] { 0xCD, 0xCD, 0xCD, 0xCD }, destination);
        Assert.True(ctx.TryReadUInt64(OutputInfo, out var info));
        Assert.Equal(0UL, info);
    }

    [Theory]
    [InlineData(16u, 19u)]
    [InlineData(32u, 4u)]
    [InlineData(24u, 1025u)]
    public void RejectsShortInputPitchAndWidthsBeyondTheHandleLimit(uint offset, uint value)
    {
        var ctx = CreateEncoder();
        Assert.True(ctx.TryWriteUInt32(EncodeParameter + offset, value));
        Assert.Equal(unchecked((int)0x80690102), PngEncExports.Encode(ctx));
    }

    [Fact]
    public void UnmappedInputReturnsAnAddressError()
    {
        var ctx = CreateEncoder();
        Assert.True(ctx.TryWriteUInt64(EncodeParameter, 0x6FFC));
        Assert.Equal(unchecked((int)0x80690101), PngEncExports.Encode(ctx));
    }

    [Fact]
    public void DeleteInvalidatesTheHandle()
    {
        var ctx = CreateEncoder();
        Assert.Equal(0, PngEncExports.Delete(ctx));
        Assert.Equal(unchecked((int)0x80690104), PngEncExports.Encode(ctx));
        Assert.Equal(unchecked((int)0x80690104), PngEncExports.Delete(ctx));
    }

    [Theory]
    [InlineData(4u, 1u, 0x80690103u)]
    [InlineData(8u, 0u, 0x80690102u)]
    [InlineData(12u, 6u, 0x80690103u)]
    public void QueryRejectsInvalidCreateParameters(uint offset, uint value, uint error)
    {
        var ctx = new CpuContext(new FakeCpuMemory(CreateParameter, 16), Generation.Gen5);
        Assert.True(ctx.TryWriteUInt32(CreateParameter, 16));
        Assert.True(ctx.TryWriteUInt32(CreateParameter + 8, 1024));
        Assert.True(ctx.TryWriteUInt32(CreateParameter + offset, value));
        ctx[CpuRegister.Rdi] = CreateParameter;
        Assert.Equal(unchecked((int)error), PngEncExports.QueryMemorySize(ctx));
    }

    private static CpuContext CreateEncoder()
    {
        var ctx = new CpuContext(new FakeCpuMemory(CreateParameter, 0x6000), Generation.Gen5);
        Assert.True(ctx.TryWriteUInt32(CreateParameter, 16));
        Assert.True(ctx.TryWriteUInt32(CreateParameter + 8, 1024));
        Assert.True(ctx.TryWriteUInt32(CreateParameter + 12, 4));
        ctx[CpuRegister.Rdi] = CreateParameter;
        var size = PngEncExports.QueryMemorySize(ctx);
        Assert.InRange(size, 1, 0x20);
        ctx[CpuRegister.Rsi] = WorkMemory;
        ctx[CpuRegister.Rdx] = (uint)size;
        ctx[CpuRegister.Rcx] = HandleOutput;
        Assert.Equal(0, PngEncExports.Create(ctx));
        Assert.True(ctx.TryReadUInt64(HandleOutput, out var handle));
        Assert.True(ctx.TryWriteUInt64(EncodeParameter, Source));
        Assert.True(ctx.TryWriteUInt64(EncodeParameter + 8, Destination));
        Assert.True(ctx.TryWriteUInt32(EncodeParameter + 16, 24));
        Assert.True(ctx.TryWriteUInt32(EncodeParameter + 20, 0x1000));
        Assert.True(ctx.TryWriteUInt32(EncodeParameter + 24, 2));
        Assert.True(ctx.TryWriteUInt32(EncodeParameter + 28, 2));
        Assert.True(ctx.TryWriteUInt32(EncodeParameter + 32, 12));
        Assert.True(ctx.TryWriteUInt16(EncodeParameter + 38, 19));
        Assert.True(ctx.TryWriteUInt16(EncodeParameter + 40, 8));
        Assert.True(ctx.TryWriteUInt16(EncodeParameter + 46, 9));
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = EncodeParameter;
        ctx[CpuRegister.Rdx] = OutputInfo;
        return ctx;
    }
}
