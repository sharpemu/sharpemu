// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using SharpEmu.Core.Loader;
using SharpEmu.HLE;

namespace SharpEmu.Core.Patches;

public sealed partial record GamePatch
{
    private static byte?[] ParsePattern(string text)
    {
        var tokens = text.Split((char[]?)null, 256, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length is 0 or > 255) throw new FormatException("A pattern must contain between 1 and 255 bytes.");
        var pattern = new byte?[tokens.Length];
        for (var index = 0; index < tokens.Length; index++)
        {
            if (tokens[index] is "?" or "??") continue;
            if (tokens[index].Length != 2 || !byte.TryParse(tokens[index], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out var value))
                throw new FormatException("Patterns require two-digit hex bytes or ? / ?? wildcards separated by whitespace.");
            pattern[index] = value;
        }
        if (pattern.All(value => value is null)) throw new FormatException("A pattern must contain a fixed byte.");
        return pattern;
    }

    private static ulong FindPattern(ICpuMemory memory, ProgramHeader[] segments, ulong loadBias, byte?[] pattern,
        List<PreparedWrite> prepared, bool executableOnly)
    {
        var buffer = new byte[64 * 1024];
        var anchor = Array.FindIndex(pattern, value => value.HasValue);
        foreach (var segment in segments)
        {
            if (executableOnly && (segment.Flags & ProgramHeaderFlags.Execute) == 0) continue;
            var start = checked(loadBias + segment.VirtualAddress);
            var end = checked(start + segment.MemorySize);
            for (var address = start; end - address >= (ulong)pattern.Length;)
            {
                var length = (int)Math.Min((ulong)buffer.Length, end - address);
                var data = buffer.AsSpan(0, length);
                if (!memory.TryRead(address, data)) throw new InvalidDataException($"Could not scan patch memory at 0x{address:X}.");

                // Later lines must see earlier staged replacements without modifying guest memory yet.
                foreach (var write in prepared)
                {
                    var overlapStart = Math.Max(address, write.Address);
                    var overlapEnd = Math.Min(address + (ulong)length, write.Address + (ulong)write.Bytes.Length);
                    if (overlapStart >= overlapEnd) continue;
                    write.Bytes.AsSpan((int)(overlapStart - write.Address), (int)(overlapEnd - overlapStart))
                        .CopyTo(data[(int)(overlapStart - address)..]);
                }

                var candidates = data.Slice(anchor, length - pattern.Length + 1);
                var next = 0;
                while (next < candidates.Length)
                {
                    var found = candidates[next..].IndexOf(pattern[anchor]!.Value);
                    if (found < 0) break;
                    var candidate = next + found;
                    var matches = true;
                    for (var index = 0; index < pattern.Length; index++)
                    {
                        if (pattern[index] is { } value && data[candidate + index] != value)
                        {
                            matches = false;
                            break;
                        }
                    }
                    if (matches) return checked(address + (ulong)candidate);
                    next = candidate + 1;
                }
                address = checked(address + (ulong)(length - pattern.Length + 1));
            }
        }
        throw new InvalidDataException("Patch pattern was not found in the target executable's loadable segments.");
    }

    private static void EncodeJump(Span<byte> bytes, ulong source, ulong target)
    {
        var displacement = (Int128)target - ((Int128)source + 5);
        if (displacement < int.MinValue || displacement > int.MaxValue)
            throw new InvalidDataException("Patch branch target is outside the signed 32-bit relative range.");
        bytes[0] = 0xE9;
        BinaryPrimitives.WriteInt32LittleEndian(bytes[1..], (int)displacement);
    }
}
