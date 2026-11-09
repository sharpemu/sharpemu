// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

[Collection(AgcCommandBufferChainCollection.Name)]
public sealed class AgcDirectShaderRegisterTests
{
    private const ulong BaseAddress = 0x2_6000_0000;
    private const ulong CommandBufferAddress = BaseAddress + 0x100;
    private const ulong PacketAddress = BaseAddress + 0x400;
    private const ulong SubmitAddress = BaseAddress + 0x800;

    [Theory]
    [InlineData("pFLArOT53+w", "sceAgcDcbSetShRegisterDirect")]
    [InlineData("QhPDD513V0w", "sceAgcDcbSetShRegisterDirectGetSize")]
    [InlineData("43WJ08sSugE", "sceAgcDcbWaitOnAddressGetSize")]
    public void ExportRegistration_UsesConfirmedIdentity(string nid, string exportName)
    {
        var manager = CreateManager();
        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libSceAgc", export.LibraryName);
        Assert.Equal(Generation.Gen5, export.Target);
    }

    [Fact]
    public void SetShaderRegisterDirect_EmitsAndInterpretsPackedRegister()
    {
        var context = CreateContext(out var memory);
        context[CpuRegister.Rsi] = ((ulong)0xFEDC_BA98 << 32) | 0xABCD_00C8;

        Assert.Equal(OrbisGen2Result.ORBIS_GEN2_OK, CreateManager().Dispatch("pFLArOT53+w", context));
        Assert.Equal(PacketAddress, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress + 12, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(0xC001_7600u, ReadUInt32(memory, PacketAddress));
        Assert.Equal(0xC8u, ReadUInt32(memory, PacketAddress + 4));
        Assert.Equal(0xFEDC_BA98u, ReadUInt32(memory, PacketAddress + 8));

        WriteUInt64(memory, SubmitAddress, PacketAddress);
        WriteUInt32(memory, SubmitAddress + 8, 3);
        context[CpuRegister.Rdi] = SubmitAddress;
        Assert.Equal(0, AgcExports.DriverSubmitDcb(context));
        Assert.True(AgcExports.TryGetGraphicsShRegisterForTests(context, 0xC8, out var registerValue));
        Assert.Equal(0xFEDC_BA98u, registerValue);
    }

    [Fact]
    public void GetShaderRegisterDirectSize_ReturnsConsumedBytes()
    {
        var context = CreateContext(out var memory);
        var size = AgcExports.GetShaderRegisterDirectSize(context);
        Assert.Equal(12, size);
        Assert.Equal(12UL, context[CpuRegister.Rax]);
        Assert.Equal(0, AgcExports.SetShaderRegisterDirect(context));
        Assert.Equal(PacketAddress + (ulong)size, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(0u, 56u)]
    [InlineData(1u, 64u)]
    [InlineData(2u, 0u)]
    [InlineData(uint.MaxValue, 0u)]
    public void GetWaitOnAddressSize_ReturnsNativePacketCapacity(uint labelSize, uint expectedBytes)
    {
        var context = CreateContext(out _);
        context[CpuRegister.Rdi] = labelSize;
        var manager = CreateManager();
        Assert.Equal((OrbisGen2Result)expectedBytes, manager.Dispatch("43WJ08sSugE", context));
        Assert.Equal(expectedBytes, context[CpuRegister.Rax]);
    }

    [Fact]
    public void SetShaderRegisterDirect_RejectsNullCommandBuffer()
    {
        var context = CreateContext(out var memory);
        context[CpuRegister.Rdi] = 0;
        Assert.Equal(0, AgcExports.SetShaderRegisterDirect(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress, ReadUInt64(memory, CommandBufferAddress + 0x10));
    }

    [Theory]
    [InlineData(8u, 0u)]
    [InlineData(12u, 1u)]
    public void SetShaderRegisterDirect_RejectsInsufficientCapacity(uint availableBytes, uint reservedDwords)
    {
        var context = CreateContext(out var memory);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + availableBytes);
        WriteUInt32(memory, CommandBufferAddress + 0x30, reservedDwords);

        Assert.Equal(0, AgcExports.SetShaderRegisterDirect(context));
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(PacketAddress, ReadUInt64(memory, CommandBufferAddress + 0x10));
        Assert.Equal(0u, ReadUInt32(memory, PacketAddress));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void SetShaderRegisterDirect_RechecksCapacityAfterCallback(bool callbackSucceeds, bool providesCapacity)
    {
        var context = CreateContext(out var memory);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x20, 0x1234);
        WriteUInt64(memory, CommandBufferAddress + 0x28, 0x5678);
        var scheduler = new CapacityCallbackScheduler(memory, callbackSucceeds, providesCapacity);
        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;
            Assert.Equal(0, AgcExports.SetShaderRegisterDirect(context));
            Assert.Equal(1, scheduler.CallCount);
            Assert.Equal(providesCapacity ? PacketAddress : 0UL, context[CpuRegister.Rax]);
            Assert.Equal(PacketAddress + (providesCapacity ? 12UL : 0UL), ReadUInt64(memory, CommandBufferAddress + 0x10));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SetShaderRegisterRangeDirect_UsesBufferProvidedByCallback(bool reservePayload)
    {
        var context = CreateContext(out var memory);
        var replacementPacketAddress = PacketAddress + 0x100;
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 8);
        WriteUInt64(memory, CommandBufferAddress + 0x20, 0x1234);
        WriteUInt64(memory, CommandBufferAddress + 0x28, 0x5678);
        WriteUInt32(memory, SubmitAddress, 17);
        WriteUInt32(memory, SubmitAddress + 4, 29);
        WriteUInt32(memory, replacementPacketAddress + 8, 0xAABBCCDD);
        WriteUInt32(memory, replacementPacketAddress + 12, 0x11223344);
        context[CpuRegister.Rsi] = 0x40;
        context[CpuRegister.Rdx] = reservePayload ? 0 : SubmitAddress;
        context[CpuRegister.Rcx] = 2;
        var scheduler = new CapacityCallbackScheduler(memory, true, true, 4, replacementPacketAddress);
        var previousScheduler = GuestThreadExecution.Scheduler;
        try
        {
            GuestThreadExecution.Scheduler = scheduler;

            Assert.Equal(0, AgcExports.CbSetShRegisterRangeDirect(context));

            Assert.Equal(1, scheduler.CallCount);
            Assert.Equal(replacementPacketAddress, context[CpuRegister.Rax]);
            Assert.Equal(0x6875000Du, ReadUInt32(memory, PacketAddress + 4));
            Assert.Equal(reservePayload ? 0xAABBCCDDu : 17u, ReadUInt32(memory, replacementPacketAddress + 8));
            Assert.Equal(reservePayload ? 0x11223344u : 29u, ReadUInt32(memory, replacementPacketAddress + 12));
            Assert.Equal(replacementPacketAddress + 16, ReadUInt64(memory, CommandBufferAddress + 0x10));
        }
        finally
        {
            GuestThreadExecution.Scheduler = previousScheduler;
        }
    }

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x1000);
        WriteUInt64(memory, CommandBufferAddress + 0x10, PacketAddress);
        WriteUInt64(memory, CommandBufferAddress + 0x18, PacketAddress + 12);
        return new CpuContext(memory, Generation.Gen5)
        {
            [CpuRegister.Rdi] = CommandBufferAddress,
            [CpuRegister.Rsi] = ((ulong)0x1122_3344 << 32) | 0xC8,
        };
    }

