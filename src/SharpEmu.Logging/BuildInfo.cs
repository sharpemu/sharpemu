// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;

namespace SharpEmu.Logging;

/// <summary>
/// Build provenance for the running emulator, populated at compile time from
/// GitHub Actions environment variables via <c>[AssemblyMetadata]</c> and
/// surfaced as a touchHLE-style banner at the top of the log.
/// </summary>
public static class BuildInfo
{
    private const string ProjectUrl = "https://github.com/sharpemu/sharpemu";
    private const string CanonicalRepository = "sharpemu/sharpemu";

    /// <summary>Short commit hash the build was produced from, or <c>null</c>.</summary>
    public static string? CommitSha { get; }

    /// <summary>Branch the build was produced from, or <c>null</c>.</summary>
    public static string? Branch { get; }

    /// <summary>Repository slug (<c>owner/name</c>) the build came from, or <c>null</c>.</summary>
    public static string? Repository { get; }

    /// <summary>URL of the GitHub Actions workflow run that produced the build, or <c>null</c>.</summary>
    public static string? WorkflowRunUrl { get; }

    /// <summary>Build configuration (e.g. <c>Debug</c> or <c>Release</c>), or <c>null</c>.</summary>
    public static string? Configuration { get; }

    /// <summary>
    /// Whether this build is an official release: a Release-configuration CI build
    /// from the canonical repository, produced by a push to <c>main</c> or a manual
    /// workflow dispatch (matching the CI <c>release</c> job). All other builds
    /// (pull requests, forks, feature branches, and local/Debug builds) are
    /// considered unofficial.
    /// </summary>
    public static bool IsOfficialRelease { get; }

    static BuildInfo()
    {
        var metadata = ReadMetadata();

        CommitSha = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildSha"));
        if (CommitSha is { Length: > 7 })
        {
            CommitSha = CommitSha[..7];
        }

        Branch = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildBranch"));
        Repository = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildRepository"));
        Configuration = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildConfiguration"));

        var serverUrl = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildServerUrl"));
        var runId = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildRunId"));
        if (serverUrl is not null && Repository is not null && runId is not null)
        {
            WorkflowRunUrl = $"{serverUrl.TrimEnd('/')}/{Repository}/actions/runs/{runId}";
        }

        var eventName = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildEventName"));
        var gitRef = Normalize(metadata.GetValueOrDefault("SharpEmu.BuildRef"));

        var isReleaseConfig = string.Equals(Configuration, "Release", StringComparison.OrdinalIgnoreCase);
        var isCanonicalRepo = string.Equals(Repository, CanonicalRepository, StringComparison.OrdinalIgnoreCase);
        var isReleaseTrigger =
            string.Equals(eventName, "workflow_dispatch", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(eventName, "push", StringComparison.OrdinalIgnoreCase) &&
             string.Equals(gitRef, "refs/heads/main", StringComparison.OrdinalIgnoreCase));

        IsOfficialRelease = isReleaseConfig && isCanonicalRepo && isReleaseTrigger && CommitSha is not null;
    }

    /// <summary>
    /// The multi-line banner, e.g.
    /// <code>
    /// SharpEmu UNOFFICIAL f11ac59 — https://github.com/sharpemu/sharpemu
    ///
    /// Built from branch "main" of "sharpemu/sharpemu" by GitHub Actions workflow run https://github.com/sharpemu/sharpemu/actions/runs/123.
    /// </code>
    /// Official release builds drop the <c>UNOFFICIAL</c> tag. Falls back to a
    /// local-build line when no CI provenance is present.
    /// </summary>
    public static string Banner
    {
        get
        {
            string version;
            if (CommitSha is null)
            {
                version = "UNOFFICIAL";
            }
            else if (IsOfficialRelease)
            {
                version = CommitSha;
            }
            else
            {
                version = $"UNOFFICIAL {CommitSha}";
            }

            var header = $"SharpEmu {version} — {ProjectUrl}";

            if (Branch is null || Repository is null || WorkflowRunUrl is null)
            {
                return $"{header}{Environment.NewLine}{Environment.NewLine}Local build (not produced by CI).";
            }

            return $"{header}{Environment.NewLine}{Environment.NewLine}" +
                   $"Built from branch \"{Branch}\" of \"{Repository}\" by GitHub Actions workflow run {WorkflowRunUrl}.";
        }
    }

    private static Dictionary<string, string> ReadMetadata()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assemblies = new[] { Assembly.GetEntryAssembly(), typeof(BuildInfo).Assembly };

        foreach (var assembly in assemblies)
        {
            if (assembly is null)
            {
                continue;
            }

            foreach (var attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (attribute.Key.StartsWith("SharpEmu.Build", StringComparison.Ordinal) &&
                    !result.ContainsKey(attribute.Key) &&
                    attribute.Value is not null)
                {
                    result[attribute.Key] = attribute.Value;
                }
            }
        }

        return result;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
