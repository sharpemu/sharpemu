// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.SaveData;
using SharpEmu.Libs.SystemService;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace SharpEmu.Libs.Np;

internal static class TrophyService
{
    internal const int DetailsSize = 1312;
    internal const int DataSize = 32;

    private const string UnlockTrophyEvent = "_UnlockTrophy";

    internal const string UnlockMarker = "[TROPHY][UNLOCK] ";

    private enum Comparison
    {
        None,
        GreaterEqual,
        Greater,
        LessEqual,
        Less,
    }

    internal sealed class Trophy
    {
        public int Id { get; init; }
        public int Grade { get; init; }
        public int GroupId { get; init; }
        public bool Hidden { get; init; }
        public bool HasReward { get; init; }
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        internal ulong? StatId { get; init; }
        internal ulong? Target { get; init; }
        internal int Comparer { get; init; }
        public byte[]? IconPng { get; init; }
        public bool Unlocked { get; set; }

        public long UnlockedAt { get; set; }

        public bool IsPlatinum => Grade == 1;

        public void WriteDetails(Span<byte> destination)
        {
            destination.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(destination, Id);
            BinaryPrimitives.WriteInt32LittleEndian(destination[4..], Grade);
            BinaryPrimitives.WriteInt32LittleEndian(destination[8..], GroupId);
            destination[12] = Hidden ? (byte)1 : (byte)0;
            destination[13] = HasReward ? (byte)1 : (byte)0;
            WriteText(destination.Slice(32, 128), Name);
            WriteText(destination.Slice(160, 1024), Description);
        }

        public void WriteData(Span<byte> destination)
        {
            destination.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(destination, Id);
            destination[4] = Unlocked ? (byte)1 : (byte)0;
            if (Unlocked)
            {
                BinaryPrimitives.WriteInt64LittleEndian(destination[24..], UnlockedAt);
            }
        }

        private static void WriteText(Span<byte> field, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            bytes.AsSpan(0, Math.Min(bytes.Length, field.Length - 1)).CopyTo(field);
        }
    }

    private readonly record struct StatRule(ulong StatId, string Input, bool IsUInt32);

    private static readonly object Gate = new();
    private static Trophy[]? _trophies;
    private static Dictionary<string, List<StatRule>> _rules = [];
    private static string? _unlockPath;

    internal static Trophy[] Trophies
    {
        get
        {
            lock (Gate)
            {
                EnsureLoaded();
                return _trophies!;
            }
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            _trophies = null;
            _rules = [];
            _unlockPath = null;
        }
    }

    internal static void OnUdsEvent(string eventName, UdsObjectNode root)
    {
        List<Trophy> unlockedNow = [];
        lock (Gate)
        {
            EnsureLoaded();
            if (_trophies!.Length == 0)
            {
                return;
            }

            if (eventName == UnlockTrophyEvent &&
                root.Values.TryGetValue("_trophy_id", out var idValue) &&
                idValue.Value is int unlockId)
            {
                var requested = _trophies.FirstOrDefault(t => t.Id == unlockId);
                if (requested is not null && !requested.Unlocked)
                {
                    Unlock(requested, unlockedNow);
                }
            }

            var rules = _rules.GetValueOrDefault(eventName) ?? [];
            foreach (var trophy in _trophies)
            {
                if (trophy.Unlocked || trophy.IsPlatinum || trophy.StatId is not { } statId ||
                    trophy.Target is not { } target)
                {
                    continue;
                }

                foreach (var rule in rules)
                {
                    if (rule.StatId != statId || !TryReadNumber(root, rule, out var value))
                    {
                        continue;
                    }

                    var matched = (Comparison)trophy.Comparer switch
                    {
                        Comparison.GreaterEqual => value >= target,
                        Comparison.Greater => value > target,
                        Comparison.LessEqual => value <= target,
                        Comparison.Less => value < target,
                        _ => false,
                    };
                    if (matched)
                    {
                        Unlock(trophy, unlockedNow);
                        break;
                    }
                }
            }

            var platinum = _trophies.FirstOrDefault(t => t.IsPlatinum && !t.Unlocked);
            if (platinum is not null && unlockedNow.Count > 0 &&
                _trophies.Where(t => !t.IsPlatinum).All(t => t.Unlocked))
            {
                Unlock(platinum, unlockedNow);
            }

            if (unlockedNow.Count > 0)
            {
                SaveUnlocks();
            }
        }

        foreach (var trophy in unlockedNow)
        {
            Console.Error.WriteLine(
                $"{UnlockMarker}{KernelMemoryCompatExports.ApplicationTitleId}|{trophy.Id}|{trophy.Grade}|{trophy.Name}");
        }
    }

