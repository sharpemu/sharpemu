// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Patches;

public sealed record GamePatchWrite(ulong Offset, byte[] Bytes)
{
    internal GamePatchPattern? Pattern { get; init; }
}

internal sealed record GamePatchPattern(byte?[] Address, long Offset, byte?[]? Target = null, int JumpSize = 0);

public sealed partial record GamePatch(
    string Id, string FileName, string Name, string Author, string Note,
    string AppVersion, string Executable, IReadOnlyList<GamePatchWrite> Writes, string? Error)
{
    public string PatchVersion { get; init; } = string.Empty;
    public bool IsPatternVersion => string.Equals(AppVersion, "mask", StringComparison.OrdinalIgnoreCase);

    public bool MatchesVersion(string? version) =>
        IsPatternVersion
            ? Error is null && Writes.Count > 0 && Writes.All(write => write.Pattern is not null)
            : !string.IsNullOrWhiteSpace(version) &&
              (string.Equals(AppVersion, version, StringComparison.OrdinalIgnoreCase) ||
               Version.TryParse(AppVersion, out var expected) && Version.TryParse(version, out var actual) && expected == actual);

    internal static GamePatch Parse(string fileName, XElement metadata)
    {
        var id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            fileName + "\n" + metadata.ToString(SaveOptions.DisableFormatting))));
        var name = (string?)metadata.Attribute("Name") ?? string.Empty;
        var author = (string?)metadata.Attribute("Author") ?? string.Empty;
        var note = (string?)metadata.Attribute("Note") ?? string.Empty;
        var version = (string?)metadata.Attribute("AppVer") ?? string.Empty;
        var executable = (string?)metadata.Attribute("AppElf") ?? string.Empty;
        var writes = new List<GamePatchWrite>();
        string? error = null;
        try
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) ||
                string.IsNullOrWhiteSpace(executable) || executable.IndexOfAny(['/', '\\', ':']) >= 0)
            {
                throw new FormatException("Name, AppVer and an AppElf file name are required.");
            }

            var imageBase = Hex((string?)metadata.Attribute("ImageBase") ?? "0");
            var list = metadata.Element("PatchList") ?? throw new FormatException("PatchList is missing.");
            var totalBytes = 0;
            foreach (var line in list.Elements())
            {
                var type = (string?)line.Attribute("Type") ?? string.Empty;
                var isPattern = type is "mask" or "mask_jump32";
                if (line.Name != "Line" || line.HasElements || line.Attributes().Any(attribute =>
                    attribute.Name != "Type" && attribute.Name != "Address" && attribute.Name != "Value" &&
                    !(isPattern && attribute.Name == "Offset") &&
                    !(type == "mask_jump32" && (attribute.Name == "Target" || attribute.Name == "Size"))))
                {
                    throw new FormatException("A patch line contains unsupported elements or attributes.");
                }

                var addressText = (string?)line.Attribute("Address") ?? throw new FormatException("Address is required.");
                var value = (string?)line.Attribute("Value") ?? throw new FormatException("Value is required.");
                var bytes = EncodeValue(type, value);
                var address = isPattern ? imageBase : Hex(addressText);
                var pattern = isPattern ? new GamePatchPattern(ParsePattern(addressText),
                    long.Parse((string?)line.Attribute("Offset") ?? "0", NumberStyles.Integer, CultureInfo.InvariantCulture)) : null;
                if (type == "mask_jump32")
                {
                    var size = int.Parse((string?)line.Attribute("Size") ?? string.Empty, NumberStyles.Integer, CultureInfo.InvariantCulture);
                    if (size is < 5 or > 256) throw new FormatException("Jump Size must be between 5 and 256 bytes.");
                    pattern = pattern! with { Target = ParsePattern((string?)line.Attribute("Target") ?? string.Empty), JumpSize = size };
                }

                totalBytes = checked(totalBytes + bytes.Length + (pattern?.JumpSize > 0 ? pattern.JumpSize + 5 : 0));
                if (address < imageBase || bytes.Length == 0 || totalBytes > 1 << 20 || writes.Count >= 4096)
                {
                    throw new FormatException("A patch line has an invalid address or size.");
                }

                writes.Add(new GamePatchWrite(address - imageBase, bytes) { Pattern = pattern });
            }

            if (writes.Count == 0)
            {
                throw new FormatException("PatchList is empty.");
            }

            if (string.Equals(version, "mask", StringComparison.OrdinalIgnoreCase) && writes.Any(write => write.Pattern is null))
            {
                throw new FormatException("AppVer=mask requires every line to use mask or mask_jump32.");
            }
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or EncoderFallbackException)
        {
            error = exception.Message;
            writes.Clear();
        }

        return new GamePatch(id, fileName, name, author, note, version, executable, writes, error)
        {
            PatchVersion = (string?)metadata.Attribute("PatchVer") ?? string.Empty,
        };
    }

    private static ulong Hex(string value)
    {
        value = value.Trim();
        return ulong.Parse(value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
            NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
    }

    private sealed record PreparedWrite(ulong Address, byte[] Bytes, byte[] Original);

    internal bool TryApply(ICpuMemory memory, IReadOnlyList<ProgramHeader> headers, ulong loadBias, out string? error)
    {
        error = Error;
        if (error is not null || Writes.Count == 0)
        {
            return false;
        }

        var segments = headers.Where(header => header.HeaderType == ProgramHeaderType.Load && header.MemorySize > 0)
            .OrderBy(header => header.VirtualAddress).ToArray();
        var prepared = new List<PreparedWrite>();
        try
        {
            if (segments.Length == 0)
            {
                throw new InvalidDataException("The executable has no loadable segments.");
            }

            var imageBase = checked(loadBias + segments.Min(header => header.VirtualAddress));
            foreach (var write in Writes)
            {
                if (write.Pattern is { } pattern)
                {
                    var match = FindPattern(memory, segments, loadBias, pattern.Address, prepared, pattern.JumpSize > 0);
                    var address = checked((ulong)((Int128)match + pattern.Offset));
                    if (pattern.Target is null)
                    {
                        PrepareWrite(address, write.Bytes);
                    }
                    else
                    {
                        var target = FindPattern(memory, segments, loadBias, pattern.Target, prepared, executableOnly: true);
                        var sourceEnd = checked(address + (ulong)pattern.JumpSize);
                        var targetEnd = checked(target + (ulong)write.Bytes.Length + 5);
                        if (address < targetEnd && target < sourceEnd)
                            throw new InvalidDataException("The branch site and code cave overlap.");

                        var branch = new byte[pattern.JumpSize];
                        branch.AsSpan().Fill(0x90);
                        EncodeJump(branch, address, target);
                        var payload = new byte[write.Bytes.Length + 5];
                        write.Bytes.CopyTo(payload, 0);
                        EncodeJump(payload.AsSpan(write.Bytes.Length), targetEnd - 5, sourceEnd);
                        PrepareWrite(address, branch, executableOnly: true);
                        PrepareWrite(target, payload, executableOnly: true);
                    }
                    continue;
                }

                PrepareWrite(checked(imageBase + write.Offset), write.Bytes);
            }

            void PrepareWrite(ulong address, byte[] bytes, bool executableOnly = false)
            {
                var end = checked(address + (ulong)bytes.Length);
                if (!segments.Any(segment => address >= checked(loadBias + segment.VirtualAddress) &&
                    end <= checked(loadBias + segment.VirtualAddress + segment.MemorySize) &&
                    (!executableOnly || (segment.Flags & ProgramHeaderFlags.Execute) != 0)))
                {
                    throw new InvalidDataException($"Patch range 0x{address:X}-0x{end:X} is outside the executable's segments.");
                }

                var original = new byte[bytes.Length];
                if (!memory.TryRead(address, original))
                {
                    throw new InvalidDataException($"Patch address 0x{address:X} is not readable.");
                }

                prepared.Add(new PreparedWrite(address, bytes, original));
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or OverflowException)
        {
            error = exception.Message;
            return false;
        }

        for (var index = 0; index < prepared.Count; index++)
        {
            var write = prepared[index];
            if (memory.TryWrite(write.Address, write.Bytes))
            {
                continue;
            }

            // Include the failing write: a memory implementation may have written part of it.
            for (var rollback = index; rollback >= 0; rollback--)
            {
                var previous = prepared[rollback];
                if (!memory.TryWrite(previous.Address, previous.Original))
                {
                    throw new InvalidOperationException($"Could not restore patch memory at 0x{previous.Address:X}.");
                }
            }

            error = $"Could not write patch memory at 0x{write.Address:X}; original bytes restored.";
            return false;
        }

        return true;
    }
}
