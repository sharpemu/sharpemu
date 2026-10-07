// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

// The libSceNet byte-order helpers take their operand in Rdi and return the converted value in
// Rax. They swap endianness unconditionally, which is correct on the little-endian hosts (and
// little-endian guest) the emulator targets, so network (big-endian) order is always a byte swap.
public sealed class NetExportsTests
{
    private readonly CpuContext _ctx = new(new FakeCpuMemory(0x1_0000_0000, 0x1000), Generation.Gen5);

    [Fact]
    public void EthernetFormattingWritesExactlyEighteenBytesAndSupportsOverlap()
    {
        const ulong address = 0x1_0000_0100;
        var initial = Enumerable.Repeat((byte)0xA5, 20).ToArray();
        new byte[] { 0x02, 0x7F, 0x80, 0xAB, 0xCD, 0xFF }.CopyTo(initial, 0);
        Assert.True(_ctx.Memory.TryWrite(address, initial));
        _ctx[CpuRegister.Rdi] = address;
        _ctx[CpuRegister.Rsi] = address;
        _ctx[CpuRegister.Rdx] = 18;
        Assert.Equal(0, NetExports.NetEtherNtostr(_ctx));
        var actual = new byte[20];
        Assert.True(_ctx.Memory.TryRead(address, actual));
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes("02:7f:80:ab:cd:ff\0"), actual[..18]);
        Assert.Equal(new byte[] { 0xA5, 0xA5 }, actual[18..]);
    }

    [Theory]
    [InlineData(17)]
    [InlineData(-1)]
    public void EthernetFormattingRejectsSmallOrNegativeCapacityWithoutWriting(int capacity)
    {
        const ulong address = 0x1_0000_0100;
        var sentinel = Enumerable.Repeat((byte)0xA5, 18).ToArray();
        Assert.True(_ctx.Memory.TryWrite(address, sentinel));
        _ctx[CpuRegister.Rdi] = address;
        _ctx[CpuRegister.Rsi] = address;
        _ctx[CpuRegister.Rdx] = unchecked((ulong)capacity);
        Assert.Equal(unchecked((int)0x80410116), NetExports.NetEtherNtostr(_ctx));
        var actual = new byte[18];
        Assert.True(_ctx.Memory.TryRead(address, actual));
        Assert.Equal(sentinel, actual);
    }

    [Fact]
    public void EpollReadiness_DistinguishesPendingAcceptFromPeerShutdown()
    {
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 1;
        _ctx[CpuRegister.Rcx] = 6;
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var socketId = _ctx[CpuRegister.Rax];
        var sockets = (System.Collections.Concurrent.ConcurrentDictionary<int, System.Net.Sockets.Socket>)
            typeof(NetExports).GetField("_sockets", System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.NonPublic)!.GetValue(null)!;
        var listener = sockets[(int)socketId];
        listener.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        _ctx[CpuRegister.Rdi] = socketId;
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(0, NetExports.NetListen(_ctx));
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, NetExports.NetEpollCreate(_ctx));
        var poll = _ctx[CpuRegister.Rax];
        ulong acceptedId = 0;
        using var client = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        const ulong input = 0x1_0000_0100, output = 0x1_0000_0200;
        try
        {
            var record = new byte[24];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record, 1);
            Assert.True(_ctx.Memory.TryWrite(input, record));
            void Watch(ulong id)
            {
                _ctx[CpuRegister.Rdi] = poll;
                _ctx[CpuRegister.Rsi] = 1;
                _ctx[CpuRegister.Rdx] = id;
                _ctx[CpuRegister.Rcx] = input;
                Assert.Equal(0, NetExports.NetEpollControl(_ctx));
            }
            uint ReadEvents()
            {
                _ctx[CpuRegister.Rdi] = poll;
                _ctx[CpuRegister.Rsi] = output;
                _ctx[CpuRegister.Rdx] = 1;
                _ctx[CpuRegister.Rcx] = 1_000_000;
                Assert.Equal(0, NetExports.NetEpollWait(_ctx));
                Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
                Assert.True(_ctx.Memory.TryRead(output, record));
                return System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(record);
            }
            Watch(socketId);
            client.Connect(listener.LocalEndPoint!);
            Assert.Equal(1u, ReadEvents());
            _ctx[CpuRegister.Rdi] = socketId;
            Assert.Equal(0, NetExports.NetAccept(_ctx));
            acceptedId = _ctx[CpuRegister.Rax];
            Watch(acceptedId);
            client.Shutdown(System.Net.Sockets.SocketShutdown.Send);
            Assert.Equal(0x11u, ReadEvents());
        }
        finally
        {
            if (acceptedId != 0)
            {
                _ctx[CpuRegister.Rdi] = acceptedId;
                NetExports.NetSocketClose(_ctx);
            }
            _ctx[CpuRegister.Rdi] = socketId;
            NetExports.NetSocketClose(_ctx);
            _ctx[CpuRegister.Rdi] = poll;
            NetExports.NetEpollDestroy(_ctx);
        }
    }

    [Fact]
    public void EpollDestroy_WakesAnInfiniteWaitWithBadDescriptor()
    {
        Assert.Equal(0, NetExports.NetEpollCreate(_ctx));
        var poll = _ctx[CpuRegister.Rax];
        using var started = new ManualResetEventSlim();
        Exception? failure = null;
        var result = 0;
        var worker = new Thread(() =>
        {
            try
            {
                var context = new CpuContext(_ctx.Memory, Generation.Gen5);
                context[CpuRegister.Rdi] = poll;
                context[CpuRegister.Rsi] = 0x1_0000_0100;
                context[CpuRegister.Rdx] = 1;
                context[CpuRegister.Rcx] = uint.MaxValue;
                started.Set();
                result = NetExports.NetEpollWait(context);
            }
            catch (Exception error) { failure = error; }
        }) { IsBackground = true };
        worker.Start();
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            Assert.False(worker.Join(TimeSpan.FromMilliseconds(20)));
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = poll;
            Assert.Equal(0, NetExports.NetEpollDestroy(_ctx));
            Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        }
        Assert.Null(failure);
        Assert.Equal(unchecked((int)0x80410109), result);
    }

    [Fact]
    public void EpollWait_EmptyPollWaitsForMicrosecondTimeoutWithoutWritingEvents()
    {
        Assert.Equal(0, NetExports.NetEpollCreate(_ctx));
        var poll = _ctx[CpuRegister.Rax];
        var output = 0x1_0000_0100UL;
        var before = Enumerable.Repeat((byte)0xCC, 48).ToArray();
        Assert.True(_ctx.Memory.TryWrite(output, before));
        try
        {
            _ctx[CpuRegister.Rdi] = poll;
            _ctx[CpuRegister.Rsi] = output;
            _ctx[CpuRegister.Rdx] = 2;
            _ctx[CpuRegister.Rcx] = 10_000;
            var clock = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal(0, NetExports.NetEpollWait(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
            Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(10));
            var after = new byte[48];
            Assert.True(_ctx.Memory.TryRead(output, after));
            Assert.Equal(before, after);
        }
        finally { _ctx[CpuRegister.Rdi] = poll; NetExports.NetEpollDestroy(_ctx); }
    }

    [Fact]
    public void EpollControlAndWait_ReportSocketReadinessAndPreserveEventData()
    {
        Assert.Equal(0, NetExports.NetEpollCreate(_ctx));
        var poll = _ctx[CpuRegister.Rax];
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 2;
        _ctx[CpuRegister.Rcx] = 17;
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var socket = _ctx[CpuRegister.Rax];
        const ulong input = 0x1_0000_0100, output = 0x1_0000_0200;
        var record = new byte[24];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record, 2);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(record.AsSpan(16), 0x123456789ABCDEF0);
        Assert.True(_ctx.Memory.TryWrite(input, record));
        Assert.True(_ctx.Memory.TryWrite(output, Enumerable.Repeat((byte)0xCC, 48).ToArray()));
        try
        {
            _ctx[CpuRegister.Rdi] = poll;
            _ctx[CpuRegister.Rsi] = 1;
            _ctx[CpuRegister.Rdx] = socket;
            _ctx[CpuRegister.Rcx] = input;
            Assert.Equal(0, NetExports.NetEpollControl(_ctx));
            Assert.Equal(unchecked((int)0x80410111), NetExports.NetEpollControl(_ctx));
            _ctx[CpuRegister.Rsi] = output;
            _ctx[CpuRegister.Rdx] = 1;
            _ctx[CpuRegister.Rcx] = 0;
            Assert.Equal(0, NetExports.NetEpollWait(_ctx));
            Assert.Equal(1UL, _ctx[CpuRegister.Rax]);
            var actual = new byte[48];
            Assert.True(_ctx.Memory.TryRead(output, actual));
            Assert.Equal(2u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(actual));
            Assert.Equal(socket, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(actual.AsSpan(8)));
            Assert.Equal(0x123456789ABCDEF0UL, System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(actual.AsSpan(16)));
            Assert.All(actual.Skip(24), value => Assert.Equal((byte)0xCC, value));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(record, 1);
            Assert.True(_ctx.Memory.TryWrite(input, record));
            _ctx[CpuRegister.Rsi] = 2;
            _ctx[CpuRegister.Rdx] = socket;
            _ctx[CpuRegister.Rcx] = input;
            Assert.Equal(0, NetExports.NetEpollControl(_ctx));
            _ctx[CpuRegister.Rsi] = output;
            _ctx[CpuRegister.Rdx] = 1;
            _ctx[CpuRegister.Rcx] = 0;
            Assert.Equal(0, NetExports.NetEpollWait(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
            _ctx[CpuRegister.Rsi] = 3;
            _ctx[CpuRegister.Rdx] = socket;
            _ctx[CpuRegister.Rcx] = 0;
            Assert.Equal(0, NetExports.NetEpollControl(_ctx));
            Assert.Equal(unchecked((int)0x80410109), NetExports.NetEpollControl(_ctx));
            _ctx[CpuRegister.Rsi] = output;
            _ctx[CpuRegister.Rdx] = 1;
            Assert.Equal(0, NetExports.NetEpollWait(_ctx));
            Assert.Equal(0UL, _ctx[CpuRegister.Rax]);
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = socket; NetExports.NetSocketClose(_ctx);
            _ctx[CpuRegister.Rdi] = poll; NetExports.NetEpollDestroy(_ctx);
        }
    }

    [Fact]
    public void EpollDescriptorsAreUniqueAndDestroyRejectsClosedDescriptors()
    {
        Assert.Equal(0, NetExports.NetEpollCreate(_ctx));
        var first = _ctx[CpuRegister.Rax];
        Assert.Equal(0, NetExports.NetEpollCreate(_ctx));
        var second = _ctx[CpuRegister.Rax];
        try
        {
            Assert.True(first > 0);
            Assert.NotEqual(first, second);
            _ctx[CpuRegister.Rdi] = first;
            Assert.Equal(0, NetExports.NetEpollDestroy(_ctx));
            Assert.Equal(unchecked((int)0x80410109), NetExports.NetEpollDestroy(_ctx));
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = first;
            NetExports.NetEpollDestroy(_ctx);
            _ctx[CpuRegister.Rdi] = second;
            NetExports.NetEpollDestroy(_ctx);
        }
    }

    [Fact]
    public void EpollCreateRejectsFlagsAndUnreadableNames()
    {
        _ctx[CpuRegister.Rsi] = 1;
        Assert.Equal(unchecked((int)0x80410116), NetExports.NetEpollCreate(_ctx));
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdi] = 1;
        Assert.Equal(unchecked((int)0x8041010E), NetExports.NetEpollCreate(_ctx));
    }

    [Fact]
    public void SocketDescriptorsFitGuestFdSet()
    {
        _ctx[CpuRegister.Rsi] = 2; // AF_INET
        _ctx[CpuRegister.Rdx] = 2; // SOCK_DGRAM
        _ctx[CpuRegister.Rcx] = 17; // UDP

        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var first = _ctx[CpuRegister.Rax];
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var second = _ctx[CpuRegister.Rax];

        try
        {
            Assert.InRange(first, 256UL, 1023UL);
            Assert.InRange(second, 256UL, 1023UL);
            Assert.NotEqual(first, second);
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = first;
            NetExports.NetSocketClose(_ctx);
            _ctx[CpuRegister.Rdi] = second;
            NetExports.NetSocketClose(_ctx);
        }
    }

    [Fact]
    public void NonblockingReceiveWithoutDataReturnsWouldBlock()
    {
        _ctx[CpuRegister.Rsi] = 2;
        _ctx[CpuRegister.Rdx] = 2;
        _ctx[CpuRegister.Rcx] = 17;
        Assert.Equal(0, NetExports.NetSocket(_ctx));
        var socket = _ctx[CpuRegister.Rax];

        try
        {
            _ctx[CpuRegister.Rdi] = socket;
            _ctx[CpuRegister.Rsi] = 0x1_0000_0000;
            _ctx[CpuRegister.Rdx] = 1;
            _ctx[CpuRegister.Rcx] = 0x80;
            Assert.Equal(unchecked((int)0x80410123), NetExports.NetRecv(_ctx));
        }
        finally
        {
            _ctx[CpuRegister.Rdi] = socket;
            NetExports.NetSocketClose(_ctx);
        }
    }

    [Fact]
    public void Htonl_SwapsAllFourBytes()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;

        Assert.Equal(0, NetExports.NetHtonl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohl_SwapsAllFourBytes()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;

        Assert.Equal(0, NetExports.NetNtohl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Htons_SwapsLowTwoBytesOnly()
    {
        // High bits above the 16-bit short must be ignored, not folded into the result.
        _ctx[CpuRegister.Rdi] = 0xFFFF_0102;

        Assert.Equal(0, NetExports.NetHtons(_ctx));
        Assert.Equal(0x0201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Ntohs_SwapsLowTwoBytesOnly()
    {
        _ctx[CpuRegister.Rdi] = 0xFFFF_0102;

        Assert.Equal(0, NetExports.NetNtohs(_ctx));
        Assert.Equal(0x0201UL, _ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Htonl_IgnoresBitsAboveThe32BitWord()
    {
        _ctx[CpuRegister.Rdi] = 0xDEADBEEF_01020304;

        Assert.Equal(0, NetExports.NetHtonl(_ctx));
        Assert.Equal(0x04030201UL, _ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0xDEADBEEFUL)]
    [InlineData(0x00000000UL)]
    [InlineData(0xFFFFFFFFUL)]
    [InlineData(0x00000001UL)]
    public void HtonlThenNtohl_RoundTripsToOriginal(ulong value)
    {
        _ctx[CpuRegister.Rdi] = value;
        NetExports.NetHtonl(_ctx);

        _ctx[CpuRegister.Rdi] = _ctx[CpuRegister.Rax];
        NetExports.NetNtohl(_ctx);

        Assert.Equal(value, _ctx[CpuRegister.Rax]);
    }

    // Regression guard: a non-palindromic value must not come back as 0. The functions previously
    // computed the swap into Rax and then called SetReturn(0), which overwrote Rax, so every
    // sceNetHtonl/Htons/Ntohl/Ntohs call returned 0 regardless of input.
    [Fact]
    public void ByteOrderConversions_DoNotReturnZeroForNonZeroInput()
    {
        _ctx[CpuRegister.Rdi] = 0x01020304;
        NetExports.NetHtonl(_ctx);
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);

        _ctx[CpuRegister.Rax] = 0;
        _ctx[CpuRegister.Rdi] = 0x0102;
        NetExports.NetHtons(_ctx);
        Assert.NotEqual(0UL, _ctx[CpuRegister.Rax]);
    }
}
