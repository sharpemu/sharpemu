// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.AvPlayer;
using Xunit;

namespace SharpEmu.Libs.Tests.AvPlayer;

[CollectionDefinition("AvPlayerPathState", DisableParallelization = true)]
public sealed class AvPlayerPathStateCollection;

[Collection("AvPlayerPathState")]
public sealed class AvPlayerPathTests : IDisposable
{
    private readonly string? _originalApp0;
    private readonly string _tempRoot;
    private readonly string _app0Root;

    public AvPlayerPathTests()
    {
        _originalApp0 = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        _tempRoot = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-avplayer-{Guid.NewGuid():N}");
        _app0Root = Path.Combine(_tempRoot, "app0");
        Directory.CreateDirectory(_app0Root);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _app0Root);
    }

    [Fact]
    public void UnrealRelativeFileUriAnchorsAtApp0AndResolvesMedia()
    {
        var mediaPath = CreateFile("Project/Content/Movies/Intro.mp4");

        var resolved = AvPlayerExports.ResolveGuestPath(
            "file://../../../Project/Content/Movies/Intro.mp4");

        Assert.NotNull(resolved);
        Assert.Equal(File.ReadAllBytes(mediaPath), File.ReadAllBytes(resolved));
        AssertPathIsInsideApp0(resolved);
    }

    [Fact]
    public void UnrealRelativeRawPathAnchorsAtApp0AndResolvesMedia()
    {
        var mediaPath = CreateFile("SampleProject/Content/Movies/Startup.mp4");

        var resolved = AvPlayerExports.ResolveGuestPath(
            "../../../SampleProject/Content/Movies/Startup.mp4");

        Assert.NotNull(resolved);
        Assert.Equal(File.ReadAllBytes(mediaPath), File.ReadAllBytes(resolved));
        AssertPathIsInsideApp0(resolved);
    }

    [Fact]
    public void UnrealRelativeRawPathCannotEscapeApp0()
    {
        var outsidePath = Path.Combine(_tempRoot, "outside.mp4");
        File.WriteAllBytes(outsidePath, [0x7F]);
        CreateFile("outside.mp4");

        Assert.Null(AvPlayerExports.ResolveGuestPath("../../../outside.mp4"));
    }

    [Fact]
    public void CurrentDirectoryRawPathResolvesInsideApp0()
    {
        var mediaPath = CreateFile("Movies/Intro.mp4");

        var resolved = AvPlayerExports.ResolveGuestPath("./Movies/Intro.mp4");

        Assert.NotNull(resolved);
        Assert.Equal(Path.GetFullPath(mediaPath), resolved);
        AssertPathIsInsideApp0(resolved);
    }

    [Theory]
    [InlineData(false, "ffmpeg", "ffprobe")]
    [InlineData(true, "ffmpeg.exe", "ffprobe.exe")]
    public void MediaToolLookupUsesPlatformNames(
        bool isWindows,
        string ffmpegName,
        string ffprobeName)
    {
        var toolDirectory = Path.Combine(_tempRoot, "Media Tools");
        Directory.CreateDirectory(toolDirectory);
        var ffmpeg = Path.Combine(toolDirectory, ffmpegName);
        File.WriteAllBytes(ffmpeg, []);

        var resolved = AvPlayerExports.FindFfmpeg(
            configured: null,
            searchPath: $"\"{toolDirectory}\"",
            isWindows);

        Assert.Equal(ffmpeg, resolved);
        Assert.Equal(
            Path.Combine(toolDirectory, ffprobeName),
            AvPlayerExports.GetFfprobePath(ffmpeg, isWindows));
    }

    [Theory]
    [InlineData(false, "ffmpeg")]
    [InlineData(true, "ffmpeg.exe")]
    public void MediaToolLookupFindsPackagedBinary(bool isWindows, string executable)
    {
        var publishDirectory = Path.Combine(_tempRoot, "publish");
        Directory.CreateDirectory(Path.Combine(publishDirectory, "ffmpeg"));
        var ffmpeg = Path.Combine(publishDirectory, "ffmpeg", executable);
        File.WriteAllBytes(ffmpeg, []);

        Assert.Equal(
            ffmpeg,
            AvPlayerExports.FindFfmpeg(
                configured: null,
                searchPath: null,
                isWindows,
                publishDirectory));
    }

    [Fact]
    public void RelativeFileUriCannotEscapeApp0()
    {
        var outsidePath = Path.Combine(_tempRoot, "outside.mp4");
        File.WriteAllBytes(outsidePath, [0x7F]);

        var resolved = AvPlayerExports.ResolveGuestPath("file://../outside.mp4");

        Assert.Null(resolved);
        Assert.Null(AvPlayerExports.ResolveGuestPath("file://%2e%2e/outside.mp4"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("app0:/../../outside.mp4"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("file://..%2foutside.mp4"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("file://../outside.mp4?query"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("file://../outside%ZZ.mp4"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("file://../outside%00.mp4"));
    }

    [Fact]
    public void AbsoluteHostPathsCannotBypassApp0()
    {
        var outsidePath = Path.Combine(_tempRoot, "outside.mp4");
        File.WriteAllBytes(outsidePath, [0x7F]);

        Assert.Null(AvPlayerExports.ResolveGuestPath(outsidePath));
        Assert.Null(AvPlayerExports.ResolveGuestPath(new Uri(outsidePath).AbsoluteUri));
    }

    [Fact]
    public void NonFileUrisAndApp0LookalikesAreRejected()
    {
        CreateFile("evil/intro.mp4");

        Assert.Null(AvPlayerExports.ResolveGuestPath("https://example.test/intro.mp4"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("file://server/share/intro.mp4"));
        Assert.Null(AvPlayerExports.ResolveGuestPath("/app0evil/intro.mp4"));
    }

    [Fact]
    public void SymlinkCannotEscapeApp0()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var outsideDirectory = Path.Combine(_tempRoot, "outside");
        Directory.CreateDirectory(outsideDirectory);
        File.WriteAllBytes(Path.Combine(outsideDirectory, "secret.mp4"), [0x7F]);
        Directory.CreateSymbolicLink(
            Path.Combine(_app0Root, "linked"),
            outsideDirectory);

        Assert.Null(AvPlayerExports.ResolveGuestPath("app0:/linked/secret.mp4"));
    }

    [Fact]
    public void App0UriStillResolvesMedia()
    {
        var mediaPath = CreateFile("movies/intro.mp4");

        var resolved = AvPlayerExports.ResolveGuestPath("app0:/movies/intro.mp4");

        Assert.Equal(Path.GetFullPath(mediaPath), resolved);
        Assert.Equal(
            Path.GetFullPath(mediaPath),
            AvPlayerExports.ResolveGuestPath("movies/intro.mp4"));
        Assert.Equal(
            Path.GetFullPath(mediaPath),
            AvPlayerExports.ResolveGuestPath("file:///app0/movies/intro.mp4"));
        Assert.Equal(
            Path.GetFullPath(mediaPath),
            AvPlayerExports.ResolveGuestPath("app0:movies/intro.mp4"));
    }

    [Fact]
    public void GuestMediaLookupIsCaseInsensitiveOnCaseSensitiveHosts()
    {
        var mediaPath = CreateFile("project/content/movies/intro.mp4");

        var resolved = AvPlayerExports.ResolveGuestPath(
            "file://../../../PROJECT/CONTENT/MOVIES/INTRO.MP4");

        Assert.NotNull(resolved);
        Assert.Equal(File.ReadAllBytes(mediaPath), File.ReadAllBytes(resolved));
        AssertPathIsInsideApp0(resolved);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _originalApp0);
        Directory.Delete(_tempRoot, recursive: true);
    }

    private string CreateFile(string relativePath)
    {
        var path = Path.Combine(_app0Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [0x01, 0x02, 0x03]);
        return path;
    }

    private void AssertPathIsInsideApp0(string resolved)
    {
        var relative = Path.GetRelativePath(
            Path.GetFullPath(_app0Root),
            Path.GetFullPath(resolved));
        Assert.False(Path.IsPathFullyQualified(relative));
        Assert.NotEqual("..", relative);
        Assert.False(
            relative.StartsWith(
                ".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal));
    }
}
