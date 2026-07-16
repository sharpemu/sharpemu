// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class KernelDirectMemoryStateCollection
{
    public const string Name = "KernelDirectMemoryState";
}

[Collection(KernelDirectMemoryStateCollection.Name)]
public sealed class KernelMapDirectMemoryTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong AddressOut = MemoryBase + 0x100;
    private const ulong QueryInfoAddress = MemoryBase + 0x200;
    private const ulong RequestedAddress = 0x15_0000_0000;
    private const ulong AlternativeAddress = 0x20_0000_0000;
    private const ulong MappingLength = 0x80000;
    private const ulong MappingAlignment = 0x4000;
    private readonly RecordingGuestAddressSpace _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;

    public KernelMapDirectMemoryTests()
    {
        KernelMemoryCompatExports.ResetDirectMemoryForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void FixedReservationFailureDoesNotSearchRelocateOrRecordMapping()
    {
        WriteUInt64(AddressOut, RequestedAddress);

        var result = MapDirect(flags: 0x10);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(RequestedAddress, ReadUInt64(AddressOut));
        Assert.Empty(_memory.SearchCalls);
        var exactCall = Assert.Single(_memory.AllocateAtCalls);
        Assert.Equal((RequestedAddress, MappingLength, false, false), exactCall);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            Query(RequestedAddress));
    }

    [Fact]
    public void FixedReservationSuccessRecordsRequestedMapping()
    {
        _memory.ExactAllocationSucceeds = true;
        WriteUInt64(AddressOut, RequestedAddress);

        var result = MapDirect(flags: 0x10);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(RequestedAddress, ReadUInt64(AddressOut));
        Assert.Empty(_memory.SearchCalls);
        var exactCall = Assert.Single(_memory.AllocateAtCalls);
        Assert.Equal((RequestedAddress, MappingLength, false, false), exactCall);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Query(RequestedAddress));
        Assert.Equal(RequestedAddress, ReadUInt64(QueryInfoAddress));
        Assert.Equal(RequestedAddress + MappingLength, ReadUInt64(QueryInfoAddress + 8));
    }

    [Fact]
    public void NonFixedReservationMayUseSearchedAlternative()
    {
        _memory.SearchedAddress = AlternativeAddress;
        WriteUInt64(AddressOut, RequestedAddress);

        var result = MapDirect(flags: 0);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.Equal(AlternativeAddress, ReadUInt64(AddressOut));
        Assert.Empty(_memory.AllocateAtCalls);
        var searchCall = Assert.Single(_memory.SearchCalls);
        Assert.Equal((RequestedAddress, MappingLength, false, MappingAlignment), searchCall);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, Query(AlternativeAddress));
        Assert.Equal(AlternativeAddress, ReadUInt64(QueryInfoAddress));
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.ResetDirectMemoryForTests();
    }

    private int MapDirect(ulong flags)
    {
        _context[CpuRegister.Rdi] = AddressOut;
        _context[CpuRegister.Rsi] = MappingLength;
        _context[CpuRegister.Rdx] = 0x03;
        _context[CpuRegister.Rcx] = flags;
        _context[CpuRegister.R8] = 0xF000_0000;
        _context[CpuRegister.R9] = MappingAlignment;
        return KernelMemoryCompatExports.KernelMapDirectMemory(_context);
    }

    private int Query(ulong address)
    {
        _context[CpuRegister.Rdi] = address;
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = QueryInfoAddress;
        _context[CpuRegister.Rcx] = 72;
        return KernelMemoryCompatExports.KernelVirtualQuery(_context);
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private sealed class RecordingGuestAddressSpace(ulong memoryBase, int memorySize)
        : ICpuMemory, IGuestAddressSpace
    {
        private readonly byte[] _controlMemory = new byte[memorySize];
        private readonly List<GuestRange> _guestRanges = [];

        public bool ExactAllocationSucceeds { get; set; }

        public ulong SearchedAddress { get; set; }

        public List<(ulong Address, ulong Size, bool Executable, bool AllowAlternative)> AllocateAtCalls { get; } = [];

        public List<(ulong Address, ulong Size, bool Executable, ulong Alignment)> SearchCalls { get; } = [];

        public ulong AllocateAt(
            ulong desiredAddress,
            ulong size,
            bool executable = true,
            bool allowAlternative = true)
        {
            AllocateAtCalls.Add((desiredAddress, size, executable, allowAlternative));
            if (!ExactAllocationSucceeds)
            {
                return 0;
            }

            AddGuestRange(desiredAddress, size);
            return desiredAddress;
        }

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            SearchCalls.Add((desiredAddress, size, executable, alignment));
            actualAddress = SearchedAddress;
            if (actualAddress == 0)
            {
                return false;
            }

            AddGuestRange(actualAddress, size);
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address)
        {
            address = 0;
            return false;
        }

        public bool TryFreeGuestMemory(ulong address)
        {
            var index = _guestRanges.FindIndex(range => range.Address == address);
            if (index < 0)
            {
                return false;
            }

            _guestRanges.RemoveAt(index);
            return true;
        }

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) => true;

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var source))
            {
                return false;
            }

            source.CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            if (!TryResolve(virtualAddress, source.Length, out var destination))
            {
                return false;
            }

            source.CopyTo(destination);
            return true;
        }

        private void AddGuestRange(ulong address, ulong size)
        {
            _guestRanges.Add(new GuestRange(address, new byte[checked((int)size)]));
        }

        private bool TryResolve(ulong address, int length, out Span<byte> result)
        {
            if (TryResolve(memoryBase, _controlMemory, address, length, out result))
            {
                return true;
            }

            foreach (var range in _guestRanges)
            {
                if (TryResolve(range.Address, range.Storage, address, length, out result))
                {
                    return true;
                }
            }

            result = default;
            return false;
        }

        private static bool TryResolve(
            ulong rangeAddress,
            byte[] storage,
            ulong address,
            int length,
            out Span<byte> result)
        {
            result = default;
            if (address < rangeAddress)
            {
                return false;
            }

            var offset = address - rangeAddress;
            if (offset > (ulong)storage.Length || (ulong)length > (ulong)storage.Length - offset)
            {
                return false;
            }

            result = storage.AsSpan(checked((int)offset), length);
            return true;
        }

        private sealed record GuestRange(ulong Address, byte[] Storage);
    }
}
