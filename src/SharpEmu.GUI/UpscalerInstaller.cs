// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SharpEmu.Libs.VideoOut.Upscaling;

namespace SharpEmu.GUI;

internal static class UpscalerInstaller
{
    private const string StreamlineArchive = "streamline-sdk-v" + DlssRuntimeFiles.StreamlineVersion + ".zip";
    private const string StreamlineDownloadUrl = "https://github.com/NVIDIA-RTX/Streamline/releases/download/v" +
        DlssRuntimeFiles.StreamlineVersion + "/" + StreamlineArchive;
    private const long StreamlineBytes = 276_016_170;
    private const string StreamlineSha256 = "32041E51DB0CAB709A271B72A352281B690C2ABBD669A827809401A5DD23956B";
    private const string XessArchive = "XeSS_SDK_" + XessRuntimeFiles.SdkVersion + ".zip";
    private const string XessDownloadUrl = "https://github.com/intel/xess/releases/download/v" +
        XessRuntimeFiles.SdkVersion + "/" + XessArchive;
    private const long XessBytes = 76_839_749;
    private const string XessSha256 = "88B8A373F30E33F3558A77A93E634F11B8132FC3047EA1A8EDEEAD32B8471990";

    // The signed Vulkan binaries in the AMD FSR SDK v2.4.0 release commit.
    private const string FsrDownloadRoot = "https://raw.githubusercontent.com/GPUOpen-LibrariesAndSDKs/FidelityFX-SDK/" +
        "7539ae8b6568ad29ed78a753404351daf3b5cca7/Kits/FidelityFX/signedbin/";
    private const long FsrLoaderBytes = 39_184;
    private const long FsrUpscalerBytes = 36_135_696;
    private const string FsrLoaderSha256 = "38A56A01FB3565C0757D9F5C1E4E69DE4B65E5F2E5769B323B200DC1F142BAB9";
    private const string FsrUpscalerSha256 = "4A7E895AF6E0B615D40A84D59EB8C2B1054AD91CDD0C739567940270D51BDAB7";

    private static readonly HttpClient Http = new();

