// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelReadEventTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong EqueueAddress = BaseAddress + 0x100;
    private const ulong SockaddrAddress = BaseAddress + 0x200;
    private const ulong EventAddress = BaseAddress + 0x300;
    private const ulong OutCountAddress = BaseAddress + 0x400;
    private const ulong TimeoutAddress = BaseAddress + 0x500;
    private const ulong ReadBufferAddress = BaseAddress + 0x600;

    [Fact]
    public async Task ReadEvent_WakesForSocketDataAndClearsAfterRead()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var acceptTask = listener.AcceptTcpClientAsync();

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSocketCompatExports.Socket(ctx));
        var fileDescriptor = unchecked((int)ctx[CpuRegister.Rax]);
        Assert.True(fileDescriptor >= 3);

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        WriteSockaddr(memory, SockaddrAddress, port);
        ctx[CpuRegister.Rdi] = unchecked((uint)fileDescriptor);
        ctx[CpuRegister.Rsi] = SockaddrAddress;
        ctx[CpuRegister.Rdx] = 16;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelSocketCompatExports.Connect(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);

        using var server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(2));
        var equeue = CreateEqueue(ctx, memory);
        const ulong expectedUserData = 0xCAFE_BABE_1234_5678;

        ctx[CpuRegister.Rdi] = equeue;
        ctx[CpuRegister.Rsi] = unchecked((uint)fileDescriptor);
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rcx] = expectedUserData;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelAddReadEvent(ctx));

        await server.GetStream().WriteAsync(new byte[] { 0x5A });
        WriteUInt32(memory, TimeoutAddress, 1_000_000);
        ConfigureWait(ctx, equeue, TimeoutAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelWaitEqueue(ctx));
        Assert.Equal(1u, ReadUInt32(memory, OutCountAddress));

        Span<byte> eventBytes = stackalloc byte[0x20];
        Assert.True(memory.TryRead(EventAddress, eventBytes));
        Assert.Equal(
            unchecked((uint)fileDescriptor),
            BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[0x00..]));
        Assert.Equal(
            KernelEventQueueCompatExports.KernelEventFilterRead,
            BinaryPrimitives.ReadInt16LittleEndian(eventBytes[0x08..]));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16LittleEndian(eventBytes[0x0A..]));
        Assert.True(BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[0x10..]) >= 1);
        Assert.Equal(
            expectedUserData,
            BinaryPrimitives.ReadUInt64LittleEndian(eventBytes[0x18..]));

        ctx[CpuRegister.Rdi] = unchecked((uint)fileDescriptor);
        ctx[CpuRegister.Rsi] = ReadBufferAddress;
        ctx[CpuRegister.Rdx] = 1;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelReadUnderscore(ctx));
        Assert.Equal(1UL, ctx[CpuRegister.Rax]);
        Span<byte> payload = stackalloc byte[1];
        Assert.True(memory.TryRead(ReadBufferAddress, payload));
        Assert.Equal(0x5A, payload[0]);

        WriteUInt32(memory, TimeoutAddress, 0);
        ConfigureWait(ctx, equeue, TimeoutAddress);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            KernelEventQueueCompatExports.KernelWaitEqueue(ctx));
        Assert.Equal(0u, ReadUInt32(memory, OutCountAddress));

        ctx[CpuRegister.Rdi] = equeue;
        ctx[CpuRegister.Rsi] = unchecked((uint)fileDescriptor);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteReadEvent(ctx));

        ctx[CpuRegister.Rdi] = unchecked((uint)fileDescriptor);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelMemoryCompatExports.KernelClose(ctx));
        ctx[CpuRegister.Rdi] = equeue;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
    }

    [Fact]
    public void AddReadEvent_RejectsUnknownDescriptor()
    {
        var memory = new FakeCpuMemory(BaseAddress, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        var equeue = CreateEqueue(ctx, memory);

        ctx[CpuRegister.Rdi] = equeue;
        ctx[CpuRegister.Rsi] = 0x7FFF_FFFF;
        ctx[CpuRegister.Rdx] = 1;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelEventQueueCompatExports.KernelAddReadEvent(ctx));

        ctx[CpuRegister.Rdi] = equeue;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelDeleteEqueue(ctx));
    }

    private static ulong CreateEqueue(CpuContext ctx, FakeCpuMemory memory)
    {
        ctx[CpuRegister.Rdi] = EqueueAddress;
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_OK,
            KernelEventQueueCompatExports.KernelCreateEqueue(ctx));
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(EqueueAddress, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static void ConfigureWait(CpuContext ctx, ulong equeue, ulong timeoutAddress)
    {
        ctx[CpuRegister.Rdi] = equeue;
        ctx[CpuRegister.Rsi] = EventAddress;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rcx] = OutCountAddress;
        ctx[CpuRegister.R8] = timeoutAddress;
    }

    private static void WriteSockaddr(FakeCpuMemory memory, ulong address, int port)
    {
        Span<byte> sockaddr = stackalloc byte[16];
        sockaddr[0] = 16;
        sockaddr[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..], unchecked((ushort)port));
        IPAddress.Loopback.GetAddressBytes().CopyTo(sockaddr[4..]);
        Assert.True(memory.TryWrite(address, sockaddr));
    }

    private static void WriteUInt32(FakeCpuMemory memory, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(memory.TryWrite(address, bytes));
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
