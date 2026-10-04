// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.Network;

public static class NetExports
{
    private const int NetErrorNoEntry = unchecked((int)0x80410102);
    private const int NetErrorBadFileDescriptor = unchecked((int)0x80410109);
    private const int NetErrorFault = unchecked((int)0x8041010E);
    private const int NetErrorAlreadyExists = unchecked((int)0x80410111);
    private const int NetErrorInvalidArgument = unchecked((int)0x80410116);
    private const int NetErrorTooManyFiles = unchecked((int)0x80410118);
    private const int NetErrorWouldBlock = unchecked((int)0x80410123);
    private const int NetErrorAddressInUse = unchecked((int)0x80410130);
    private const int NetErrorTooManyOpenFiles = unchecked((int)0x80410118);
    private const int NetErrorResolverNoDns = unchecked((int)0x804101E1);
    private const int NetErrorNotInitialized = unchecked((int)0x804101C8);
    private const int NetErrnoNoEntry = 2;
    private const int NetErrnoBadFileDescriptor = 9;
    private const int NetErrnoFault = 14;
    private const int NetErrnoAlreadyExists = 17;
    private const int NetErrnoInvalidArgument = 22;
    private const int NetErrnoTooManyFiles = 24;
    private const int NetErrnoWouldBlock = 35;
    private const int NetErrnoAddressInUse = 48;
    private const int NetErrnoTooManyOpenFiles = 24;
    private const int NetErrnoNotInitialized = 200;
    private const int NetErrnoResolverNoDns = 225;
    private const int MaxNameLength = 256;
    private const int EpollIdBase = 1024;
    private const int EpollCapacity = 32;
    private const int NetEpollEventSize = 24;
    private const uint NetEpollIn = 0x00000001;
    private const uint NetEpollOut = 0x00000002;
    private const uint NetEpollError = 0x00000008;
    private static ReadOnlySpan<byte> OfflineMacAddress => [0x02, 0x53, 0x48, 0x41, 0x52, 0x50];

    private static readonly ConcurrentDictionary<int, NetPool> _pools = new();
    private static readonly ConcurrentDictionary<int, ResolverContext> _resolvers = new();
    private static readonly ConcurrentDictionary<int, Socket> _sockets = new();
    private static readonly object EpollGate = new();
    private static readonly Dictionary<int, NetEpollContext> _epolls = new();
    private static int _nextPoolId;
    private static int _nextResolverId = 0x2000;
    private static int _nextSocketId = 255;
    private static readonly object _socketIdGate = new();
    // The platform networking module is usable immediately after it is loaded.
    // Games and middleware (notably FMOD) can create internal sockets before an
    // explicit sceNetInit call reaches application code.
    private static bool _initialized = true;

    [ThreadStatic]
    private static nint _errnoAddress;

    private sealed record NetPool(string Name, int Size, int Flags);

    private sealed record ResolverContext(string Name, int PoolId, int Flags, int LastError);

    private sealed class NetEpollContext(string name)
    {
        public string Name { get; } = name;

        public List<NetEpollRegistration> Registrations { get; } = [];
    }

    private readonly record struct NetEpollRegistration(int SocketId, NetEpollEvent Event);

    private readonly record struct NetEpollEvent(uint Events, ulong Data);

    internal static void ResetEpollsForTests()
    {
        lock (EpollGate)
        {
            _epolls.Clear();
            Monitor.PulseAll(EpollGate);
        }
    }

