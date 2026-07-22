// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Bink;
using Xunit;

namespace SharpEmu.Libs.Tests.Bink;

public sealed class Bink2MovieBridgeTests : IDisposable
{
    private readonly string _tempDirectory = Path.Combine(
        Path.GetTempPath(),
        $"sharpemu-bink-{Guid.NewGuid():N}");

    public Bink2MovieBridgeTests()
    {
        Directory.CreateDirectory(_tempDirectory);
    }

    [Fact]
    public void HeaderPreservesFractionalFrameRate()
    {
        var path = WriteHeader("KB2j"u8, 3840, 2160, 30_000, 1_001);

        Assert.True(Bink2MovieBridge.TryReadBinkInfo(path, out var info));
        Assert.Equal(3840u, info.Width);
        Assert.Equal(2160u, info.Height);
        Assert.Equal(30_000u, info.FramesPerSecondNumerator);
        Assert.Equal(1_001u, info.FramesPerSecondDenominator);
    }

    [Theory]
    [InlineData("KB2g")]
    [InlineData("KB2i")]
    [InlineData("KB2j")]
    public void HeaderAcceptsBink2Revisions(string signature)
    {
        var path = WriteHeader(
            System.Text.Encoding.ASCII.GetBytes(signature),
            1920,
            1080,
            60,
            1);

        Assert.True(Bink2MovieBridge.TryReadBinkInfo(path, out _));
    }

    [Fact]
    public void HeaderRejectsMissingFrameRateDenominator()
    {
        var path = WriteHeader("KB2j"u8, 1920, 1080, 60, 0);

        Assert.False(Bink2MovieBridge.TryReadBinkInfo(path, out _));
    }

    private string WriteHeader(
        ReadOnlySpan<byte> signature,
        uint width,
        uint height,
        uint fpsNumerator,
        uint fpsDenominator)
    {
        var header = new byte[36];
        signature.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x14), width);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x18), height);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x1C), fpsNumerator);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(0x20), fpsDenominator);
        var path = Path.Combine(_tempDirectory, $"{Guid.NewGuid():N}.bk2");
        File.WriteAllBytes(path, header);
        return path;
    }

    public void Dispose()
    {
        Directory.Delete(_tempDirectory, recursive: true);
    }
}
