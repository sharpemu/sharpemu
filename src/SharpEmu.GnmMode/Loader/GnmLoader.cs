// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SharpEmu.Core.Loader;

namespace SharpEmu.GnmMode.Loader;

public sealed class GnmLoader
{
    private readonly record struct Payload(int Header, ulong Flags, ulong Offset, ulong Size);

    private readonly int _imageLength;
    private readonly bool _isSelf;
    private readonly ProgramHeader[] _segments;
    private readonly Payload[] _payloads;
    private readonly Dictionary<long, ulong> _dynamicEntries = new();
    private readonly List<uint> _importedModuleNames = [];
    private readonly int _dynamicDataIndex;

    public ElfHeader ElfHeader { get; }
    public IReadOnlyList<ProgramHeader> ProgramHeaders => _segments;
    public bool IsSelf => _isSelf;
    public IReadOnlyDictionary<long, ulong> DynamicEntries => _dynamicEntries;
    public IReadOnlyList<uint> ImportedModuleNames => _importedModuleNames;

    private GnmLoader(ReadOnlySpan<byte> image)
    {
        _imageLength = image.Length;
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(Slice(image, 0, 4));
        _isSelf = magic == 0x1D3D154F;
        if (!_isSelf && magic != 0x464C457F)
            throw new InvalidDataException("GNM mode requires a decrypted PS4 ELF or SELF.");

        if (_isSelf && (image.Length < 32 || image[5] != 1 || image[6] != 1 || image[7] != 0x12))
            throw new InvalidDataException("Invalid PS4 SELF header layout.");

        var payloadCount = _isSelf ? BinaryPrimitives.ReadUInt16LittleEndian(Slice(image, 24, 2)) : 0;
        var elfOffset = _isSelf ? checked(32 + payloadCount * 32) : 0;
        var elf = Slice(image, (ulong)elfOffset, 64);
        if (!elf[..4].SequenceEqual("\u007fELF"u8) || elf[4] != 2 || elf[5] != 1 ||
            elf[7] != 9 || elf[8] != 0 || BinaryPrimitives.ReadUInt16LittleEndian(elf[18..]) != 62)
            throw new InvalidDataException("GNM mode requires a PS4 x86-64 little-endian ELF.");

        ElfHeader = MemoryMarshal.Read<ElfHeader>(elf);
        if (ElfHeader.Type is not (3 or 0xFE10 or 0xFE18))
            throw new NotSupportedException($"PS4 ELF type 0x{ElfHeader.Type:X} is not a supported relocatable image.");

        var headerOffset = BinaryPrimitives.ReadUInt64LittleEndian(elf[32..]);
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(elf[54..]);
        var headerCount = BinaryPrimitives.ReadUInt16LittleEndian(elf[56..]);
        if (headerCount != 0 && headerSize < 56)
            throw new InvalidDataException("Invalid PS4 ELF program header size.");
        _segments = new ProgramHeader[headerCount];
        for (var i = 0; i < headerCount; i++)
        {
            var header = Slice(image, checked((ulong)elfOffset + headerOffset + (ulong)i * headerSize), 56);
            _segments[i] = MemoryMarshal.Read<ProgramHeader>(header);
        }

        var payloads = new List<Payload>();
        for (var i = 0; i < payloadCount; i++)
        {
            var entry = Slice(image, (ulong)(32 + i * 32), 32);
            var flags = BinaryPrimitives.ReadUInt64LittleEndian(entry);
            if ((flags & 0x800) == 0)
                continue;
            var header = (int)((flags >> 20) & 0xFFF);
            if (header >= headerCount)
                throw new InvalidDataException("PS4 SELF payload references an invalid program header.");
            payloads.Add(new(header, flags, BinaryPrimitives.ReadUInt64LittleEndian(entry[8..]),
                BinaryPrimitives.ReadUInt64LittleEndian(entry[16..])));
        }
        _payloads = payloads.ToArray();
        _dynamicDataIndex = Array.FindIndex(_segments, segment => segment.Type == 0x61000000);
        var dynamicIndex = Array.FindIndex(_segments, segment => segment.Type == 2);
        if (dynamicIndex < 0)
            return;
        var dynamicTable = ReadSegment(image, dynamicIndex);
        if (dynamicTable.Length % 16 != 0)
            throw new InvalidDataException("PS4 dynamic table is not entry-aligned.");
        for (var offset = 0; offset < dynamicTable.Length; offset += 16)
        {
            var tag = BinaryPrimitives.ReadInt64LittleEndian(dynamicTable[offset..]);
            if (tag == 0)
                break;
            var value = BinaryPrimitives.ReadUInt64LittleEndian(dynamicTable[(offset + 8)..]);
            _dynamicEntries[tag] = value;
            if (tag is 1 or 0x6100000F or 0x61000045)
                _importedModuleNames.Add((uint)value);
        }
    }

