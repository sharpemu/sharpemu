// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using SharpEmu.Core.Memory;

namespace SharpEmu.Core.Loader;

// Env-driven byte patches for the main executable. SHARPEMU_GUEST_PATCHES is a
// semicolon-separated list of OFFSET=NEWHEX or OFFSET=NEWHEX?OLDHEX entries.
// OFFSET is the ELF virtual address (module-relative); the guest address is
// imageBase + OFFSET. Unset means no-op. Any bad entry fails the launch.
internal static class GuestBytePatcher
{
    public const string EnvironmentVariable = "SHARPEMU_GUEST_PATCHES";

    internal readonly record struct Patch(ulong Offset, byte[] New, byte[]? Old, string Text);

    public static void ApplyFromEnvironment(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase)
    {
        var text = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        Apply(memory, programHeaders, imageBase, Parse(text));
    }

    internal static List<Patch> Parse(string text)
    {
        var patches = new List<Patch>();
        foreach (var rawEntry in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = rawEntry.Trim();
            if (entry.Length == 0)
            {
                continue;
            }

            var equals = entry.IndexOf('=');
            if (equals <= 0)
            {
                throw Bad(entry, "expected OFFSET=NEWHEX[?OLDHEX]");
            }

            var offsetText = entry[..equals].Trim();
            if (offsetText.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                offsetText = offsetText[2..];
            }

            if (!ulong.TryParse(offsetText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var offset))
            {
                throw Bad(entry, "invalid OFFSET");
            }

            var rest = entry[(equals + 1)..];
            var question = rest.IndexOf('?');
            var newText = question < 0 ? rest : rest[..question];
            if (!TryParseHex(newText, out var newBytes) || newBytes.Length == 0)
            {
                throw Bad(entry, "invalid NEWHEX (need non-empty, even-length hex)");
            }

            byte[]? oldBytes = null;
            if (question >= 0)
            {
                if (!TryParseHex(rest[(question + 1)..], out oldBytes) || oldBytes.Length == 0)
                {
                    throw Bad(entry, "invalid OLDHEX (need non-empty, even-length hex)");
                }

                if (oldBytes.Length != newBytes.Length)
                {
                    throw Bad(entry, "OLDHEX and NEWHEX lengths differ");
                }
            }

            patches.Add(new Patch(offset, newBytes, oldBytes, entry));
        }

        return patches;
    }

    internal static void Apply(
        IVirtualMemory memory,
        IReadOnlyList<ProgramHeader> programHeaders,
        ulong imageBase,
        IReadOnlyList<Patch> patches)
    {
        // Validate and snapshot every patch first so a failure applies nothing.
        var originals = new byte[patches.Count][];
        for (var i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];
            var length = (ulong)patch.New.Length;
            if (!IsMapped(programHeaders, patch.Offset, length))
            {
                throw Bad(patch.Text, "offset range is outside the main executable's loaded segments");
            }

            var current = new byte[patch.New.Length];
            if (!memory.TryRead(imageBase + patch.Offset, current))
            {
                throw Bad(patch.Text, $"cannot read guest memory at 0x{imageBase + patch.Offset:X}");
            }

            if (patch.Old is not null && !current.AsSpan().SequenceEqual(patch.Old))
            {
                throw Bad(
                    patch.Text,
                    $"current bytes {Convert.ToHexString(current).ToLowerInvariant()} do not match OLDHEX {Convert.ToHexString(patch.Old).ToLowerInvariant()}");
            }

            originals[i] = current;
        }

        for (var i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];
            var guest = imageBase + patch.Offset;
            // PhysicalVirtualMemory.TryWrite temporarily lifts page protection
            // (execute-only text included) and flushes the instruction cache.
            if (!memory.TryWrite(guest, patch.New))
            {
                throw Bad(patch.Text, $"write to guest 0x{guest:X} failed");
            }

            Console.WriteLine(
                $"[LOADER][PATCH] offset=0x{patch.Offset:X} guest=0x{guest:X} len={patch.New.Length} " +
                $"old={Convert.ToHexString(originals[i]).ToLowerInvariant()} new={Convert.ToHexString(patch.New).ToLowerInvariant()}");
        }
    }

    private static bool IsMapped(IReadOnlyList<ProgramHeader> programHeaders, ulong offset, ulong length)
    {
        for (var i = 0; i < programHeaders.Count; i++)
        {
            var header = programHeaders[i];
            if (header.HeaderType != ProgramHeaderType.Load)
            {
                continue;
            }

            var start = header.VirtualAddress;
            var end = start + header.MemorySize;
            if (offset >= start && length <= end - start && offset - start <= end - start - length)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseHex(string text, out byte[] bytes)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }

        text = string.Concat(text.Where(static c => !char.IsWhiteSpace(c)));
        bytes = [];
        if (text.Length == 0 || (text.Length & 1) != 0)
        {
            return false;
        }

        var result = new byte[text.Length / 2];
        for (var i = 0; i < result.Length; i++)
        {
            if (!byte.TryParse(text.AsSpan(i * 2, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out result[i]))
            {
                return false;
            }
        }

        bytes = result;
        return true;
    }

    private static InvalidDataException Bad(string entry, string reason) =>
        new($"{EnvironmentVariable} entry '{entry}': {reason}");
}
