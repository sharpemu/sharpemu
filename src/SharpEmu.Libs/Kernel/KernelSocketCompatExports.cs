// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

internal static class KernelSocketCompatExports
{
    private const int AddressFamilyInet = 2;
    private const int SocketTypeMask = 0xF;
    private const int SocketTypeStream = 1;
    private const int SocketTypeDatagram = 2;
    private const int SocketTypeNonBlocking = 0x20000000;
    private const int MaxUdpPayloadBytes = 65_507;
    private const int MaxUdpPacketBytes = 65_535;
    private const int FcntlGetFlags = 3;
    private const int FcntlSetFlags = 4;
    private const int OpenFlagReadWrite = 2;
    private const int OpenFlagNonBlocking = 4;
    private const int MessageFlagPeek = 0x02;
    private const int MessageFlagDontWait = 0x80;
    private const int ErrnoIo = 5;
    private const int ErrnoBadFileDescriptor = 9;
    private const int ErrnoAccess = 13;
    private const int ErrnoFault = 14;
    private const int ErrnoInvalidArgument = 22;
    private const int ErrnoWouldBlock = 35;
    private const int ErrnoNotSocket = 38;
    private const int ErrnoDestinationAddressRequired = 39;
    private const int ErrnoMessageSize = 40;
    private const int ErrnoProtocolNotSupported = 43;
    private const int ErrnoAddressFamilyNotSupported = 47;
    private const int ErrnoAddressInUse = 48;
    private const int ErrnoAddressNotAvailable = 49;
    private const int ErrnoNetworkDown = 50;
    private const int ErrnoNetworkUnreachable = 51;
    private const int ErrnoConnectionAborted = 53;
    private const int ErrnoConnectionReset = 54;
    private const int ErrnoNoBufferSpace = 55;
    private const int ErrnoNotConnected = 57;
    private const int ErrnoTimedOut = 60;
    private const int ErrnoConnectionRefused = 61;
    private const int ErrnoHostDown = 64;
    private const int ErrnoHostUnreachable = 65;

    private sealed class EmulatedSocketState
    {
        public TcpClient? Client;
        public NetworkStream? Stream;
        public System.Net.Sockets.Socket? DatagramSocket;
        public int GuestSocketType = SocketTypeStream;
        public IPAddress BoundAddress = IPAddress.Any;
        public int BoundPort;
        public bool Bound;
        public bool Connected;
        public int StatusFlags = OpenFlagReadWrite;
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

