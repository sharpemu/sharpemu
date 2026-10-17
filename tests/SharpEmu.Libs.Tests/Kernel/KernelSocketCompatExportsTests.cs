// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

public sealed class KernelSocketCompatExportsTests
{
    [Fact]
    public void PosixSendTo_SendsIpv4DatagramFromKernelDescriptor()
    {
        const ulong memoryBase = 0x0000_7FFF_3000_0000;
        const ulong payloadAddress = memoryBase + 0x100;
        const ulong sockaddrAddress = memoryBase + 0x200;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        using var receiver = new System.Net.Sockets.Socket(
            AddressFamily.InterNetwork,
            SocketType.Dgram,
            ProtocolType.Udp);
        receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        receiver.ReceiveTimeout = 1000;
        var receiverPort = ((IPEndPoint)receiver.LocalEndPoint!).Port;

        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelSocketCompatExports.Socket(context));
        var guestFd = checked((int)context[CpuRegister.Rax]);

        try
        {
            byte[] payload = [0x10, 0x20, 0x30, 0x40];
            Assert.True(memory.TryWrite(payloadAddress, payload));

            Span<byte> sockaddr = stackalloc byte[16];
            sockaddr[0] = 16;
            sockaddr[1] = 2;
            BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..4], checked((ushort)receiverPort));
            IPAddress.Loopback.GetAddressBytes().CopyTo(sockaddr[4..8]);
            Assert.True(memory.TryWrite(sockaddrAddress, sockaddr));

            context[CpuRegister.Rdi] = unchecked((uint)guestFd);
            context[CpuRegister.Rsi] = payloadAddress;
            context[CpuRegister.Rdx] = unchecked((uint)payload.Length);
            context[CpuRegister.Rcx] = 0;
            context[CpuRegister.R8] = sockaddrAddress;
            context[CpuRegister.R9] = 16;

            Assert.Equal(payload.Length, NetExports.PosixSendTo(context));
            Assert.Equal(unchecked((ulong)(long)payload.Length), context[CpuRegister.Rax]);

            var received = new byte[payload.Length];
            EndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            Assert.Equal(payload.Length, receiver.ReceiveFrom(received, ref sender));
            Assert.Equal(payload, received);
        }
        finally
        {
            KernelSocketCompatExports.TryCloseSocketFd(guestFd);
        }
    }

    [Fact]
    public void PosixReceiveFrom_ReceivesDatagramAndWritesSourceAddress()
    {
        const ulong memoryBase = 0x0000_7FFF_3100_0000;
        const ulong bindAddress = memoryBase + 0x100;
        const ulong localAddress = memoryBase + 0x200;
        const ulong localLengthAddress = memoryBase + 0x240;
        const ulong payloadAddress = memoryBase + 0x300;
        const ulong sourceAddress = memoryBase + 0x400;
        const ulong sourceLengthAddress = memoryBase + 0x440;
        var memory = new FakeCpuMemory(memoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);

        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = 0;
        Assert.Equal(0, KernelSocketCompatExports.Socket(context));
        var guestFd = checked((int)context[CpuRegister.Rax]);

        try
        {
            WriteSockaddr(memory, bindAddress, IPAddress.Loopback, 0);
            context[CpuRegister.Rdi] = unchecked((uint)guestFd);
            context[CpuRegister.Rsi] = bindAddress;
            context[CpuRegister.Rdx] = 16;
            Assert.Equal(0, KernelSocketCompatExports.Bind(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            WriteUInt32(memory, localLengthAddress, 16);
            context[CpuRegister.Rdi] = unchecked((uint)guestFd);
            context[CpuRegister.Rsi] = localAddress;
            context[CpuRegister.Rdx] = localLengthAddress;
            Assert.Equal(0, KernelSocketCompatExports.Getsockname(context));
            var guestPort = ReadPort(memory, localAddress);

            using var sender = new System.Net.Sockets.Socket(
                AddressFamily.InterNetwork,
                SocketType.Dgram,
                ProtocolType.Udp);
            byte[] payload = [0x50, 0x60, 0x70, 0x80];
            Assert.Equal(
                payload.Length,
                sender.SendTo(payload, new IPEndPoint(IPAddress.Loopback, guestPort)));
            var senderPort = ((IPEndPoint)sender.LocalEndPoint!).Port;

            WriteUInt32(memory, sourceLengthAddress, 16);
            context[CpuRegister.Rdi] = unchecked((uint)guestFd);
            context[CpuRegister.Rsi] = payloadAddress;
            context[CpuRegister.Rdx] = unchecked((uint)payload.Length);
            context[CpuRegister.Rcx] = 0x80;
            context[CpuRegister.R8] = sourceAddress;
            context[CpuRegister.R9] = sourceLengthAddress;

            Assert.Equal(payload.Length, NetExports.PosixReceiveFrom(context));
            Assert.Equal(payload, ReadBytes(memory, payloadAddress, payload.Length));
            Assert.Equal(16U, ReadUInt32(memory, sourceLengthAddress));
            Assert.Equal(senderPort, ReadPort(memory, sourceAddress));
        }
        finally
        {
            KernelSocketCompatExports.TryCloseSocketFd(guestFd);
        }
    }

    [Fact]
    public void Connect_InvalidSockaddrLeavesFdOpenForGuestClose()
    {
        const ulong memoryBase = 0x0000_7FFF_3000_0000;
        var context = new CpuContext(new FakeCpuMemory(memoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 2;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 6;

        Assert.Equal(0, KernelSocketCompatExports.Socket(context));
        Assert.NotEqual(ulong.MaxValue, context[CpuRegister.Rax]);
        var guestFd = checked((int)context[CpuRegister.Rax]);

        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            context[CpuRegister.Rsi] = memoryBase;
            context[CpuRegister.Rdx] = 0;

            Assert.Equal(0, KernelSocketCompatExports.Connect(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);

            context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                KernelMemoryCompatExports.PosixClose(context));
            Assert.Equal(0UL, context[CpuRegister.Rax]);

            // A second close of an already-closed fd fails per the POSIX ABI:
            // -1 with errno set, not the raw Orbis NOT_FOUND sentinel.
            context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            Assert.Equal(-1, KernelMemoryCompatExports.PosixClose(context));
            Assert.Equal(ulong.MaxValue, context[CpuRegister.Rax]);
        }
        finally
        {
            KernelSocketCompatExports.TryCloseSocketFd(guestFd);
        }
    }

    private static void WriteSockaddr(
        FakeCpuMemory memory,
        ulong address,
        IPAddress ipAddress,
        int port)
    {
        Span<byte> sockaddr = stackalloc byte[16];
        sockaddr[0] = 16;
        sockaddr[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..4], checked((ushort)port));
        ipAddress.GetAddressBytes().CopyTo(sockaddr[4..8]);
        Assert.True(memory.TryWrite(address, sockaddr));
    }

    private static int ReadPort(FakeCpuMemory memory, ulong address)
    {
        Span<byte> sockaddr = stackalloc byte[16];
        Assert.True(memory.TryRead(address, sockaddr));
        return BinaryPrimitives.ReadUInt16BigEndian(sockaddr[2..4]);
    }

    private static byte[] ReadBytes(FakeCpuMemory memory, ulong address, int length)
    {
        var bytes = new byte[length];
        Assert.True(memory.TryRead(address, bytes));
        return bytes;
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
