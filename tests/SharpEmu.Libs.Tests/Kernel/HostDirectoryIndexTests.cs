// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Ampr;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// HostDirectoryIndex replaces the per-component lstat that guest path
// resolution used to pay with one listing of the component's parent. That
// makes it a cache in front of the sandbox boundary, so the verdicts it
// reports have to be exactly as conservative as a fresh stat: a symlink stays
// a symlink (the escape the resolver denies), a name the listing has never
// seen is "unknown" rather than "absent", and a listing that a write made
// stale is dropped.
// Shares the kernel path-resolution statics (the listing cache the resolver
// reads) with the other Kernel/AMPR state tests, so it runs in their collection
// rather than in parallel with them.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class HostDirectoryIndexTests : IDisposable
{
    private readonly string _root;

    public HostDirectoryIndexTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            $"sharpemu-dirindex-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        HostDirectoryIndex.ClearForTests();
    }

    public void Dispose()
    {
        HostDirectoryIndex.ClearForTests();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Describe_ReportsFileWithItsLength()
    {
        var file = Path.Combine(_root, "asset.bin");
        File.WriteAllBytes(file, new byte[1234]);

        var entry = HostDirectoryIndex.Describe(file);

        Assert.Equal(HostDirectoryIndex.Verdict.File, entry.Verdict);
        Assert.Equal(1234, entry.Length);
    }

    [Fact]
    public void Describe_ReportsDirectory()
    {
        var directory = Path.Combine(_root, "levels");
        Directory.CreateDirectory(directory);

        var entry = HostDirectoryIndex.Describe(directory);

        Assert.Equal(HostDirectoryIndex.Verdict.Directory, entry.Verdict);
    }

    // The listing covers every name the directory holds, so a name it does not
    // hold is genuinely absent - the answer that lets an APR resolve fail
    // without a stat of its own.
    [Fact]
    public void Describe_ReportsAbsentForMissingNameInAListedDirectory()
    {
        File.WriteAllBytes(Path.Combine(_root, "present.bin"), [1]);

        var entry = HostDirectoryIndex.Describe(Path.Combine(_root, "missing.bin"));

        Assert.Equal(HostDirectoryIndex.Verdict.Absent, entry.Verdict);
    }

    // Nothing is known about a path whose parent could not be listed, and the
    // caller must fall back to its own probe rather than treat it as absent.
    [Fact]
    public void Describe_ReportsUnknownWhenTheParentDoesNotExist()
    {
        var entry = HostDirectoryIndex.Describe(
            Path.Combine(_root, "no-such-dir", "asset.bin"));

        Assert.Equal(HostDirectoryIndex.Verdict.Unknown, entry.Verdict);
    }

    [Fact]
    public void Describe_ReportsUnknownForAPathWithoutAParent()
    {
        Assert.Equal(
            HostDirectoryIndex.Verdict.Unknown,
            HostDirectoryIndex.Describe(Path.DirectorySeparatorChar.ToString()).Verdict);
        Assert.Equal(
            HostDirectoryIndex.Verdict.Unknown,
            HostDirectoryIndex.Describe(string.Empty).Verdict);
    }

    // A symlink is the escape the resolver's containment guard exists to deny,
    // so the index must never report one as an ordinary file or directory.
    [Fact]
    public void Describe_ReportsSymlinkAsSymlink()
    {
        var target = Path.Combine(_root, "target");
        Directory.CreateDirectory(target);
        var fileTarget = Path.Combine(_root, "target.bin");
        File.WriteAllBytes(fileTarget, [1, 2]);

        var directoryLink = Path.Combine(_root, "dir-link");
        var fileLink = Path.Combine(_root, "file-link");
        try
        {
            Directory.CreateSymbolicLink(directoryLink, target);
            File.CreateSymbolicLink(fileLink, fileTarget);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // An unprivileged host cannot create links; nothing to assert.
            return;
        }

        Assert.Equal(
            HostDirectoryIndex.Verdict.Symlink,
            HostDirectoryIndex.Describe(directoryLink).Verdict);
        Assert.Equal(
            HostDirectoryIndex.Verdict.Symlink,
            HostDirectoryIndex.Describe(fileLink).Verdict);
    }

    // A listing is taken once and reused, so every write that changes what the
    // directory holds has to drop it or the index would keep reporting a file
    // the guest just created as absent.
    [Fact]
    public void Invalidate_RevealsAFileCreatedAfterTheListing()
    {
        var file = Path.Combine(_root, "late.bin");
        Assert.Equal(
            HostDirectoryIndex.Verdict.Absent,
            HostDirectoryIndex.Describe(file).Verdict);

        File.WriteAllBytes(file, new byte[7]);
        HostDirectoryIndex.Invalidate(file);

        var entry = HostDirectoryIndex.Describe(file);
        Assert.Equal(HostDirectoryIndex.Verdict.File, entry.Verdict);
        Assert.Equal(7, entry.Length);
    }

    [Fact]
    public void Invalidate_DropsTheListingOfTheDirectoryItself()
    {
        var directory = Path.Combine(_root, "content");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "asset.bin");
        Assert.Equal(
            HostDirectoryIndex.Verdict.Absent,
            HostDirectoryIndex.Describe(file).Verdict);

        File.WriteAllBytes(file, new byte[3]);
        HostDirectoryIndex.Invalidate(directory);

        Assert.Equal(
            HostDirectoryIndex.Verdict.File,
            HostDirectoryIndex.Describe(file).Verdict);
    }

    // Key equivalence has to match the host filesystem's. Where names fold
    // case, a differently-cased spelling is the same file and must resolve;
    // where they do not, two spellings are two files and the index must never
    // serve one for the other.
    [Fact]
    public void Describe_MatchesTheHostFilesystemsCaseRules()
    {
        File.WriteAllBytes(Path.Combine(_root, "Data.bin"), new byte[5]);

        var folded = HostDirectoryIndex.Describe(Path.Combine(_root, "DATA.BIN"));

        if (HostFoldsCase())
        {
            Assert.Equal(HostDirectoryIndex.Verdict.File, folded.Verdict);
            Assert.Equal(5, folded.Length);
        }
        else
        {
            Assert.Equal(HostDirectoryIndex.Verdict.Absent, folded.Verdict);
        }
    }

    [Fact]
    public void Describe_KeepsDistinctlyCasedSiblingsApartOnACaseSensitiveHost()
    {
        if (HostFoldsCase())
        {
            return;
        }

        File.WriteAllBytes(Path.Combine(_root, "asset.bin"), new byte[3]);
        File.WriteAllBytes(Path.Combine(_root, "ASSET.BIN"), new byte[7]);

        Assert.Equal(3, HostDirectoryIndex.Describe(Path.Combine(_root, "asset.bin")).Length);
        Assert.Equal(7, HostDirectoryIndex.Describe(Path.Combine(_root, "ASSET.BIN")).Length);
    }

    // Hidden and system attributes have no meaning to the guest, so the listing
    // must cover entries the default enumeration options would skip.
    [Fact]
    public void Describe_IncludesDotPrefixedEntries()
    {
        var hidden = Path.Combine(_root, ".hidden.bin");
        File.WriteAllBytes(hidden, new byte[2]);

        Assert.Equal(
            HostDirectoryIndex.Verdict.File,
            HostDirectoryIndex.Describe(hidden).Verdict);
    }

    private bool HostFoldsCase()
    {
        var name = $"probe_{Guid.NewGuid():N}";
        File.WriteAllText(Path.Combine(_root, name + ".tmp"), string.Empty);
        var folds = File.Exists(Path.Combine(_root, name.ToUpperInvariant() + ".TMP"));
        File.Delete(Path.Combine(_root, name + ".tmp"));
        HostDirectoryIndex.ClearForTests();
        return folds;
    }

    // A walk of a read-only image saves its listings; the next boot of the same
    // dump answers from them without asking the host volume at all, which is
    // what takes the cold metadata reads off the guest's resource scan.
    [Fact]
    public void WarmCore_SeedsListingsFromTheSnapshotAPreviousWalkSaved()
    {
        var tree = CreateTree();
        var snapshot = Path.Combine(_root, "snapshot.idx");

        Assert.False(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 42));
        Assert.True(File.Exists(snapshot));

        HostDirectoryIndex.ClearForTests();
        // Gone from the host, so only the snapshot can still describe it.
        File.Delete(Path.Combine(tree, "maps", "m01.bin"));

        Assert.True(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 42));
        var entry = HostDirectoryIndex.Describe(Path.Combine(tree, "maps", "m01.bin"));
        Assert.Equal(HostDirectoryIndex.Verdict.File, entry.Verdict);
        Assert.Equal(10, entry.Length);
        Assert.Equal(
            HostDirectoryIndex.Verdict.Directory,
            HostDirectoryIndex.Describe(Path.Combine(tree, "maps", "deep")).Verdict);
        Assert.Equal(
            HostDirectoryIndex.Verdict.File,
            HostDirectoryIndex.Describe(Path.Combine(tree, "maps", "deep", "d.bin")).Verdict);
        Assert.Equal(
            HostDirectoryIndex.Verdict.Absent,
            HostDirectoryIndex.Describe(Path.Combine(tree, "maps", "missing.bin")).Verdict);
    }

    // The dump fingerprint is what vouches for the snapshot; a dump that changed
    // is walked again and the snapshot replaced.
    [Fact]
    public void WarmCore_WalksAgainWhenTheDumpFingerprintChanged()
    {
        var tree = CreateTree();
        var snapshot = Path.Combine(_root, "snapshot.idx");
        Assert.False(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 42));
        HostDirectoryIndex.ClearForTests();
        File.Delete(Path.Combine(tree, "maps", "m01.bin"));

        Assert.False(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 43));
        Assert.Equal(
            HostDirectoryIndex.Verdict.Absent,
            HostDirectoryIndex.Describe(Path.Combine(tree, "maps", "m01.bin")).Verdict);

        HostDirectoryIndex.ClearForTests();
        Assert.True(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 43));
    }

    // Without a fingerprint there is nothing to validate a snapshot against, so
    // none is written or read.
    [Fact]
    public void WarmCore_KeepsNoSnapshotWithoutAFingerprint()
    {
        var tree = CreateTree();
        var snapshot = Path.Combine(_root, "snapshot.idx");

        Assert.False(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 0));

        Assert.False(File.Exists(snapshot));
    }

    // A snapshot cut short would seed a tree missing whole directories, whose
    // files the index would then report as absent.
    [Fact]
    public void WarmCore_RejectsATruncatedSnapshot()
    {
        var tree = CreateTree();
        var snapshot = Path.Combine(_root, "snapshot.idx");
        Assert.False(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 42));
        using (var stream = new FileStream(snapshot, FileMode.Open))
        {
            stream.SetLength(stream.Length - 4);
        }

        HostDirectoryIndex.ClearForTests();

        Assert.False(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 42));
    }

    // The snapshot sits in front of the same containment guard as a live
    // listing, so a link must come back as a link.
    [Fact]
    public void WarmCore_SnapshotKeepsSymlinksAsSymlinks()
    {
        var tree = CreateTree();
        var link = Path.Combine(tree, "maps", "link");
        try
        {
            Directory.CreateSymbolicLink(link, _root);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            // An unprivileged host cannot create links; nothing to assert.
            return;
        }

        var snapshot = Path.Combine(_root, "snapshot.idx");
        Assert.False(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 42));
        HostDirectoryIndex.ClearForTests();

        Assert.True(HostDirectoryIndex.WarmCore(tree, snapshot, fingerprint: 42));
        Assert.Equal(
            HostDirectoryIndex.Verdict.Symlink,
            HostDirectoryIndex.Describe(link).Verdict);
    }

    // The production boot path: BeginWarm finds the snapshot path and the dump
    // fingerprint itself, and the boot that saved the listings and the boot that
    // reads them back must land on the very same file.
    [Fact]
    public void BeginWarm_RoundTripsTheSnapshotThroughTheCacheDirectory()
    {
        var tree = CreateTree();
        Directory.CreateDirectory(Path.Combine(tree, "sce_sys"));
        File.WriteAllText(Path.Combine(tree, "sce_sys", "param.json"), "{}");
        var previousCacheDir = Environment.GetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE");
        Environment.SetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE", Path.Combine(_root, "cache"));
        try
        {
            var snapshot = AmprFileRegistry.GetDirectoryIndexSnapshotPath(tree);
            Assert.StartsWith(Path.Combine(_root, "cache"), snapshot, StringComparison.Ordinal);

            HostDirectoryIndex.BeginWarm(tree, persistSnapshot: true);
            Assert.True(HostDirectoryIndex.WaitForSnapshot(TimeSpan.FromSeconds(10)));
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!File.Exists(snapshot) && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(10);
            }

            Assert.True(File.Exists(snapshot));

            HostDirectoryIndex.ClearForTests();
            File.Delete(Path.Combine(tree, "maps", "m01.bin"));
            HostDirectoryIndex.BeginWarm(tree, persistSnapshot: true);
            Assert.True(HostDirectoryIndex.WaitForSnapshot(TimeSpan.FromSeconds(10)));

            // Only a seeded listing can still describe the deleted file.
            var entry = HostDirectoryIndex.Describe(Path.Combine(tree, "maps", "m01.bin"));
            Assert.Equal(HostDirectoryIndex.Verdict.File, entry.Verdict);
            Assert.Equal(10, entry.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE", previousCacheDir);
        }
    }

    // The id cache and the snapshot resolve their directory on two threads at
    // once during boot; both must settle on the preferred directory rather than
    // one of them falling back elsewhere.
    [Fact]
    public void CacheFilePaths_ResolvedConcurrentlyShareOneDirectory()
    {
        var tree = CreateTree();
        var previousCacheDir = Environment.GetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE");
        var cacheDir = Path.Combine(_root, "cache-concurrent");
        Environment.SetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE", cacheDir);
        try
        {
            var directories = new string?[32];
            Parallel.For(
                0,
                directories.Length,
                new ParallelOptions { MaxDegreeOfParallelism = directories.Length },
                i => directories[i] = Path.GetDirectoryName(
                    AmprFileRegistry.GetDirectoryIndexSnapshotPath(tree)));

            Assert.All(directories, directory => Assert.Equal(cacheDir, directory));
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_AMPR_INDEX_CACHE", previousCacheDir);
        }
    }

    private string CreateTree()
    {
        var tree = Path.Combine(_root, "app0");
        Directory.CreateDirectory(Path.Combine(tree, "maps", "deep"));
        File.WriteAllBytes(Path.Combine(tree, "maps", "m01.bin"), new byte[10]);
        File.WriteAllBytes(Path.Combine(tree, "maps", "deep", "d.bin"), [1]);
        File.WriteAllBytes(Path.Combine(tree, "top.bin"), [1, 2, 3]);
        return tree;
    }
}