    [SysAbiExport(
        Nid = "TU-d9PfIHPM",
        ExportName = "socket",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Socket(CpuContext ctx)
    {
        var addressFamily = unchecked((int)ctx[CpuRegister.Rdi]);
        var rawSocketType = unchecked((int)ctx[CpuRegister.Rsi]);
        var socketType = rawSocketType & SocketTypeMask;
        var protocol = unchecked((int)ctx[CpuRegister.Rdx]);

        System.Net.Sockets.Socket? datagramSocket = null;
        if (addressFamily == AddressFamilyInet && socketType == SocketTypeDatagram)
        {
            try
            {
                datagramSocket = new System.Net.Sockets.Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                {
                    EnableBroadcast = true,
                    ExclusiveAddressUse = false,
                };
                datagramSocket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                if ((rawSocketType & SocketTypeNonBlocking) != 0)
                {
                    datagramSocket.Blocking = false;
                }
            }
            catch (SocketException ex)
            {
                datagramSocket?.Dispose();
                return SetPosixSocketFailure(ctx, MapSocketErrorToGuestErrno(ex.SocketErrorCode));
            }
        }

        var fd = KernelMemoryCompatExports.AllocateGuestFileDescriptor();
        lock (Gate)
        {
            Sockets[fd] = new EmulatedSocketState
            {
                DatagramSocket = datagramSocket,
                GuestSocketType = socketType,
                StatusFlags = OpenFlagReadWrite |
                    (((rawSocketType & SocketTypeNonBlocking) != 0) ? OpenFlagNonBlocking : 0),
            };
        }

        LogNet($"socket fd={fd} family={addressFamily} type=0x{rawSocketType:X} protocol={protocol} datagram={datagramSocket is not null}");
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

        if (state.DatagramSocket is { } datagramSocket)
        {
            try
            {
                datagramSocket.Bind(new IPEndPoint(ipAddress, port));
                UpdateBoundEndpoint(state, datagramSocket);
                LogNet($"bind udp ok: fd={fd} ip={state.BoundAddress} port={state.BoundPort}");
            }
            catch (SocketException ex)
            {
                LogNet($"bind udp failed: fd={fd} ip={ipAddress} port={port} error={ex.SocketErrorCode}");
                return SetPosixSocketFailure(ctx, MapSocketErrorToGuestErrno(ex.SocketErrorCode));
            }
            catch (ObjectDisposedException)
            {
                return SetPosixSocketFailure(ctx, ErrnoBadFileDescriptor);
            }
        }
        else
        {
            state.BoundAddress = ipAddress;
            state.BoundPort = port;
            state.Bound = true;
        }

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
        sockaddr.Clear();
        sockaddr[0] = 16;
        sockaddr[1] = AddressFamilyInet;
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
        Nid = "8nY19bKoiZk",
        ExportName = "fcntl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Fcntl(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var command = unchecked((int)ctx[CpuRegister.Rsi]);
        var argument = unchecked((int)ctx[CpuRegister.Rdx]);
        System.Net.Sockets.Socket? datagramSocket = null;
        bool? blocking = null;
        var isSocket = false;

        lock (Gate)
        {
            if (Sockets.TryGetValue(fd, out var state) && state is not null)
            {
                isSocket = true;
                switch (command)
                {
                    case FcntlGetFlags:
                        ctx[CpuRegister.Rax] = unchecked((uint)state.StatusFlags);
                        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                    case FcntlSetFlags:
                        state.StatusFlags = argument;
                        datagramSocket = state.DatagramSocket;
                        blocking = (argument & OpenFlagNonBlocking) == 0;
                        break;
                }
            }
        }

        if (!isSocket)
        {
            return KernelMemoryCompatExports.PosixFcntl(ctx);
        }

        if (datagramSocket is not null && blocking.HasValue)
        {
            try
            {
                datagramSocket.Blocking = blocking.Value;
            }
            catch (SocketException ex)
            {
                return SetPosixSocketFailure(ctx, MapSocketErrorToGuestErrno(ex.SocketErrorCode));
            }
            catch (ObjectDisposedException)
            {
                return SetPosixSocketFailure(ctx, ErrnoBadFileDescriptor);
            }
        }

        // Unknown commands and non-socket fds succeed benignly: guest code feeds
        // any returned value straight back into F_SETFL, so an error-shaped result
        // would circulate as bogus flags.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fFxGkxF2bVo",
        ExportName = "setsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Setsockopt(CpuContext ctx)
    {
        // Emulated sockets have no per-option behavior to configure; accept and ignore.
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "oBr313PppNE",
        ExportName = "sendto",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Sendto(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requestedRaw = ctx[CpuRegister.Rdx];
        var destinationAddress = ctx[CpuRegister.R8];
        var destinationLengthRaw = ctx[CpuRegister.R9];

        if (requestedRaw > MaxUdpPayloadBytes)
        {
            return SetPosixSocketFailure(ctx, ErrnoMessageSize);
        }

        var requested = unchecked((int)requestedRaw);
        if (requested > 0 && bufferAddress == 0)
        {
            return SetPosixSocketFailure(ctx, ErrnoFault);
        }
        if (destinationAddress == 0)
        {
            return SetPosixSocketFailure(ctx, ErrnoDestinationAddressRequired);
        }
        if (destinationLengthRaw > int.MaxValue ||
            !TryParseGuestSockaddrIn(
                destinationAddress,
                unchecked((int)destinationLengthRaw),
                ctx,
                out var ipAddress,
                out var port))
        {
            return SetPosixSocketFailure(ctx, ErrnoInvalidArgument);
        }

        if (!TryGetEmulatedSocketState(fd, out var state) || state is null)
        {
            return SetPosixSocketFailure(ctx, ErrnoBadFileDescriptor);
        }
        if (state.DatagramSocket is not { } datagramSocket)
        {
            return SetPosixSocketFailure(ctx, ErrnoNotSocket);
        }

        var payload = requested == 0
            ? Array.Empty<byte>()
            : GC.AllocateUninitializedArray<byte>(requested);
        if (requested > 0 && !ctx.Memory.TryRead(bufferAddress, payload))
        {
            return SetPosixSocketFailure(ctx, ErrnoFault);
        }

        var redirectApplied = TryApplyNetRedirect(ref ipAddress);
        if (!IsGuestUdpOutboundAllowed(ipAddress, redirectApplied))
        {
            // LAN discovery and telemetry traffic must not escape the host by
            // default. Report a complete datagram so titles can continue their
            // offline paths; an explicit redirect still permits real testing.
            LogNet($"sendto suppressed by outbound policy: fd={fd} ip={ipAddress} port={port} bytes={requested}");
            ctx[CpuRegister.Rax] = unchecked((ulong)requested);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        int sent;
        try
        {
            sent = datagramSocket.SendTo(
                payload,
                0,
                payload.Length,
                SocketFlags.None,
                new IPEndPoint(ipAddress, port));
            UpdateBoundEndpoint(state, datagramSocket);
        }
        catch (SocketException ex)
        {
            return SetPosixSocketFailure(ctx, MapSocketErrorToGuestErrno(ex.SocketErrorCode));
        }
        catch (ObjectDisposedException)
        {
            return SetPosixSocketFailure(ctx, ErrnoBadFileDescriptor);
        }
        catch (ArgumentException)
        {
            return SetPosixSocketFailure(ctx, ErrnoInvalidArgument);
        }

        LogNet($"sendto ok: fd={fd} ip={ipAddress} port={port} bytes={sent}");
        ctx[CpuRegister.Rax] = unchecked((ulong)sent);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "lUk6wrGXyMw",
        ExportName = "recvfrom",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Recvfrom(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requestedRaw = ctx[CpuRegister.Rdx];
        var guestFlags = unchecked((int)ctx[CpuRegister.Rcx]);
        var sourceAddress = ctx[CpuRegister.R8];
        var sourceLengthAddress = ctx[CpuRegister.R9];

        if (requestedRaw > int.MaxValue)
        {
            return SetPosixSocketFailure(ctx, ErrnoMessageSize);
        }

        var requested = unchecked((int)requestedRaw);
        if (requested > 0 && bufferAddress == 0)
        {
            return SetPosixSocketFailure(ctx, ErrnoFault);
        }

        var sourceCapacity = 0u;
        if (sourceLengthAddress != 0)
        {
            if (!ctx.TryReadUInt32(sourceLengthAddress, out sourceCapacity))
            {
                return SetPosixSocketFailure(ctx, ErrnoFault);
            }
        }

        if (!TryGetEmulatedSocketState(fd, out var state) || state is null)
        {
            return SetPosixSocketFailure(ctx, ErrnoBadFileDescriptor);
        }
        if (state.DatagramSocket is not { } datagramSocket)
        {
            return SetPosixSocketFailure(ctx, ErrnoNotSocket);
        }

        var nonBlocking = (state.StatusFlags & OpenFlagNonBlocking) != 0 ||
            (guestFlags & MessageFlagDontWait) != 0;
        try
        {
            if (nonBlocking && !datagramSocket.Poll(0, SelectMode.SelectRead))
            {
                return SetPosixSocketFailure(ctx, ErrnoWouldBlock);
            }
        }
        catch (SocketException ex)
        {
            return SetPosixSocketFailure(ctx, MapSocketErrorToGuestErrno(ex.SocketErrorCode));
        }
        catch (ObjectDisposedException)
        {
            return SetPosixSocketFailure(ctx, ErrnoBadFileDescriptor);
        }

        var receiveBuffer = ArrayPool<byte>.Shared.Rent(MaxUdpPacketBytes);
        try
        {
            EndPoint remoteEndpoint = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try
            {
                var socketFlags = (guestFlags & MessageFlagPeek) != 0
                    ? SocketFlags.Peek
                    : SocketFlags.None;
                received = datagramSocket.ReceiveFrom(
                    receiveBuffer,
                    0,
                    MaxUdpPacketBytes,
                    socketFlags,
                    ref remoteEndpoint);
                UpdateBoundEndpoint(state, datagramSocket);
            }
            catch (SocketException ex)
            {
                return SetPosixSocketFailure(ctx, MapSocketErrorToGuestErrno(ex.SocketErrorCode));
            }
            catch (ObjectDisposedException)
            {
                return SetPosixSocketFailure(ctx, ErrnoBadFileDescriptor);
            }
            catch (ArgumentException)
            {
                return SetPosixSocketFailure(ctx, ErrnoInvalidArgument);
            }

            var copied = Math.Min(received, requested);
            if (copied > 0 && !ctx.Memory.TryWrite(bufferAddress, receiveBuffer.AsSpan(0, copied)))
            {
                return SetPosixSocketFailure(ctx, ErrnoFault);
            }
            if (sourceAddress != 0 &&
                sourceLengthAddress != 0 &&
                remoteEndpoint is IPEndPoint remoteIpEndpoint &&
                !TryWriteGuestSockaddrIn(ctx, sourceAddress, sourceLengthAddress, sourceCapacity, remoteIpEndpoint))
            {
                return SetPosixSocketFailure(ctx, ErrnoFault);
            }

            LogNet($"recvfrom ok: fd={fd} source={remoteEndpoint} bytes={copied} datagram={received}");
            ctx[CpuRegister.Rax] = unchecked((ulong)copied);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(receiveBuffer);
        }
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
        Nid = "8Kcp5d-q1Uo",
        ExportName = "sceNetInetPton",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int SceNetInetPton(CpuContext ctx)
    {
        return InetPton(ctx);
    }

    [SysAbiExport(
        Nid = "5jRCs2axtr4",
        ExportName = "inet_ntop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int InetNtop(CpuContext ctx)
    {
        // const char* inet_ntop(int af, const void* src, char* dst, socklen_t size):
        // returns dst on success, NULL on failure.
        var af = unchecked((int)ctx[CpuRegister.Rdi]);
        var srcAddress = ctx[CpuRegister.Rsi];
        var dstAddress = ctx[CpuRegister.Rdx];
        var size = unchecked((int)ctx[CpuRegister.Rcx]);
        ctx[CpuRegister.Rax] = 0;
        if (af != 2 || srcAddress == 0 || dstAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<byte> packed = stackalloc byte[4];
        if (!ctx.Memory.TryRead(srcAddress, packed))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var text = $"{packed[0]}.{packed[1]}.{packed[2]}.{packed[3]}";
        if (size < text.Length + 1)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var encoded = new byte[text.Length + 1];
        Encoding.ASCII.GetBytes(text, encoded);
        if (!ctx.Memory.TryWrite(dstAddress, encoded))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        ctx[CpuRegister.Rax] = dstAddress;
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

    private static bool TryWriteGuestSockaddrIn(
        CpuContext ctx,
        ulong address,
        ulong addrlenAddress,
        uint capacity,
        IPEndPoint endpoint)
    {
        Span<byte> sockaddr = stackalloc byte[16];
        sockaddr.Clear();
        sockaddr[0] = 16;
        sockaddr[1] = AddressFamilyInet;
        BinaryPrimitives.WriteUInt16BigEndian(sockaddr.Slice(2, 2), unchecked((ushort)endpoint.Port));
        var addressBytes = endpoint.Address.GetAddressBytes();
        if (addressBytes.Length != 4)
        {
            return false;
        }
        addressBytes.CopyTo(sockaddr.Slice(4, 4));

        var writeLength = unchecked((int)Math.Min(capacity, (uint)sockaddr.Length));
        if (writeLength > 0 && !ctx.Memory.TryWrite(address, sockaddr.Slice(0, writeLength)))
        {
            return false;
        }

        return ctx.TryWriteUInt32(addrlenAddress, unchecked((uint)writeLength));
    }

    private static void UpdateBoundEndpoint(EmulatedSocketState state, System.Net.Sockets.Socket socket)
    {
        try
        {
            if (socket.LocalEndPoint is not IPEndPoint localEndpoint)
            {
                return;
            }

            state.BoundAddress = localEndpoint.Address;
            state.BoundPort = localEndpoint.Port;
            state.Bound = true;
        }
        catch (ObjectDisposedException)
        {
            // A concurrent guest close owns disposal; the caller reports its
            // operation result and must not resurrect the descriptor state.
        }
    }

    private static int SetPosixSocketFailure(CpuContext ctx, int errno)
    {
        _ = KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int MapSocketErrorToGuestErrno(SocketError error) => error switch
    {
        SocketError.Interrupted => 4,
        SocketError.AccessDenied => ErrnoAccess,
        SocketError.Fault => ErrnoFault,
        SocketError.InvalidArgument => ErrnoInvalidArgument,
        SocketError.WouldBlock or SocketError.IOPending or SocketError.InProgress => ErrnoWouldBlock,
        SocketError.NotSocket => ErrnoNotSocket,
        SocketError.MessageSize => ErrnoMessageSize,
        SocketError.ProtocolNotSupported => ErrnoProtocolNotSupported,
        SocketError.AddressFamilyNotSupported => ErrnoAddressFamilyNotSupported,
        SocketError.AddressAlreadyInUse => ErrnoAddressInUse,
        SocketError.AddressNotAvailable => ErrnoAddressNotAvailable,
        SocketError.NetworkDown => ErrnoNetworkDown,
        SocketError.NetworkUnreachable => ErrnoNetworkUnreachable,
        SocketError.ConnectionAborted => ErrnoConnectionAborted,
        SocketError.ConnectionReset => ErrnoConnectionReset,
        SocketError.NoBufferSpaceAvailable => ErrnoNoBufferSpace,
        SocketError.NotConnected => ErrnoNotConnected,
        SocketError.TimedOut => ErrnoTimedOut,
        SocketError.ConnectionRefused => ErrnoConnectionRefused,
        SocketError.HostDown => ErrnoHostDown,
        SocketError.HostUnreachable => ErrnoHostUnreachable,
        _ => ErrnoIo,
    };

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
        return redirectApplied || IsNetRedirectConfigured() || IPAddress.IsLoopback(ipAddress);
    }

    private static bool IsGuestUdpOutboundAllowed(IPAddress ipAddress, bool redirectApplied)
    {
        return redirectApplied || IPAddress.IsLoopback(ipAddress);
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
