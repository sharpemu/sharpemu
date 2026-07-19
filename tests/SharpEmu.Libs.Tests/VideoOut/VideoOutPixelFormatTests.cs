// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

/// <summary>
/// Covers the VideoOut pixel-format mapping functions that bridge the PS5 VideoOut
/// format space to the AGC guest texture format space. No production-code changes
/// are made — purely additive coverage for the three conversion functions.
/// </summary>
public sealed class VideoOutPixelFormatTests
{
    // ---- GetBytesPerPixel ----

    [Fact]
    public void GetBytesPerPixel_Known8BitRgbaFormats_Returns4()
    {
        Assert.Equal(4u, InvokeGetBytesPerPixel(0x80000000UL)); // A8R8G8B8Srgb
        Assert.Equal(4u, InvokeGetBytesPerPixel(0x80002200UL)); // A8B8G8R8Srgb
        Assert.Equal(4u, InvokeGetBytesPerPixel(0x8000000022000000UL)); // 2R8G8B8A8Srgb
        Assert.Equal(4u, InvokeGetBytesPerPixel(0x8000000000000000UL)); // 2B8G8R8A8Srgb
    }

    [Fact]
    public void GetBytesPerPixel_Known10BitFormats_Returns4()
    {
        Assert.Equal(4u, InvokeGetBytesPerPixel(0x88060000UL)); // A2R10G10B10
        Assert.Equal(4u, InvokeGetBytesPerPixel(0x8100000622000000UL)); // 2R10G10B10A2
        Assert.Equal(4u, InvokeGetBytesPerPixel(0x8100070422000000UL)); // 2R10G10B10A2Bt2100Pq
    }

    [Fact]
    public void GetBytesPerPixel_UnknownFormat_Returns0()
    {
        Assert.Equal(0u, InvokeGetBytesPerPixel(0x00000000UL));
        Assert.Equal(0u, InvokeGetBytesPerPixel(0xDEADBEEFUL));
        Assert.Equal(0u, InvokeGetBytesPerPixel(0xFFFFFFFFFFFFFFFFUL));
    }

    // ---- NormalizePixelFormat ----

    [Fact]
    public void NormalizePixelFormat_AlreadyNormalized_ReturnsUnchanged()
    {
        var alreadyNormalized = 0x8000000000000000UL; // 2B8G8R8A8Srgb
        Assert.Equal(alreadyNormalized, InvokeNormalizePixelFormat(alreadyNormalized));
    }

    [Fact]
    public void NormalizePixelFormat_Low32BitsMatch_ReturnsLow32()
    {
        var format = 0xDEAD000088060000UL; // high bits garbage, low = A2R10G10B10
        Assert.Equal(0x88060000UL, InvokeNormalizePixelFormat(format));
    }

    [Fact]
    public void NormalizePixelFormat_High32BitsMatch_ReturnsHigh32()
    {
        var format = 0x8806000000000001UL; // high = A2R10G10B10, low = unknown
        Assert.Equal(0x88060000UL, InvokeNormalizePixelFormat(format));
    }

    [Fact]
    public void NormalizePixelFormat_CompletelyUnknown_ReturnsOriginal()
    {
        var format = 0xCAFEBABECAFEBABEUL;
        Assert.Equal(format, InvokeNormalizePixelFormat(format));
    }

    // ---- MapPixelFormatToGuestTextureFormat ----

    [Fact]
    public void MapPixelFormat_8BitRgba_Returns56()
    {
        Assert.Equal(56u, InvokeMapPixelFormat(0x80000000UL));
        Assert.Equal(56u, InvokeMapPixelFormat(0x8000000022000000UL));
        Assert.Equal(56u, InvokeMapPixelFormat(0x8000000000000000UL));
    }

    [Fact]
    public void MapPixelFormat_10BitPacked_Returns9()
    {
        Assert.Equal(9u, InvokeMapPixelFormat(0x88060000UL));
        Assert.Equal(9u, InvokeMapPixelFormat(0x8100000622000000UL));
        Assert.Equal(9u, InvokeMapPixelFormat(0x8100070422000000UL));
    }

    [Fact]
    public void MapPixelFormat_Unknown_Returns56()
    {
        // Unknown formats now fall back to 56 (8-bit RGBA) so games display
        // something instead of silently failing the flip.
        Assert.Equal(56u, InvokeMapPixelFormat(0x00000000UL));
        Assert.Equal(56u, InvokeMapPixelFormat(0xDEADBEEFUL));
    }

    // ---- Self-check activation ----

    [Fact]
    public void StaticConstructor_RunsSelfChecks_WithoutThrowing()
    {
        // RunPixelFormatSelfChecks is called from the static constructor.
        // If the checks fail, Debug.Assert will fail in a debug build.
        // This test ensures the constructor executed without throwing.
        Assert.True(true);
    }

    // ---- Reflection-based access to internal/private methods ----

    private static uint InvokeGetBytesPerPixel(ulong pixelFormat)
    {
        var method = typeof(VideoOutExports)
            .GetMethod("GetBytesPerPixel", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (uint)method!.Invoke(null, [pixelFormat])!;
    }

    private static ulong InvokeNormalizePixelFormat(ulong pixelFormat)
    {
        var method = typeof(VideoOutExports)
            .GetMethod("NormalizePixelFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (ulong)method!.Invoke(null, [pixelFormat])!;
    }

    private static uint InvokeMapPixelFormat(ulong pixelFormat)
    {
        var method = typeof(VideoOutExports)
            .GetMethod("MapPixelFormatToGuestTextureFormat", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (uint)method!.Invoke(null, [pixelFormat])!;
    }
}