    private static ModuleManager CreateManager()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        return manager;
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static void WriteUInt64(FakeCpuMemory memory, ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private sealed class CapacityCallbackScheduler(FakeCpuMemory memory, bool succeeds, bool providesCapacity,
        uint expectedDwords = 3, ulong replacementPacketAddress = PacketAddress) : IGuestThreadScheduler
    {
        public int CallCount { get; private set; }
        public bool SupportsGuestContextTransfer => false;

        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong bufferAddress,
            ulong requiredDwords, ulong userData, ulong stackAddress, ulong stackSize, string reason,
            out ulong returnValue, out string? error)
        {
            CallCount++;
            Assert.Equal(0x1234UL, entryPoint);
            Assert.Equal(CommandBufferAddress, bufferAddress);
            Assert.Equal((ulong)expectedDwords, requiredDwords);
            Assert.Equal(0x5678UL, userData);
            if (providesCapacity)
            {
                WriteUInt64(memory, CommandBufferAddress + 0x10, replacementPacketAddress);
                WriteUInt64(memory, CommandBufferAddress + 0x18, replacementPacketAddress + expectedDwords * 4);
            }

            returnValue = 0;
            error = succeeds ? null : "The capacity callback failed.";
            return succeeds;
        }

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context) => throw new NotSupportedException();
        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error) => throw new NotSupportedException();
        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error) => throw new NotSupportedException();
        public void Pump(CpuContext callerContext, string reason) => throw new NotSupportedException();
        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => throw new NotSupportedException();
        public bool HasPendingGuestExceptionForCurrentThread() => false;
        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => throw new NotSupportedException();
        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => throw new NotSupportedException();
        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => throw new NotSupportedException();
        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong firstArgument, ulong secondArgument,
            ulong stackAddress, ulong stackSize, string reason, out string? error) => throw new NotSupportedException();
        public bool TryCallGuestFunction(CpuContext callerContext, ulong entryPoint, ulong firstArgument, ulong secondArgument,
            ulong thirdArgument, ulong fourthArgument, ulong stackAddress, ulong stackSize, string reason,
            out ulong returnValue, out string? error) => throw new NotSupportedException();
        public bool TryCallGuestContinuation(CpuContext callerContext, GuestCpuContinuation continuation, string reason,
            out string? error) => throw new NotSupportedException();
        public bool TryRaiseGuestException(CpuContext callerContext, ulong threadHandle, ulong handler, int exceptionType,
            out string? error) => throw new NotSupportedException();
    }
}
