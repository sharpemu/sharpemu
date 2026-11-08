// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Evaluation;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Evaluation;

// The copy kernels games ship, one guest memory image, and the request the dispatch path builds for them.
internal static class EvaluationTestSupport
{
    public const ulong MemoryBase = 0x10_0000;
    public const ulong SourceAddress = MemoryBase + 0x20000;
    public const ulong DestinationAddress = MemoryBase + 0x40000;
    public const ulong ControlAddress = MemoryBase + 0x60000;

    public static readonly uint[] ModuloKernel104 =
    [
        0xBEFC03FF, 0xA65CD91E,
        0xBF960000, 0xBFA00003, 0xD7460002, 0x04010C0C, 0xF4201A84, 0xFA000000, 0xBF8CC07F, 0x7DA8046A, 0xBF88002A, 0xF4200304,
        0xFA000004, 0xBF8CC07F, 0x7E000C0C, 0xBF070C80, 0x8588807E, 0x7E005700, 0x100000FF, 0x4F800000, 0x7E060F00, 0xD5766A00,
        0x0202060C, 0x7D8A0280, 0x4C020080, 0x02000101, 0xD56A0001, 0x00020700, 0x4C000303, 0x4A020303, 0x02000101, 0xD56A0000,
        0x00020500, 0xD5690001, 0x0002000C, 0x4C060302, 0x7D8C02F9, 0x06068A02, 0x7D86060C, 0x87EA6A0A, 0x50000080, 0xD5286A00,
        0x002A00C1, 0xD5010000, 0x002200C1, 0xD5690000, 0x0002000C, 0x4C000102, 0xE0002000, 0x80000000, 0xBF8C3F70, 0xE0102000,
        0x80010002, 0xBF810000,
    ];

    public static readonly uint[] ModuloKernel107 =
    [
        0xBFA00002, 0xD7460000, 0x04010C0C, 0xF4201A84, 0xFA000000, 0xBF8CC07F, 0x7DA8006A, 0xBF88001D, 0xF4200204, 0xFA000004,
        0xBF8CC07F, 0x7E020C08, 0x81EA0880, 0x7E025701, 0x100202FF, 0x4F7FFFFE, 0x7E020F01, 0x7ED60501, 0x936A6B6A, 0x9AEA6B6A,
        0x4A02026A, 0xD56A0001, 0x00020101, 0xD5690001, 0x00020208, 0x4C020300, 0x7D860208, 0x4E040208, 0x02020501, 0x7D860208,
        0x4E040208, 0x02020501, 0xE0002000, 0x80000101, 0xBF8C3F70, 0xE0102000, 0x80010100, 0xBF810000,
    ];

    public static readonly uint[] MaskedKernel =
    [
        0xBEFC03FF, 0x94E6DD39,
        0xBF960000, 0xBFA00001, 0xD7460000, 0x04010C0C, 0xF4201A84, 0xFA000000, 0xBF8CC07F, 0x7DA8006A, 0xBF880009, 0xF4201A84,
        0xFA000004, 0xBF8CC07F, 0x3602006A, 0xE0002000, 0x80000101, 0xBF8C3F70, 0xE0102000, 0x80010100, 0xBF810000,
    ];

    public static Gen5ShaderProgram Decode(uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new CodeMemory(bytes), Generation.Gen5), 0x1000, out var program, out var error), error);
        return program;
    }

    public static uint[] DwordBuffer(ulong address, uint records) =>
        [unchecked((uint)address), 0x0004_0000 | (uint)(address >> 32), records, 0x0001_4004];

    public static uint[] ControlBuffer(ulong address) =>
        [unchecked((uint)address), 0x0010_0000 | (uint)(address >> 32), 1, 0x0004_DFAC];

    public static uint[] CopyUserData(uint sourceRecords, uint destinationRecords, uint[]? control = null) =>
        [.. DwordBuffer(SourceAddress, sourceRecords), .. DwordBuffer(DestinationAddress, destinationRecords), .. control ?? ControlBuffer(ControlAddress)];

    public static Gen5DispatchRequest CopyRequest(uint[] userData, uint groups, uint waveSize = 64, uint? threadLimit = null, uint threads = 64) => new()
    {
        UserData = userData,
        SystemRegisters = new Gen5ComputeSystemRegisters(12, null, null, null),
        ThreadsX = threads,
        GroupsX = groups,
        LimitsThreads = threadLimit is not null,
        ThreadLimitX = threadLimit ?? uint.MaxValue,
        WaveSize = waveSize,
    };

    public static EvaluationMemory CopyMemory(uint count, uint parameter, uint[] source)
    {
        var memory = new EvaluationMemory();
        memory.WriteWords(SourceAddress, source);
        memory.WriteWords(ControlAddress, count, parameter, 0, 0);
        return memory;
    }

    public static uint ModuloIndex(uint index, uint period) => period == 0 ? index : index % period;

    private sealed class CodeMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < 0x1000 || address - 0x1000 > (ulong)(bytes.Length - destination.Length)) return false;
            bytes.AsSpan((int)(address - 0x1000), destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}

// A flat guest memory image; a range named as owned stands for bytes the GPU still owns.
internal sealed class EvaluationMemory : IGen5EvaluationMemory
{
    private readonly byte[] _bytes = new byte[0x10_0000];

    public (ulong Address, ulong Size) Owned { get; set; }

    public int Reads { get; private set; }

    public void WriteWords(ulong address, params uint[] words)
    {
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_bytes.AsSpan((int)(address - EvaluationTestSupport.MemoryBase) + index * sizeof(uint)), words[index]);
        }
    }

    public uint ReadWord(ulong address) =>
        BinaryPrimitives.ReadUInt32LittleEndian(_bytes.AsSpan((int)(address - EvaluationTestSupport.MemoryBase)));

    public bool TryRead(ulong address, Span<byte> destination)
    {
        Reads++;
        var size = (ulong)destination.Length;
        if (address < EvaluationTestSupport.MemoryBase || address + size > EvaluationTestSupport.MemoryBase + (ulong)_bytes.Length)
        {
            return false;
        }

        if (Owned.Size != 0 && address < Owned.Address + Owned.Size && Owned.Address < address + size)
        {
            return false;
        }

        _bytes.AsSpan((int)(address - EvaluationTestSupport.MemoryBase), destination.Length).CopyTo(destination);
        return true;
    }
}
