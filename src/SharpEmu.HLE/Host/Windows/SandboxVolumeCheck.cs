// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SharpEmu.HLE.Host.Windows;

/// <summary>A volume as the sandbox needs it: its file system and whether it stores ACLs (FAT, FAT32 and exFAT do not).</summary>
public sealed record SandboxVolume(string FileSystem, bool SupportsAcls);

/// <summary>One granted path the AppContainer could never open, with the message that tells the user what to do.</summary>
public sealed record SandboxVolumeProblem(SandboxFolder Folder, SandboxVolume Volume, string Message);

/// <summary>
/// The launch failure for folders on a volume without ACLs, typed so the launcher can recognise it (and offer a way
/// out) without parsing the message. <see cref="Exception.Message"/> is the same text the command line prints.
/// </summary>
public sealed class SandboxVolumeException : InvalidOperationException
{
    public SandboxVolumeException(IReadOnlyList<SandboxVolumeProblem> problems)
        : base(SandboxVolumeCheck.Format(problems))
    {
        Problems = problems;
    }

    public IReadOnlyList<SandboxVolumeProblem> Problems { get; }

    /// <summary>The first problem on the game folder, or null when only other folders are affected.</summary>
    public SandboxVolumeProblem? GameProblem => Problems.FirstOrDefault(problem => problem.Folder.IsGame);
}

/// <summary>
/// An AppContainer is allowed into a folder only through an access control entry, and a volume without ACLs cannot
/// hold one: nothing on it can be opened from inside the container. The launch must fail before the process exists,
/// with a message that says why, rather than later with "file not found".
/// </summary>
public static class SandboxVolumeCheck
{
    /// <summary>
    /// The folders of the plan on a volume without ACLs. <paramref name="getVolume"/> maps a path to its volume, or
    /// null when that cannot be told (the grant itself then reports the problem).
    /// </summary>
    public static IReadOnlyList<SandboxVolumeProblem> Find(
        IEnumerable<SandboxFolder> folders, Func<string, SandboxVolume?> getVolume)
    {
        var problems = new List<SandboxVolumeProblem>();
        foreach (var folder in folders)
        {
            if (getVolume(folder.Path) is { SupportsAcls: false } volume)
            {
                problems.Add(new SandboxVolumeProblem(folder, volume, Describe(folder, volume)));
            }
        }

        return problems;
    }

    /// <summary>Throws <see cref="SandboxVolumeException"/> when any folder of the plan is on a volume without ACLs.</summary>
    public static void ThrowIfUnusable(IEnumerable<SandboxFolder> folders, Func<string, SandboxVolume?> getVolume)
    {
        if (Find(folders, getVolume) is { Count: > 0 } problems)
        {
            throw new SandboxVolumeException(problems);
        }
    }

    /// <summary>The single launch error for the problems found, one line each; empty when there are none.</summary>
    public static string Format(IReadOnlyList<SandboxVolumeProblem> problems) =>
        string.Join(" ", problems.Select(problem => problem.Message));

    private static string Describe(SandboxFolder folder, SandboxVolume volume)
    {
        var fileSystem = string.IsNullOrWhiteSpace(volume.FileSystem) ? "file system without ACL support" : volume.FileSystem;
        var advice = folder.IsGame
            ? "Move it to an NTFS drive, convert the game to .zar, or launch without the sandbox."
            : "Use a folder on an NTFS drive for it, or launch without the sandbox.";
        var article = "AEIOUaeiou".Contains(fileSystem[0]) ? "an" : "a";
        return $"The sandbox cannot access {folder.Path} ({folder.Purpose}) on {article} {fileSystem} drive. {advice}";
    }

    /// <summary>The volume holding a path, from GetVolumeInformationW on its root; null when the query fails.</summary>
    [SupportedOSPlatform("windows")]
    public static SandboxVolume? QueryVolume(string path)
    {
        var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        root = System.IO.Path.TrimEndingDirectorySeparator(root) + System.IO.Path.DirectorySeparatorChar;
        var name = new StringBuilder(64);
        if (!GetVolumeInformationW(root, null, 0, out _, out _, out var flags, name, name.Capacity))
        {
            return null;
        }

        return new SandboxVolume(name.ToString(), (flags & FilePersistentAcls) != 0);
    }

    private const uint FilePersistentAcls = 0x00000008;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetVolumeInformationW(
        string rootPathName,
        StringBuilder? volumeNameBuffer,
        int volumeNameSize,
        out uint volumeSerialNumber,
        out uint maximumComponentLength,
        out uint fileSystemFlags,
        StringBuilder fileSystemNameBuffer,
        int fileSystemNameSize);
}
