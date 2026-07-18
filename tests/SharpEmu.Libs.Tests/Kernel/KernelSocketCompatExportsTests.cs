// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;
using HostSocket = System.Net.Sockets.Socket;

namespace SharpEmu.Libs.Tests.Kernel;

[CollectionDefinition("KernelSocketCompat", DisableParallelization = true)]
public sealed class KernelSocketCompatCollectionDefinition;

[Collection("KernelSocketCompat")]
public sealed class KernelSocketCompatExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x0000_7FFF_3000_0000;
    private const ulong TlsBase = MemoryBase + 0x1000;
    private const ulong PayloadAddress = MemoryBase + 0x2000;
    private const ulong SockaddrAddress = MemoryBase + 0x3000;
    private const ulong SourceAddress = MemoryBase + 0x3100;
    private const ulong SourceLengthAddress = MemoryBase + 0x3200;
    private const ulong ErrnoAddress = TlsBase + 0x40;
    private const int AddressFamilyInet = 2;
    private const int SocketTypeStream = 1;
    private const int SocketTypeDatagram = 2;
    private const int SocketTypeNonBlocking = 0x20000000;
    private readonly string? _previousNetRedirect;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10_000);
    private readonly CpuContext _context;

    public KernelSocketCompatExportsTests()
    {
        _previousNetRedirect = Environment.GetEnvironmentVariable("SHARPEMU_NET_REDIRECT");
        Environment.SetEnvironmentVariable("SHARPEMU_NET_REDIRECT", null);
        _context = new CpuContext(_memory, Generation.Gen5)
        {
            FsBase = TlsBase,
        };
    }

    [Fact]
    public void Sendto_SendsGuestDatagramToLoopbackEndpoint()
    {
        var guestFd = CreateGuestUdpSocket();
        try
        {
            using var receiver = new HostSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
            {
                ReceiveTimeout = 2_000,
            };
            receiver.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var receiverEndpoint = Assert.IsType<IPEndPoint>(receiver.LocalEndPoint);
            var payload = new byte[] { 0x53, 0x68, 0x61, 0x72, 0x70, 0x45, 0x6D, 0x75 };
            Assert.True(_memory.TryWrite(PayloadAddress, payload));
            WriteGuestSockaddr(SockaddrAddress, receiverEndpoint);

            _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            _context[CpuRegister.Rsi] = PayloadAddress;
            _context[CpuRegister.Rdx] = unchecked((ulong)payload.Length);
            _context[CpuRegister.Rcx] = 0;
            _context[CpuRegister.R8] = SockaddrAddress;
            _context[CpuRegister.R9] = 16;

            Assert.Equal(0, KernelSocketCompatExports.Sendto(_context));
            Assert.Equal(unchecked((ulong)payload.Length), _context[CpuRegister.Rax]);

            var received = new byte[64];
            var receivedLength = receiver.Receive(received);
            Assert.Equal(payload, received.AsSpan(0, receivedLength).ToArray());
        }
        finally
        {
            CloseGuestSocket(guestFd);
        }
    }

    [Fact]
    public void Recvfrom_ConsumesPrequeuedDatagramAndWritesSourceSockaddr()
    {
        var guestFd = CreateGuestUdpSocket();
        try
        {
            var guestEndpoint = BindGuestSocketToEphemeralLoopbackPort(guestFd);
            using var sender = new HostSocket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            sender.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            var senderEndpoint = Assert.IsType<IPEndPoint>(sender.LocalEndPoint);
            var payload = new byte[] { 0x10, 0x20, 0x30, 0x40, 0x50 };
            Assert.Equal(payload.Length, sender.SendTo(payload, guestEndpoint));
            Assert.True(_context.TryWriteUInt32(SourceLengthAddress, 16));

            _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            _context[CpuRegister.Rsi] = PayloadAddress;
            _context[CpuRegister.Rdx] = 64;
            _context[CpuRegister.Rcx] = 0;
            _context[CpuRegister.R8] = SourceAddress;
            _context[CpuRegister.R9] = SourceLengthAddress;

            Assert.Equal(0, KernelSocketCompatExports.Recvfrom(_context));
            Assert.Equal(unchecked((ulong)payload.Length), _context[CpuRegister.Rax]);

            var received = new byte[payload.Length];
            Assert.True(_memory.TryRead(PayloadAddress, received));
            Assert.Equal(payload, received);
            Assert.True(_context.TryReadUInt32(SourceLengthAddress, out var sourceLength));
            Assert.Equal(16u, sourceLength);
            Assert.Equal(senderEndpoint, ReadGuestSockaddr(SourceAddress));
        }
        finally
        {
            CloseGuestSocket(guestFd);
        }
    }

    [Fact]
    public void Recvfrom_EmptyNonBlockingSocketReturnsWouldBlockErrno()
    {
        var guestFd = CreateGuestUdpSocket(nonBlocking: true);
        try
        {
            _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
            _context[CpuRegister.Rsi] = PayloadAddress;
            _context[CpuRegister.Rdx] = 64;
            _context[CpuRegister.Rcx] = 0;
            _context[CpuRegister.R8] = 0;
            _context[CpuRegister.R9] = 0;

            Assert.Equal(0, KernelSocketCompatExports.Recvfrom(_context));
            Assert.Equal(ulong.MaxValue, _context[CpuRegister.Rax]);
            Assert.True(_context.TryReadInt32(ErrnoAddress, out var errno));
            Assert.Equal(35, errno);
        }
        finally
        {
            CloseGuestSocket(guestFd);
        }
    }

    [Fact]
    public void Connect_FailureLeavesFdOpenForGuestClose()
    {
        var guestFd = CreateGuestTcpSocket();

        // Non-loopback target is denied by the outbound policy (no SHARPEMU_NET_REDIRECT).
        WriteGuestSockaddr(SockaddrAddress, new IPEndPoint(IPAddress.Parse("203.0.113.1"), 443));
        _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
        _context[CpuRegister.Rsi] = SockaddrAddress;
        _context[CpuRegister.Rdx] = 16;
        Assert.Equal(0, KernelSocketCompatExports.Connect(_context));
        Assert.Equal(ulong.MaxValue, _context[CpuRegister.Rax]);

        // POSIX: a failed connect does not close the fd; the guest's close must find it.
        _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.PosixClose(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);

        _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            KernelMemoryCompatExports.PosixClose(_context));
    }

    [Theory]
    [InlineData("oBr313PppNE", "sendto")]
    [InlineData("lUk6wrGXyMw", "recvfrom")]
    public void UdpNids_RegisterAsLibKernelExports(string nid, string exportName)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export), $"NID {nid} did not register.");
        Assert.Equal(exportName, export.Name);
        Assert.Equal("libKernel", export.LibraryName);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("SHARPEMU_NET_REDIRECT", _previousNetRedirect);
    }

    private int CreateGuestTcpSocket()
    {
        _context[CpuRegister.Rdi] = AddressFamilyInet;
        _context[CpuRegister.Rsi] = SocketTypeStream;
        _context[CpuRegister.Rdx] = 6;

        Assert.Equal(0, KernelSocketCompatExports.Socket(_context));
        Assert.NotEqual(ulong.MaxValue, _context[CpuRegister.Rax]);
        return checked((int)_context[CpuRegister.Rax]);
    }

    private int CreateGuestUdpSocket(bool nonBlocking = false)
    {
        _context[CpuRegister.Rdi] = AddressFamilyInet;
        _context[CpuRegister.Rsi] = unchecked((ulong)(SocketTypeDatagram |
            (nonBlocking ? SocketTypeNonBlocking : 0)));
        _context[CpuRegister.Rdx] = 17;

        Assert.Equal(0, KernelSocketCompatExports.Socket(_context));
        Assert.NotEqual(ulong.MaxValue, _context[CpuRegister.Rax]);
        return checked((int)_context[CpuRegister.Rax]);
    }

    private IPEndPoint BindGuestSocketToEphemeralLoopbackPort(int guestFd)
    {
        WriteGuestSockaddr(SockaddrAddress, new IPEndPoint(IPAddress.Loopback, 0));
        _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
        _context[CpuRegister.Rsi] = SockaddrAddress;
        _context[CpuRegister.Rdx] = 16;
        Assert.Equal(0, KernelSocketCompatExports.Bind(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);

        Assert.True(_context.TryWriteUInt32(SourceLengthAddress, 16));
        _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
        _context[CpuRegister.Rsi] = SourceAddress;
        _context[CpuRegister.Rdx] = SourceLengthAddress;
        Assert.Equal(0, KernelSocketCompatExports.Getsockname(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        return ReadGuestSockaddr(SourceAddress);
    }

    private void CloseGuestSocket(int guestFd)
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)guestFd);
        _ = KernelMemoryCompatExports.PosixClose(_context);
    }

    private void WriteGuestSockaddr(ulong address, IPEndPoint endpoint)
    {
        Span<byte> sockaddr = stackalloc byte[16];
        sockaddr.Clear();
        sockaddr[0] = 16;
        sockaddr[1] = AddressFamilyInet;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr[2..4], checked((ushort)endpoint.Port));
        endpoint.Address.GetAddressBytes().CopyTo(sockaddr[4..8]);
        Assert.True(_memory.TryWrite(address, sockaddr));
    }

    private IPEndPoint ReadGuestSockaddr(ulong address)
    {
        Span<byte> sockaddr = stackalloc byte[16];
        Assert.True(_memory.TryRead(address, sockaddr));
        Assert.Equal(16, sockaddr[0]);
        Assert.Equal(AddressFamilyInet, sockaddr[1]);
        var port = BinaryPrimitives.ReadUInt16BigEndian(sockaddr[2..4]);
        return new IPEndPoint(new IPAddress(sockaddr[4..8]), port);
    }
}