    public static bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public static async Task InstallAsync(SuperResolutionBackend backend, string destination, IProgress<int>? progress,
        CancellationToken cancellationToken, HttpClient? client = null)
    {
        var files = backend switch
        {
            SuperResolutionBackend.Dlss => DlssRuntimeFiles.Required.Select(file => file.Name).ToArray(),
            SuperResolutionBackend.Fsr => FsrRuntimeFiles.Required,
            SuperResolutionBackend.Xess => XessRuntimeFiles.Required,
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };
        if (HasAllFiles(destination, files))
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        using var installLock = new FileStream(Path.Combine(parent, $".{Path.GetFileName(destination)}.install.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        if (HasAllFiles(destination, files))
        {
            return;
        }

        var staging = Path.Combine(parent, $".install-{Guid.NewGuid():N}");
        var payload = Path.Combine(staging, "payload");
        var preserveBackup = false;
        try
        {
            Directory.CreateDirectory(payload);
            client ??= Http;
            if (backend == SuperResolutionBackend.Dlss)
            {
                var archivePath = Path.Combine(staging, StreamlineArchive);
                await DownloadAsync(client, StreamlineDownloadUrl, archivePath, StreamlineBytes, StreamlineSha256,
                    progress, 0, StreamlineBytes, cancellationToken);
                using var archive = ZipFile.OpenRead(archivePath);
                // Streamline probes this Vulkan helper even when only DLSS Super Resolution is requested.
                files = [.. files, "NvLowLatencyVk.dll"];
                foreach (var name in files)
                {
                    await ExtractAsync(archive, "bin/x64/" + name, Path.Combine(payload, name), cancellationToken);
                }
            }
            else if (backend == SuperResolutionBackend.Xess)
            {
                var archivePath = Path.Combine(staging, XessArchive);
                await DownloadAsync(client, XessDownloadUrl, archivePath, XessBytes, XessSha256, progress, 0, XessBytes, cancellationToken);
                using var archive = ZipFile.OpenRead(archivePath);
                files = [XessRuntimeFiles.Library, "LICENSE.txt", "third-party-programs.txt"];
                foreach (var name in files)
                {
                    var entry = name == XessRuntimeFiles.Library ? "bin/" + name : name;
                    await ExtractAsync(archive, entry, Path.Combine(payload, name), cancellationToken);
                }
            }
            else
            {
                const long total = FsrLoaderBytes + FsrUpscalerBytes;
                await DownloadAsync(client, FsrDownloadRoot + FsrRuntimeFiles.Loader, Path.Combine(payload, FsrRuntimeFiles.Loader),
                    FsrLoaderBytes, FsrLoaderSha256, progress, 0, total, cancellationToken);
                await DownloadAsync(client, FsrDownloadRoot + FsrRuntimeFiles.Upscaler, Path.Combine(payload, FsrRuntimeFiles.Upscaler),
                    FsrUpscalerBytes, FsrUpscalerSha256, progress, FsrLoaderBytes, total, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            PublishFiles(payload, destination, files, Path.Combine(staging, "backup"), ref preserveBackup);
        }
        finally
        {
            if (!preserveBackup && Directory.Exists(staging))
            {
                try
                {
                    Directory.Delete(staging, recursive: true);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Console.Error.WriteLine($"[PSSR][WARN] Could not remove {staging}: {exception.Message}");
                }
            }
        }
    }

    private static async Task ExtractAsync(ZipArchive archive, string entryName, string destination, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(entryName) ?? throw new InvalidDataException($"The SDK archive is missing {entryName}.");
        await using var source = entry.Open();
        await using var output = File.Create(destination);
        await source.CopyToAsync(output, cancellationToken);
    }

    private static bool HasAllFiles(string directory, IReadOnlyList<string> files) =>
        files.All(name => File.Exists(Path.Combine(directory, name)));

    private static async Task DownloadAsync(HttpClient client, string url, string path, long size, string sha256,
        IProgress<int>? progress, long previousBytes, long totalBytes, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var name = Path.GetFileName(path);
        if (response.Content.Headers.ContentLength is { } contentLength && contentLength != size)
        {
            throw new InvalidDataException($"Unexpected download size for {name}.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = File.Create(path);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long written = 0;
        var lastPercent = (int)(previousBytes * 100 / totalBytes);
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            written += count;
            if (written > size)
            {
                throw new InvalidDataException($"Unexpected download size for {name}.");
            }

            hash.AppendData(buffer, 0, count);
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            var percent = (int)((previousBytes + written) * 100 / totalBytes);
            if (percent != lastPercent)
            {
                progress?.Report(percent);
                lastPercent = percent;
            }
        }

        if (written != size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"SHA-256 or size verification failed for {name}.");
        }
    }

    internal static void PublishFiles(string payload, string destination, IReadOnlyList<string> files, string backupDirectory,
        ref bool preserveBackup)
    {
        if (!Directory.Exists(destination))
        {
            Directory.Move(payload, destination);
            return;
        }

        Directory.CreateDirectory(backupDirectory);
        var installed = new List<(string Target, string? Backup)>(files.Count);
        try
        {
            foreach (var name in files)
            {
                var target = Path.Combine(destination, name);
                var backup = File.Exists(target) ? Path.Combine(backupDirectory, name) : null;
                if (backup is not null)
                {
                    File.Copy(target, backup);
                }

                File.Move(Path.Combine(payload, name), target, overwrite: true);
                installed.Add((target, backup));
            }
        }
        catch (Exception installError) when (installError is IOException or UnauthorizedAccessException)
        {
            try
            {
                for (var index = installed.Count - 1; index >= 0; index--)
                {
                    var (target, backup) = installed[index];
                    if (backup is null)
                    {
                        File.Delete(target);
                    }
                    else
                    {
                        File.Move(backup, target, overwrite: true);
                    }
                }
            }
            catch (Exception rollbackError) when (rollbackError is IOException or UnauthorizedAccessException)
            {
                preserveBackup = true;
                throw new IOException($"{installError.Message} Restoration failed: {rollbackError.Message} Backup: {backupDirectory}", rollbackError);
            }

            throw;
        }
    }
}
