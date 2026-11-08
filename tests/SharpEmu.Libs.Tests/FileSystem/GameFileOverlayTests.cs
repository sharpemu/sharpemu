// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.Core;
using SharpEmu.HLE;
using SharpEmu.HLE.FileSystem;
using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.AvPlayer;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class GameFileOverlayTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-overlay-").FullName;
    private readonly string? _previous = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
    private readonly string? _previousIndex = Environment.GetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE");
    private string Root => Path.Combine(_directory, "game");
    private string Mod => Path.Combine(_directory, "mod");

    public GameFileOverlayTests()
    {
        Directory.CreateDirectory(Root);
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", Root);
        Environment.SetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE", Path.Combine(_directory, "index"));
        KernelMemoryCompatExports.RegisterGuestPathMount("/app0", Root);
        KernelMemoryCompatExports.RegisterGuestPathMount("$", Root);
    }

    public void Dispose()
    {
        App0Archive.Unmount();
        AmprFileRegistry.ClearForTests();
        KernelMemoryCompatExports.UnregisterGuestPathMount("/app0");
        KernelMemoryCompatExports.UnregisterGuestPathMount("$");
        Environment.SetEnvironmentVariable("SHARPEMU_APP0_DIR", _previous);
        Environment.SetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE", _previousIndex);
        Directory.Delete(_directory, recursive: true);
    }

    private static string Write(string root, string relative, byte[] data)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ModsOverrideFolderAndArchiveReadsWithoutCopyingOrChangingOriginals(bool archived)
    {
        var archivePath = Path.Combine(_directory, "game.zar");
        var original = ZArchiveTestWriter.Build([("data/level.bin", new byte[] { 1, 2 }), ("data/base.bin", new byte[] { 3 })]);
        var level = Path.Combine(Root, "data", "level.bin");
        if (archived)
        {
            File.WriteAllBytes(archivePath, original);
            App0Archive.Mount(ZArchive.Open(archivePath)!, Root);
        }
        else
        {
            Write(Root, "data/level.bin", [1, 2]);
            Write(Root, "data/base.bin", [3]);
        }
        var modFile = Write(Mod, "data/level.bin", [9, 8, 7]);
        Write(Mod, "new/extra.bin", [6]);
        Assert.Equal(level, KernelMemoryCompatExports.ResolveGuestPath("/app0/data/level.bin"));
        using (var overlay = new GameFileOverlay(Root, [Mod]))
        {
            Assert.Equal(modFile, KernelMemoryCompatExports.ResolveGuestPath("/app0/data/level.bin"));
            Assert.Equal(modFile, KernelMemoryCompatExports.ResolveGuestPath("$/data/level.bin"));
            using var stream = App0Archive.OpenReadOrFile(level);
            Assert.Equal(3, stream.Length);
            Assert.Equal(9, stream.ReadByte());
            var fs = new PhysicalFileSystem();
            Assert.True(fs.TryReadAllBytes(level, out var bytes));
            Assert.Equal(new byte[] { 9, 8, 7 }, bytes);
            Assert.True(GameFileOverlay.IsDirectory(Path.Combine(Root, "new")));
            Assert.Equal(["data", "new"], GameFileOverlay.List(Root).Order());
            Assert.Equal(["data/base.bin", "data/level.bin", "new/extra.bin"],
                App0Archive.EnumerateFilesRecursive(Root, "*.bin").Select(path => Path.GetRelativePath(Root, path).Replace('\\', '/')).Order());
            using var fallback = App0Archive.OpenReadOrFile(Path.Combine(Root, "data", "base.bin"));
            Assert.Equal(3, fallback.ReadByte());
        }
        Assert.Equal(level, KernelMemoryCompatExports.ResolveGuestPath("/app0/data/level.bin"));
        using (var stream = App0Archive.OpenReadOrFile(level)) Assert.Equal(1, stream.ReadByte());
        Assert.False(Directory.Exists(Path.Combine(Root, "new")));
        if (archived)
        {
            Assert.Equal(original, File.ReadAllBytes(archivePath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(Root));
        }
        else Assert.Equal(new byte[] { 1, 2 }, File.ReadAllBytes(level));
    }

    [Fact]
    public void LaterModsWinAndSiblingOrTraversalPathsCannotMatch()
    {
        var first = Write(Mod, "data.bin", [1]);
        var secondRoot = Path.Combine(_directory, "second");
        var second = Write(secondRoot, "data.bin", [2]);
        var file = Path.Combine(Root, "data.bin");
        using var overlay = new GameFileOverlay(Root, [Mod, secondRoot]);
        Assert.Equal(second, GameFileOverlay.ResolveFile(file));
        var outside = Root + "-other" + Path.DirectorySeparatorChar + "data.bin";
        Assert.Equal(outside, GameFileOverlay.ResolveFile(outside));
        outside = Path.Combine(Root, "..", "data.bin");
        Assert.Equal(outside, GameFileOverlay.ResolveFile(outside));
        Assert.NotEqual(first, GameFileOverlay.ResolveFile(file));
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void KernelReadUsesModBytesForBothGenerations(Generation generation)
    {
        Write(Root, "data.bin", [1]);
        Write(Mod, "data.bin", [9, 8, 7]);
        using var overlay = new GameFileOverlay(Root, [Mod]);
        const ulong address = 0x1_0000_0000;
        var memory = new FakeCpuMemory(address, 0x1000);
        memory.WriteCString(address + 0x100, "/app0/data.bin");
        var context = new CpuContext(memory, generation);
        context[CpuRegister.Rdi] = address + 0x100;
        context[CpuRegister.Rsi] = 0;
        Assert.Equal(0, KernelExports.KernelOpen(context));
        var fd = context[CpuRegister.Rax];
        try
        {
            context[CpuRegister.Rdi] = fd;
            context[CpuRegister.Rsi] = address + 0x200;
            context[CpuRegister.Rdx] = 3;
            Assert.Equal(0, KernelMemoryCompatExports.KernelReadUnderscore(context));
            Assert.Equal(3UL, context[CpuRegister.Rax]);
            var actual = new byte[3];
            Assert.True(memory.TryRead(address + 0x200, actual));
            Assert.Equal(new byte[] { 9, 8, 7 }, actual);
        }
        finally
        {
            context[CpuRegister.Rdi] = fd;
            KernelMemoryCompatExports.KernelClose(context);
        }
    }

    [Fact]
    public void MediaAndCachedAprIndexesSeeModFiles()
    {
        Write(Root, "sce_sys/param.json", "{}"u8.ToArray());
        Write(Root, "data/base.bin", [1]);
        AmprFileRegistry.ClearForTests();
        AmprFileRegistry.EnsureApp0Indexed(Root);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(_directory, "index")));
        AmprFileRegistry.ClearForTests();
        var extra = Write(Mod, "data/extra.bin", [2]);
        var movie = Write(Mod, "movies/intro.mp4", [3]);
        using var overlay = new GameFileOverlay(Root, [Mod]);
        AmprFileRegistry.EnsureApp0Indexed(Root);
        Assert.True(AmprFileRegistry.TryGetHostPath(AmprFileRegistry.ComputeFileId("/app0/data/extra.bin"), out var indexed));
        Assert.Equal(extra, GameFileOverlay.ResolveFile(indexed));
        Assert.Equal(movie, AvPlayerExports.ResolveGuestPath("/app0/movies/intro.mp4"));
        Assert.False(File.Exists(Path.Combine(Root, "movies", "intro.mp4")));
    }

    [Fact]
    public void ModOnlyDirectoriesCanBeStattedAndOpenedAndListingsMergeAllSources()
    {
        Write(Root, "data/base.bin", [1]);
        Write(Mod, "data/extra.bin", [2]);
        Write(Mod, "added/new.bin", [3]);
        using var overlay = new GameFileOverlay(Root, [Mod]);
        const ulong address = 0x1_0000_0000;
        var memory = new FakeCpuMemory(address, 0x2000);
        var context = new CpuContext(memory, Generation.Gen5);
        foreach (var path in new[] { "/app0/data", "/app0/added", "/app0/added/new.bin" })
        {
            memory.WriteCString(address + 0x100, path);
            context[CpuRegister.Rdi] = address + 0x100;
            context[CpuRegister.Rsi] = address + 0x300;
            Assert.Equal(0, KernelMemoryCompatExports.KernelStat(context));
        }
        foreach (var directory in new[] { "data", "added" })
        {
            memory.WriteCString(address + 0x100, "/app0/" + directory);
            context[CpuRegister.Rdi] = address + 0x100;
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(0, KernelExports.KernelOpen(context));
            var fd = context[CpuRegister.Rax];
            try
            {
                context[CpuRegister.Rdi] = fd;
                context[CpuRegister.Rsi] = address + 0x500;
                context[CpuRegister.Rdx] = 1024;
                Assert.Equal(0, KernelMemoryCompatExports.KernelGetdents(context));
                var payload = new byte[(int)context[CpuRegister.Rax]];
                Assert.True(memory.TryRead(address + 0x500, payload));
                var names = new List<string>();
                for (var offset = 0; offset < payload.Length; offset += BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(offset + 4)))
                    names.Add(Encoding.UTF8.GetString(payload, offset + 8, payload[offset + 7]));
                Assert.Equal(directory == "data" ? [".", "..", "base.bin", "extra.bin"] : new[] { ".", "..", "new.bin" }, names);
            }
            finally
            {
                context[CpuRegister.Rdi] = fd;
                KernelMemoryCompatExports.KernelClose(context);
            }
        }
        memory.WriteCString(address + 0x100, "/app0/added/new.bin");
        context[CpuRegister.Rdi] = address + 0x100;
        context[CpuRegister.Rsi] = 0x2;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED, KernelExports.KernelOpen(context));
        Assert.Equal(new byte[] { 3 }, File.ReadAllBytes(Path.Combine(Mod, "added", "new.bin")));
    }

    [Fact]
    public void SymbolicLinksInsideAModCannotExposeOutsideFiles()
    {
        Write(Mod, "keep.bin", [1]);
        Write(_directory, "outside/secret.bin", [7]);
        var link = Path.Combine(Mod, "link");
        try { Directory.CreateSymbolicLink(link, Path.Combine(_directory, "outside")); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        using var overlay = new GameFileOverlay(Root, [Mod]);
        var requested = Path.Combine(Root, "link", "secret.bin");
        Assert.Equal(requested, GameFileOverlay.ResolveFile(requested));
        Assert.DoesNotContain("link", GameFileOverlay.List(Root));
        Directory.Delete(link);
    }
}
