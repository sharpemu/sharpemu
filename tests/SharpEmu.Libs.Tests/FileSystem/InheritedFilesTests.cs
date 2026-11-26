// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE.FileSystem;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

// The registry is process-wide, so these tests never run beside other users of it.
[Collection("InheritedFiles")]
public sealed class InheritedFilesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-inherited-").FullName;

    public void Dispose()
    {
        InheritedFiles.ReplaceForTests(null);
        Directory.Delete(_directory, recursive: true);
    }

    private static InheritedFile? Describe(nint handle, string path) => new(handle, path, 10, DateTime.UnixEpoch);

    [Fact]
    public void Format_AndParse_RoundTripSeveralFiles()
    {
        var first = Path.Combine(_directory, "a.zar");
        var second = Path.Combine(_directory, "sub dir", "b;c.zar");

        var text = InheritedFiles.Format([(1234, first), (56, second)]);
        var parsed = InheritedFiles.Parse(text, Describe);

        Assert.Equal(2, parsed.Count);
        Assert.Equal(1234, parsed[first].Handle);
        Assert.Equal(56, parsed[second].Handle);
        Assert.Equal(second, parsed[second].Path);
    }

    [Fact]
    public void Format_UsesTheHandleValueAndTheFullPath()
    {
        // Windows handles and Windows path rules.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(@"4660|C:\games\x.zar", InheritedFiles.Format([(0x1234, @"C:\games\sub\..\x.zar")]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("*")]
    [InlineData("nohandle")]
    [InlineData("|C:\\a.zar")]
    [InlineData("abc|C:\\a.zar")]
    [InlineData("-5|C:\\a.zar")]
    [InlineData("0|C:\\a.zar")]
    [InlineData("12|")]
    public void Parse_IgnoresMalformedEntries(string? value)
    {
        Assert.Empty(InheritedFiles.Parse(value, Describe));
    }

    [Fact]
    public void Parse_KeepsGoodEntriesBesideBadOnes()
    {
        // Windows handles and Windows path rules.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var parsed = InheritedFiles.Parse(@"junk*7|C:\games\ok.zar*x|C:\no.zar", Describe);

        Assert.Equal(@"C:\games\ok.zar", Assert.Single(parsed).Key);
    }

    [Fact]
    public void Parse_DropsAHandleThatIsNotAnOpenFile()
    {
        Assert.Empty(InheritedFiles.Parse(@"7|C:\games\ok.zar", (_, _) => null));
        Assert.Empty(InheritedFiles.Parse($@"{0x7ffffff0}|C:\games\ok.zar"));
    }

    [Fact]
    public void TryGet_ComparesFullPathsWithoutCase()
    {
        // Windows handles and Windows path rules.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var files = InheritedFiles.Parse(@"7|C:\Games\Sub\Game.zar", Describe);

        Assert.True(InheritedFiles.TryGet(files, @"c:\games\SUB\..\sub\game.ZAR", out var file));
        Assert.Equal(7, file.Handle);
        Assert.False(InheritedFiles.TryGet(files, @"C:\Games\Other.zar", out _));
        Assert.False(InheritedFiles.TryGet(files, null, out _));
        Assert.False(InheritedFiles.TryGet(files, "  ", out _));
    }

    [Fact]
    public void ARegisteredFileExistsEvenWhenThePathIsNotOnDisk()
    {
        var path = Path.Combine(_directory, "not-on-disk.zar");
        Assert.False(InheritedFiles.Exists(path));

        InheritedFiles.ReplaceForTests(InheritedFiles.Parse($"7|{path}", Describe));

        Assert.True(InheritedFiles.Exists(path));
        Assert.True(ZArchive.IsArchivePath(path));
    }

    [Fact]
    public void ARealHandleGivesLengthStampAndAnArchiveThatReadsThroughIt()
    {
        var data = new byte[150_000];
        new System.Random(5).NextBytes(data);
        var real = Path.Combine(_directory, "real.zar");
        File.WriteAllBytes(real, ZArchiveTestWriter.Build([("eboot.bin", data)]));
        var stamp = File.GetLastWriteTimeUtc(real);
        // The handle stands for a path that does not exist, as an exFAT path does for the AppContainer.
        var virtualPath = Path.Combine(_directory, "virtual.zar");
        using var handle = File.OpenHandle(real, FileMode.Open, FileAccess.Read, FileShare.Read);
        InheritedFiles.ReplaceForTests(InheritedFiles.Parse($"{(long)handle.DangerousGetHandle()}|{virtualPath}"));

        var (length, written) = InheritedFiles.GetStamp(virtualPath);
        Assert.Equal(new FileInfo(real).Length, length);
        Assert.Equal(stamp, written);

        // Several archives and threads share the one handle; positional reads keep them independent.
        using var first = ZArchive.Open(virtualPath)!;
        using var second = ZArchive.Open(virtualPath)!;
        Assert.NotNull(first);
        var failures = 0;
        Parallel.For(0, 8, index =>
        {
            var archive = index % 2 == 0 ? first : second;
            var node = archive.LookUp("eboot.bin");
            var actual = new byte[data.Length];
            if (archive.Read(node, 0, actual) != data.Length || !actual.AsSpan().SequenceEqual(data))
            {
                Interlocked.Increment(ref failures);
            }
        });
        Assert.Equal(0, failures);

        // Disposing an archive must not close the launcher's handle.
        first.Dispose();
        second.Dispose();
        Assert.Equal(new FileInfo(real).Length, RandomAccess.GetLength(handle));
        Assert.Equal(1, RandomAccess.Read(handle, new byte[1], 0));
    }
}
