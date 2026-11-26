// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SharpEmu.HLE.FileSystem;

namespace SharpEmu.HLE.Host.Windows;

public enum SandboxAccess
{
    ReadExecute,
    ReadWrite,
    /// <summary>
    /// Read attributes of this one folder, nothing below it and no listing. It makes a folder that only holds
    /// granted subfolders (user/savedata) answer "exists" instead of "access denied" when the runtime creates it.
    /// </summary>
    AttributesOnly = -1,
}

/// <summary>One path the sandboxed emulator may touch and how. A file entry covers that file alone.</summary>
public sealed record SandboxFolder(
    string Path, SandboxAccess Access, string Purpose, bool IsFile = false, bool IsGame = false)
{
    public string Describe() =>
        $"{Label(Access)} {Path} ({Purpose})";

    private static string Label(SandboxAccess access) => access switch
    {
        SandboxAccess.ReadWrite => "RW+Low",
        SandboxAccess.AttributesOnly => "attrs",
        _ => "RX",
    };
}

/// <summary>What the launcher knows about this launch; the plan is derived from it alone.</summary>
/// <param name="ExecutableDirectory">The folder of the emulator executable the child starts.</param>
/// <param name="DataRoot">The user data root (SharpEmuPaths.DataRoot) the parent resolved.</param>
/// <param name="EbootPath">The game's eboot.bin (or .zar archive), when known.</param>
/// <param name="LogFilePath">The log file the child mirrors its console into, when it opens one.</param>
/// <param name="Arguments">The child's command line, searched for the GNM session file.</param>
/// <param name="GetVariable">Reads the environment variables the child will inherit.</param>
public sealed record SandboxInputs(
    string ExecutableDirectory,
    string DataRoot,
    string? EbootPath,
    string? LogFilePath,
    IReadOnlyList<string> Arguments,
    Func<string, string?> GetVariable);

