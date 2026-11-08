// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections;
using System.Reflection;
using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.VideoOut;
using SharpEmu.HLE;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(SchedulingStateCollection.Name)]
public sealed class GnmVideoOutResolutionTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong Status = MemoryBase + 0x100;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _context;
    private readonly int _handle;

    public GnmVideoOutResolutionTests()
    {
        _context = new CpuContext(_memory, Generation.Gen4);
        _handle = VideoOutExports.VideoOutOpen(_context);
        Assert.True(_handle > 0);
    }

    public void Dispose()
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)_handle);
        _ = VideoOutExports.VideoOutClose(_context);
    }

    [Fact]
    public void ResolutionExportIsRegisteredOnlyInGnmMode()
    {
        var export = Assert.Single(GnmExportRegistry.CreateExports(), export => export.Nid == "6kPnj51T62Y");
        Assert.Equal("sceVideoOutGetResolutionStatus", export.Name);
        Assert.Equal("libSceVideoOut", export.LibraryName);
        Assert.Equal(Generation.Gen4, export.Target);
        Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            export => export.Nid == "6kPnj51T62Y");
    }

    [Theory]
    [InlineData(1920u, 1080u, 60u, 3UL)]
    [InlineData(3840u, 2160u, 120u, 13UL)]
    public void ResolutionStatusUsesTheGen4LayoutAndCurrentOutputMode(uint width, uint height, uint rate, ulong rateCode)
    {
        var ports = (IDictionary)typeof(VideoOutExports).GetField("_ports", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        var port = ports[_handle]!;
        port.GetType().GetProperty("OutputWidth")!.SetValue(port, width);
        port.GetType().GetProperty("OutputHeight")!.SetValue(port, height);
        port.GetType().GetProperty("RefreshRate")!.SetValue(port, rate);
        Assert.True(_memory.TryWrite(Status - 1, Enumerable.Repeat((byte)0xA5, 0x32).ToArray()));
        _context[CpuRegister.Rdi] = unchecked((ulong)_handle);
        _context[CpuRegister.Rsi] = Status;
        _context[CpuRegister.Rdx] = 0x1234;

        Assert.Equal(0, GnmVideoOutExports.GetResolutionStatus(_context));
        Assert.Equal(0UL, _context[CpuRegister.Rax]);
        Assert.Equal(unchecked((ulong)_handle), _context[CpuRegister.Rdi]);
        Assert.Equal(Status, _context[CpuRegister.Rsi]);
        Assert.Equal(0x1234UL, _context[CpuRegister.Rdx]);
        var bytes = new byte[0x32];
        Assert.True(_memory.TryRead(Status - 1, bytes));
        Assert.Equal(0xA5, bytes[0]);
        Assert.Equal(0xA5, bytes[^1]);
        var status = bytes.AsSpan(1, 0x30);
        Assert.Equal(width, BinaryPrimitives.ReadUInt32LittleEndian(status));
        Assert.Equal(height, BinaryPrimitives.ReadUInt32LittleEndian(status[4..]));
        Assert.Equal(width, BinaryPrimitives.ReadUInt32LittleEndian(status[8..]));
        Assert.Equal(height, BinaryPrimitives.ReadUInt32LittleEndian(status[12..]));
        Assert.Equal(rateCode, BinaryPrimitives.ReadUInt64LittleEndian(status[16..]));
        Assert.All(status[24..].ToArray(), value => Assert.Equal(0, value));
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(MemoryBase + 0x1000)]
    [InlineData(MemoryBase + 0x1000 - 0x2F)]
    public void InvalidStatusAddressesReturnTheVideoOutAddressError(ulong address)
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)_handle);
        _context[CpuRegister.Rsi] = address;
        Assert.Equal(unchecked((int)0x80290002), GnmVideoOutExports.GetResolutionStatus(_context));
        Assert.Equal(unchecked((ulong)(int)0x80290002), _context[CpuRegister.Rax]);
    }

    [Fact]
    public void InvalidHandleDoesNotWriteStatus()
    {
        var marker = Enumerable.Repeat((byte)0xA5, 0x30).ToArray();
        Assert.True(_memory.TryWrite(Status, marker));
        _context[CpuRegister.Rdi] = ulong.MaxValue;
        _context[CpuRegister.Rsi] = Status;
        Assert.Equal(unchecked((int)0x8029000B), GnmVideoOutExports.GetResolutionStatus(_context));
        var bytes = new byte[0x30];
        Assert.True(_memory.TryRead(Status, bytes));
        Assert.Equal(marker, bytes);
    }
}
