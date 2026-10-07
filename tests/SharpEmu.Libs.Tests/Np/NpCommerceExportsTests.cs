// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[CollectionDefinition("NpCommerce", DisableParallelization = true)]
public sealed class NpCommerceCollection;

[Collection("NpCommerce")]
public sealed class NpCommerceExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1_0000);
    private readonly CpuContext _context;

    public NpCommerceExportsTests()
    {
        NpCommerceDialogState.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose() => NpCommerceDialogState.ResetForTests();

    [Fact]
    public void OfflineLifecycle_ReturnsCanceledResultAndPreservesUserData()
    {
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogInitialize(_context));
        Assert.Equal(
            NpCommerceDialogState.ErrorAlreadyInitialized,
            NpCommerceExports.NpCommerceDialogInitialize(_context));

        const ulong userData = 0x1_1234_5678;
        var parameter = WriteRequest(
            MemoryBase + 0x100,
            size: 0x80,
            mode: 5,
            userData,
            features: 0x55,
            targets: ["plus", "subscription"]);
        _context[CpuRegister.Rdi] = parameter;
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogOpen(_context));
        Assert.Equal(NpCommerceDialogStatus.Running, NpCommerceDialogState.Status);

        var request = Assert.IsType<NpCommerceDialogRequest>(NpCommerceDialogState.RequestForTests);
        Assert.False(request.IsOpen2);
        Assert.Equal(9, request.UserId);
        Assert.Equal(5, request.Mode);
        Assert.Equal(3U, request.ServiceLabel);
        Assert.Equal(0x55UL, request.Features);
        Assert.Equal(userData, request.UserData);
        Assert.Equal(["plus", "subscription"], request.Targets);

        Assert.Equal(3, NpCommerceExports.NpCommerceDialogUpdateStatus(_context));
        Assert.Equal(NpCommerceDialogStatus.Finished, NpCommerceDialogState.Status);

        var resultAddress = MemoryBase + 0x800;
        _context[CpuRegister.Rdi] = resultAddress;
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogGetResult(_context));
        Span<byte> result = stackalloc byte[0x30];
        Assert.True(_memory.TryRead(resultAddress, result));
        Assert.Equal(NpCommerceDialogState.ResultUserCanceled, BinaryPrimitives.ReadInt32LittleEndian(result));
        Assert.Equal(0, result[0x04]);
        Assert.Equal(userData, BinaryPrimitives.ReadUInt64LittleEndian(result[0x08..]));

        Assert.Equal(0, NpCommerceExports.NpCommerceDialogTerminate(_context));
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogUpdateStatus(_context));
        Assert.Equal(
            NpCommerceDialogState.ErrorNotInitialized,
            NpCommerceExports.NpCommerceDialogTerminate(_context));
    }

    [Fact]
    public void Open2_AcceptsExtendedStructureAndIgnoresFeaturesOutsidePlusMode()
    {
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogInitialize(_context));
        _context[CpuRegister.Rdi] = WriteRequest(
            MemoryBase + 0x1000,
            size: 0x88,
            mode: 1,
            userData: 0xAABB,
            features: 0xFFFF,
            targets: ["product-label"]);

        Assert.Equal(0, NpCommerceExports.NpCommerceDialogOpen2(_context));
        var request = Assert.IsType<NpCommerceDialogRequest>(NpCommerceDialogState.RequestForTests);
        Assert.True(request.IsOpen2);
        Assert.Equal(0UL, request.Features);
        Assert.Equal("product-label", Assert.Single(request.Targets));
    }

    [Fact]
    public void FinishedDialog_CanOpenAgain()
    {
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogInitialize(_context));
        _context[CpuRegister.Rdi] = WriteRequest(MemoryBase + 0x2000, 0x80, 0, 1);
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogOpen(_context));
        Assert.Equal(3, NpCommerceExports.NpCommerceDialogUpdateStatus(_context));

        _context[CpuRegister.Rdi] = WriteRequest(MemoryBase + 0x2200, 0x80, 2, 2);
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogOpen(_context));
        Assert.Equal(NpCommerceDialogStatus.Running, NpCommerceDialogState.Status);
        Assert.Equal(2UL, NpCommerceDialogState.RequestForTests!.UserData);
    }

    [Fact]
    public void InvalidStatePointersAndParameters_ReturnDefinedErrors()
    {
        _context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        Assert.Equal(
            NpCommerceDialogState.ErrorNotInitialized,
            NpCommerceExports.NpCommerceDialogOpen(_context));
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogInitialize(_context));

        _context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            NpCommerceDialogState.ErrorArgumentNull,
            NpCommerceExports.NpCommerceDialogOpen(_context));

        _context[CpuRegister.Rdi] = WriteRequest(MemoryBase + 0x3000, 0x70, 0, 0);
        Assert.Equal(
            NpCommerceDialogState.ErrorParameterInvalid,
            NpCommerceExports.NpCommerceDialogOpen(_context));

        _context[CpuRegister.Rdi] = WriteRequest(MemoryBase + 0x3200, 0x80, 9, 0x99);
        Assert.Equal(
            NpCommerceDialogState.ErrorParameterInvalid,
            NpCommerceExports.NpCommerceDialogOpen(_context));
        Assert.Equal(NpCommerceDialogStatus.Finished, NpCommerceDialogState.Status);

        _context[CpuRegister.Rdi] = 0;
        Assert.Equal(
            NpCommerceDialogState.ErrorArgumentNull,
            NpCommerceExports.NpCommerceDialogGetResult(_context));
    }

    [Fact]
    public void GetResultBeforeCompletionAndBadGuestMemory_ReturnErrors()
    {
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogInitialize(_context));
        _context[CpuRegister.Rdi] = MemoryBase + 0x4000;
        Assert.Equal(
            NpCommerceDialogState.ErrorNotFinished,
            NpCommerceExports.NpCommerceDialogGetResult(_context));

        _context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpCommerceExports.NpCommerceDialogOpen(_context));

        _context[CpuRegister.Rdi] = WriteRequest(MemoryBase + 0x4200, 0x80, 0, 0);
        Assert.Equal(0, NpCommerceExports.NpCommerceDialogOpen(_context));
        Assert.Equal(3, NpCommerceExports.NpCommerceDialogUpdateStatus(_context));
        _context[CpuRegister.Rdi] = 0xDEAD_BEEF;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            NpCommerceExports.NpCommerceDialogGetResult(_context));
    }

    [Fact]
    public void ConcurrentInitialize_HasOneWinner()
    {
        var results = new int[64];
        Parallel.For(0, results.Length, index =>
        {
            var context = new CpuContext(_memory, Generation.Gen5);
            results[index] = NpCommerceExports.NpCommerceDialogInitialize(context);
        });

        Assert.Single(results, result => result == 0);
        Assert.Equal(results.Length - 1, results.Count(result => result == NpCommerceDialogState.ErrorAlreadyInitialized));
    }

    private ulong WriteRequest(
        ulong address,
        int size,
        int mode,
        ulong userData,
        ulong features = 0,
        IReadOnlyList<string>? targets = null)
    {
        Span<byte> parameter = stackalloc byte[0x88];
        parameter.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(parameter, 0x30);
        BinaryPrimitives.WriteInt32LittleEndian(parameter[0x30..], size);
        BinaryPrimitives.WriteInt32LittleEndian(parameter[0x34..], 9);
        BinaryPrimitives.WriteInt32LittleEndian(parameter[0x38..], mode);
        BinaryPrimitives.WriteUInt32LittleEndian(parameter[0x3C..], 3);
        BinaryPrimitives.WriteUInt64LittleEndian(parameter[0x50..], features);
        BinaryPrimitives.WriteUInt64LittleEndian(parameter[0x58..], userData);

        if (targets is { Count: > 0 })
        {
            var pointerArray = address + 0x200;
            BinaryPrimitives.WriteUInt64LittleEndian(parameter[0x40..], pointerArray);
            BinaryPrimitives.WriteUInt32LittleEndian(parameter[0x48..], checked((uint)targets.Count));
            for (var index = 0; index < targets.Count; index++)
            {
                var textAddress = address + 0x300 + checked((ulong)(index * 0x100));
                _memory.WriteCString(textAddress, targets[index]);
                Assert.True(_context.TryWriteUInt64(pointerArray + checked((ulong)(index * sizeof(ulong))), textAddress));
            }
        }

        Assert.True(_memory.TryWrite(address, parameter));
        return address;
    }
}