/// <summary>
/// The folders an AppContainer child gets and the environment that makes it use exactly those folders. Inside the
/// container the user's profile folders (AppData, Documents, Temp) are not reachable, so every path the emulator
/// writes is resolved by the parent and handed over explicitly. <see cref="InheritedFiles"/> are files the child
/// cannot be granted (an exFAT drive has no ACLs) or needs no grant for: the parent opens them and passes the handles.
/// </summary>
public sealed record SandboxPlan(
    IReadOnlyList<SandboxFolder> Folders,
    IReadOnlyDictionary<string, string> Environment,
    string TempDirectory,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> InheritedFiles)
{
    public const string UserDirectoryVariable = "SHARPEMU_USER_DIR";

    // Variables naming a folder the emulator writes when set, with what each one is for.
    private static readonly (string Name, string Purpose)[] WritableDirectoryVariables =
    [
        ("SHARPEMU_SAVEDATA_DIR", "save data"),
        ("SHARPEMU_TEMP0_DIR", "/temp0"),
        ("SHARPEMU_DOWNLOAD0_DIR", "/download0"),
        ("SHARPEMU_HOSTAPP_DIR", "/hostapp"),
        ("SHARPEMU_DEVLOG_APP_DIR", "/devlog/app"),
        ("SHARPEMU_SHADER_ANALYSIS_CACHE_PATH", "shader analysis cache"),
        ("SHARPEMU_AMPR_INDEX_CACHE", "APR index cache"),
        ("__GL_SHADER_DISK_CACHE_PATH", "driver shader cache"),
    ];

    /// <summary>
    /// The launch error when the game the launcher was given does not exist, else null. Without it a missing .zar would
    /// be planned as a game folder and fail later on the volume check with a message about the drive.
    /// </summary>
    public static string? FindMissingGame(SandboxInputs inputs)
    {
        if (string.IsNullOrWhiteSpace(inputs.EbootPath))
        {
            return null;
        }

        var ebootPath = System.IO.Path.GetFullPath(inputs.EbootPath);
        return File.Exists(ebootPath) || Directory.Exists(ebootPath) ? null : $"Game file not found: {ebootPath}";
    }

    public static SandboxPlan Build(SandboxInputs inputs)
    {
        var executableDirectory = Normalize(inputs.ExecutableDirectory);
        var dataRoot = Normalize(inputs.DataRoot);
        var userDirectory = System.IO.Path.Combine(dataRoot, "user");
        var tempDirectory = System.IO.Path.Combine(userDirectory, "sandbox-temp");
        var builder = new Builder();
        var environment = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var inheritedFiles = new List<string>();
        builder.Add(executableDirectory, SandboxAccess.ReadExecute, "emulator folder");
        var game = AddGame(builder, inputs, executableDirectory, inheritedFiles);

        // Everything the emulator keeps (settings read, saves, caches, logs, mods, temp) lives under user/. The data
        // root itself is not granted: it may be the emulator folder (read-only here) or a folder of the user's own.
        // The folders keyed by title id are granted for the launched game only, so a game cannot read or damage the
        // saves of another one; without a title id the whole folder is granted, as before.
        var userWarnings = new List<string>();
        AddUserFolders(builder, inputs, userDirectory, game, userWarnings);
        if (!string.Equals(dataRoot, executableDirectory, StringComparison.OrdinalIgnoreCase) &&
            System.IO.Path.Combine(dataRoot, "gui-settings.json") is { } settingsFile && File.Exists(settingsFile))
        {
            builder.Add(settingsFile, SandboxAccess.ReadExecute, "launcher settings", isFile: true);
        }

        foreach (var (name, purpose) in WritableDirectoryVariables)
        {
            var value = inputs.GetVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && value != "0")
            {
                builder.Add(Normalize(value), SandboxAccess.ReadWrite, $"{name}: {purpose}");
            }
        }

        // The pipeline cache variable names a file; its linked, binaries, analysis, translations and driver
        // siblings sit beside it.
        if (inputs.GetVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH") is { } cachePath && !string.IsNullOrWhiteSpace(cachePath))
        {
            var directory = System.IO.Path.GetDirectoryName(Normalize(System.Environment.ExpandEnvironmentVariables(cachePath)));
            if (!string.IsNullOrEmpty(directory))
            {
                builder.Add(directory, SandboxAccess.ReadWrite, "SHARPEMU_VK_PIPELINE_CACHE_PATH: pipeline cache");
            }
        }

        if (!string.IsNullOrWhiteSpace(inputs.LogFilePath) &&
            System.IO.Path.GetDirectoryName(Normalize(inputs.LogFilePath)) is { Length: > 0 } logDirectory)
        {
            builder.Add(logDirectory, SandboxAccess.ReadWrite, "log file");
        }

        // The parent keeps the session file of a compatibility-mode (GNM) game in its own temp folder, and the
        // child writes its restart request beside it; that one session folder is all it needs of the real temp.
        foreach (var argument in inputs.Arguments)
        {
            const string sessionPrefix = "--gnm-session=";
            if (argument.StartsWith(sessionPrefix, StringComparison.Ordinal) &&
                System.IO.Path.GetDirectoryName(Normalize(argument[sessionPrefix.Length..])) is { Length: > 0 } sessionDirectory)
            {
                builder.Add(sessionDirectory, SandboxAccess.ReadWrite, "GNM LoadExec session");
            }
        }

        environment["TEMP"] = tempDirectory;
        environment["TMP"] = tempDirectory;
        environment[UserDirectoryVariable] = dataRoot;
        // Without it the child would look for the cache in %LOCALAPPDATA%, which the container hides.
        if (string.IsNullOrWhiteSpace(inputs.GetVariable("SHARPEMU_AMPR_INDEX_CACHE")))
        {
            environment["SHARPEMU_AMPR_INDEX_CACHE"] = System.IO.Path.Combine(userDirectory, "ampr-index");
        }

        var warnings = new List<string>(builder.Warnings);
        warnings.AddRange(userWarnings);
        if (inputs.GetVariable("SHARPEMU_WRITABLE_APP0") == "1")
        {
            warnings.Add("SHARPEMU_WRITABLE_APP0=1: the game folder is read-only in the sandbox, guest writes under /app0 will fail");
        }

        if (inputs.GetVariable("SHARPEMU_CRASH_CAPTURE") == "1")
        {
            warnings.Add("SHARPEMU_CRASH_CAPTURE=1: the crash helper process cannot start under the one-process job limit");
        }

        return new SandboxPlan(builder.Folders, environment, tempDirectory, warnings, inheritedFiles);
    }

    public string Describe()
    {
        var text = new StringBuilder();
        text.Append(string.Join("; ", Folders.Select(folder => folder.Describe())));
        text.Append("; TEMP=TMP=").Append(TempDirectory);
        foreach (var file in InheritedFiles)
        {
            text.Append("; handle ").Append(file).Append(" (game archive, opened by the launcher)");
        }

        foreach (var warning in Warnings)
        {
            text.Append("; WARNING ").Append(warning);
        }

        return text.ToString();
    }

    /// <summary>What the plan learned about the launched game: its title id and the folder name of its app0.</summary>
    private sealed record GameInfo(string? TitleId, string? AppName);

    // Folders of user/ the runtime writes (or reads) directly. Each one is created by the launcher when it is writable.
    private static void AddUserFolders(
        Builder builder, SandboxInputs inputs, string userDirectory, GameInfo game, List<string> warnings)
    {
        if (game.TitleId is not { } titleId)
        {
            builder.Add(userDirectory, SandboxAccess.ReadWrite, "user data: saves, caches, logs, mods, temp");
            warnings.Add("The title id of the game could not be read (no sce_sys/param.json with a titleId): " +
                "the whole user folder is writable, so the saves of other games are reachable");
            return;
        }

        var saveSegment = SandboxTitleId.SaveDataSegment(titleId);
        var cacheSegment = SandboxTitleId.CacheSegment(titleId);
        string User(params string[] parts) => System.IO.Path.Combine([userDirectory, .. parts]);

        // A configured save or pipeline cache location replaces the default one: the child never touches the
        // default folder, and the configured folder is granted by the variables below.
        if (string.IsNullOrWhiteSpace(inputs.GetVariable("SHARPEMU_SAVEDATA_DIR")))
        {
            builder.Add(User("savedata", saveSegment), SandboxAccess.ReadWrite, $"saves of {titleId}");
            // sceSaveDataInitialize creates the save root; without this entry that fails with "access denied"
            // inside the container even though the folder exists, and the entry reveals no title folder.
            builder.Add(User("savedata"), SandboxAccess.AttributesOnly, "save root exists check");
        }

        if (string.IsNullOrWhiteSpace(inputs.GetVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")))
        {
            builder.Add(User("pipeline_cache", cacheSegment), SandboxAccess.ReadWrite, $"pipeline cache of {titleId}");
        }

        builder.Add(User("game_logs", cacheSegment), SandboxAccess.ReadWrite, $"game logs of {titleId}");
        builder.Add(User("temp", game.AppName ?? "default"), SandboxAccess.ReadWrite, "per-game scratch (/download0)");
        builder.Add(User("logs"), SandboxAccess.ReadWrite, "logs");
        builder.Add(User("sandbox-temp"), SandboxAccess.ReadWrite, "sandbox temp");
        builder.Add(User("ampr-index"), SandboxAccess.ReadWrite, "APR index cache");
        builder.Add(User("redzone_cache"), SandboxAccess.ReadWrite, "patched code cache");

        // Read by the runtime, written by the launcher.
        foreach (var directory in new[] { User("mods"), User("patches") })
        {
            if (Directory.Exists(directory))
            {
                builder.Add(directory, SandboxAccess.ReadExecute, System.IO.Path.GetFileName(directory));
            }
        }

        if (File.Exists(User("entitlements.json")))
        {
            builder.Add(User("entitlements.json"), SandboxAccess.ReadExecute, "add-on entitlements", isFile: true);
        }
    }

    private static GameInfo AddGame(
        Builder builder, SandboxInputs inputs, string executableDirectory, List<string> inheritedFiles)
    {
        string? app0Root = null;
        string? archiveName = null;
        if (!string.IsNullOrWhiteSpace(inputs.EbootPath))
        {
            var ebootPath = System.IO.Path.GetFullPath(inputs.EbootPath);
            if (ZArchive.IsArchivePath(ebootPath))
            {
                // The launcher opens the archive and the child reads it through the inherited handle: no ACE, so it
                // works on any drive. The runtime mounts it at a folder under user/zar-cache beside the executable.
                inheritedFiles.Add(ebootPath);
                archiveName = System.IO.Path.GetFileNameWithoutExtension(ebootPath);
                builder.Add(System.IO.Path.Combine(executableDirectory, "user", "zar-cache"), SandboxAccess.ReadWrite, "archive cache");
            }
            else if (System.IO.Path.GetDirectoryName(ebootPath) is { Length: > 0 } gameDirectory)
            {
                app0Root = gameDirectory;
                builder.Add(gameDirectory, SandboxAccess.ReadExecute, "game folder (app0)", isGame: true);
                // SharpEmuRuntime mounts /app0 at the parent of a decrypted sidecar folder that sits beside Media.
                if (string.Equals(System.IO.Path.GetFileName(gameDirectory), "decrypted", StringComparison.OrdinalIgnoreCase) &&
                    System.IO.Path.GetDirectoryName(gameDirectory) is { Length: > 0 } parent &&
                    Directory.Exists(System.IO.Path.Combine(parent, "Media")))
                {
                    app0Root = parent;
                    builder.Add(parent, SandboxAccess.ReadExecute, "game folder (parent of decrypted)", isGame: true);
                }
            }
        }

        if (inputs.GetVariable("SHARPEMU_APP0_DIR") is { } configuredApp0 && !string.IsNullOrWhiteSpace(configuredApp0))
        {
            app0Root = Normalize(configuredApp0);
            builder.Add(app0Root, SandboxAccess.ReadExecute, "SHARPEMU_APP0_DIR", isGame: true);
        }

        // Add-on content: SHARPEMU_ADDCONT_DIR, else the addcont folder beside the game folder, when there is one.
        var addcont = inputs.GetVariable("SHARPEMU_ADDCONT_DIR");
        if (!string.IsNullOrWhiteSpace(addcont))
        {
            builder.Add(Normalize(addcont), SandboxAccess.ReadExecute, "SHARPEMU_ADDCONT_DIR");
        }
        else if (app0Root is not null &&
            Normalize(System.IO.Path.Combine(app0Root, "..", "addcont")) is { } sibling && Directory.Exists(sibling))
        {
            builder.Add(sibling, SandboxAccess.ReadExecute, "add-on content");
        }

        // The runtime names the per-game scratch folder after the app0 folder, invalid characters replaced.
        var appName = archiveName ?? (app0Root is null ? null : System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(app0Root)));
        if (!string.IsNullOrWhiteSpace(appName))
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            appName = new string([.. appName.Select(character => Array.IndexOf(invalid, character) >= 0 ? '_' : character)]);
        }
        else
        {
            appName = null;
        }

        return new GameInfo(SandboxTitleId.Read(inputs.EbootPath, app0Root), appName);
    }

    private static string Normalize(string path) =>
        System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));

    internal static bool IsUnder(string parent, string child) =>
        child.StartsWith(parent + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    // Collects folders without duplicates: a folder inside one that already gives the same access (or more) adds
    // nothing, and a read-write folder swallows the read-only folders inside it.
    private sealed class Builder
    {
        private readonly List<SandboxFolder> _folders = [];
        private readonly List<string> _warnings = [];

        public IReadOnlyList<SandboxFolder> Folders => _folders;

        public IReadOnlyList<string> Warnings => _warnings;

        public void Add(string path, SandboxAccess access, string purpose, bool isFile = false, bool isGame = false)
        {
            foreach (var existing in _folders)
            {
                var same = string.Equals(existing.Path, path, StringComparison.OrdinalIgnoreCase);
                if (same && existing.IsFile == isFile)
                {
                    if (access == SandboxAccess.ReadWrite && existing.Access == SandboxAccess.ReadExecute)
                    {
                        _folders[_folders.IndexOf(existing)] = new SandboxFolder(path, access, purpose, isFile, existing.IsGame || isGame);
                        _warnings.Add($"{path} is both read-only and writable: it is writable");
                    }
                    else if (access > existing.Access)
                    {
                        _folders[_folders.IndexOf(existing)] = new SandboxFolder(path, access, purpose, isFile, existing.IsGame || isGame);
                    }

                    return;
                }

                // A folder grant covers what lies inside it when it gives the same or more; an attributes-only entry
                // covers nothing below it.
                if (!existing.IsFile && existing.Access != SandboxAccess.AttributesOnly && IsUnder(existing.Path, path) &&
                    (existing.Access == SandboxAccess.ReadWrite || access != SandboxAccess.ReadWrite))
                {
                    if (existing.Access == SandboxAccess.ReadWrite && access == SandboxAccess.ReadExecute)
                    {
                        _warnings.Add($"{path} ({purpose}) lies inside the writable folder {existing.Path}");
                    }

                    return;
                }
            }

            if (!isFile)
            {
                foreach (var inside in _folders
                    .Where(folder => IsUnder(path, folder.Path) && access != SandboxAccess.AttributesOnly &&
                        (access == SandboxAccess.ReadWrite || folder.Access != SandboxAccess.ReadWrite))
                    .ToArray())
                {
                    _folders.Remove(inside);
                    if (access == SandboxAccess.ReadWrite && inside.Access == SandboxAccess.ReadExecute)
                    {
                        _warnings.Add($"{inside.Path} ({inside.Purpose}) lies inside the writable folder {path}");
                    }
                }
            }

            _folders.Add(new SandboxFolder(path, access, purpose, isFile, isGame));
        }
    }
}

