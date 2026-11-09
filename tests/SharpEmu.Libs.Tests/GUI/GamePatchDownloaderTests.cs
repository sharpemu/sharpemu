// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using System.Xml;
using SharpEmu.Core.Patches;
using SharpEmu.GUI;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class GamePatchDownloaderTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sharpemu-patch-download-").FullName;
    private string Destination => Path.Combine(_root, "patches");
    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static string Xml(string version = "01.004.000", string titleId = "PPSA00001") => $$"""
        <Patch><TitleID><ID>{{titleId}}</ID></TitleID>
          <Metadata Name="Example patch" AppVer="{{version}}" AppElf="eboot.bin">
            <PatchList><Line Type="{{(version == "mask" ? "mask" : "bytes")}}" Address="{{(version == "mask" ? "aa bb" : "0x10")}}" Value="90"/></PatchList>
          </Metadata>
        </Patch>
        """;

    private static string Index(string version = "01.004.000", string file = "Match.xml", string titleId = "PPSA00001") =>
        JsonSerializer.Serialize(new
        {
            formatVersion = 1,
            files = new[]
            {
                new { file, titleIds = new[] { titleId }, versions = new[] { version } },
                new { file = "Unrelated.xml", titleIds = new[] { "PPSA99999" }, versions = new[] { "01.004.000" } },
                new { file = "OtherVersion.xml", titleIds = new[] { "PPSA00001" }, versions = new[] { "01.099.000" } },
            },
        });

    [Theory]
    [InlineData("PPSA00001", "01.004.000", false)]
    [InlineData("ppsa00001", "1.4.0", false)]
    [InlineData("CUSA00001", "01.09", true)]
    [InlineData("cusa00001", "1.9", true)]
    public async Task DownloadsOnlyTheMatchingGameAndVersionWithoutEnablingIt(string titleId, string version, bool compatibilityMode)
    {
        var expectedVersion = compatibilityMode ? "01.09" : "01.004.000";
        var expectedTitle = compatibilityMode ? "CUSA00001" : "PPSA00001";
        var xml = Xml(expectedVersion, expectedTitle);
        var handler = new RepositoryHandler(Index(expectedVersion, titleId: expectedTitle), xml, compatibilityMode: compatibilityMode);
        using var client = new HttpClient(handler);
        var count = await GamePatchDownloader.DownloadAsync(titleId, version, Destination, client: client, compatibilityMode: compatibilityMode);
        Assert.Equal(1, count);
        var repository = compatibilityMode ? "ps4patches" : "ps5patches";
        var patches = compatibilityMode ? "orbis_patches" : "prospero_patches";
        Assert.Equal(new[] { $"/sharpemu/{repository}/main/patches.json", $"/sharpemu/{repository}/main/{patches}/Match.xml" }, handler.Requests);
        Assert.Equal(xml, await File.ReadAllTextAsync(Path.Combine(Destination, "Match.xml")));
        var installed = new GamePatchRepository(Destination);
        Assert.Single(installed.Load(titleId));
        Assert.Empty(installed.LoadEnabled(titleId));
        Assert.Single(Directory.GetFiles(Destination));
    }

    [Theory]
    [InlineData("PPSA00002", "01.004.000", false)]
    [InlineData("PPSA00001", "01.007.000", false)]
    [InlineData("PPSA00001", null, false)]
    [InlineData("CUSA00002", "01.09", true)]
    [InlineData("CUSA00001", "01.08", true)]
    [InlineData("CUSA00001", null, true)]
    public async Task MissingTitleOrVersionDoesNotRequestAnyXml(string titleId, string? version, bool compatibilityMode)
    {
        var handler = new RepositoryHandler(compatibilityMode ? Index("01.09", titleId: "CUSA00001") : Index(), Xml(),
            compatibilityMode: compatibilityMode);
        using var client = new HttpClient(handler);
        Assert.Equal(0, await GamePatchDownloader.DownloadAsync(titleId, version, Destination, client: client, compatibilityMode: compatibilityMode));
        Assert.Single(handler.Requests);
        Assert.Equal($"/sharpemu/{(compatibilityMode ? "ps4patches" : "ps5patches")}/main/patches.json", handler.Requests[0]);
        Assert.False(Directory.Exists(Destination));
    }

    [Theory]
    [InlineData("01.007.000", false)]
    [InlineData(null, false)]
    [InlineData("01.09", true)]
    [InlineData(null, true)]
    public async Task PatternBasedPatchesCanMatchAnyInstalledVersion(string? version, bool compatibilityMode)
    {
        var titleId = compatibilityMode ? "CUSA00001" : "PPSA00001";
        var handler = new RepositoryHandler(Index("mask", titleId: titleId), Xml("mask", titleId), compatibilityMode: compatibilityMode);
        using var client = new HttpClient(handler);
        Assert.Equal(1, await GamePatchDownloader.DownloadAsync(titleId, version, Destination, client: client, compatibilityMode: compatibilityMode));
        Assert.True(Assert.Single(new GamePatchRepository(Destination).Load(titleId)).IsPatternVersion);
    }

    [Theory]
    [InlineData("../escape.xml")]
    [InlineData("..\\escape.xml")]
    [InlineData("/escape.xml")]
    [InlineData("C:escape.xml")]
    [InlineData("Match.xml?redirect=1")]
    [InlineData("Match.xml#fragment")]
    [InlineData("Match.json")]
    public async Task UnsafeIndexFilenamesNeverReachTheNetworkOrFileSystem(string file)
    {
        var handler = new RepositoryHandler(Index(file: file), Xml());
        using var client = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidDataException>(() => GamePatchDownloader.DownloadAsync("PPSA00001", "01.004.000", Destination, client: client));
        Assert.Single(handler.Requests);
        Assert.False(Directory.Exists(Destination));
        Assert.False(File.Exists(Path.Combine(_root, "escape.xml")));
    }

    [Theory]
    [InlineData("{\"formatVersion\":2,\"files\":[]}")]
    [InlineData("{\"formatVersion\":1}")]
    [InlineData("{\"formatVersion\":1,\"files\":[null]}")]
    [InlineData("{\"formatVersion\":1,\"files\":[{\"file\":\"Match.xml\"}]}")]
    public async Task InvalidIndexesAreErrorsNotMissingPatchResults(string index)
    {
        using var client = new HttpClient(new RepositoryHandler(index, Xml()));
        await Assert.ThrowsAsync<InvalidDataException>(() => GamePatchDownloader.DownloadAsync("PPSA00001", "01.004.000", Destination, client: client));
        Assert.False(Directory.Exists(Destination));
    }

    [Theory]
    [InlineData("01.007.000", "PPSA00001")]
    [InlineData("01.004.000", "PPSA00002")]
    public async Task DownloadedXmlIsRevalidatedBeforeReplacingLocalFiles(string version, string titleId)
    {
        Directory.CreateDirectory(Destination);
        var path = Path.Combine(Destination, "Match.xml");
        await File.WriteAllTextAsync(path, "keep local patch");
        using var client = new HttpClient(new RepositoryHandler(Index(), Xml(version, titleId)));
        await Assert.ThrowsAsync<InvalidDataException>(() => GamePatchDownloader.DownloadAsync("PPSA00001", "01.004.000", Destination, client: client));
        Assert.Equal("keep local patch", await File.ReadAllTextAsync(path));
        Assert.Single(Directory.GetFiles(Destination));
    }

    [Theory]
    [InlineData("<html>not XML patches</html>")]
    [InlineData("<Patch><TitleID>")]
    [InlineData("<!DOCTYPE Patch [<!ENTITY x SYSTEM 'file:///test'>]><Patch/>")]
    public async Task MalformedDownloadsAreNeverSaved(string xml)
    {
        using var client = new HttpClient(new RepositoryHandler(Index(), xml));
        var error = await Record.ExceptionAsync(() => GamePatchDownloader.DownloadAsync("PPSA00001", "01.004.000", Destination, client: client));
        Assert.True(error is XmlException or FormatException, error?.ToString());
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task HttpFailureIsNotReportedAsPatchNotFound()
    {
        using var client = new HttpClient(new RepositoryHandler(Index(), Xml(), HttpStatusCode.ServiceUnavailable));
        await Assert.ThrowsAsync<HttpRequestException>(() => GamePatchDownloader.DownloadAsync("PPSA00001", "01.004.000", Destination, client: client));
        Assert.False(Directory.Exists(Destination));
    }

    [Fact]
    public async Task ExistingSelectionSurvivesAnUnchangedDownload()
    {
        Directory.CreateDirectory(Destination);
        await File.WriteAllTextAsync(Path.Combine(Destination, "Match.xml"), Xml());
        var repository = new GamePatchRepository(Destination);
        repository.SetEnabled("PPSA00001", Assert.Single(repository.Load("PPSA00001")), true);
        using var client = new HttpClient(new RepositoryHandler(Index(), Xml()));
        Assert.Equal(1, await GamePatchDownloader.DownloadAsync("PPSA00001", "01.004.000", Destination, client: client));
        Assert.Contains(Assert.Single(repository.Load("PPSA00001")).Id, repository.LoadEnabled("PPSA00001"));
        Assert.Empty(Directory.GetFiles(Destination, "*.tmp"));
    }

    [Fact]
    public async Task CancellationDoesNotCreateOrReplacePatchFiles()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var client = new HttpClient(new RepositoryHandler(Index(), Xml()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GamePatchDownloader.DownloadAsync("PPSA00001", "01.004.000", Destination, cancellation.Token, client));
        Assert.False(Directory.Exists(Destination));
    }

    private sealed class RepositoryHandler(string index, string xml, HttpStatusCode xmlStatus = HttpStatusCode.OK,
        bool compatibilityMode = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("raw.githubusercontent.com", request.RequestUri!.Host);
            Requests.Add(request.RequestUri.AbsolutePath);
            var isIndex = request.RequestUri.AbsolutePath.EndsWith("patches.json", StringComparison.Ordinal);
            if (!isIndex) Assert.EndsWith(compatibilityMode ? "/orbis_patches/Match.xml" : "/prospero_patches/Match.xml", request.RequestUri.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(isIndex ? HttpStatusCode.OK : xmlStatus)
            {
                Content = new StringContent(isIndex ? index : xml, Encoding.UTF8),
            });
        }
    }
}
