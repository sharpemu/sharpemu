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
        public TcpClient? Client;
        public NetworkStream? Stream;
        public IPAddress BoundAddress = IPAddress.Any;
        public int BoundPort;
        public bool Bound;
        public bool Connected;
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
        var fd = KernelMemoryCompatExports.AllocateGuestFileDescriptor();
        lock (Gate)
        {
            Sockets[fd] = new EmulatedSocketState();
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
            RemoveEmulatedSocketFd(fd);
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
            RemoveEmulatedSocketFd(fd);
            ctx[CpuRegister.Rax] = unchecked((ulong)0xFFFFFFFFFFFFFFFF);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!TryEstablishHostTcpConnection(ipAddress, port, out var client, out var stream))
        {
            LogNet($"connect failed: fd={fd} ip={ipAddress} port={port}");
            RemoveEmulatedSocketFd(fd);
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

        state.BoundAddress = ipAddress;
        state.BoundPort = port;
        state.Bound = true;
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
        state.Stream = null;
        state.Client = null;
        state.Connected = false;
    }

    private static void RemoveEmulatedSocketFd(int fd)
    {
        lock (Gate)
        {
            if (Sockets.Remove(fd, out var socketState))
            {
                DisposeEmulatedSocket(socketState);
            }
        }
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
