// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using SharpEmu.Core.Mods;
using Xunit;

namespace SharpEmu.Libs.Tests.FileSystem;

public sealed class GameModRepositoryTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("sharpemu-mods-").FullName;
    private GameModRepository Repository => new("PPSA00001", Path.Combine(_directory, "mods"));
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string Zip(string name, params (string Path, byte[] Data)[] files)
    {
        var path = Path.Combine(_directory, name + ".zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entry, data) in files)
        {
            using var stream = archive.CreateEntry(entry).Open();
            stream.Write(data);
        }
        return path;
    }

    [Theory]
    [InlineData("PPSA00001")]
    [InlineData("CUSA00001")]
    [InlineData("PATH-123456")]
    public void ManualModsAreDiscoveredAndSelectionsSurviveReopening(string key)
    {
        var repository = new GameModRepository(key, _directory);
        Directory.CreateDirectory(Path.Combine(repository.DirectoryPath, "Textures"));
        Directory.CreateDirectory(Path.Combine(repository.DirectoryPath, "Audio"));
        Directory.CreateDirectory(Path.Combine(repository.DirectoryPath, ".import-partial"));
        Assert.Equal(["Audio", "Textures"], repository.Load().Select(mod => mod.Name));
        Assert.All(repository.Load(), mod => Assert.False(mod.Enabled));
        repository.SetEnabled("Textures", true);
        var reopened = new GameModRepository(key.ToLowerInvariant(), _directory);
        Assert.True(reopened.Load().Single(mod => mod.Name == "Textures").Enabled);
        reopened.SetEnabled("Textures", false);
        Assert.All(repository.Load(), mod => Assert.False(mod.Enabled));
        Assert.Throws<DirectoryNotFoundException>(() => repository.SetEnabled("Missing", true));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("C:\\outside")]
    [InlineData("")]
    public void GameKeysCannotEscapeTheModsDirectory(string key) =>
        Assert.Throws<ArgumentException>(() => new GameModRepository(key, _directory));

    [Theory]
    [InlineData("data/level.bin")]
    [InlineData("app0/data/level.bin")]
    [InlineData("Textures/data/level.bin")]
    public void ZipInstallationPreservesGamePathsAndNeverOverwritesAnotherMod(string entry)
    {
        var zip = Zip("Textures", (entry, [1, 2, 3]));
        Assert.Equal("Textures", Repository.ImportZip(zip));
        Assert.Equal("Textures (2)", Repository.ImportZip(zip));
        Assert.All(Repository.Load(), mod =>
        {
            Assert.False(mod.Enabled);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(mod.DirectoryPath, "data", "level.bin")));
        });
        Assert.Empty(Directory.GetDirectories(Repository.DirectoryPath, ".import-*"));
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("data/../../outside.bin")]
    [InlineData("/outside.bin")]
    [InlineData("C:/outside.bin")]
    [InlineData("data\\..\\outside.bin")]
    [InlineData("data/file.bin:stream")]
    [InlineData("data/./level.bin")]
    [InlineData("data/file. ")]
    [InlineData("data/CON.txt")]
    public void UnsafeZipPathsAreRejectedBeforeInstallation(string entry)
    {
        var zip = Zip("Unsafe", ("good.bin", [1]), (entry, [2]));
        Assert.Throws<InvalidDataException>(() => Repository.ImportZip(zip));
        Assert.Empty(Repository.Load());
        Assert.False(File.Exists(Path.Combine(_directory, "outside.bin")));
    }

    [Fact]
    public void ZipSymlinksAreRejected()
    {
        var path = Zip("Symlink", ("link", "../outside"u8.ToArray()));
        using (var archive = ZipFile.Open(path, ZipArchiveMode.Update))
            archive.Entries[0].ExternalAttributes = unchecked((int)0xA1FF0000);
        Assert.Throws<InvalidDataException>(() => Repository.ImportZip(path));
        Assert.Empty(Repository.Load());
    }

    [Fact]
    public void DuplicatePathsRollBackTheWholeInstall()
    {
        var zip = Zip("Duplicate", ("data/file.bin", [1]), ("data/file.bin", [2]));
        Assert.Throws<InvalidDataException>(() => Repository.ImportZip(zip));
        Assert.Empty(Directory.EnumerateFileSystemEntries(Repository.DirectoryPath));
    }

    [Fact]
    public void ProgressIncludesBytesInsideALargeFileAndFinishesAtTheTotal()
    {
        var bytes = new byte[5 * 1024 * 1024];
        var zip = Zip("Large", ("data/large.bin", bytes), ("data/last.bin", [1, 2]));
        var updates = new List<ModImportProgress>();
        Repository.ImportZip(zip, new InlineProgress(updates.Add));
        Assert.Contains(updates, update => update.ExtractedBytes > 0 && update.ExtractedBytes < bytes.Length && update.FileName == "data" + Path.DirectorySeparatorChar + "large.bin");
        Assert.Equal(bytes.Length + 2, updates[^1].TotalBytes);
        Assert.Equal(updates[^1].TotalBytes, updates[^1].ExtractedBytes);
        Assert.True(updates.Select(update => update.ExtractedBytes).SequenceEqual(updates.Select(update => update.ExtractedBytes).Order()));
    }

    [Fact]
    public void CancellationDuringExtractionRemovesPartialFilesAndKeepsExistingMods()
    {
        Repository.ImportZip(Zip("Existing", ("keep.bin", [7])));
        using var cancellation = new CancellationTokenSource();
        var zip = Zip("Cancelled", ("data/big.bin", new byte[5 * 1024 * 1024]));
        Assert.Throws<OperationCanceledException>(() => Repository.ImportZip(zip,
            new InlineProgress(update => { if (update.ExtractedBytes > 0) cancellation.Cancel(); }), cancellation.Token));
        Assert.Equal("Existing", Assert.Single(Repository.Load()).Name);
        Assert.Equal(new byte[] { 7 }, File.ReadAllBytes(Path.Combine(Repository.DirectoryPath, "Existing", "keep.bin")));
        Assert.Empty(Directory.GetDirectories(Repository.DirectoryPath, ".import-*"));
    }

    private sealed class InlineProgress(Action<ModImportProgress> report) : IProgress<ModImportProgress>
    {
        public void Report(ModImportProgress value) => report(value);
    }
}