    internal static bool TryGetReadEventState(
        int socketId,
        ulong lowWater,
        out bool ready,
        out ulong availableBytes,
        out ushort eventFlags)
    {
        ready = false;
        availableBytes = 0;
        eventFlags = 0;
        if (!_sockets.TryGetValue(socketId, out var socket))
        {
            return false;
        }

        try
        {
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

        return true;
    }

    [SysAbiExport(
        Nid = "Nlev7Lg8k3A",
        ExportName = "sceNetInit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetInit(CpuContext ctx)
    {
        _initialized = true;
        TraceNet("init", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "cTGkc6-TBlI",
        ExportName = "sceNetTerm",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetTerm(CpuContext ctx)
    {
        _initialized = false;
        _pools.Clear();
        _resolvers.Clear();
        lock (EpollGate)
        {
            _epolls.Clear();
            Monitor.PulseAll(EpollGate);
        }
        foreach (var socket in _sockets.Values)
        {
            socket.Dispose();
        }
        _sockets.Clear();
        TraceNet("term", 0, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "6Oc0bLsIYe0",
        ExportName = "sceNetGetMacAddress",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetGetMacAddress(CpuContext ctx)
    {
        var destinationAddress = ctx[CpuRegister.Rdi];
        var flags = unchecked((int)ctx[CpuRegister.Rsi]);
        if (destinationAddress == 0 || flags != 0 || !ctx.Memory.TryWrite(destinationAddress, OfflineMacAddress))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        TraceNet("get_mac_address", 0, destinationAddress, unchecked((ulong)flags), 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "Q4qBuN-c0ZM",
        ExportName = "sceNetSocket",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSocket(CpuContext ctx)
    {
        if (!_initialized)
        {
            return SetNetError(ctx, NetErrorNotInitialized, NetErrnoNotInitialized);
        }

        var nameAddress = ctx[CpuRegister.Rdi];
        var family = unchecked((int)ctx[CpuRegister.Rsi]);
        var type = unchecked((int)ctx[CpuRegister.Rdx]);
        var protocol = unchecked((int)ctx[CpuRegister.Rcx]);
        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;

        if (!TryTranslateSocketParameters(family, type, protocol, out var addressFamily, out var socketType, out var protocolType))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            var socket = new Socket(addressFamily, socketType, protocolType);
            var id = AddSocket(socket);
            if (id < 0)
            {
                socket.Dispose();
                return SetNetError(ctx, NetErrorTooManyFiles, NetErrnoTooManyFiles);
            }
            TraceNet("socket.create", id, unchecked((ulong)family), unchecked((ulong)type), unchecked((ulong)protocol));
            ctx[CpuRegister.Rax] = unchecked((ulong)id);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "45ggEzakPJQ",
        ExportName = "sceNetSocketClose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSocketClose(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        // Some SDK paths create a BSD/libKernel socket and release it through
        // sceNetSocketClose. Both APIs share the guest descriptor namespace.
        if (KernelSocketCompatExports.TryCloseSocketFd(id))
        {
            TraceNet("socket.close.posix", id, 0, 0, 0);
            return ctx.SetReturn(0);
        }

        if (!_sockets.TryRemove(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        socket.Dispose();
        TraceNet("socket.close", id, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "9wO9XrMsNhc",
        ExportName = "sceNetRecv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetRecv(CpuContext ctx) => ReceiveSocket(ctx, withAddress: false);

    [SysAbiExport(
        Nid = "304ooNZxWDY",
        ExportName = "sceNetRecvfrom",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetRecvfrom(CpuContext ctx) => ReceiveSocket(ctx, withAddress: true);

    private static int ReceiveSocket(CpuContext ctx, bool withAddress)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = ctx[CpuRegister.Rdx];
        var flags = unchecked((int)ctx[CpuRegister.Rcx]);
        var address = withAddress ? ctx[CpuRegister.R8] : 0;
        var addressLength = withAddress ? ctx[CpuRegister.R9] : 0;
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (bufferAddress == 0 || (address != 0 && addressLength == 0) ||
            (flags & ~(0x2 | 0x4 | 0x80 | 0x20000)) != 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (requested == 0)
        {
            return ctx.SetReturn(0);
        }

        var count = (int)Math.Min(requested, 1UL << 20);
        var socketFlags = (flags & 0x2) != 0 ? SocketFlags.Peek : SocketFlags.None;

        try
        {
            if ((flags & 0x80) != 0 && !socket.Poll(0, SelectMode.SelectRead))
            {
                return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
            }

            var payload = new byte[count];
            IPEndPoint? endpoint = null;
            int received;
            if (address == 0)
            {
                received = socket.Receive(payload, socketFlags);
            }
            else
            {
                EndPoint remote = new IPEndPoint(IPAddress.Any, 0);
                received = socket.ReceiveFrom(payload, socketFlags, ref remote);
                endpoint = remote as IPEndPoint;
            }
            if (!ctx.Memory.TryWrite(bufferAddress, payload.AsSpan(0, received)))
            {
                return SetNetError(ctx, NetErrorFault, NetErrnoFault);
            }

            if (address != 0 && endpoint is not null)
            {
                Span<byte> lengthBytes = stackalloc byte[sizeof(uint)];
                if (!ctx.Memory.TryRead(addressLength, lengthBytes))
                {
                    return SetNetError(ctx, NetErrorFault, NetErrnoFault);
                }

                Span<byte> guestAddress = stackalloc byte[16];
                guestAddress[0] = 16;
                guestAddress[1] = 2;
                BinaryPrimitives.WriteUInt16BigEndian(guestAddress[2..], unchecked((ushort)endpoint.Port));
                endpoint.Address.MapToIPv4().GetAddressBytes().CopyTo(guestAddress[4..]);
                var copyLength = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(lengthBytes), 16u);
                BinaryPrimitives.WriteUInt32LittleEndian(lengthBytes, 16);
                if (!ctx.Memory.TryWrite(address, guestAddress[..copyLength]) ||
                    !ctx.Memory.TryWrite(addressLength, lengthBytes))
                {
                    return SetNetError(ctx, NetErrorFault, NetErrnoFault);
                }
            }

            return ctx.SetReturn(received);
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending or SocketError.TimedOut)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    [SysAbiExport(
        Nid = "2mKX2Spso7I",
        ExportName = "sceNetSetsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetSetsockopt(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var valueLength = unchecked((int)ctx[CpuRegister.R8]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        // ORBIS_NET_SOL_SOCKET / ORBIS_NET_SO_NBIO. This is the first option
        // used by FMOD's discovery socket and maps directly to host blocking.
        if (level == 0xFFFF && option == 0x1200)
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            if (valueLength < value.Length || valueAddress == 0 || !ctx.Memory.TryRead(valueAddress, value))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            socket.Blocking = BinaryPrimitives.ReadInt32LittleEndian(value) == 0;
            TraceNet("socket.nonblocking", id, socket.Blocking ? 0UL : 1UL, 0, 0);
            return ctx.SetReturn(0);
        }

        // ORBIS_NET_SO_REUSEADDR uses the BSD value 0x0004.
        if (level == 0xFFFF && option == 0x0004)
        {
            Span<byte> value = stackalloc byte[sizeof(int)];
            if (valueLength < value.Length || valueAddress == 0 || !ctx.Memory.TryRead(valueAddress, value))
            {
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
            }

            socket.SetSocketOption(
                SocketOptionLevel.Socket,
                SocketOptionName.ReuseAddress,
                BinaryPrimitives.ReadInt32LittleEndian(value) != 0);
            TraceNet("socket.reuseaddr", id, BinaryPrimitives.ReadUInt32LittleEndian(value), 0, 0);
            return ctx.SetReturn(0);
        }

        return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
    }

    /// <summary>
    /// POSIX alias of <see cref="NetSetsockopt"/>; identical
    /// (fd, level, option, value, length) argument order.
    /// </summary>
    [SysAbiExport(
        Nid = "fFxGkxF2bVo",
        ExportName = "setsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSetsockopt(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return KernelSocketCompatExports.IsEmulatedSocketFd(id)
            ? KernelSocketCompatExports.PosixSetSocketOption(ctx)
            : NetSetsockopt(ctx);
    }

    /// <summary>
    /// Reads back the socket options this backend actually tracks: SO_NBIO,
    /// SO_REUSEADDR and SO_ERROR.
    /// </summary>
    /// <remarks>
    /// Anything else returns EINVAL rather than a zero-filled buffer. A caller
    /// that receives success for an option nobody stored would treat whatever
    /// happens to be in its output buffer as the real setting, which is a harder
    /// failure to trace than an explicit rejection.
    /// </remarks>
    [SysAbiExport(
        Nid = "6O8EwYOgH9Y",
        ExportName = "getsockopt",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetsockopt(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (KernelSocketCompatExports.IsEmulatedSocketFd(id))
        {
            return KernelSocketCompatExports.PosixGetSocketOption(ctx);
        }

        var level = unchecked((int)ctx[CpuRegister.Rsi]);
        var option = unchecked((int)ctx[CpuRegister.Rdx]);
        var valueAddress = ctx[CpuRegister.Rcx];
        var lengthAddress = ctx[CpuRegister.R8];
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (valueAddress == 0 || lengthAddress == 0 || level != 0xFFFF)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> lengthBytes = stackalloc byte[sizeof(int)];
        if (!ctx.Memory.TryRead(lengthAddress, lengthBytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (BinaryPrimitives.ReadInt32LittleEndian(lengthBytes) < sizeof(int))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        int value;
        switch (option)
        {
            // ORBIS_NET_SO_NBIO: mirrors what sceNetSetsockopt stored.
            case 0x1200:
                value = socket.Blocking ? 0 : 1;
                break;
            case 0x0004:
                value = (int)socket.GetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.ReuseAddress)! != 0 ? 1 : 0;
                break;
            // ORBIS_NET_SO_ERROR: nothing here records per-socket async errors,
            // so report "no pending error" rather than inventing one.
            case 0x1007:
                value = 0;
                break;
            default:
                return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> valueBytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(valueBytes, value);
        BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, sizeof(int));
        if (!ctx.Memory.TryWrite(valueAddress, valueBytes) ||
            !ctx.Memory.TryWrite(lengthAddress, lengthBytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        TraceNet("socket.getsockopt", id, unchecked((uint)option), unchecked((uint)value), 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "fZOeZIOEmLw",
        ExportName = "send",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSend(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var length = unchecked((int)ctx[CpuRegister.Rdx]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        if (length < 0 || (length != 0 && bufferAddress == 0))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (length == 0)
        {
            return ctx.SetReturn(0);
        }

        var payload = new byte[length];
        if (!ctx.Memory.TryRead(bufferAddress, payload))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            var sent = socket.Send(payload, SocketFlags.None);
            TraceNet("socket.send", id, unchecked((uint)length), unchecked((uint)sent), 0);
            return ctx.SetReturn(sent);
        }
        catch (SocketException exception)
            when (exception.SocketErrorCode == SocketError.WouldBlock)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
    }

    [SysAbiExport(
        Nid = "oBr313PppNE",
        ExportName = "sendto",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSendTo(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return KernelSocketCompatExports.IsEmulatedSocketFd(id)
            ? KernelSocketCompatExports.PosixSendTo(ctx)
            : SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
    }

    [SysAbiExport(
        Nid = "lUk6wrGXyMw",
        ExportName = "recvfrom",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixReceiveFrom(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return KernelSocketCompatExports.IsEmulatedSocketFd(id)
            ? KernelSocketCompatExports.PosixReceiveFrom(ctx)
            : SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
    }

    /// <summary>
    /// Formats a binary address as text. Pure conversion with no socket state,
    /// so it behaves identically to the console version for AF_INET/AF_INET6.
    /// </summary>
    [SysAbiExport(
        Nid = "5jRCs2axtr4",
        ExportName = "inet_ntop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixInetNtop(CpuContext ctx)
    {
        var family = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        var destinationSize = unchecked((int)ctx[CpuRegister.Rcx]);
        if (sourceAddress == 0 || destinationAddress == 0 || destinationSize <= 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // ORBIS_NET_AF_INET / ORBIS_NET_AF_INET6, matching TryMapAddressFamily.
        var addressLength = family switch
        {
            2 => 4,
            28 => 16,
            _ => 0,
        };

        if (addressLength == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var rawAddress = new byte[addressLength];
        if (!ctx.Memory.TryRead(sourceAddress, rawAddress))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var text = new IPAddress(rawAddress).ToString();
        var encoded = Encoding.ASCII.GetBytes(text);

        // POSIX requires the terminator to fit as well; a truncated address string
        // is worse than a reported failure because the caller cannot detect it.
        if (encoded.Length + 1 > destinationSize)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var buffer = new byte[encoded.Length + 1];
        encoded.CopyTo(buffer, 0);
        if (!ctx.Memory.TryWrite(destinationAddress, buffer))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // inet_ntop returns the destination pointer on success.
        ctx[CpuRegister.Rax] = destinationAddress;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "bErx49PgxyY",
        ExportName = "sceNetBind",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetBind(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
        if (!TryReadSocketAddress(ctx, ctx[CpuRegister.Rsi], unchecked((int)ctx[CpuRegister.Rdx]), out var endpoint))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        try
        {
            socket.Bind(endpoint);
            TraceNet("socket.bind", id, unchecked((ulong)endpoint.Port), 0, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return SetNetError(ctx, NetErrorAddressInUse, NetErrnoAddressInUse);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "kOj1HiAGE54",
        ExportName = "sceNetListen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetListen(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        try
        {
            socket.Listen(Math.Max(0, unchecked((int)ctx[CpuRegister.Rsi])));
            TraceNet("socket.listen", id, ctx[CpuRegister.Rsi], 0, 0);
            return ctx.SetReturn(0);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "PIWqhn9oSxc",
        ExportName = "sceNetAccept",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetAccept(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_sockets.TryGetValue(id, out var socket))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        try
        {
            var accepted = socket.Accept();
            var acceptedId = AddSocket(accepted);
            if (acceptedId < 0)
            {
                accepted.Dispose();
                return SetNetError(ctx, NetErrorTooManyFiles, NetErrnoTooManyFiles);
            }
            TraceNet("socket.accept", acceptedId, unchecked((ulong)id), 0, 0);
            ctx[CpuRegister.Rax] = unchecked((ulong)acceptedId);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (SocketException ex) when (ex.SocketErrorCode is SocketError.WouldBlock or SocketError.IOPending)
        {
            return SetNetError(ctx, NetErrorWouldBlock, NetErrnoWouldBlock);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }
    }

    [SysAbiExport(
        Nid = "HQOwnfMGipQ",
        ExportName = "sceNetErrnoLoc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetErrnoLoc(CpuContext ctx)
    {
        if (_errnoAddress == 0)
        {
            _errnoAddress = Marshal.AllocHGlobal(sizeof(int));
            Marshal.WriteInt32(_errnoAddress, 0);
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)_errnoAddress);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "SF47kB2MNTo",
        ExportName = "sceNetEpollCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetEpollCreate(CpuContext ctx)
    {
        if (!_initialized)
        {
            return SetNetError(ctx, NetErrorNotInitialized, NetErrnoNotInitialized);
        }

        var nameAddress = ctx[CpuRegister.Rdi];
        var flags = unchecked((int)ctx[CpuRegister.Rsi]);
        if (nameAddress == 0 || flags != 0 ||
            !TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var name))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        lock (EpollGate)
        {
            for (var index = 0; index < EpollCapacity; index++)
            {
                var id = EpollIdBase + index;
                if (_epolls.ContainsKey(id))
                {
                    continue;
                }

                _epolls.Add(id, new NetEpollContext(name));
                TraceNet("epoll.create", id, nameAddress, 0, 0);
                return ctx.SetReturn(id);
            }
        }

        return SetNetError(ctx, NetErrorTooManyOpenFiles, NetErrnoTooManyOpenFiles);
    }

    [SysAbiExport(
        Nid = "ZVw46bsasAk",
        ExportName = "sceNetEpollControl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetEpollControl(CpuContext ctx)
    {
        const int EpollControlAdd = 1;
        const int EpollControlModify = 2;
        const int EpollControlDelete = 3;

        var epollId = unchecked((int)ctx[CpuRegister.Rdi]);
        var operation = unchecked((int)ctx[CpuRegister.Rsi]);
        var socketId = unchecked((int)ctx[CpuRegister.Rdx]);
        var eventAddress = ctx[CpuRegister.Rcx];

        if (operation is < EpollControlAdd or > EpollControlDelete ||
            (operation is EpollControlAdd or EpollControlModify && eventAddress == 0) ||
            (operation == EpollControlDelete && eventAddress != 0))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (!_sockets.ContainsKey(socketId))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        NetEpollEvent requestedEvent = default;
        if (eventAddress != 0 && !TryReadNetEpollEvent(ctx, eventAddress, out requestedEvent))
        {
            return SetNetError(ctx, NetErrorFault, NetErrnoFault);
        }

        lock (EpollGate)
        {
            if (!_epolls.TryGetValue(epollId, out var epoll))
            {
                return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
            }

            var registrationIndex = epoll.Registrations.FindIndex(value => value.SocketId == socketId);
            switch (operation)
            {
                case EpollControlAdd when registrationIndex >= 0:
                    return SetNetError(ctx, NetErrorAlreadyExists, NetErrnoAlreadyExists);
                case EpollControlAdd:
                    epoll.Registrations.Add(new NetEpollRegistration(socketId, requestedEvent));
                    break;
                case EpollControlModify when registrationIndex < 0:
                    return SetNetError(ctx, NetErrorNoEntry, NetErrnoNoEntry);
                case EpollControlModify:
                    epoll.Registrations[registrationIndex] =
                        new NetEpollRegistration(socketId, requestedEvent);
                    break;
                case EpollControlDelete when registrationIndex < 0:
                    return SetNetError(ctx, NetErrorNoEntry, NetErrnoNoEntry);
                case EpollControlDelete:
                    epoll.Registrations.RemoveAt(registrationIndex);
                    break;
            }

            Monitor.PulseAll(EpollGate);
        }

        TraceNet(
            "epoll.control",
            epollId,
            unchecked((uint)operation),
            unchecked((uint)socketId),
            requestedEvent.Events);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "drjIbDbA7UQ",
        ExportName = "sceNetEpollWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetEpollWait(CpuContext ctx)
    {
        var epollId = unchecked((int)ctx[CpuRegister.Rdi]);
        var eventsAddress = ctx[CpuRegister.Rsi];
        var maxEvents = unchecked((int)ctx[CpuRegister.Rdx]);
        var timeoutMicroseconds = unchecked((int)ctx[CpuRegister.Rcx]);

        if (eventsAddress == 0)
        {
            return SetNetError(ctx, NetErrorFault, NetErrnoFault);
        }

        if (maxEvents <= 0 || timeoutMicroseconds < -1)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        NetEpollRegistration[] registrations;
        lock (EpollGate)
        {
            if (!_epolls.TryGetValue(epollId, out var epoll))
            {
                return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
            }

            if (epoll.Registrations.Count == 0 && timeoutMicroseconds != 0)
            {
                if (timeoutMicroseconds < 0)
                {
                    while (_epolls.TryGetValue(epollId, out epoll) && epoll.Registrations.Count == 0)
                    {
                        Monitor.Wait(EpollGate);
                    }
                }
                else
                {
                    Monitor.Wait(
                        EpollGate,
                        TimeSpan.FromTicks((long)timeoutMicroseconds * TimeSpan.TicksPerMicrosecond));
                }

                if (!_epolls.TryGetValue(epollId, out epoll))
                {
                    return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
                }
            }

            registrations = epoll.Registrations.ToArray();
        }

        if (registrations.Length == 0)
        {
            TraceNet("epoll.wait", epollId, unchecked((uint)maxEvents), unchecked((uint)timeoutMicroseconds), 0);
            return ctx.SetReturn(0);
        }

        var readSockets = new List<Socket>();
        var writeSockets = new List<Socket>();
        var errorSockets = new List<Socket>();
        var hostRegistrations = new Dictionary<Socket, NetEpollRegistration>();
        foreach (var registration in registrations)
        {
            if (!_sockets.TryGetValue(registration.SocketId, out var socket))
            {
                continue;
            }

            hostRegistrations[socket] = registration;
            if ((registration.Event.Events & NetEpollIn) != 0)
            {
                readSockets.Add(socket);
            }
            if ((registration.Event.Events & NetEpollOut) != 0)
            {
                writeSockets.Add(socket);
            }
            errorSockets.Add(socket);
        }

        if (hostRegistrations.Count == 0)
        {
            return ctx.SetReturn(0);
        }

        try
        {
            Socket.Select(readSockets, writeSockets, errorSockets, timeoutMicroseconds);
        }
        catch (ObjectDisposedException)
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }
        catch (SocketException)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        var readyCount = 0;
        foreach (var (socket, registration) in hostRegistrations)
        {
            uint readyEvents = 0;
            if (readSockets.Contains(socket))
            {
                readyEvents |= NetEpollIn;
            }
            if (writeSockets.Contains(socket))
            {
                readyEvents |= NetEpollOut;
            }
            if (errorSockets.Contains(socket))
            {
                readyEvents |= NetEpollError;
            }
            if (readyEvents == 0)
            {
                continue;
            }

            var outputAddress = eventsAddress + (ulong)(readyCount * NetEpollEventSize);
            if (!TryWriteNetEpollEvent(
                    ctx,
                    outputAddress,
                    readyEvents,
                    registration.SocketId,
                    registration.Event.Data))
            {
                return SetNetError(ctx, NetErrorFault, NetErrnoFault);
            }

            readyCount++;
            if (readyCount == maxEvents)
            {
                break;
            }
        }

        TraceNet(
            "epoll.wait",
            epollId,
            unchecked((uint)maxEvents),
            unchecked((uint)timeoutMicroseconds),
            unchecked((uint)readyCount));
        return ctx.SetReturn(readyCount);
    }

    [SysAbiExport(
        Nid = "Inp1lfL+Jdw",
        ExportName = "sceNetEpollDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetEpollDestroy(CpuContext ctx)
    {
        var epollId = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (EpollGate)
        {
            if (!_epolls.Remove(epollId))
            {
                return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
            }

            Monitor.PulseAll(EpollGate);
        }

        TraceNet("epoll.destroy", epollId, 0, 0, 0);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "dgJBaeJnGpo",
        ExportName = "sceNetPoolCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolCreate(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var size = unchecked((int)ctx[CpuRegister.Rsi]);
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);

        if (size <= 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;

        var id = Interlocked.Increment(ref _nextPoolId);
        _pools[id] = new NetPool(name, size, flags);

        TraceNet("pool.create", id, unchecked((ulong)size), unchecked((ulong)flags), _initialized ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "K7RlrTkI-mw",
        ExportName = "sceNetPoolDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetPoolDestroy(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_pools.TryRemove(id, out _))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        TraceNet("pool.destroy", id, 0, 0, _initialized ? 1UL : 0UL);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "9T2pDF2Ryqg",
        ExportName = "sceNetHtonl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetHtonl(CpuContext ctx)
    {
        var value = unchecked((uint)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "iWQWrwiSt8A",
        ExportName = "sceNetHtons",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetHtons(CpuContext ctx)
    {
        var value = unchecked((ushort)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "pQGpHYopAIY",
        ExportName = "sceNetNtohl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetNtohl(CpuContext ctx)
    {
        var value = unchecked((uint)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Rbvt+5Y2iEw",
        ExportName = "sceNetNtohs",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetNtohs(CpuContext ctx)
    {
        var value = unchecked((ushort)ctx[CpuRegister.Rdi]);
        // The byte-swapped result is the return value and already lives in Rax; return OK as the
        // dispatch status without going through SetReturn, which would overwrite Rax with 0.
        ctx[CpuRegister.Rax] = BinaryPrimitives.ReverseEndianness(value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "v6M4txecCuo",
        ExportName = "sceNetEtherNtostr",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetEtherNtostr(CpuContext ctx)
    {
        const int EtherStringLength = 18;
        const string HexDigits = "0123456789abcdef";

        var address = ctx[CpuRegister.Rdi];
        var destination = ctx[CpuRegister.Rsi];
        var destinationLength = ctx[CpuRegister.Rdx];
        if (address == 0 || destination == 0 || destinationLength < EtherStringLength)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> macAddress = stackalloc byte[6];
        if (!ctx.Memory.TryRead(address, macAddress))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        Span<byte> text = stackalloc byte[EtherStringLength];
        for (var index = 0; index < macAddress.Length; index++)
        {
            var value = macAddress[index];
            var offset = index * 3;
            text[offset] = unchecked((byte)HexDigits[value >> 4]);
            text[offset + 1] = unchecked((byte)HexDigits[value & 0xF]);
            if (index != macAddress.Length - 1)
            {
                text[offset + 2] = (byte)':';
            }
        }
        text[^1] = 0;

        if (!ctx.Memory.TryWrite(destination, text))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        TraceNet("ether_ntostr", 0, address, destination, destinationLength);
        return ctx.SetReturn(0);
    }

    [SysAbiExport(
        Nid = "C4UgDHHPvdw",
        ExportName = "sceNetResolverCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverCreate(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var poolId = unchecked((int)ctx[CpuRegister.Rsi]);
        var flags = unchecked((int)ctx[CpuRegister.Rdx]);
        if (flags != 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        var name = TryReadUtf8Z(ctx, nameAddress, MaxNameLength, out var value)
            ? value
            : string.Empty;
        var id = Interlocked.Increment(ref _nextResolverId);
        _resolvers[id] = new ResolverContext(name, poolId, flags, 0);
        TraceNet("resolver.create", id, unchecked((ulong)poolId), unchecked((ulong)flags), _initialized ? 1UL : 0UL);
        ctx[CpuRegister.Rax] = unchecked((ulong)id);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "kJlYH5uMAWI",
        ExportName = "sceNetResolverDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverDestroy(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        return _resolvers.TryRemove(id, out _)
            ? ctx.SetReturn(0)
            : ctx.SetReturn(NetErrorBadFileDescriptor);
    }

    [SysAbiExport(
        Nid = "J5i3hiLJMPk",
        ExportName = "sceNetResolverGetError",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverGetError(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        var statusAddress = ctx[CpuRegister.Rsi];
        if (statusAddress == 0)
        {
            return ctx.SetReturn(NetErrorInvalidArgument);
        }

        if (!_resolvers.TryGetValue(id, out var resolver))
        {
            return ctx.SetReturn(NetErrorBadFileDescriptor);
        }

        Span<byte> status = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(status, resolver.LastError);
        return ctx.Memory.TryWrite(statusAddress, status)
            ? ctx.SetReturn(0)
            : ctx.SetReturn((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
    }

    private static int SetNetError(CpuContext ctx, int result, int errno)
    {
        if (_errnoAddress == 0)
        {
            _errnoAddress = Marshal.AllocHGlobal(sizeof(int));
        }
        Marshal.WriteInt32(_errnoAddress, errno);
        return ctx.SetReturn(result);
    }

    [SysAbiExport(
        Nid = "Nd91WaWmG2w",
        ExportName = "sceNetResolverStartNtoa",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetResolverStartNtoa(CpuContext ctx)
    {
        var id = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!_resolvers.TryGetValue(id, out var resolver))
        {
            return SetNetError(ctx, NetErrorBadFileDescriptor, NetErrnoBadFileDescriptor);
        }

        var destination = ctx[CpuRegister.Rdx];
        var flags = unchecked((int)ctx[CpuRegister.R9]);
        if (destination == 0 || ctx[CpuRegister.Rsi] == 0 ||
            !TryReadUtf8Z(ctx, ctx[CpuRegister.Rsi], MaxNameLength, out var hostname) ||
            (flags & ~0x10000) != 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if ((flags & 0x10000) == 0 && IPAddress.TryParse(hostname, out var parsed) &&
            parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            return ctx.Memory.TryWrite(destination, parsed.GetAddressBytes())
                ? ctx.SetReturn(0)
                : SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        _resolvers.TryUpdate(id, resolver with { LastError = NetErrorResolverNoDns }, resolver);
        return SetNetError(ctx, NetErrorResolverNoDns, NetErrnoResolverNoDns);
    }

    private static int AddSocket(Socket socket)
    {
        lock (_socketIdGate)
        {
            // Guest fd_set has 1024 bits; IDs outside it corrupt the guest stack.
            for (var attempt = 0; attempt < 768; attempt++)
            {
                _nextSocketId = _nextSocketId == 1023 ? 256 : _nextSocketId + 1;
                if (_sockets.TryAdd(_nextSocketId, socket))
                {
                    return _nextSocketId;
                }
            }
        }

        return -1;
    }

    private static bool TryTranslateSocketParameters(
        int family,
        int type,
        int protocol,
        out AddressFamily addressFamily,
        out SocketType socketType,
        out ProtocolType protocolType)
    {
        addressFamily = family switch
        {
            2 => AddressFamily.InterNetwork,
            28 => AddressFamily.InterNetworkV6,
            _ => AddressFamily.Unspecified,
        };
        socketType = type switch
        {
            1 => SocketType.Stream,
            2 => SocketType.Dgram,
            _ => SocketType.Unknown,
        };
        protocolType = protocol switch
        {
            0 when socketType == SocketType.Stream => ProtocolType.Tcp,
            0 when socketType == SocketType.Dgram => ProtocolType.Udp,
            6 => ProtocolType.Tcp,
            17 => ProtocolType.Udp,
            _ => ProtocolType.Unknown,
        };

        return addressFamily != AddressFamily.Unspecified &&
            socketType != SocketType.Unknown &&
            protocolType != ProtocolType.Unknown;
    }

    private static bool TryReadSocketAddress(CpuContext ctx, ulong address, int length, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Any, 0);
        if (address == 0 || length < 16)
        {
            return false;
        }

        Span<byte> bytes = stackalloc byte[16];
        if (!ctx.Memory.TryRead(address, bytes) || bytes[1] != 2)
        {
            return false;
        }

        var port = BinaryPrimitives.ReadUInt16BigEndian(bytes[2..4]);
        endpoint = new IPEndPoint(new IPAddress(bytes[4..8]), port);
        return true;
    }

    private static bool TryReadUtf8Z(CpuContext ctx, ulong address, int maxLength, out string value)
    {
        value = string.Empty;
        if (address == 0)
        {
            return true;
        }

        Span<byte> one = stackalloc byte[1];
        var bytes = new byte[maxLength];
        var count = 0;
        for (; count < maxLength; count++)
        {
            if (!ctx.Memory.TryRead(address + (ulong)count, one))
            {
                return false;
            }

            if (one[0] == 0)
            {
                break;
            }

            bytes[count] = one[0];
        }

        value = Encoding.UTF8.GetString(bytes, 0, count);
        return true;
    }

    private static bool TryReadNetEpollEvent(
        CpuContext ctx,
        ulong address,
        out NetEpollEvent epollEvent)
    {
        Span<byte> bytes = stackalloc byte[NetEpollEventSize];
        if (!ctx.Memory.TryRead(address, bytes))
        {
            epollEvent = default;
            return false;
        }

        epollEvent = new NetEpollEvent(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]));
        return true;
    }

    private static bool TryWriteNetEpollEvent(
        CpuContext ctx,
        ulong address,
        uint events,
        int socketId,
        ulong data)
    {
        Span<byte> bytes = stackalloc byte[NetEpollEventSize];
        bytes.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, events);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[8..], unchecked((uint)socketId));
        BinaryPrimitives.WriteUInt64LittleEndian(bytes[16..], data);
        return ctx.Memory.TryWrite(address, bytes);
    }

    [SysAbiExport(
        Nid = "8Kcp5d-q1Uo",
        ExportName = "sceNetInetPton",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetInetPton(CpuContext ctx)
    {
        var addressFamily = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];
        var destinationAddress = ctx[CpuRegister.Rdx];
        if (sourceAddress == 0 || destinationAddress == 0)
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        if (!TryReadUtf8Z(ctx, sourceAddress, MaxNameLength, out var source))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        var family = addressFamily switch
        {
            2 => AddressFamily.InterNetwork,      // AF_INET
            28 => AddressFamily.InterNetworkV6,   // AF_INET6
            _ => AddressFamily.Unknown,
        };
        if (family == AddressFamily.Unknown ||
            !IPAddress.TryParse(source, out var parsed) ||
            parsed.AddressFamily != family)
        {
            // Match BSD inet_pton: return 0 for a parseable-family miss.
            ctx[CpuRegister.Rax] = 0;
            return 0;
        }

        var bytes = parsed.GetAddressBytes();
        if (!ctx.Memory.TryWrite(destinationAddress, bytes))
        {
            return SetNetError(ctx, NetErrorInvalidArgument, NetErrnoInvalidArgument);
        }

        TraceNet("inet_pton", addressFamily, sourceAddress, destinationAddress, (ulong)bytes.Length);
        ctx[CpuRegister.Rax] = 1;
        return 1;
    }

    [SysAbiExport(
        Nid = "9vA2aW+CHuA",
        ExportName = "sceNetInetNtop",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceNet")]
    public static int NetInetNtop(CpuContext ctx) => PosixInetNtop(ctx);

    private static void TraceNet(string operation, int id, ulong arg0, ulong arg1, ulong arg2)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_NET"), "1", StringComparison.Ordinal))
        {
            return;
        }

        Console.Error.WriteLine(
            $"[LOADER][TRACE] net.{operation} id={id} arg0=0x{arg0:X16} arg1=0x{arg1:X16} arg2=0x{arg2:X16}");
    }
}
