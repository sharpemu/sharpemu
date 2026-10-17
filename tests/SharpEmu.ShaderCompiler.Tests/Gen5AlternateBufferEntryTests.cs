// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5AlternateBufferEntryTests
{
    private const ulong ShaderAddress = 0x1000;
    private const ulong BufferAddress = 0x76F0D70000;
    private const uint BufferLength = 520;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BranchFetchesShareTheirEntryDescriptor(bool hasPrimaryFetch)
    {
        var words = new List<uint>();
        if (hasPrimaryFetch) words.AddRange([0xE0002000, 0x8007071E]);
        words.AddRange([
            0xBF840002, 0x811C8228, 0xBF820006,
            0xE0002000, 0x8007071E,
            0xE0002000, 0x80071E1C,
            0xE0002000, 0x8007251D,
            0xBF810000,
        ]);
        var (plan, snapshot) = Materialize(words);
        Assert.Equal(hasPrimaryFetch ? 4 : 3, plan.Memory.Count);
        Assert.All(plan.Memory.Entries, entry => Assert.Equal(0u, entry.Resource));
        AssertDescriptor(Assert.Single(snapshot.Buffers), BufferAddress);
    }

    [Fact]
    public void BranchFetchUsesThePointerWrittenBeforeTheBranch()
    {
        var (_, snapshot) = Materialize([
            0x811C901C, 0xBF840002, 0x811C8228, 0xBF820002,
            0xE0002000, 0x8007071E, 0xBF810000,
        ]);
        AssertDescriptor(Assert.Single(snapshot.Buffers), BufferAddress + 16);
    }

    [Fact]
    public void SeparateBranchesKeepSeparateDescriptors()
    {
        var (plan, snapshot) = Materialize([
            0xBF840004, 0x811C901C,
            0xE0002000, 0x8007071E, 0xBF820002,
            0xE0002000, 0x80071E1C, 0xBF810000,
        ]);
        AssertDescriptor(snapshot.Buffers[(int)plan.Memory.Entries.Single(entry => entry.Pc == 8).Resource], BufferAddress + 16);
        AssertDescriptor(snapshot.Buffers[(int)plan.Memory.Entries.Single(entry => entry.Pc == 20).Resource], BufferAddress);
    }

    [Fact]
    public void NestedBranchesKeepTheirOwnEntryDescriptors()
    {
        var (plan, snapshot) = Materialize([
            0xBF840002, 0x811C8228, 0xBF82000A,
            0xBF840005, 0x811C901C,
            0xE0002000, 0x8007071E, 0xBF820005, 0xBF800000,
            0xE0002000, 0x80071E1C,
            0xE0002000, 0x8007251D, 0xBF810000,
        ]);
        AssertDescriptor(snapshot.Buffers[(int)plan.Memory.Entries.Single(entry => entry.Pc == 20).Resource], BufferAddress + 16);
        AssertDescriptor(snapshot.Buffers[(int)plan.Memory.Entries.Single(entry => entry.Pc == 36).Resource], BufferAddress);
        Assert.Equal(plan.Memory.Entries.Single(entry => entry.Pc == 36).Resource,
            plan.Memory.Entries.Single(entry => entry.Pc == 44).Resource);
    }

    [Fact]
    public void GlobalLoadKeepsTheAddressFromItsBranchEntry()
    {
        var (plan, snapshot) = Materialize([
            0xBE9D03FF, (uint)(BufferAddress >> 32),
            0xBF840002, 0x811C8228, 0xBF820002,
            0xDC308000, 0x071C001E, 0xBF810000,
        ]);
        Assert.Equal(20u, plan.Memory[0].Pc);
        Assert.Equal(BufferAddress, Assert.Single(snapshot.DeviceAddressRanges).Base);
    }

    private static void AssertDescriptor(uint[] descriptor, ulong address)
    {
        Assert.Equal(unchecked((uint)address), descriptor[0]);
        Assert.Equal((uint)(address >> 32) | (4u << 16), descriptor[1]);
        Assert.Equal(BufferLength / sizeof(uint), descriptor[2]);
        Assert.Equal(0x00016204u, descriptor[3]);
    }

    private static (ShaderResourcePlan Plan, ResourceSnapshot Snapshot) Materialize(IReadOnlyList<uint> words)
    {
        var memory = new ShaderMemory(words);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(context, ShaderAddress, out var program, out var error), error);
        var registers = new uint[41];
        registers[28] = unchecked((uint)BufferAddress);
        registers[29] = (uint)(BufferAddress >> 32) | (4u << 16);
        registers[30] = BufferLength / sizeof(uint);
        registers[31] = 0x00016204;
        registers[40] = 0x7FFFFFFF;
        var plan = Extract(program!, userDataCount: 41);
        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        Assert.True(ResourceMaterializer.Materialize(plan, Inputs(registers), ref snapshot, ref specialization, out var failure), failure.ToString());
        return (plan, snapshot);
    }

    private sealed class ShaderMemory : ICpuMemory
    {
        private const ulong MappedBufferLength = 4096;
        private readonly byte[] _shaderBytes;

        public ShaderMemory(IReadOnlyList<uint> words)
        {
            _shaderBytes = new byte[words.Count * sizeof(uint)];
            for (var index = 0; index < words.Count; index++)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(_shaderBytes.AsSpan(index * sizeof(uint)), words[index]);
            }
        }

        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address >= ShaderAddress && address - ShaderAddress + (ulong)destination.Length <= (ulong)_shaderBytes.Length)
            {
                _shaderBytes.AsSpan((int)(address - ShaderAddress), destination.Length).CopyTo(destination);
                return true;
            }

            if (address >= BufferAddress && address - BufferAddress + (ulong)destination.Length <= MappedBufferLength)
            {
                destination.Fill(0x5A);
                return true;
            }
            return false;
        }

        public bool CanRead(ulong address, ulong size) =>
            (size <= (ulong)_shaderBytes.Length && address >= ShaderAddress &&
                address - ShaderAddress <= (ulong)_shaderBytes.Length - size) ||
            (size <= MappedBufferLength && address >= BufferAddress &&
                address - BufferAddress <= MappedBufferLength - size);

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
