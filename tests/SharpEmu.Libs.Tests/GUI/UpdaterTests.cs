// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GUI;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Libs.Tests.GUI;

public sealed class UpdaterTests
{
    [Fact]
    public async Task CheckAsync_TimeoutRetriesAfterConfiguredDelay()
    {
        if (RuntimeInformation.ProcessArchitecture != Architecture.X64 &&
            !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_ALLOW_NON_X64"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var requests = 0;
        var delays = new List<TimeSpan>();
        using var client = new HttpClient(new DelegateHandler(_ =>
        {
            requests++;
            if (requests == 1)
            {
                throw new OperationCanceledException();
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") };
        }));
        using var scope = Updater.UseHttpClientForTests(client, (delay, _) =>
        {
            delays.Add(delay);
            return Task.CompletedTask;
        });

        var update = await Updater.CheckAsync("abcdef0");

        Assert.Null(update);
        Assert.Equal(2, requests);
        Assert.Equal([TimeSpan.FromSeconds(10)], delays);
    }

    [Fact]
    public void ParseReleases_PreservesNewestToOldestVersionedChangelog()
    {
        const string json = """
            [
              {
                "tag_name": "v0.0.3-hotfix-2",
                "target_commitish": "d5108e854d609808f17093a6f5dbbc711d09ad2e",
                "body": "Build for commit [`d5108e8`](https://example.test/d5108e8).\n\nNewest changes.",
                "assets": [{
                  "name": "sharpemu-0.0.3-hotfix-2-win-x64.zip",
                  "browser_download_url": "https://example.test/new.zip",
                  "size": 42,
                  "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "created_at": "2026-07-28T00:00:00Z"
                }]
              },
              {
                "tag_name": "v0.0.3-hotfix-1",
                "body": "Build for commit [`a3130e3`](https://example.test/a3130e3).\n\nOlder changes.",
                "assets": [{
                  "name": "sharpemu-0.0.3-hotfix-1-win-x64.zip",
                  "browser_download_url": "https://example.test/old.zip",
                  "size": 24,
                  "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                  "created_at": "2026-07-20T00:00:00Z"
                }]
              }
            ]
            """;

        var releases = Updater.ParseReleases(json, "win-x64", ".zip");

        Assert.Collection(
            releases,
            newest =>
            {
                Assert.Equal("d5108e8", newest.Sha);
                Assert.Equal("v0.0.3-hotfix-2", newest.TagName);
                Assert.Equal("Newest changes.", newest.Changelog.Single().Notes.Split("\n\n")[1]);
            },
            oldest =>
            {
                Assert.Equal("a3130e3", oldest.Sha);
                Assert.Equal("v0.0.3-hotfix-1", oldest.TagName);
                Assert.Equal("Older changes.", oldest.Changelog.Single().Notes.Split("\n\n")[1]);
            });
    }

    [Fact]
    public void ParseReleasePages_FiltersNonVersionedReleasesAcrossPages()
    {
        const string newestPage = """
            [{
              "tag_name": "v0.0.3",
              "body": "Build for commit [`92e3abe`](https://example.test/92e3abe).",
              "assets": [{
                "name": "sharpemu-0.0.3-win-x64.zip",
                "browser_download_url": "https://example.test/current.zip",
                "size": 42,
                "digest": "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "created_at": "2026-07-28T00:00:00Z"
              }]
            }, {
              "tag_name": "win64-main-fa2616d",
              "body": "Build for commit [`fa2616d`](https://example.test/fa2616d).",
              "assets": [{
                "name": "sharpemu-main-win-x64.zip",
                "browser_download_url": "https://example.test/dev.zip",
                "size": 42,
                "digest": "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "created_at": "2026-07-27T00:00:00Z"
              }]
            }]
            """;
        const string olderPage = """
            [{
              "tag_name": "v0.0.2",
              "body": "Build for commit [`abcdef0`](https://example.test/abcdef0).",
              "assets": [{
                "name": "sharpemu-0.0.2-win-x64.zip",
                "browser_download_url": "https://example.test/older.zip",
                "size": 24,
                "digest": "sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "created_at": "2026-07-20T00:00:00Z"
              }]
            }]
            """;

        var releases = Updater.ParseReleasePages([newestPage, olderPage], "win-x64", ".zip");

        Assert.Equal(["v0.0.3", "v0.0.2"], releases.Select(release => release.TagName));
    }

    [Fact]
    public void UpdateManifest_ParsesReleaseMetadata()
    {
        var manifest = UpdateManifest.Parse("""
            {"schema":1,"version":"0.0.3","commit":"d5108e854d609808f17093a6f5dbbc711d09ad2e","sha256sums":"archive.zip  abc"}
            """);

        Assert.NotNull(manifest);
        Assert.Equal("0.0.3", manifest!.Version);
        Assert.StartsWith("d5108e8", manifest.Commit);
        Assert.Equal("archive.zip  abc", manifest.Sha256Sums);
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }
}
