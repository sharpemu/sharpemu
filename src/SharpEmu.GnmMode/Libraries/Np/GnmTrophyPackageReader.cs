// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using SharpEmu.Libs.Np;

namespace SharpEmu.GnmMode.Libraries.Np;

public sealed class GnmTrophyPackageReader : ITrophyPackageReader
{
    public const string KeyEnvironmentName = "SHARPEMU_GNM_TROPHY_KEY";
    private readonly byte[] _key;
    public string ConfigurationStamp { get; }

    public GnmTrophyPackageReader(string? key)
    {
        _key = TryNormalizeKey(key, out var normalized) && normalized.Length != 0 ? Convert.FromHexString(normalized) : [];
        ConfigurationStamp = Convert.ToHexString(SHA256.HashData(_key));
    }

    public static bool TryNormalizeKey(string? key, out string normalized)
    {
        normalized = key?.Trim().ToUpperInvariant() ?? string.Empty;
        return normalized.Length == 0 || normalized.Length == 32 && normalized.All(char.IsAsciiHexDigit);
    }

    public static void Configure(string? key) => TrophyCatalog.CompatibilityReader = new GnmTrophyPackageReader(key);

    public IReadOnlyList<TrophyEntry> Load(string gameDirectory, bool includeIcons, string? language) =>
        LoadSet(gameDirectory, includeIcons, language).Trophies;

    internal sealed record TrophySet(string Title, string Description, TrophyEntry[] Trophies)
    {
        public static TrophySet Empty { get; } = new(string.Empty, string.Empty, []);
    }

    internal TrophySet LoadSet(string gameDirectory, bool includeIcons, string? language)
    {
        try
        {
            using var archive = new Archive(gameDirectory);
            var configuration = ReadXml(archive, "TROPCONF", gameDirectory) ?? ReadXml(archive, "TROP", gameDirectory);
            if (configuration?.Root?.Name.LocalName != "trophyconf")
                return TrophySet.Empty;
            var languageIndex = Enumerable.Range(0, 31).FirstOrDefault(i =>
                TrophyLanguage.FromSystemLanguage(i).Equals(language, StringComparison.OrdinalIgnoreCase), 1);
            var text = ReadXml(archive, $"TROP_{languageIndex:D2}", gameDirectory) ??
                ReadXml(archive, "TROP_01", gameDirectory) ?? ReadXml(archive, "TROP", gameDirectory) ?? configuration;
            var localized = text.Root!.Elements("trophy").ToDictionary(t => Number(t, "id"));
            List<TrophyEntry> trophies = [];
            foreach (var definition in configuration.Root.Elements("trophy"))
            {
                var id = Number(definition, "id");
                var type = (string?)definition.Attribute("ttype");
                var grade = type is { Length: 1 } ? "PGSB".IndexOf(type[0]) + 1 : 0;
                if (id is < 0 or >= 128 || grade is < 1 or > 4 || trophies.Any(t => t.Id == id))
                    throw new InvalidDataException("Invalid trophy definition.");
                var labels = localized.GetValueOrDefault(id) ?? definition;
                trophies.Add(new TrophyEntry(id, grade, (string?)labels.Element("name") ?? string.Empty,
                    (string?)labels.Element("detail") ?? string.Empty, (string?)definition.Attribute("hidden") == "yes",
                    false, null, includeIcons ? archive.Read($"TROP{id:D3}.PNG") : null)
                {
                    GroupId = Number(definition, "gid", 0) is var group && group == 0 ? -1 : group,
                    PlatinumId = Number(definition, "pid", -1),
                });
            }
            return new TrophySet((string?)text.Root.Element("title-name") ?? string.Empty,
                (string?)text.Root.Element("title-detail") ?? string.Empty, trophies.OrderBy(t => t.Id).ToArray());
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or XmlException
            or ArgumentException or InvalidOperationException or OverflowException)
        {
            return TrophySet.Empty;
        }
    }

