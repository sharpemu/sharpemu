// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Frozen;
using System.Text.Json;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Np;

// The add-on (DLC) entitlements a title owns offline. Real hardware reads them from
// the signed-in PSN account. No file in a game dump lists them: Ghost of Yotei's labels
// only exist inside its eboot, and GTA V and Street Fighter 6 enumerate them at run time
// through sceNpEntitlementAccessGetAddcontEntitlementInfoList. Offline the emulator
// therefore derives them from editable data instead of hard-coding a global list:
//
// - user/entitlements.json maps a title id (or a concept id) to its owned labels, for
//   example { "PPSA04264": ["85y-je", "5d5c48", "_mtqu6"] }. A title id entry wins over a
//   concept id entry. The built-in defaults below only serve as the fallback for a
//   missing entry, so they can be changed per machine without rebuilding.
// - The built-in defaults are keyed by the PS5 concept id from param.json, not by title
//   id: a title id is per region, while every regional release of a game (for example
//   PPSA04264 and PPSA04263 for GTA V) shares one concept id and so matches the same row.
// - An addcont directory (SHARPEMU_ADDCONT_DIR, else <app0>/../addcont): every
//   subfolder is one installed add-on package and its folder name is its label.
public static class AddcontEntitlements
{
    public readonly record struct Entitlement(string Label, uint PackageType, uint DownloadStatus);

    // package_type 3 = PSAL (license-style add-on); download_status 4 = INSTALLED.
    private const uint PackageTypePsal = 3;
    private const uint DownloadStatusInstalled = 4;
    private const int EntitlementLabelLength = 16;
    private const string EntitlementsFileName = "entitlements.json";

    // Built-in defaults for the games the emulator was tested with, keyed by concept id:
    // GTA V Enhanced (201930) gates Story Mode on its three labels and Ghost of Yotei
    // (10003923) checks its application and base game entitlements before it creates the
    // frontend. A user/entitlements.json entry overrides them.
    private static readonly Dictionary<string, string[]> DefaultLabels = new()
    {
        ["201930"] = ["85y-je", "5d5c48", "_mtqu6"],
        ["10003923"] = ["GHOST2APP0000000", "GHOST2BASE000000"],
    };

    // The concept id of the running title (null for a PS4 title or a missing param.json).
    private static string? _conceptId;

    // One immutable snapshot per (title, concept id), swapped as a whole so a racing
    // reader never pairs a title with another title's labels.
    private sealed class Snapshot(string title, string? conceptId, Entitlement[] owned)
    {
        public string Title { get; } = title;
        public string? ConceptId { get; } = conceptId;
        public Entitlement[] Owned { get; } = owned;
        public FrozenDictionary<string, Entitlement> ByLabel { get; } =
            owned.ToFrozenDictionary(entitlement => entitlement.Label, StringComparer.Ordinal);
    }

    private static Snapshot? _snapshot;

    // Test seams: the folders the configuration file and the packages are read from.
    internal static string? DataRootOverride { get; set; }
    internal static string? AddcontRootOverride { get; set; }

    internal static void ResetRuntimeState()
    {
        Volatile.Write(ref _conceptId, null);
        Volatile.Write(ref _snapshot, null);
    }

    // Records the concept id of the loaded title; the cached snapshot rebuilds when it changes.
    public static void ConfigureConceptId(string? conceptId) =>
        Volatile.Write(ref _conceptId, string.IsNullOrWhiteSpace(conceptId) ? null : conceptId);

    // Entitlement labels are at most 16 characters of letters, digits, '-' and '_'. The
    // guest chooses the label passed to the mount call, so nothing else may reach a path.
    public static bool IsValidLabel(string? label)
    {
        if (string.IsNullOrEmpty(label) || label.Length > EntitlementLabelLength)
        {
            return false;
        }

        foreach (var character in label)
        {
            if (!(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    // The labels the running title owns, merged from the configuration file, the
    // built-in defaults and the installed addcont packages. Cached per title and concept id.
    public static IReadOnlyList<Entitlement> Owned() => GetSnapshot().Owned;

    public static bool Owns(string label) => GetSnapshot().ByLabel.ContainsKey(label);

    public static bool TryGet(string label, out Entitlement entitlement) =>
        GetSnapshot().ByLabel.TryGetValue(label, out entitlement);

    private static Snapshot GetSnapshot()
    {
        var title = KernelMemoryCompatExports.ApplicationTitleId;
        var conceptId = Volatile.Read(ref _conceptId);
        var current = Volatile.Read(ref _snapshot);
        if (current is not null &&
            string.Equals(current.Title, title, StringComparison.Ordinal) &&
            string.Equals(current.ConceptId, conceptId, StringComparison.Ordinal))
        {
            return current;
        }

        var labels = new List<string>();
        if (TryLoadConfiguredLabels(title, conceptId, out var configured))
        {
            labels.AddRange(configured);
        }
        else if (conceptId is not null && DefaultLabels.TryGetValue(conceptId, out var defaults))
        {
            labels.AddRange(defaults);
        }

        labels.AddRange(EnumerateAddcontLabels());

        var owned = labels
            .Select(NormalizeLabel)
            .Where(IsValidLabel)
            .Distinct(StringComparer.Ordinal)
            .Select(label => new Entitlement(label, PackageTypePsal, DownloadStatusInstalled))
            .ToArray();

        var snapshot = new Snapshot(title, conceptId, owned);
        Volatile.Write(ref _snapshot, snapshot);
        return snapshot;
    }

    // The host folder add-on packages are read from: an explicit variable, else the
    // addcont folder next to the game image (the PS5 dump layout).
    public static string AddcontRoot()
    {
        if (AddcontRootOverride is not null)
        {
            return AddcontRootOverride;
        }

        var configured = Environment.GetEnvironmentVariable("SHARPEMU_ADDCONT_DIR");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        var app0Root = Environment.GetEnvironmentVariable("SHARPEMU_APP0_DIR");
        return string.IsNullOrWhiteSpace(app0Root)
            ? Path.GetFullPath("addcont")
            : Path.GetFullPath(Path.Combine(app0Root, "..", "addcont"));
    }

    // The host folder of one installed add-on package, when it exists.
    public static bool TryResolvePackageRoot(string label, out string hostRoot)
    {
        hostRoot = string.Empty;
        if (!IsValidLabel(label))
        {
            return false;
        }

        hostRoot = Path.Combine(AddcontRoot(), label);
        return Directory.Exists(hostRoot);
    }

    private static string[] EnumerateAddcontLabels()
    {
        var root = AddcontRoot();
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.GetDirectories(root)
                .Select(Path.GetFileName)
                .Where(IsValidLabel)
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryLoadConfiguredLabels(string title, string? conceptId, out string[] labels)
    {
        labels = [];
        var path = Path.Combine(DataRootOverride ?? SharpEmuPaths.DataRoot, "user", EntitlementsFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // The title id entry wins over the concept id entry.
            if (!(root.TryGetProperty(title, out var element) && element.ValueKind == JsonValueKind.Array) &&
                !(conceptId is not null && root.TryGetProperty(conceptId, out element) &&
                  element.ValueKind == JsonValueKind.Array))
            {
                return false;
            }

            labels = element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString())
                .Where(label => !string.IsNullOrWhiteSpace(label))
                .Select(label => label!)
                .ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    // An entitlement label clamped to the field length the guest APIs read and write.
    public static string NormalizeLabel(string label) =>
        label.Length > EntitlementLabelLength ? label[..EntitlementLabelLength] : label;
}