    private static void Unlock(Trophy trophy, List<Trophy> unlockedNow)
    {
        trophy.Unlocked = true;
        trophy.UnlockedAt = DateTime.UtcNow.Ticks / 10;
        unlockedNow.Add(trophy);
    }

    private static bool TryReadNumber(UdsObjectNode root, StatRule rule, out ulong value)
    {
        value = 0;
        if (!root.Values.TryGetValue(rule.Input, out var property))
        {
            return false;
        }

        switch (property.Kind, property.Value)
        {
            case (UdsValueKind.UInt32, uint u32):
                value = u32;
                return true;
            case (UdsValueKind.UInt64, ulong u64) when !rule.IsUInt32:
                value = u64;
                return true;
            case (UdsValueKind.Int32, int i32) when i32 >= 0:
                value = (ulong)i32;
                return true;
            case (UdsValueKind.Int64, long i64) when i64 >= 0:
                value = (ulong)i64;
                return true;
            default:
                return false;
        }
    }

    private static void EnsureLoaded()
    {
        if (_trophies is not null)
        {
            return;
        }

        try
        {
            var trophyPath = KernelMemoryCompatExports.ResolveGuestPath("/app0/sce_sys/trophy2/trophy00.ucp");
            _trophies = File.Exists(trophyPath)
                ? ReadTrophies(File.ReadAllBytes(trophyPath), language: TrophyLanguage.FromSystemLanguage(SystemServiceExports.SystemLanguage))
                : [];
            var udsPath = KernelMemoryCompatExports.ResolveGuestPath("/app0/sce_sys/uds/uds00.ucp");
            _rules = File.Exists(udsPath) ? ReadRules(File.ReadAllBytes(udsPath)) : [];
            _unlockPath = UnlockFilePath(KernelMemoryCompatExports.ApplicationTitleId);
            ApplyUnlocks(_trophies, _unlockPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidOperationException or ArgumentException
                                       or UnauthorizedAccessException)
        {
            _trophies ??= [];
        }
    }

    internal static string UnlockFilePath(string titleId) =>
        Path.Combine(
            Path.GetDirectoryName(SaveDataStorage.Root()) ?? SaveDataStorage.Root(),
            "trophies",
            titleId,
            "unlocks.json");

    internal static void ApplyUnlocks(Trophy[] trophies, string? unlockPath)
    {
        if (unlockPath is null || !File.Exists(unlockPath))
        {
            return;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(unlockPath));
        if (!document.RootElement.TryGetProperty("unlocked", out var unlocked) ||
            unlocked.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var entry in unlocked.EnumerateObject())
        {
            if (int.TryParse(entry.Name, out var id) && entry.Value.TryGetInt64(out var at))
            {
                var trophy = trophies.FirstOrDefault(t => t.Id == id);
                if (trophy is not null)
                {
                    trophy.Unlocked = true;
                    trophy.UnlockedAt = at;
                }
            }
        }
    }

    private static void SaveUnlocks()
    {
        if (_unlockPath is null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_unlockPath)!);
            var unlocked = _trophies!
                .Where(t => t.Unlocked)
                .ToDictionary(t => t.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), t => t.UnlockedAt);
            var temporary = _unlockPath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(new { unlocked }));
            File.Move(temporary, _unlockPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Could not save trophy progress: {ex.Message}");
        }
    }

    private sealed class TrophyPackage
    {
        private readonly Stream _stream;
        private readonly Dictionary<string, (long Offset, int Length)> _files = new(StringComparer.Ordinal);

        public TrophyPackage(Stream stream)
        {
            _stream = stream;
            Span<byte> header = stackalloc byte[0x40];
            if (stream.Length < 0x40 || stream.Read(header) != header.Length ||
                BinaryPrimitives.ReadUInt32BigEndian(header) != 0xB228C60Au)
            {
                return;
            }

            var fileCount = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(header[0x10..]), 4096u);
            var tableStart = (long)BinaryPrimitives.ReadUInt32BigEndian(header[0x14..]) + 0x20;
            var available = Math.Max(0, (stream.Length - tableStart) / 0x40);
            fileCount = (int)Math.Min(fileCount, available);
            var table = new byte[fileCount * 0x40];
            stream.Position = tableStart;
            stream.ReadExactly(table);
            for (var i = 0; i < fileCount; i++)
            {
                var entry = table.AsSpan(i * 0x40, 0x40);
                var nameBytes = entry[..0x20];
                var nameLength = nameBytes.IndexOf((byte)0);
                var name = Encoding.UTF8.GetString(nameBytes[..(nameLength < 0 ? nameBytes.Length : nameLength)]);
                var offset = BinaryPrimitives.ReadUInt64BigEndian(entry[0x20..]);
                var length = BinaryPrimitives.ReadUInt64BigEndian(entry[0x28..]);
                if (offset <= (ulong)stream.Length && length <= (ulong)stream.Length - offset && length <= int.MaxValue)
                {
                    _files[name] = ((long)offset, (int)length);
                }
            }
        }

        public IEnumerable<string> Names => _files.Keys;

        public byte[]? Read(string name)
        {
            if (!_files.TryGetValue(name, out var file))
            {
                return null;
            }

            var bytes = new byte[file.Length];
            _stream.Position = file.Offset;
            _stream.ReadExactly(bytes);
            return bytes;
        }

        public JsonDocument? Json(string name) => Read(name) is { } bytes ? JsonDocument.Parse(bytes) : null;
    }

    internal static Trophy[] ReadTrophies(byte[] package, bool includeIcons = false, string? language = null)
    {
        using var stream = new MemoryStream(package, writable: false);
        return ReadTrophies(stream, includeIcons, language);
    }

    internal static Trophy[] ReadTrophies(Stream stream, bool includeIcons = false, string? language = null)
    {
        var package = new TrophyPackage(stream);
        using var conf = package.Json("tropconf.json");
        if (conf is null || !conf.RootElement.TryGetProperty("trophies", out var list))
        {
            return [];
        }

        var defaultLanguage = conf.RootElement.TryGetProperty("defaultLanguage", out var lang)
            ? lang.GetString() ?? "en-US"
            : "en-US";
        var languages = package.Names
            .Where(name => name.StartsWith("tropmeta_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
            .Select(name => name["tropmeta_".Length..^".json".Length])
            .ToArray();
        var english = TrophyLanguage.Resolve(languages, "en-US") ?? defaultLanguage;
        var texts = ReadTexts(package, english);
        if (TrophyLanguage.Resolve(languages, language) is { } localized && localized != english)
        {
            foreach (var (id, text) in ReadTexts(package, localized))
            {
                if (text.Name.Length != 0)
                {
                    texts[id] = (text.Name, text.Detail.Length != 0 ? text.Detail : texts.GetValueOrDefault(id).Detail ?? string.Empty);
                }
            }
        }

        var result = new SortedDictionary<int, Trophy>();
        foreach (var definition in list.EnumerateArray())
        {
            if (!TryReadNumber(definition, "id", out var idValue) ||
                !definition.TryGetProperty("grade", out var gradeElement) ||
                gradeElement.GetString() is not { Length: 1 } grade)
            {
                continue;
            }

            var gradeIndex = "PGSB".IndexOf(grade[0]);
            if (gradeIndex < 0)
            {
                continue;
            }

            var id = (int)idValue;
            ulong? statId = null;
            ulong? target = null;
            var comparison = Comparison.None;
            if (definition.TryGetProperty("unlockCondition", out var condition) &&
                condition.ValueKind == JsonValueKind.Object)
            {
                statId = TryReadNumber(condition, "udsStatId", out var stat) ? stat : null;
                target = TryReadNumber(condition, "targetValue", out var targetValue) ? targetValue : null;
                comparison = condition.TryGetProperty("comparator", out var comparator)
                    ? comparator.GetString() switch
                    {
                        "ge" => Comparison.GreaterEqual,
                        "gt" => Comparison.Greater,
                        "le" => Comparison.LessEqual,
                        "lt" => Comparison.Less,
                        _ => Comparison.None,
                    }
                    : Comparison.None;
            }

            texts.TryGetValue(id, out var trophyText);
            result[id] = new Trophy
            {
                Id = id,
                Grade = gradeIndex + 1,
                GroupId = TryReadNumber(definition, "groupId", out var group) ? (int)group : 0,
                Hidden = definition.TryGetProperty("hidden", out var hidden) && hidden.ValueKind == JsonValueKind.True,
                HasReward = definition.TryGetProperty("hasReward", out var reward) && reward.ValueKind == JsonValueKind.True,
                Name = trophyText.Name ?? string.Empty,
                Description = trophyText.Detail ?? string.Empty,
                StatId = statId,
                Target = target,
                Comparer = (int)comparison,
                IconPng = includeIcons ? package.Read($"trop{id:D4}.png") : null,
            };
        }

        return [.. result.Values];
    }

    private static Dictionary<int, (string Name, string Detail)> ReadTexts(
        TrophyPackage package,
        string language)
    {
        var texts = new Dictionary<int, (string Name, string Detail)>();
        using var meta = package.Json($"tropmeta_{language}.json");
        if (meta is not null &&
            meta.RootElement.TryGetProperty("metadata", out var metadata) &&
            metadata.TryGetProperty("trophyMetadata", out var trophyTexts))
        {
            foreach (var text in trophyTexts.EnumerateArray())
            {
                if (TryReadNumber(text, "id", out var id))
                {
                    texts[(int)id] = (
                        text.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty,
                        text.TryGetProperty("detail", out var d) ? d.GetString() ?? string.Empty : string.Empty);
                }
            }
        }

        return texts;
    }

    private static Dictionary<string, List<StatRule>> ReadRules(byte[] package)
    {
        var rules = new Dictionary<string, List<StatRule>>(StringComparer.Ordinal);
        using var stream = new MemoryStream(package, writable: false);
        var files = new TrophyPackage(stream);
        using var definitions = files.Json("stats_definition.json");
        using var extraction = files.Json("stats_extraction.json");
        if (definitions is null || extraction is null ||
            !definitions.RootElement.TryGetProperty("statDefinitionArray", out var statArray) ||
            !extraction.RootElement.TryGetProperty("statsExtractionRuleArray", out var ruleArray))
        {
            return rules;
        }

        var stats = new Dictionary<ulong, bool>();
        foreach (var definition in statArray.EnumerateArray())
        {
            if (!TryReadNumber(definition, "statId", out var id) ||
                !definition.TryGetProperty("aggregation", out var aggregation) || aggregation.GetString() != "latest" ||
                !definition.TryGetProperty("dataType", out var type) ||
                type.GetString() is not ("uint32" or "uint64"))
            {
                continue;
            }

            stats[id] = type.GetString() == "uint32";
        }

        foreach (var rule in ruleArray.EnumerateArray())
        {
            if (!rule.TryGetProperty("condition", out var condition) || condition.ValueKind != JsonValueKind.Object ||
                !condition.TryGetProperty("eventName", out var eventName) || eventName.GetString() is not { } name ||
                !rule.TryGetProperty("action", out var action) ||
                !action.TryGetProperty("input", out var input) || input.GetString() is not { } path ||
                !path.StartsWith("$.", StringComparison.Ordinal) || path.Length == 2 ||
                !action.TryGetProperty("output", out var output) ||
                !TryReadNumber(output, "statId", out var statId) ||
                !stats.TryGetValue(statId, out var isUInt32))
            {
                continue;
            }

            if (!rules.TryGetValue(name, out var forEvent))
            {
                rules[name] = forEvent = [];
            }

            forEvent.Add(new StatRule(statId, path[2..], isUInt32));
        }

        return rules;
    }

    private static bool TryReadNumber(JsonElement element, string property, out ulong value)
    {
        value = 0;
        if (!element.TryGetProperty(property, out var field))
        {
            return false;
        }

        return field.ValueKind == JsonValueKind.Number
            ? field.TryGetUInt64(out value)
            : ulong.TryParse(field.GetString(), out value);
    }
}
