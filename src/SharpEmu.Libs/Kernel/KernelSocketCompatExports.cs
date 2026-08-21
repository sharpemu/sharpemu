// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

internal static class KernelSocketCompatExports
{
    private sealed class EmulatedSocketState
    {
        public int Family;
        public int Type;
        public int Protocol;
        public TcpClient? Client;
        public NetworkStream? Stream;
        public System.Net.Sockets.Socket? DatagramSocket;
        public IPAddress BoundAddress = IPAddress.Any;
        public int BoundPort;
        public bool Bound;
        public bool Connected;
        public bool ReuseAddress;
        public bool KeepAlive;
        public bool Broadcast;
        public bool ReusePort;
        public bool IPv6Only;
        public bool NoDelay;
        public int SendBufferSize;
        public int ReceiveBufferSize;
        public int SendLowWater = 1;
        public int ReceiveLowWater = 1;
    }

    private static readonly object Gate = new();
    private static readonly Dictionary<int, EmulatedSocketState> Sockets = new();

    internal static bool IsEmulatedSocketFd(int fd)
    {
        lock (Gate)
        {
            return Sockets.ContainsKey(fd);
        }
    }

    internal static bool TryGetReadEventState(
        int fd,
        ulong lowWater,
        out bool ready,
        out ulong availableBytes,
        out ushort eventFlags)
    {
        ready = false;
        availableBytes = 0;
        eventFlags = 0;

        lock (Gate)
        {
            if (!Sockets.TryGetValue(fd, out var state))
            {
                return false;
            }

            if (!state.Connected || state.Client is null)
            {
                return true;
            }

            try
            {
                var socket = state.Client.Client;
                var readSignaled = socket.Poll(0, SelectMode.SelectRead);
                availableBytes = unchecked((ulong)Math.Max(0, socket.Available));
                if (readSignaled && availableBytes == 0)
                {
                    ready = true;
                    eventFlags = KernelEventQueueCompatExports.KernelEventFlagEof;
                }
                else
                {
                    ready = availableBytes >= Math.Max(1UL, lowWater);
                }
            }
            catch (SocketException)
            {
                ready = true;
                eventFlags = KernelEventQueueCompatExports.KernelEventFlagEof;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
        }

        return true;
    }

    internal static bool TryCloseSocketFd(int fd)
    {
        lock (Gate)
        {
            if (!Sockets.Remove(fd, out var state))
            {
                return false;
            }

            DisposeEmulatedSocket(state);
            return true;
        }
    }

    internal static bool TryReadSocketFd(
        CpuContext ctx,
        int fd,
        ulong bufferAddress,
        int requested,
        out ulong bytesRead,
        out OrbisGen2Result error)
    {
        bytesRead = 0;
        error = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;

        if (!TryGetEmulatedSocketState(fd, out var state) ||
            state is null ||
            !state.Connected ||
            state.Stream is null)
        {
            return false;
        }

        var socketBuffer = GC.AllocateUninitializedArray<byte>(requested);
        int socketRead;
        try
        {
            socketRead = state.Stream.Read(socketBuffer, 0, requested);
        }
        catch (IOException)
        {
            return false;
        }

        if (socketRead > 0 && !ctx.Memory.TryWrite(bufferAddress, socketBuffer.AsSpan(0, socketRead)))
        {
            error = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return true;
        }

        bytesRead = unchecked((ulong)socketRead);
        error = OrbisGen2Result.ORBIS_GEN2_OK;
        return true;
    }

    internal static bool TryWriteSocketFd(
        CpuContext ctx,
        int fd,
        byte[] payload,
        out OrbisGen2Result error)
    {
        error = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;

        if (!TryGetEmulatedSocketState(fd, out var state) ||
            state is null ||
            !state.Connected ||
            state.Stream is null)
        {
            return false;
        }

        try
        {
            state.Stream.Write(payload, 0, payload.Length);
            state.Stream.Flush();
        }
        catch (IOException)
        {
            return false;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NET"), "1", StringComparison.Ordinal))
        {
            Console.Out.Write(Encoding.UTF8.GetString(payload));
            Console.Out.Flush();
        }

        error = OrbisGen2Result.ORBIS_GEN2_OK;
        return true;
    }

    internal static int PosixSetSocketOption(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var valueLength = unchecked((int)ctx[CpuRegister.R8]);

        if (valueAddress == 0)
        {
            return PosixSocketFailure(ctx, 14);
        }

        if (valueLength < sizeof(int))
        {
            return PosixSocketFailure(ctx, 22);
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(valueAddress, valueBytes))
        {
            return PosixSocketFailure(ctx, 14);
        }

        var value = BinaryPrimitives.ReadInt32LittleEndian(valueBytes);
        lock (Gate)
        {
            if (!Sockets.TryGetValue(fd, out var state))
            {
                return PosixSocketFailure(ctx, 9);
            }

            if (!TrySetSocketOptionLocked(state, level, option, value))
            {
                LogNet($"setsockopt unsupported: fd={fd} level=0x{level:X} option=0x{option:X}");
                return PosixSocketFailure(ctx, 22);
            }
        }

        LogNet($"setsockopt: fd={fd} level=0x{level:X} option=0x{option:X} value={value}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    internal static int PosixSendTo(CpuContext ctx)
    {
        const int msgDontRoute = 0x4;
        const int msgDontWait = 0x80;
        const int msgNoSignal = 0x20000;

        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = ctx[CpuRegister.Rdx];
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);
        var sockaddrAddress = ctx[CpuRegister.R8];
        var addrlen = unchecked((int)ctx[CpuRegister.R9]);

        if (!TryGetEmulatedSocketState(fd, out var state) || state is null)
        {
            return PosixSocketFailure(ctx, 9);
        }

        if (state.Type != 2 || state.DatagramSocket is null)
        {
            return PosixSocketFailure(ctx, 41);
        }

        if (length > int.MaxValue)
        {
            return PosixSocketFailure(ctx, 40);
        }

        if (length != 0 && bufferAddress == 0)
        {
            return PosixSocketFailure(ctx, 14);
        }

        if (sockaddrAddress == 0)
        {
            return PosixSocketFailure(ctx, 39);
        }

        const int supportedFlags = msgDontRoute | msgDontWait | msgNoSignal;
        if ((flags & ~supportedFlags) != 0)
        {
            return PosixSocketFailure(ctx, 45);
        }

        if (!TryParseGuestSockaddrIn(sockaddrAddress, addrlen, ctx, out var ipAddress, out var port))
        {
            return PosixSocketFailure(ctx, 47);
        }

        var redirectApplied = TryApplyNetRedirect(ref ipAddress);
        if (!IsGuestSocketOutboundAllowed(ipAddress, redirectApplied))
        {
            LogNet($"sendto denied by outbound policy: fd={fd} ip={ipAddress} port={port} len={length}");
            return PosixSocketFailure(ctx, 51);
        }

        var payload = GC.AllocateUninitializedArray<byte>(checked((int)length));
        if (payload.Length != 0 && !ctx.Memory.TryRead(bufferAddress, payload))
        {
            return PosixSocketFailure(ctx, 14);
        }

        var socketFlags = (flags & msgDontRoute) != 0
            ? SocketFlags.DontRoute
            : SocketFlags.None;
        var socket = state.DatagramSocket;
        var restoreBlocking = false;
        var priorBlocking = socket.Blocking;
        try
        {
            if ((flags & msgDontWait) != 0 && priorBlocking)
            {
                socket.Blocking = false;
                restoreBlocking = true;
            }

            var sent = socket.SendTo(payload, socketFlags, new IPEndPoint(ipAddress, port));
            LogNet($"sendto: fd={fd} ip={ipAddress} port={port} len={length} sent={sent} flags=0x{flags:X}");
            return ctx.SetReturn(sent, typeof(long));
        }
        catch (SocketException exception)
        {
            var errno = MapSocketErrorToPosixErrno(exception.SocketErrorCode);
            LogNet($"sendto failed: fd={fd} ip={ipAddress} port={port} len={length} socket_error={exception.SocketErrorCode} errno={errno}");
            return PosixSocketFailure(ctx, errno);
        }
        catch (ObjectDisposedException)
        {
            return PosixSocketFailure(ctx, 9);
        }
        finally
        {
            if (restoreBlocking)
            {
                try { socket.Blocking = priorBlocking; } catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }

    internal static int PosixReceiveFrom(CpuContext ctx)
    {
        const int msgPeek = 0x2;
        const int msgDontRoute = 0x4;
        const int msgWaitAll = 0x40;
        const int msgDontWait = 0x80;
        const int msgNoSignal = 0x20000;

        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = ctx[CpuRegister.Rdx];
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);
        var sourceAddress = ctx[CpuRegister.R8];
        var sourceLengthAddress = ctx[CpuRegister.R9];

        if (!TryGetEmulatedSocketState(fd, out var state) || state is null)
        {
            return PosixSocketFailure(ctx, 9);
        }

        if (state.Type != 2 || state.DatagramSocket is null)
        {
            return PosixSocketFailure(ctx, 41);
        }

        if (length > int.MaxValue)
        {
            return PosixSocketFailure(ctx, 40);
        }

        if (length != 0 && bufferAddress == 0)
        {
            return PosixSocketFailure(ctx, 14);
        }

        if (sourceAddress != 0 && sourceLengthAddress == 0)
        {
            return PosixSocketFailure(ctx, 14);
        }

        const int supportedFlags = msgPeek | msgDontRoute | msgWaitAll | msgDontWait | msgNoSignal;
        if ((flags & ~supportedFlags) != 0)
        {
            return PosixSocketFailure(ctx, 45);
        }

        uint sourceCapacity = 0;
        if (sourceAddress != 0)
        {
            Span<byte> sourceLengthBytes = stackalloc byte[sizeof(uint)];
            if (!ctx.Memory.TryRead(sourceLengthAddress, sourceLengthBytes))
            {
                return PosixSocketFailure(ctx, 14);
            }

            sourceCapacity = BinaryPrimitives.ReadUInt32LittleEndian(sourceLengthBytes);
        }

        var payload = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var socketFlags = SocketFlags.None;
        if ((flags & msgPeek) != 0)
        {
            socketFlags |= SocketFlags.Peek;
        }
        if ((flags & msgDontRoute) != 0)
        {
            socketFlags |= SocketFlags.DontRoute;
        }
        // MSG_WAITALL does not change datagram boundaries.

        var socket = state.DatagramSocket;
        var restoreBlocking = false;
        var priorBlocking = socket.Blocking;
        try
        {
            if ((flags & msgDontWait) != 0 && priorBlocking)
            {
                socket.Blocking = false;
                restoreBlocking = true;
            }

            EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
            var received = socket.ReceiveFrom(payload, socketFlags, ref endpoint);
            if (received != 0 && !ctx.Memory.TryWrite(bufferAddress, payload.AsSpan(0, received)))
            {
                return PosixSocketFailure(ctx, 14);
            }

            if (sourceAddress != 0 && endpoint is IPEndPoint sourceEndpoint)
            {
                Span<byte> guestAddress = stackalloc byte[16];
                guestAddress[0] = 16;
                guestAddress[1] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(guestAddress[2..4], checked((ushort)sourceEndpoint.Port));
                sourceEndpoint.Address.MapToIPv4().GetAddressBytes().CopyTo(guestAddress[4..8]);
                var writeLength = (int)Math.Min(sourceCapacity, (uint)guestAddress.Length);
                if ((writeLength != 0 && !ctx.Memory.TryWrite(sourceAddress, guestAddress[..writeLength])) ||
                    !TryWriteUInt32(ctx, sourceLengthAddress, (uint)guestAddress.Length))
                {
                    return PosixSocketFailure(ctx, 14);
                }
            }

            LogNet($"recvfrom: fd={fd} len={length} received={received} flags=0x{flags:X} source={endpoint}");
            return ctx.SetReturn(received, typeof(long));
        }
        catch (SocketException exception)
        {
            var errno = MapSocketErrorToPosixErrno(exception.SocketErrorCode);
            if (errno != 35)
            {
                LogNet($"recvfrom failed: fd={fd} len={length} socket_error={exception.SocketErrorCode} errno={errno}");
            }
            return PosixSocketFailure(ctx, errno);
        }
        catch (ObjectDisposedException)
        {
            return PosixSocketFailure(ctx, 9);
        }
        finally
        {
            if (restoreBlocking)
            {
                try { socket.Blocking = priorBlocking; } catch (SocketException) { }
                catch (ObjectDisposedException) { }
            }
        }
    }

    internal static int PosixGetSocketOption(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var lengthAddress = ctx[CpuRegister.R8];
        if (valueAddress == 0 || lengthAddress == 0)
        {
            return PosixSocketFailure(ctx, 14);
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(lengthAddress, lengthBytes))
        {
            return PosixSocketFailure(ctx, 14);
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes) < sizeof(int))
        {
            return PosixSocketFailure(ctx, 22);
        }

        int value;
        lock (Gate)
        {
            if (!Sockets.TryGetValue(fd, out var state))
            {
                return PosixSocketFailure(ctx, 9);
            }

            if (!TryGetSocketOptionLocked(state, level, option, out value))
            {
                return PosixSocketFailure(ctx, 22);
            }
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, sizeof(int));
        if (!ctx.Memory.TryWrite(valueAddress, valueBytes) ||
            !ctx.Memory.TryWrite(lengthAddress, lengthBytes))
        {
            return PosixSocketFailure(ctx, 14);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "TU-d9PfIHPM",
        ExportName = "socket",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Socket(CpuContext ctx)
    {
        var family = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var protocol = unchecked((int)ctx[CpuRegister.Rdx]);
        System.Net.Sockets.Socket? datagramSocket = null;
        if (family == 2 && type == 2 && protocol is 0 or 17)
        {
            try
            {
                datagramSocket = new System.Net.Sockets.Socket(
                    AddressFamily.InterNetwork,
                    SocketType.Dgram,
                    ProtocolType.Udp);
            }
            catch (SocketException exception)
            {
                return PosixSocketFailure(ctx, MapSocketErrorToPosixErrno(exception.SocketErrorCode));
            }
        }

        var fd = KernelMemoryCompatExports.AllocateGuestFileDescriptor();
        lock (Gate)
        {
            Sockets[fd] = new EmulatedSocketState
            {
                Family = family,
                Type = type,
                Protocol = protocol,
                DatagramSocket = datagramSocket,
            };
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)fd);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "XVL8So3QJUk",
        ExportName = "connect",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Connect(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var sockaddrAddress = ctx[CpuRegister.Rsi];
        var addrlen = unchecked((int)ctx[CpuRegister.Rdx]);

        if (!TryGetEmulatedSocketState(fd, out _))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryParseGuestSockaddrIn(sockaddrAddress, addrlen, ctx, out var ipAddress, out var port))
        {
            LogNet($"connect sockaddr parse failed: fd={fd} addr=0x{sockaddrAddress:X} len={addrlen}");
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var redirectApplied = TryApplyNetRedirect(ref ipAddress);
        if (redirectApplied)
        {
            LogNet($"connect redirect: fd={fd} ip={ipAddress} port={port}");
        }

        if (!IsGuestTcpOutboundAllowed(ipAddress, redirectApplied))
        {
            LogNet($"connect denied by outbound policy: fd={fd} ip={ipAddress} port={port}");
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryEstablishHostTcpConnection(ipAddress, port, out var client, out var stream))
        {
            LogNet($"connect failed: fd={fd} ip={ipAddress} port={port}");
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        LogNet($"connect ok: fd={fd} ip={ipAddress} port={port}");

        lock (Gate)
        {
            if (!Sockets.TryGetValue(fd, out var state) || state is null)
            {
                try { stream.Dispose(); } catch (IOException) { }
                try { client.Dispose(); } catch (IOException) { }
                ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            DisposeEmulatedSocket(state);
            state.Client = client;
            state.Stream = stream;
            state.Connected = true;
            state.BoundAddress = ipAddress;
            state.BoundPort = port;
            state.Bound = true;
            ApplySocketOptions(state);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "KuOmgKoqCdY",
        ExportName = "bind",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Bind(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var sockaddrAddress = ctx[CpuRegister.Rsi];
        var addrlen = unchecked((int)ctx[CpuRegister.Rdx]);

        if (!TryGetEmulatedSocketState(fd, out var state) || state is null)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryParseGuestSockaddrIn(sockaddrAddress, addrlen, ctx, out var ipAddress, out var port))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (state.DatagramSocket is not null)
        {
            try
            {
                state.DatagramSocket.Bind(new IPEndPoint(ipAddress, port));
                if (state.DatagramSocket.LocalEndPoint is IPEndPoint localEndpoint)
                {
                    ipAddress = localEndpoint.Address;
                    port = localEndpoint.Port;
                }
            }
            catch (SocketException exception)
            {
                return PosixSocketFailure(ctx, MapSocketErrorToPosixErrno(exception.SocketErrorCode));
            }
            catch (ObjectDisposedException)
            {
                return PosixSocketFailure(ctx, 9);
            }
        }

        lock (Gate)
        {
            state.BoundAddress = ipAddress;
            state.BoundPort = port;
            state.Bound = true;
        }
        LogNet($"bind: fd={fd} ip={ipAddress} port={port}");
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RenI1lL1WFk",
        ExportName = "getsockname",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Getsockname(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var sockaddrAddress = ctx[CpuRegister.Rsi];
        var addrlenAddress = ctx[CpuRegister.Rdx];

        if (!TryGetEmulatedSocketState(fd, out var state) || state is null || !state.Bound)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<byte> addrlenBuffer = stackalloc byte[4];
        if (!ctx.Memory.TryRead(addrlenAddress, addrlenBuffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var addrlen = BinaryPrimitives.ReadInt32LittleEndian(addrlenBuffer);
        if (addrlen < 8)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<byte> sockaddr = stackalloc byte[16];
        sockaddr[0] = 16;
        sockaddr[1] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr.Slice(2, 2), (ushort)state.BoundPort);
        var addressBytes = state.BoundAddress.GetAddressBytes();
        if (addressBytes.Length == 4)
        {
            addressBytes.CopyTo(sockaddr.Slice(4, 4));
        }

        var writeLength = Math.Min(addrlen, 16);
        if (!ctx.Memory.TryWrite(sockaddrAddress, sockaddr.Slice(0, writeLength)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        BinaryPrimitives.WriteInt32LittleEndian(addrlenBuffer, writeLength);
        if (!ctx.Memory.TryWrite(addrlenAddress, addrlenBuffer))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "9oiX1kyeedA",
        ExportName = "bzero",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Bzero(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var length = unchecked((int)ctx[CpuRegister.Rsi]);
        if (length > 0 && address != 0)
        {
            var zeros = new byte[length];
            if (!ctx.Memory.TryWrite(address, zeros))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4n51s0zEf0c",
        ExportName = "inet_pton",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int InetPton(CpuContext ctx)
    {
        var af = unchecked((int)ctx[CpuRegister.Rdi]);
        var srcAddress = ctx[CpuRegister.Rsi];
        var dstAddress = ctx[CpuRegister.Rdx];
        if (af != 2 || srcAddress == 0 || dstAddress == 0)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadCString(srcAddress, ctx, out var text) ||
            !TryParseIpv4Address(text, out var octets))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<byte> packed = stackalloc byte[4];
        packed[0] = octets[0];
        packed[1] = octets[1];
        packed[2] = octets[2];
        packed[3] = octets[3];
        if (!ctx.Memory.TryWrite(dstAddress, packed))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 1;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jogUIsOV3-U",
        ExportName = "htons",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Htons(CpuContext ctx)
    {
        var value = unchecked((ushort)ctx[CpuRegister.Rdi]);
        var swapped = (ushort)(((value & 0x00FF) << 8) | ((value >> 8) & 0x00FF));
        ctx[CpuRegister.Rax] = swapped;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryGetEmulatedSocketState(int fd, out EmulatedSocketState? state)
    {
        lock (Gate)
        {
            return Sockets.TryGetValue(fd, out state);
        }
    }

    private static bool TrySetSocketOptionLocked(
        EmulatedSocketState state,
        int level,
        int option,
        int value)
    {
        switch (level, option)
        {
            case (0xFFFF, 0x0004):
                state.ReuseAddress = value != 0;
                break;
            case (0xFFFF, 0x0008):
                state.KeepAlive = value != 0;
                break;
            case (0xFFFF, 0x0020):
                state.Broadcast = value != 0;
                break;
            case (0xFFFF, 0x0200):
                state.ReusePort = value != 0;
                break;
            case (0xFFFF, 0x1001) when value > 0:
                state.SendBufferSize = value;
                break;
            case (0xFFFF, 0x1002) when value > 0:
                state.ReceiveBufferSize = value;
                break;
            case (0xFFFF, 0x1003) when value > 0:
                state.SendLowWater = value;
                break;
            case (0xFFFF, 0x1004) when value > 0:
                state.ReceiveLowWater = value;
                break;
            case (41, 27) when state.Family == 28:
                state.IPv6Only = value != 0;
                break;
            case (6, 1) when state.Type == 1:
                state.NoDelay = value != 0;
                break;
            default:
                return false;
        }

        ApplySocketOptions(state);
        return true;
    }

    private static bool TryGetSocketOptionLocked(
        EmulatedSocketState state,
        int level,
        int option,
        out int value)
    {
        value = (level, option) switch
        {
            (0xFFFF, 0x0004) => state.ReuseAddress ? 1 : 0,
            (0xFFFF, 0x0008) => state.KeepAlive ? 1 : 0,
            (0xFFFF, 0x0020) => state.Broadcast ? 1 : 0,
            (0xFFFF, 0x0200) => state.ReusePort ? 1 : 0,
            (0xFFFF, 0x1001) => state.SendBufferSize,
            (0xFFFF, 0x1002) => state.ReceiveBufferSize,
            (0xFFFF, 0x1003) => state.SendLowWater,
            (0xFFFF, 0x1004) => state.ReceiveLowWater,
            (41, 27) when state.Family == 28 => state.IPv6Only ? 1 : 0,
            (6, 1) when state.Type == 1 => state.NoDelay ? 1 : 0,
            _ => -1,
        };
        return value >= 0;
    }

    private static void ApplySocketOptions(EmulatedSocketState state)
    {
        var socket = state.DatagramSocket ?? state.Client?.Client;
        if (socket is null)
        {
            return;
        }

        try
        {
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, state.ReuseAddress);
            if (socket.SocketType == SocketType.Stream)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, state.KeepAlive);
            }

            if (socket.SocketType == SocketType.Dgram)
            {
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Broadcast, state.Broadcast);
            }
            if (state.SendBufferSize > 0)
            {
                socket.SendBufferSize = state.SendBufferSize;
            }

            if (state.ReceiveBufferSize > 0)
            {
                socket.ReceiveBufferSize = state.ReceiveBufferSize;
            }
            if (socket.AddressFamily == AddressFamily.InterNetworkV6)
            {
                socket.DualMode = !state.IPv6Only;
            }

            if (socket.SocketType == SocketType.Stream)
            {
                socket.NoDelay = state.NoDelay;
            }
        }
        catch (SocketException)
        {
            // The guest option remains stored when the host cannot apply it.
        }
    }

    private static int PosixSocketFailure(CpuContext ctx, int errno)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }

    private static bool TryWriteUInt32(CpuContext ctx, ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return ctx.Memory.TryWrite(address, bytes);
    }

    private static bool TryParseGuestSockaddrIn(
        ulong address,
        int addrlen,
        CpuContext ctx,
        out IPAddress ipAddress,
        out int port)
    {
        ipAddress = IPAddress.None;
        port = 0;
        if (address == 0 || addrlen < 8)
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[16];
        var readLength = Math.Min(addrlen, buffer.Length);
        if (!ctx.Memory.TryRead(address, buffer.Slice(0, readLength)))
        {
            return false;
        }

        if (buffer[1] != 2)
        {
            return false;
        }

        port = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(2, 2));
        ipAddress = new IPAddress(buffer.Slice(4, 4).ToArray());
        return true;
    }

    private static void DisposeEmulatedSocket(EmulatedSocketState state)
    {
        try { state.Stream?.Dispose(); } catch (IOException) { }
        try { state.Client?.Dispose(); } catch (IOException) { }
        try { state.DatagramSocket?.Dispose(); } catch (SocketException) { }
        state.Stream = null;
        state.Client = null;
        state.DatagramSocket = null;
        state.Connected = false;
    }

    private static void LogNet(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NET"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][DEBUG] {message}");
        }
    }

    private static bool TryApplyNetRedirect(ref IPAddress ipAddress)
    {
        var redirect = Environment.GetEnvironmentVariable("SHARPEMU_NET_REDIRECT");
        if (string.IsNullOrWhiteSpace(redirect))
        {
            return false;
        }

        if (!IPAddress.TryParse(redirect.Trim(), out var redirectAddress))
        {
            return false;
        }

        ipAddress = redirectAddress;
        return true;
    }

    private static bool IsNetRedirectConfigured()
    {
        var redirect = Environment.GetEnvironmentVariable("SHARPEMU_NET_REDIRECT");
        return !string.IsNullOrWhiteSpace(redirect);
    }

    private static bool IsGuestTcpOutboundAllowed(IPAddress ipAddress, bool redirectApplied)
    {
        return IsGuestSocketOutboundAllowed(ipAddress, redirectApplied);
    }

    private static bool IsGuestSocketOutboundAllowed(IPAddress ipAddress, bool redirectApplied)
    {
        return redirectApplied || IsNetRedirectConfigured() || IPAddress.IsLoopback(ipAddress);
    }

    private static int MapSocketErrorToPosixErrno(SocketError socketError)
    {
        return socketError switch
        {
            SocketError.WouldBlock or SocketError.IOPending => 35,
            SocketError.DestinationAddressRequired => 39,
            SocketError.MessageSize => 40,
            SocketError.ProtocolType => 41,
            SocketError.ProtocolOption => 42,
            SocketError.OperationNotSupported => 45,
            SocketError.AddressFamilyNotSupported => 47,
            SocketError.AddressAlreadyInUse => 48,
            SocketError.AddressNotAvailable => 49,
            SocketError.NetworkDown => 50,
            SocketError.NetworkUnreachable => 51,
            SocketError.ConnectionReset => 54,
            SocketError.NoBufferSpaceAvailable => 55,
            SocketError.IsConnected => 56,
            SocketError.NotConnected => 57,
            SocketError.TimedOut => 60,
            SocketError.ConnectionRefused => 61,
            SocketError.HostDown => 64,
            SocketError.HostUnreachable => 65,
            SocketError.AccessDenied => 13,
            _ => 22,
        };
    }

    private static bool TryEstablishHostTcpConnection(
        IPAddress ipAddress,
        int port,
        out TcpClient client,
        out NetworkStream stream)
    {
        client = null!;
        stream = null!;
        if (!TryConnectTcpClient(ipAddress, port, out client))
        {
            return false;
        }

        stream = client.GetStream();
        return true;
    }

    private static bool TryConnectTcpClient(IPAddress ipAddress, int port, out TcpClient client)
    {
        client = new TcpClient();
        try
        {
            var connectTask = client.ConnectAsync(ipAddress, port);
            if (!connectTask.Wait(TimeSpan.FromMilliseconds(500)))
            {
                client.Dispose();
                client = null!;
                return false;
            }

            return true;
        }
        catch (SocketException)
        {
            client.Dispose();
            client = null!;
            return false;
        }
        catch (IOException)
        {
            client.Dispose();
            client = null!;
            return false;
        }
    }

    private static bool TryReadCString(ulong address, CpuContext ctx, out string text)
    {
        const int maxLength = 64;
        var buffer = new byte[maxLength];
        var length = 0;
        for (; length < maxLength; length++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)length, buffer.AsSpan(length, 1)))
            {
                text = string.Empty;
                return false;
            }

            if (buffer[length] == 0)
            {
                break;
            }
        }

        text = Encoding.ASCII.GetString(buffer, 0, length);
        return true;
    }

    private static bool TryParseIpv4Address(string text, out byte[] octets)
    {
        octets = Array.Empty<byte>();
        var parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            return false;
        }

        var parsed = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out parsed[i]))
            {
                return false;
            }
        }

        octets = parsed;
        return true;
    }
}
