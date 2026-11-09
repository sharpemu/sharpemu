// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[CollectionDefinition("PngSplashEnvironment", DisableParallelization = true)]
public sealed class PngSplashEnvironmentCollection;

[Collection("PngSplashEnvironment")]
public sealed class PngSplashLoaderTests
{
    private const int IhdrCrcOffset = 29;
    private const int IdatCrcOffset = 53;
    private const int IendCrcOffset = 65;
    private const string ValidRgbPng =
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAIAAACQd1PeAAAADElEQVR4nGMQMgkDAAD4AJ3MaiF4AAAAAElFTkSuQmCC";

    [Theory]
    [InlineData("pic0.png", true)]
    [InlineData("pic1.png", true)]
    [InlineData("pic5.png", false)]
    public void SplashUsesOnlyTheKnownArtworkNames(string fileName, bool expected)
    {
        var app0Root = Path.Combine(Path.GetTempPath(), $"sharpemu-png-{Guid.NewGuid():N}");
        var sceSys = Path.Combine(app0Root, "sce_sys");
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        Directory.CreateDirectory(sceSys);
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", app0Root);
            File.WriteAllBytes(Path.Combine(sceSys, fileName), Convert.FromBase64String(ValidRgbPng));
            Assert.Equal(expected, PngSplashLoader.TryLoad(out _, out _, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(app0Root, recursive: true);
        }
    }

    [Fact]
    public void ValidPic0LoadsEvenWhenTheFallbackIsInvalid()
    {
        var app0Root = Path.Combine(Path.GetTempPath(), $"sharpemu-png-{Guid.NewGuid():N}");
        var sceSys = Path.Combine(app0Root, "sce_sys");
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        Directory.CreateDirectory(sceSys);
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", app0Root);
            File.WriteAllBytes(Path.Combine(sceSys, "pic0.png"), Convert.FromBase64String(ValidRgbPng));
            File.WriteAllBytes(Path.Combine(sceSys, "pic1.png"), new byte[1]);
            Assert.True(PngSplashLoader.TryLoad(out var pixels, out _, out _));
            Assert.Equal([0x56, 0x34, 0x12, 0xFF], pixels);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previous);
            Directory.Delete(app0Root, recursive: true);
        }
    }

    [Theory]
    [InlineData(IhdrCrcOffset)]
    [InlineData(IdatCrcOffset)]
    [InlineData(IendCrcOffset)]
    public void TryLoadRejectsInvalidChunkCrc(int crcOffset)
    {
        var app0Root = Path.Combine(Path.GetTempPath(), $"sharpemu-png-{Guid.NewGuid():N}");
        var sceSysPath = Path.Combine(app0Root, "sce_sys");
        var pngPath = Path.Combine(sceSysPath, "pic0.png");
        var previousApp0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");

        Directory.CreateDirectory(sceSysPath);
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", app0Root);

            var png = Convert.FromBase64String(ValidRgbPng);
            File.WriteAllBytes(pngPath, png);

            Assert.True(PngSplashLoader.TryLoad(out var pixels, out var width, out var height));
            Assert.Equal(1U, width);
            Assert.Equal(1U, height);
            Assert.Equal([0x56, 0x34, 0x12, 0xFF], pixels);

            png[crcOffset] ^= 0x01;
            File.WriteAllBytes(pngPath, png);

            Assert.False(PngSplashLoader.TryLoad(out _, out _, out _));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", previousApp0Root);
            Directory.Delete(app0Root, recursive: true);
        }
    }
}
