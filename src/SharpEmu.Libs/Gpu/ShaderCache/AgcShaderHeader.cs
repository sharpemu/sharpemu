// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal enum AgcShaderType : byte
{
    Compute = 0,
    Pixel = 1,
    Geometry = 2,
}

internal readonly record struct AgcRegister(uint Offset, uint Value);

internal sealed class AgcShaderHeader
{
    public const int FixedBytes = 96;
    public const int MaxHeaderBytes = 1 << 16;
    public const int MaxCodeBytes = 1 << 22;
    private const uint FileMagic = 0x34333231;
    private const uint Version = 0x18;
    private const int UserDataField = 8;
    private const int CodeField = 16;
    private const int ContextRegistersField = 24;
    private const int ShaderRegistersField = 32;
    private const int SpecialsField = 40;
    private const int InputSemanticsField = 48;
    private const int OutputSemanticsField = 56;
    private const int UserDataBlockBytes = 56;
    private const int UserDataPointerCount = 5;
    private const int DispatchModifierOffset = 16;
    private const int MinSpecialsBytes = 20;
    private const int DirectResourceCountOffset = 44;
    private const int FetchTableSlot = 8;
    private const int EmbeddedFetchSlot = 10;

    public static ReadOnlySpan<byte> Signature => [0x31, 0x32, 0x33, 0x34, 0x18, 0x00, 0x00, 0x00];

    private AgcShaderHeader(byte[] bytes)
    {
        Bytes = bytes;
    }

    public byte[] Bytes { get; }

    public uint HeaderSize => U32(64);

    public uint CodeSize => U32(68);

    public AgcShaderType Type => (AgcShaderType)Bytes[90];

    public uint ScratchDwords => U16(84);

    public uint InputSemanticCount => U32(80);

    public uint OutputSemanticCount => U16(86);

    public int InputSemanticsOffset => Pointer(InputSemanticsField);

    public IReadOnlyList<AgcRegister> ContextRegisters => Registers(ContextRegistersField, Bytes[91]);

    public IReadOnlyList<AgcRegister> ShaderRegisters => Registers(ShaderRegistersField, Bytes[92]);

    public uint DispatchModifier
    {
        get
        {
            var specials = Pointer(SpecialsField);
            return specials != 0 && U16(88) >= MinSpecialsBytes ? U32(specials + DispatchModifierOffset) : 0;
        }
    }

    public uint[] InputSemantics => Words(InputSemanticsField, (int)Math.Min(InputSemanticCount, 32u));

    public uint[] OutputSemantics => Words(OutputSemanticsField, (int)OutputSemanticCount);

    public bool HasVertexFetchTables
    {
        get
        {
            var userData = Pointer(UserDataField);
            if (userData == 0)
            {
                return false;
            }

            var count = U16(userData + DirectResourceCountOffset);
            var offsets = SelfRelative(userData);
            bool Bound(int slot) => count > slot && (offsets == 0 || U16(offsets + slot * 2) != ushort.MaxValue);
            return Bound(FetchTableSlot) || Bound(EmbeddedFetchSlot);
        }
    }

    public static bool IsSupported(byte type) => type <= (byte)AgcShaderType.Geometry;

    public static bool TryPeekSizes(ReadOnlySpan<byte> fixedBytes, out int headerBytes, out int codeBytes, out byte type)
    {
        headerBytes = codeBytes = 0;
        type = 0;
        if (fixedBytes.Length < FixedBytes || !fixedBytes[..8].SequenceEqual(Signature))
        {
            return false;
        }

        var header = BinaryPrimitives.ReadUInt32LittleEndian(fixedBytes[64..]);
        var code = BinaryPrimitives.ReadUInt32LittleEndian(fixedBytes[68..]);
        type = fixedBytes[90];
        if (header < FixedBytes || header > MaxHeaderBytes || code == 0 || code > MaxCodeBytes || code % sizeof(uint) != 0)
        {
            return false;
        }

        headerBytes = (int)header;
        codeBytes = (int)code;
        return true;
    }

    public static bool TryParse(ReadOnlySpan<byte> bytes, ulong headerAddress, out AgcShaderHeader header)
    {
        header = null!;
        if (!TryPeekSizes(bytes, out var headerBytes, out _, out var type) || headerBytes != bytes.Length || !IsSupported(type))
        {
            return false;
        }

        var normalized = new AgcShaderHeader(bytes.ToArray());
        foreach (var field in (ReadOnlySpan<int>)[UserDataField, ContextRegistersField, ShaderRegistersField, SpecialsField, InputSemanticsField, OutputSemanticsField])
        {
            if (!normalized.Normalize(field, headerAddress))
            {
                return false;
            }
        }

        BinaryPrimitives.WriteUInt64LittleEndian(normalized.Bytes.AsSpan(CodeField), 0);
        var userData = normalized.Pointer(UserDataField);
        if (userData != 0)
        {
            if (userData + UserDataBlockBytes > normalized.Bytes.Length)
            {
                return false;
            }

            for (var index = 0; index < UserDataPointerCount; index++)
            {
                var field = userData + index * sizeof(ulong);
                if (!normalized.Normalize(field, headerAddress))
                {
                    if (index == 0)
                    {
                        return false;
                    }

                    BinaryPrimitives.WriteUInt64LittleEndian(normalized.Bytes.AsSpan(field), 0);
                }
            }
        }

        try
        {
            _ = normalized.ContextRegisters;
            _ = normalized.ShaderRegisters;
            _ = normalized.InputSemantics;
            _ = normalized.OutputSemantics;
        }
        catch (InvalidDataException)
        {
            return false;
        }

        header = normalized;
        return true;
    }

    private bool Normalize(int field, ulong headerAddress)
    {
        var value = BinaryPrimitives.ReadUInt64LittleEndian(Bytes.AsSpan(field));
        if (value == 0)
        {
            return true;
        }

        var relative = (long)value;
        if (relative > -field && relative < Bytes.Length - field)
        {
            return true;
        }

        if (headerAddress == 0 || value < headerAddress || value - headerAddress >= (ulong)Bytes.Length)
        {
            return false;
        }

        BinaryPrimitives.WriteInt64LittleEndian(Bytes.AsSpan(field), (long)(value - headerAddress) - field);
        return true;
    }

    private int SelfRelative(int field)
    {
        var relative = BinaryPrimitives.ReadInt64LittleEndian(Bytes.AsSpan(field));
        return relative == 0 ? 0 : checked((int)(field + relative));
    }

    private int Pointer(int field) => SelfRelative(field);

    private AgcRegister[] Registers(int field, int count)
    {
        var start = Pointer(field);
        if (count == 0)
        {
            return [];
        }

        if (start <= 0 || start + count * 8 > Bytes.Length)
        {
            throw new InvalidDataException("The AGC register table is outside the header.");
        }

        var registers = new AgcRegister[count];
        for (var index = 0; index < count; index++)
        {
            registers[index] = new AgcRegister(U32(start + index * 8), U32(start + index * 8 + 4));
        }

        return registers;
    }

    private uint[] Words(int field, int count)
    {
        var start = Pointer(field);
        if (count == 0 || start == 0)
        {
            return [];
        }

        if (start < 0 || start + count * 4 > Bytes.Length)
        {
            throw new InvalidDataException("The AGC semantic table is outside the header.");
        }

        var words = new uint[count];
        for (var index = 0; index < count; index++)
        {
            words[index] = U32(start + index * 4);
        }

        return words;
    }

    private uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset));

    private ushort U16(int offset) => BinaryPrimitives.ReadUInt16LittleEndian(Bytes.AsSpan(offset));
}