    internal static byte[]? ReadIcon(string gameDirectory, string name)
    {
        try
        {
            using var archive = new Archive(gameDirectory);
            return archive.Read(name);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    private XDocument? ReadXml(Archive archive, string name, string gameDirectory)
    {
        var payload = archive.Read(name + ".SFM");
        if (payload is null)
        {
            payload = archive.Read(name + ".ESFM");
            if (payload is null || _key.Length != 16)
                return null;
            if (payload.Length < 32 || payload.Length % 16 != 0)
                throw new InvalidDataException("Invalid encrypted trophy metadata.");
            using var aes = Aes.Create();
            aes.Key = _key;
            var trpKey = aes.EncryptCbc(ReadCommunicationId(gameDirectory), new byte[16], PaddingMode.None);
            aes.Key = trpKey;
            payload = aes.DecryptCbc(payload.AsSpan(16), payload.AsSpan(0, 16), PaddingMode.None);
        }
        var end = Array.LastIndexOf(payload, (byte)'>');
        if (end < 0)
            throw new InvalidDataException("Invalid trophy metadata.");
        using var stream = new MemoryStream(payload, 0, end + 1, writable: false);
        using var reader = XmlReader.Create(stream, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 8 * 1024 * 1024,
        });
        return XDocument.Load(reader);
    }

    private static byte[] ReadCommunicationId(string gameDirectory)
    {
        using var stream = File.OpenRead(Path.Combine(gameDirectory, "sce_sys", "npbind.dat"));
        Span<byte> header = stackalloc byte[128];
        stream.ReadExactly(header);
        if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0xD294A018 ||
            BinaryPrimitives.ReadUInt64BigEndian(header[24..]) == 0)
            throw new InvalidDataException("Invalid trophy binding.");
        Span<byte> entry = stackalloc byte[4];
        stream.ReadExactly(entry);
        var length = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
        if (BinaryPrimitives.ReadUInt16BigEndian(entry) != 0x10 || length is < 12 or > 16)
            throw new InvalidDataException("Invalid trophy communication ID.");
        var id = new byte[16];
        stream.ReadExactly(id.AsSpan(0, length));
        if (!Encoding.ASCII.GetString(id).StartsWith("NPWR", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid trophy communication ID.");
        return id;
    }

    private static int Number(XElement element, string attribute, int fallback = -1) =>
        int.TryParse((string?)element.Attribute(attribute), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    private sealed class Archive : IDisposable
    {
        private readonly FileStream _stream;
        private readonly Dictionary<string, (long Offset, int Size)> _entries = new(StringComparer.Ordinal);

        public Archive(string gameDirectory)
        {
            _stream = File.OpenRead(Path.Combine(gameDirectory, "sce_sys", "trophy", "trophy00.trp"));
            try
            {
                Span<byte> header = stackalloc byte[96];
                _stream.ReadExactly(header);
                var count = BinaryPrimitives.ReadUInt32BigEndian(header[16..]);
                var entrySize = BinaryPrimitives.ReadUInt32BigEndian(header[20..]);
                if (BinaryPrimitives.ReadUInt32BigEndian(header) != 0xDCA24D00 ||
                    BinaryPrimitives.ReadUInt32BigEndian(header[4..]) is < 1 or > 3 || count > 4096 || entrySize != 64 ||
                    BinaryPrimitives.ReadUInt64BigEndian(header[8..]) != (ulong)_stream.Length || 96L + count * 64L > _stream.Length)
                    throw new InvalidDataException("Invalid trophy archive.");
                Span<byte> entry = stackalloc byte[64];
                for (var i = 0; i < count; i++)
                {
                    _stream.ReadExactly(entry);
                    var name = Encoding.ASCII.GetString(entry[..32]).TrimEnd('\0');
                    var offset = BinaryPrimitives.ReadUInt64BigEndian(entry[32..]);
                    var length = BinaryPrimitives.ReadUInt64BigEndian(entry[40..]);
                    if (length > 16 * 1024 * 1024 || offset < 96UL + count * 64UL || offset > (ulong)_stream.Length ||
                        length > (ulong)_stream.Length - offset || !_entries.TryAdd(name, ((long)offset, (int)length)))
                        throw new InvalidDataException("Invalid trophy archive entry.");
                }
            }
            catch
            {
                _stream.Dispose();
                throw;
            }
        }

        public byte[]? Read(string name)
        {
            if (!_entries.TryGetValue(name, out var entry))
                return null;
            _stream.Position = entry.Offset;
            var data = new byte[entry.Size];
            _stream.ReadExactly(data);
            return data;
        }

        public void Dispose() => _stream.Dispose();
    }
}
