// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.GnmMode.Libraries.Codec;
using SharpEmu.HLE;
using SharpEmu.Libs.Codec;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GnmVideoFrameLayoutTests
{
    [Fact]
    public void GuestPixelsAndPictureMetadataSurviveDecoderDeletion()
    {
        const ulong start = 0x2_0000_0000;
        var ctx = new CpuContext(new FakeCpuMemory(start, 4096), Generation.Gen5);
        var slot = start + 0x100;
        var output = start + 0x200;
        var pixels = start + 0x400;
        var info = start + 0x500;
        Assert.True(ctx.TryWriteUInt64(slot + 8, pixels));
        Assert.True(ctx.TryWriteUInt64(slot + 16, 64));
        Assert.True(ctx.TryWriteUInt64(output, 0x38));
        Assert.True(ctx.TryWriteUInt64(info, 104));
        ctx[CpuRegister.Rdx] = start + 0x80;
        // Exercise the registered handle and picture exports without loading a native codec.
        Assert.Equal(0, Videodec2Exports.CreateDecoder(ctx, static () => null));
        var handle = Read64(ctx, start + 0x80);
        Assert.NotEqual(0UL, handle);
        var frame = new Videodec2DecodedFrame([17, 31, 43], 2, 1, 2, false) { Pts = 9876, KeyFrame = true };
        Assert.True(Videodec2Exports.TryWriteDecodedFrame(ctx, slot, output, frame));
        ctx[CpuRegister.Rdi] = handle;
        Assert.Equal(0, Videodec2Exports.Videodec2DeleteDecoder(ctx));
        Assert.True(Videodec2Exports.TryGetPicture(pixels, out var cached));
        Assert.Empty(cached.Nv12);
        byte[] copied = new byte[3];
        Assert.True(ctx.Memory.TryRead(pixels, copied));
        Assert.Equal(frame.Nv12, copied);
        ctx[CpuRegister.Rdi] = output;
        ctx[CpuRegister.Rsi] = info;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(0, Videodec2Exports.Videodec2GetPictureInfo(ctx));
        Assert.Equal(9876UL, Read64(ctx, info + 0x10));
    }

    [Theory]
    [InlineData(66u, 18u, 256u, false)]
    [InlineData(1920u, 1080u, 2048u, false)]
    [InlineData(1920u, 1080u, 2048u, true)]
    [InlineData(64u, 16u, 64u, false)]
    public void Nv12PlanesUsePs4PitchCodedHeightAndCropWithoutChangingTheirSamples(uint width, uint height, uint sourcePitch, bool legacy)
    {
        const ulong start = 0x1_0000_0000;
        const ulong frameBuffer = start + 0x100;
        const ulong output = start + 0x200;
        const ulong picture = start + 0x300;
        const ulong pixels = start + 0x4000;
        const uint capacity = 6 * 1024 * 1024;
        var memory = new FakeCpuMemory(start, 8 * 1024 * 1024);
        var context = new CpuContext(memory, Generation.Gen4);
        Assert.True(context.TryWriteUInt64(frameBuffer, 0x20));
        Assert.True(context.TryWriteUInt64(frameBuffer + 8, pixels));
        Assert.True(context.TryWriteUInt64(frameBuffer + 0x10, capacity));
        Assert.True(context.TryWriteUInt64(output, legacy ? 0x30UL : 0x38UL));
        Assert.True(context.TryWriteUInt64(output + 0x30, 0xAABBCCDDEEFF0011));
        Assert.True(context.TryWriteUInt64(picture, 0x78));
        var nv12 = new byte[sourcePitch * (height + (height + 1) / 2)];
        for (var row = 0u; row < height; row++)
            for (var x = 0u; x < width; x++)
                nv12[row * sourcePitch + x] = (byte)((row * 3 + x) % 220 + 16);
        for (var row = 0u; row < (height + 1) / 2; row++)
            for (var x = 0u; x < width; x += 2)
            {
                nv12[sourcePitch * height + row * sourcePitch + x] = (byte)(64 + row % 100);
                nv12[sourcePitch * height + row * sourcePitch + x + 1] = (byte)(192 - x % 100);
            }
        var decoded = new Videodec2DecodedFrame(nv12, width, height, sourcePitch, false)
            { Pts = 12345, Dts = 12000, AttachedData = 99 };
        Assert.True(Videodec2Exports.TryWriteDecodedFrame(context, frameBuffer, output, decoded));
        var normalize = typeof(GnmVideodec2Exports).GetMethod("NormalizeOutput", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(0, (int)normalize.Invoke(null, [context, frameBuffer, output])!);
        var pitch = (width + 63) & ~63u;
        var codedHeight = (height + 15) & ~15u;
        var size = pitch * codedHeight * 3 / 2;
        Assert.Equal(pitch, Read32(context, output + 0x14));
        Assert.Equal(codedHeight, Read32(context, output + 0x18));
        if (legacy)
            Assert.Equal(0xAABBCCDDEEFF0011UL, Read64(context, output + 0x30));
        else
            Assert.Equal(pitch, Read32(context, output + 0x34));
        Assert.True(context.TryReadUInt64(output + 0x28, out var bytes));
        Assert.Equal((ulong)size, bytes);
        var actual = new byte[size];
        Assert.True(memory.TryRead(pixels, actual));
        for (var row = 0u; row < codedHeight; row++)
            for (var x = 0u; x < pitch; x++)
                Assert.Equal(nv12[Math.Min(row, height - 1) * sourcePitch + Math.Min(x, width - 1)], actual[row * pitch + x]);
        for (var row = 0u; row < codedHeight / 2; row++)
            for (var x = 0u; x < pitch; x += 2)
            {
                var from = sourcePitch * height + Math.Min(row, (height + 1) / 2 - 1) * sourcePitch + Math.Min(x, width - 2);
                var to = pitch * codedHeight + row * pitch + x;
                Assert.Equal(nv12[from], actual[to]);
                Assert.Equal(nv12[from + 1], actual[to + 1]);
            }
        context[CpuRegister.Rdi] = output;
        context[CpuRegister.Rsi] = picture;
        Assert.Equal(0, GnmVideodec2Exports.GetPictureInfo(context));
        Assert.Equal(((width + 15) & ~15u) - width, Read32(context, picture + 0x3C));
        Assert.Equal(codedHeight - height, Read32(context, picture + 0x44));
        Assert.True(context.TryReadUInt64(picture + 0x10, out var pts));
        Assert.Equal(12345UL, pts);
        Assert.Equal(capacity, (uint)Read64(context, frameBuffer + 0x10));
    }

    private static uint Read32(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt32(address, out var value));
        return value;
    }

    private static ulong Read64(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt64(address, out var value));
        return value;
    }
}
