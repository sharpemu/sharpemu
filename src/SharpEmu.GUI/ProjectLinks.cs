// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.GUI;

internal static class ProjectLinks
{
    private const string RepositorySlug = "sharpemu/sharpemu";

    public static string RepositoryUrl => $"https://github.com/{RepositorySlug}";

    public static string LatestCommitApiUrl =>
        $"https://api.github.com/repos/{RepositorySlug}/commits/main";

    public static string CommitUrl(string sha) => $"{RepositoryUrl}/commit/{sha}";

    public static string DocumentUrl(string path) =>
        $"{RepositoryUrl}/blob/main/{path.TrimStart('/')}";
}
