// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Core.Loader;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;

namespace SharpEmu.GnmMode.Loader;

public sealed partial class GnmSelfLoader
{
    private readonly record struct Symbol(uint Name, byte Info, ushort Section, ulong Value, ulong Size);
    private readonly record struct Relocation(ulong Offset, uint Type, uint Symbol, long Addend);

    private static Symbol[] ReadSymbols(ReadOnlySpan<byte> table)
    {
        if (table.Length % 24 != 0)
            throw new InvalidDataException("PS4 symbol table has an invalid size.");
        var result = new Symbol[table.Length / 24];
        for (var i = 0; i < result.Length; i++)
        {
            var entry = table.Slice(i * 24, 24);
            result[i] = new(BinaryPrimitives.ReadUInt32LittleEndian(entry), entry[4],
                BinaryPrimitives.ReadUInt16LittleEndian(entry[6..]), BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[16..]));
        }
        return result;
    }

    private static List<Relocation> ReadRelocations(GnmLoader layout, ReadOnlySpan<byte> data)
    {
        var result = new List<Relocation>();
        foreach (var (tag, sizeTag) in new[] { (0x6100002FL, 0x61000031L), (0x61000029L, 0x6100002DL) })
        {
            var table = ReadTable(layout, data, tag, sizeTag);
            if (table.Length % 24 != 0)
                throw new InvalidDataException("PS4 relocation table has an invalid size.");
            for (var i = 0; i < table.Length; i += 24)
            {
                var entry = table.AsSpan(i, 24);
                var info = BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]);
                result.Add(new(BinaryPrimitives.ReadUInt64LittleEndian(entry), (uint)info, (uint)(info >> 32),
                    BinaryPrimitives.ReadInt64LittleEndian(entry[16..])));
            }
        }
        return result;
    }

    private Dictionary<ulong, string> CreateStubs(IVirtualMemory memory, IModuleManager? modules,
        List<Relocation> relocations, Symbol[] symbols, byte[] strings)
    {
        var nids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relocation in relocations)
        {
            if (relocation.Type is 0 or 8 or 16 or 17 or 18 or 38 || relocation.Symbol == 0)
                continue;
            var symbol = GetSymbol(symbols, relocation.Symbol);
            if (symbol.Section != 0)
                continue;
            var nid = Nid(ReadString(strings, symbol.Name));
            if ((symbol.Info >> 4) != 2 || modules?.TryGetExport(nid, out _) == true)
                nids.Add(nid);
        }
        var result = new Dictionary<ulong, string>();
        var stubBase = checked(0x700000000000UL - (ulong)_imageIndex++ * 0x1000000);
        if (nids.Count == 0)
            return result;
        var bytes = new byte[checked((int)Align((ulong)nids.Count * 16))];
        var index = 0;
        foreach (var nid in nids)
        {
            bytes[index * 16] = 0xCC;
            bytes[index * 16 + 1] = 0xC3;
            result.Add(stubBase + (ulong)index++ * 16, nid);
        }
        memory.Map(stubBase, (ulong)bytes.Length, 0, bytes, ProgramHeaderFlags.Read | ProgramHeaderFlags.Execute);
        return result;
    }

    private static List<ImportedSymbolRelocation> ApplyRelocations(IVirtualMemory memory, ulong imageBase, uint tlsId,
        List<Relocation> relocations, Symbol[] symbols, byte[] strings, Dictionary<ulong, string> stubs)
    {
        var imported = new List<ImportedSymbolRelocation>();
        var byNid = stubs.ToDictionary(entry => entry.Value, entry => entry.Key, StringComparer.Ordinal);
        Span<byte> valueBytes = stackalloc byte[8];
        foreach (var relocation in relocations)
        {
            var type = relocation.Type;
            if (type == 0)
                continue;
            var target = checked(imageBase + relocation.Offset);
            var symbol = GetSymbol(symbols, relocation.Symbol);
            var value = 0UL;
            var addend = type is 6 or 7 ? 0 : relocation.Addend;
            if (type is 8 or 38)
                value = Add(imageBase, addend);
            else if (type == 16)
            {
                if (tlsId == 0)
                    throw new InvalidDataException("PS4 TLS relocation references a module without PT_TLS.");
                value = tlsId;
            }
            else if (type is 17 or 18)
            {
                if (!GuestTlsTemplate.TryGetStaticOffset(tlsId, out var staticOffset))
                    throw new InvalidDataException("PS4 TLS offset relocation has no registered module.");
                value = Add(symbol.Value, addend);
                if (type == 18)
                    value = unchecked(value - staticOffset);
            }
            else
            {
                if (type is not (1 or 2 or 4 or 6 or 7 or 10 or 11 or 24 or 32 or 33))
                    throw new NotSupportedException($"PS4 relocation type {type} is not implemented.");
                if (relocation.Symbol != 0 && symbol.Section == 0)
                {
                    var nid = Nid(ReadString(strings, symbol.Name));
                    if (!byNid.TryGetValue(nid, out value) && (symbol.Info >> 4) != 2)
                        throw new InvalidDataException($"Missing PS4 import stub for {nid}.");
                    imported.Add(new(target, addend, nid, (symbol.Info & 15) == 1));
                }
                else if (relocation.Symbol != 0)
                    value = SymbolAddress(symbol, imageBase);
                if (type is 32 or 33)
                    value = symbol.Size;
                value = Add(value, addend);
                if (type is 2 or 4 or 24)
                    value = unchecked(value - target);
            }

            var width = type is 2 or 4 or 10 or 11 or 32 ? 4 : 8;
            if (type is 2 or 4 or 11)
            {
                var signed = unchecked((long)value);
                if (signed is < int.MinValue or > int.MaxValue)
                    throw new InvalidDataException("PS4 signed 32-bit relocation overflow.");
                BinaryPrimitives.WriteInt32LittleEndian(valueBytes, (int)signed);
            }
            else if (width == 4)
            {
                if (value > uint.MaxValue)
                    throw new InvalidDataException("PS4 unsigned 32-bit relocation overflow.");
                BinaryPrimitives.WriteUInt32LittleEndian(valueBytes, (uint)value);
            }
            else
                BinaryPrimitives.WriteUInt64LittleEndian(valueBytes, value);
            if (!memory.TryWrite(target, valueBytes[..width]))
                throw new InvalidDataException($"PS4 relocation target 0x{target:X} is not mapped.");
        }
        return imported;
    }

    private static Symbol GetSymbol(Symbol[] symbols, uint index)
    {
        if (index == 0)
            return default;
        if (index >= symbols.Length)
            throw new InvalidDataException("PS4 relocation symbol index exceeds its table.");
        return symbols[index];
    }

    private static ulong SymbolAddress(Symbol symbol, ulong imageBase) =>
        symbol.Section == 0xFFF1 ? symbol.Value : checked(imageBase + symbol.Value);

    private static ulong Add(ulong value, long addend) => unchecked(value + (ulong)addend);
}
