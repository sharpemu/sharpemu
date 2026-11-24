// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;

namespace SharpEmu.HLE.FileSystem.Pkg;

// A fake/debug PS5 package ("\x7FFIH") opened for reading. Retail packages and packages whose outer
// PFS is encrypted are refused at Open: their files need console keys. Extraction streams every
// file block by block, so a package of any size needs only a few hundred KiB of memory.
public sealed class Ps5Package : IGamePackage
{
    private const string ImageFileName = "pfs_image.dat";
    private const string LayoutFileName = "naps_pkg_layout.dat";
    // A layout is a few MiB at most; anything larger is not a NAPS file.
    private const long MaxLayoutSize = 256L << 20;

    private readonly Ps5Source _source;
    private readonly Ps5Fih _fih;
    private readonly Ps5Cnt _cnt;
    private readonly Ps5OuterPfs _outer;
    private readonly string? _paramTitle;
    private readonly string? _paramTitleId;

    private Ps5InnerImage? _inner;
    private List<Ps5InnerFile>? _files;
    private long _filesSize;

    private Ps5Package(Ps5Source source, Ps5Fih fih, Ps5Cnt cnt, Ps5OuterPfs outer)
    {
        _source = source;
        _fih = fih;
        _cnt = cnt;
        _outer = outer;
        foreach (var file in cnt.Files)
        {
            if (file.Name == "param.json")
            {
                (_paramTitleId, _paramTitle) = ReadParamJson(file.Bytes);
                break;
            }
        }
    }

    public string ContentId => _cnt.ContentId;

    // param.json titleId, else the title id inside the content id (UP9000-PPSA01234_00-...).
    public string TitleId =>
        !string.IsNullOrWhiteSpace(_paramTitleId) ? _paramTitleId : ContentId.Length >= 16 ? ContentId.Substring(7, 9) : string.Empty;

    // param.json localizedParameters[defaultLanguage].titleName, else the en-US one.
    public string? Title => _paramTitle;

    // Nothing in the container reliably tells a game from a patch or DLC, so this stays null (a game).
    public string? Category => null;

    public bool IsGameApplication => true;

    public string GetFolderName(string fallback) =>
        PkgPackage.ToFolderName(Title) ?? PkgPackage.ToFolderName(TitleId) ?? fallback;

    public static Ps5Package Open(string path)
    {
        var source = new Ps5Source(path);
        try
        {
            var fih = Ps5Fih.Read(source);
            var cnt = Ps5Cnt.Read(source, fih.CntOffset);
            var outer = Ps5OuterPfs.Open(source, fih.PfsOffset, fih.PfsSize, fih.SuperblockOffset);
            return new Ps5Package(source, fih, cnt, outer);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    // Size on disk of everything Extract writes (sce_sys metadata plus the game files), computed
    // without extracting. It decodes the inner image's metadata tail to walk the tree.
    public long GetInstalledSize()
    {
        LoadTree();
        return _filesSize + _cnt.FilesSize;
    }

    public void Extract(string destination, IProgress<PkgExtractProgress>? progress = null, CancellationToken cancellation = default) =>
        Extract(destination, progress, cancellation, PkgPackage.ProgressStepBytes, PkgPackage.GetAvailableSpace(destination));

    internal void Extract(
        string destination,
        IProgress<PkgExtractProgress>? progress,
        CancellationToken cancellation,
        long progressStepBytes,
        long? availableBytes)
    {
        var (image, files) = LoadTree();
        var total = _filesSize;

        // Resolve every destination first: an unsafe name must fail before anything is written.
        var metadataRoot = Path.Combine(destination, "sce_sys");
        var metadata = _cnt.Files
            .Select(file => (Path: PkgPackage.ResolveSafe(metadataRoot, file.Name.Split('/')), file.Bytes))
            .ToList();
        var targets = files
            .Select(file => (Path: PkgPackage.ResolveSafe(destination, file.Components), File: file))
            .ToList();

        var needed = total + _cnt.FilesSize;
        if (availableBytes is { } available && available < needed)
        {
            throw new IOException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"Not enough free disk space: need {needed / (1024.0 * 1024 * 1024):0.00} GB, {available / (1024.0 * 1024 * 1024):0.00} GB free."));
        }

        Directory.CreateDirectory(destination);
        foreach (var (path, bytes) in metadata)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }

        long done = 0;
        foreach (var (path, file) in targets)
        {
            cancellation.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var relative = Path.GetRelativePath(destination, path);
            progress?.Report(new PkgExtractProgress(done, total, relative));
            var sinceReport = 0L;
            using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
            image.CopyTo(file.Logical, file.Size, chunk =>
            {
                output.Write(chunk);
                done += chunk.Length;
                sinceReport += chunk.Length;
                cancellation.ThrowIfCancellationRequested();
                if (sinceReport >= progressStepBytes)
                {
                    sinceReport = 0;
                    progress?.Report(new PkgExtractProgress(done, total, relative));
                }
            });
            progress?.Report(new PkgExtractProgress(done, total, relative));
        }

        progress?.Report(new PkgExtractProgress(total, total, string.Empty));
    }

    // Opens the inner image and lists its files once; both GetInstalledSize and Extract need them.
    private (Ps5InnerImage Image, List<Ps5InnerFile> Files) LoadTree()
    {
        if (_inner is not null && _files is not null)
        {
            return (_inner, _files);
        }

        var imageInode = _outer.Find(ImageFileName)
            ?? throw new InvalidDataException($"The PS5 package's outer PFS has no {ImageFileName}.");
        var layoutInode = _outer.Find(LayoutFileName)
            ?? throw new InvalidDataException($"The PS5 package's outer PFS has no {LayoutFileName}; images without a NAPS layout are not supported.");
        var layoutFile = _outer.OpenFile(layoutInode);
        if (layoutFile.Size > MaxLayoutSize)
        {
            throw new InvalidDataException($"{LayoutFileName} is far larger than any layout.");
        }

        var inner = Ps5InnerImage.Open(_outer.OpenFile(imageInode), layoutFile.ReadAll());
        var files = inner.ListFiles();
        long size = 0;
        foreach (var file in files)
        {
            // Every name goes through ResolveSafe again at Extract; the size can be summed now.
            size += file.Size;
        }

        _inner = inner;
        _files = files;
        _filesSize = size;
        return (inner, files);
    }

    private static (string? TitleId, string? Title) ReadParamJson(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var titleId = root.TryGetProperty("titleId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            string? title = null;
            if (root.TryGetProperty("localizedParameters", out var localized) && localized.ValueKind == JsonValueKind.Object)
            {
                var defaultLanguage = localized.TryGetProperty("defaultLanguage", out var language) && language.ValueKind == JsonValueKind.String
                    ? language.GetString()
                    : null;
                // defaultLanguage sits inside localizedParameters; a copy at the top level is tolerated too.
                if (defaultLanguage is null && root.TryGetProperty("defaultLanguage", out var rootLanguage) && rootLanguage.ValueKind == JsonValueKind.String)
                {
                    defaultLanguage = rootLanguage.GetString();
                }

                foreach (var key in new[] { defaultLanguage, "en-US" })
                {
                    if (key is not null
                        && localized.TryGetProperty(key, out var entry)
                        && entry.ValueKind == JsonValueKind.Object
                        && entry.TryGetProperty("titleName", out var name)
                        && name.ValueKind == JsonValueKind.String)
                    {
                        title = name.GetString();
                        break;
                    }
                }
            }

            return (titleId, title);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public void Dispose() => _source.Dispose();
}
