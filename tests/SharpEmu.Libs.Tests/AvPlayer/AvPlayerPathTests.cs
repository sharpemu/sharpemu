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
        var rootWithSeparator =
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(_app0Root)) +
            Path.DirectorySeparatorChar;
        Assert.StartsWith(
            rootWithSeparator,
            Path.GetFullPath(resolved),
            StringComparison.OrdinalIgnoreCase);
    }
}
