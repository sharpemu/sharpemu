// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Np;

// The add-on (DLC) entitlements a title owns offline. Real hardware reads them from
// the signed-in PSN account; offline the emulator derives them from editable data
// instead of hard-coding a global list:
//
// - user/entitlements.json maps a title id to its owned labels, for example
//   { "PPSA04264": ["85y-je", "5d5c48", "_mtqu6"] }. The built-in defaults below
//   only serve as the fallback for a missing file, so they can be changed per
//   machine without rebuilding.
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

    // Built-in defaults for the titles the emulator was tested with: GTA V Enhanced
    // gates Story Mode on its three labels and Ghost of Yotei checks its application
    // and base game entitlements before it creates the frontend. A user/entitlements.json
    // entry overrides them.
    private static readonly Dictionary<string, string[]> DefaultLabels = new()
    {
        ["PPSA04264"] = ["85y-je", "5d5c48", "_mtqu6"],
        ["PPSA26344"] = ["GHOST2APP0000000", "GHOST2BASE000000"],
    };

    private static string? _cachedTitle;
    private static Entitlement[] _cachedOwned = [];

    // The labels the running title owns, merged from the configuration file, the
    // built-in defaults and the installed addcont packages. Cached per title.
    public static IReadOnlyList<Entitlement> Owned()
    {
        var title = KernelMemoryCompatExports.ApplicationTitleId;
        if (string.Equals(Volatile.Read(ref _cachedTitle), title, StringComparison.Ordinal))
        {
            return _cachedOwned;
        }

        var labels = new List<string>();
        if (TryLoadConfiguredLabels(title, out var configured))
        {
            labels.AddRange(configured);
        }
        else if (DefaultLabels.TryGetValue(title, out var defaults))
        {
            labels.AddRange(defaults);
        }

        labels.AddRange(EnumerateAddcontLabels());

        var owned = labels
            .Select(NormalizeLabel)
            .Where(label => label.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .Select(label => new Entitlement(label, PackageTypePsal, DownloadStatusInstalled))
            .ToArray();

        Volatile.Write(ref _cachedTitle, title);
        _cachedOwned = owned;
        return owned;
    }

    // The host folder add-on packages are read from: an explicit variable, else the
    // addcont folder next to the game image (the PS5 dump layout).
    public static string AddcontRoot()
    {
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
        hostRoot = Path.Combine(AddcontRoot(), NormalizeLabel(label));
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
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryLoadConfiguredLabels(string title, out string[] labels)
    {
        labels = [];
        var path = Path.Combine(SharpEmuPaths.DataRoot, "user", EntitlementsFileName);
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            if (!document.RootElement.TryGetProperty(title, out var element) ||
                element.ValueKind != JsonValueKind.Array)
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