/// <summary>One path the sandbox added an access control entry (and for writable folders a Low label) to.</summary>
public sealed record SandboxGrantEntry(string Path, SandboxAccess Access, bool IsFile, bool Labeled);

/// <summary>
/// What a launch changed on disk, kept in the user folder so <c>--sandbox-cleanup</c> knows what to revoke. Every
/// launch adds to it; cleanup removes the file.
/// </summary>
public sealed record SandboxGrantRecord(string ProfileName, IReadOnlyList<SandboxGrantEntry> Entries)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string FilePath(string dataRoot) =>
        System.IO.Path.Combine(dataRoot, "user", "sandbox-grants.json");

    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);

    public static SandboxGrantRecord FromJson(string json) =>
        JsonSerializer.Deserialize<SandboxGrantRecord>(json, JsonOptions)
            ?? throw new JsonException("The sandbox grant record is empty.");

    /// <summary>The record without the folder entry at this path (revoked by a launch that no longer needs it).</summary>
    public SandboxGrantRecord Without(string path) =>
        this with
        {
            Entries = [.. Entries.Where(entry => entry.IsFile || !string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))],
        };

    /// <summary>Adds entries; a path already recorded keeps the wider access and any label.</summary>
    public SandboxGrantRecord Merge(IEnumerable<SandboxGrantEntry> added)
    {
        var merged = new List<SandboxGrantEntry>(Entries);
        foreach (var entry in added)
        {
            var index = merged.FindIndex(known =>
                known.IsFile == entry.IsFile && string.Equals(known.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                merged.Add(entry);
                continue;
            }

            var known = merged[index];
            merged[index] = known with
            {
                Access = known.Access == SandboxAccess.ReadWrite || entry.Access == SandboxAccess.ReadWrite
                    ? SandboxAccess.ReadWrite
                    : SandboxAccess.ReadExecute,
                Labeled = known.Labeled || entry.Labeled,
            };
        }

        return this with { Entries = merged };
    }
}