    public static GnmLoader Open(ReadOnlySpan<byte> image) => new(image);

    public static bool IsGnmExecutable(Stream stream)
    {
        var position = stream.Position;
        try
        {
            Span<byte> header = stackalloc byte[64];
            stream.Position = 0;
            if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length)
                return false;
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (magic == 0x1D3D154F)
            {
                var offset = 32L + BinaryPrimitives.ReadUInt16LittleEndian(header[24..]) * 32L;
                if (offset > stream.Length - header.Length)
                    return false;
                stream.Position = offset;
                stream.ReadExactly(header);
            }
            else if (magic != 0x464C457F)
                return false;
            return header[..4].SequenceEqual("\u007fELF"u8) && header[7] == 9 && header[8] == 0;
        }
        finally
        {
            stream.Position = position;
        }
    }

    public ulong GetSegmentOffset(int index)
    {
        var segment = _segments[index];
        if (!_isSelf)
        {
            ValidateRange((ulong)_imageLength, segment.Offset, segment.FileSize);
            return segment.Offset;
        }

        foreach (var payload in _payloads)
            if (payload.Header == index)
                return ResolvePayload(payload, 0, segment.FileSize);

        foreach (var payload in _payloads)
        {
            var carrier = _segments[payload.Header];
            if (segment.Offset < carrier.Offset || segment.Offset - carrier.Offset > carrier.FileSize)
                continue;
            var displacement = segment.Offset - carrier.Offset;
            if (segment.FileSize <= carrier.FileSize - displacement)
                return ResolvePayload(payload, displacement, segment.FileSize);
        }

        throw new InvalidDataException($"No PS4 SELF payload contains program header {index}.");
    }

    public ReadOnlySpan<byte> ReadSegment(ReadOnlySpan<byte> image, int index) =>
        Slice(image, GetSegmentOffset(index), _segments[index].FileSize);

    public bool TryReadDynamicTable(ReadOnlySpan<byte> image, long tag, ulong size, out byte[] table)
    {
        table = [];
        if (_dynamicDataIndex < 0 || !_dynamicEntries.TryGetValue(tag, out var offset))
            return false;

        ValidateRange(_segments[_dynamicDataIndex].FileSize, offset, size);
        table = Slice(image, checked(GetSegmentOffset(_dynamicDataIndex) + offset), size).ToArray();
        return true;
    }

    private ulong ResolvePayload(Payload payload, ulong displacement, ulong size)
    {
        if ((payload.Flags & 0xA) != 0)
            throw new NotSupportedException("Encrypted or compressed PS4 SELF payloads must be decrypted and unpacked first.");
        ValidateRange(payload.Size, displacement, size);
        var offset = checked(payload.Offset + displacement);
        ValidateRange((ulong)_imageLength, offset, size);
        return offset;
    }

    private static ReadOnlySpan<byte> Slice(ReadOnlySpan<byte> image, ulong offset, ulong size)
    {
        ValidateRange((ulong)image.Length, offset, size);
        return image.Slice(checked((int)offset), checked((int)size));
    }

    private static void ValidateRange(ulong length, ulong offset, ulong size)
    {
        if (offset > length || size > length - offset)
            throw new InvalidDataException("PS4 image table or segment extends beyond its container.");
    }
}
