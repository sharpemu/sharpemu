// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class HttpExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ServerName = MemoryBase + 0x100;
    private const ulong Scheme = MemoryBase + 0x140;
    private const ulong RequestPath = MemoryBase + 0x180;
    private const ulong HeaderName = MemoryBase + 0x1C0;
    private const int InvalidId = unchecked((int)0x80431100);
    private const int BeforeSend = unchecked((int)0x80431065);
    private const int ResolverNoDns = unchecked((int)0x80436002);
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public HttpExportsTests()
    {
        _ctx = new CpuContext(_memory, Generation.Gen5);
        _memory.WriteCString(ServerName, "example.invalid");
        _memory.WriteCString(Scheme, "https");
        _memory.WriteCString(RequestPath, "/upload");
        _memory.WriteCString(HeaderName, "Content-Type");
    }

    // A request is created as on a console; sending it fails without a network and leaves no response to read.
    [Fact]
    public void RequestsAreCreatedButNeverReachTheServer()
    {
        var context = Call(HttpExports.HttpInit, 1, 0, 0x10000);
        var template = Call(HttpExports.HttpCreateTemplate, (ulong)context, 0, 1, 0);
        var connection = Call(HttpExports.HttpCreateConnection, (ulong)template, ServerName, Scheme, 443, 1);
        var request = Call(HttpExports.HttpCreateRequest, (ulong)connection, 1, RequestPath, 16);
        Assert.NotEqual(template, connection);
        Assert.NotEqual(connection, request);

        Assert.Equal(0, Call(HttpExports.HttpAddRequestHeader, (ulong)request, HeaderName, Scheme, 0));
        Assert.Equal(0, Call(HttpExports.HttpSetRequestContentLength, (ulong)request, 32));
        Assert.Equal(ResolverNoDns, Call(HttpExports.HttpSendRequest, (ulong)request, 0, 0));
        Assert.Equal(BeforeSend, Call(HttpExports.HttpGetStatusCode, (ulong)request, MemoryBase));
        Assert.Equal(BeforeSend, Call(HttpExports.HttpReadData, (ulong)request, MemoryBase, 16));

        Assert.Equal(0, Call(HttpExports.HttpDeleteConnection, (ulong)connection));
        Assert.Equal(InvalidId, Call(HttpExports.HttpDeleteRequest, (ulong)request));
        Assert.Equal(InvalidId, Call(HttpExports.HttpCreateConnection, (ulong)connection, ServerName, Scheme, 443, 1));
        Assert.Equal(0, Call(HttpExports.HttpTerm, (ulong)context));
    }

    private int Call(Func<CpuContext, int> export, params ulong[] arguments)
    {
        CpuRegister[] registers = [CpuRegister.Rdi, CpuRegister.Rsi, CpuRegister.Rdx, CpuRegister.Rcx, CpuRegister.R8];
        for (var index = 0; index < registers.Length; index++)
        {
            _ctx[registers[index]] = index < arguments.Length ? arguments[index] : 0;
        }

        _ctx[CpuRegister.Rax] = 0;
        var result = export(_ctx);
        return result == 0 && _ctx[CpuRegister.Rax] != 0 ? unchecked((int)_ctx[CpuRegister.Rax]) : result;
    }
}
