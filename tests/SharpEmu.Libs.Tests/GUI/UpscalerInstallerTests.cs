// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using SharpEmu.GUI;
using SharpEmu.Libs.VideoOut.Upscaling;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class UpscalerInstallerTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("sharpemu-upscaler-test-");

    [Theory]
    [InlineData(SuperResolutionBackend.Dlss)]
    [InlineData(SuperResolutionBackend.Fsr)]
    [InlineData(SuperResolutionBackend.Xess)]
    public async Task CompleteInstallationSkipsTheDownload(SuperResolutionBackend backend)
    {
        var destination = _root.CreateSubdirectory("installed").FullName;
        var files = backend switch
        {
            SuperResolutionBackend.Dlss => DlssRuntimeFiles.Required.Select(file => file.Name),
            SuperResolutionBackend.Fsr => FsrRuntimeFiles.Required,
            SuperResolutionBackend.Xess => XessRuntimeFiles.Required,
            _ => throw new ArgumentOutOfRangeException(nameof(backend)),
        };
        foreach (var name in files)
        {
            File.WriteAllText(Path.Combine(destination, name), "installed");
        }

        using var client = new HttpClient(new CorruptDownload());
        await UpscalerInstaller.InstallAsync(backend, destination, null, CancellationToken.None, client);

        Assert.All(files, name => Assert.Equal("installed", File.ReadAllText(Path.Combine(destination, name))));
        Assert.Single(_root.GetDirectories());
    }

    [Fact]
    public async Task RejectsCorruptDownloadBeforeChangingInstalledFiles()
    {
        var destination = _root.CreateSubdirectory("fsr").FullName;
        var loader = Path.Combine(destination, FsrRuntimeFiles.Loader);
        await File.WriteAllTextAsync(loader, "local renderer");
        using var client = new HttpClient(new CorruptDownload());

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            UpscalerInstaller.InstallAsync(SuperResolutionBackend.Fsr, destination, null, CancellationToken.None, client));

        Assert.Equal("local renderer", await File.ReadAllTextAsync(loader));
        Assert.Single(Directory.GetFiles(destination));
        Assert.Single(_root.GetDirectories());
    }

    [Fact]
    public void RestoresExistingFilesWhenReplacingTheSetFails()
    {
        var destination = _root.CreateSubdirectory("fsr").FullName;
        var payload = _root.CreateSubdirectory("payload").FullName;
        var loader = Path.Combine(destination, FsrRuntimeFiles.Loader);
        File.WriteAllText(loader, "old loader");
        File.WriteAllText(Path.Combine(payload, FsrRuntimeFiles.Loader), "new loader");
        File.WriteAllText(Path.Combine(payload, FsrRuntimeFiles.Upscaler), "new upscaler");
        Directory.CreateDirectory(Path.Combine(destination, FsrRuntimeFiles.Upscaler));
        var preserveBackup = false;

        var error = Record.Exception(() => UpscalerInstaller.PublishFiles(payload, destination, FsrRuntimeFiles.Required,
            Path.Combine(_root.FullName, "backup"), ref preserveBackup));

        Assert.True(error is IOException or UnauthorizedAccessException);
        Assert.Equal("old loader", File.ReadAllText(loader));
        Assert.True(Directory.Exists(Path.Combine(destination, FsrRuntimeFiles.Upscaler)));
        Assert.False(preserveBackup);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private sealed class CorruptDownload : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[39_184]),
            });
    }
}
