// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using System.Buffers.Binary;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class HttpConnectionTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong UserAgentAddress = MemoryBase + 0x100;
    private const ulong ServerNameAddress = MemoryBase + 0x200;
    private const ulong SchemeAddress = MemoryBase + 0x300;
    private const ulong PathAddress = MemoryBase + 0x400;
    private const ulong HeaderNameAddress = MemoryBase + 0x500;
    private const ulong HeaderValueAddress = MemoryBase + 0x600;
    private const ulong OutputAddress = MemoryBase + 0x700;
    private const int InvalidId = unchecked((int)0x80431100);
    private const int InvalidValue = unchecked((int)0x804311FE);
    private const int BeforeSend = unchecked((int)0x80431065);
    private const int Timeout = unchecked((int)0x80431068);
    private const int UnknownMethod = unchecked((int)0x8043106B);

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void CreateConnectionExportRegistersForBothGenerations(Generation generation)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

        Assert.True(manager.TryGetExport("Kiwv9r4IZCc", out var export));
        Assert.Equal("sceHttpCreateConnection", export.Name);
        Assert.Equal("libSceHttp", export.LibraryName);
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void CreateConnectionReturnsLogicalHandleWithoutOpeningHostNetwork(Generation generation)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        memory.WriteCString(UserAgentAddress, "SharpEmu test");
        memory.WriteCString(ServerNameAddress, "telemetry.example.invalid");
        memory.WriteCString(SchemeAddress, "https");
        var context = new CpuContext(memory, generation);
        var contextId = CreateHttpContext(context);

        try
        {
            var templateId = CreateTemplate(context, contextId);
            context[CpuRegister.Rdi] = unchecked((ulong)templateId);
            context[CpuRegister.Rsi] = ServerNameAddress;
            context[CpuRegister.Rdx] = SchemeAddress;
            context[CpuRegister.Rcx] = 443;
            context[CpuRegister.R8] = 1;

            Assert.Equal(0, HttpExports.HttpCreateConnection(context));
            Assert.InRange(unchecked((int)context[CpuRegister.Rax]), 1, int.MaxValue);

            context[CpuRegister.Rdi] = unchecked((ulong)templateId);
            Assert.Equal(0, HttpExports.HttpDeleteTemplate(context));
            context[CpuRegister.Rsi] = ServerNameAddress;
            context[CpuRegister.Rdx] = SchemeAddress;
            context[CpuRegister.Rcx] = 443;
            context[CpuRegister.R8] = 1;
            Assert.Equal(InvalidId, HttpExports.HttpCreateConnection(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)contextId);
            Assert.Equal(0, HttpExports.HttpTerm(context));
        }
    }

    [Fact]
    public void CreateConnectionValidatesTemplateAndGuestStrings()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        memory.WriteCString(UserAgentAddress, "SharpEmu test");
        memory.WriteCString(ServerNameAddress, "host.example.invalid");
        memory.WriteCString(SchemeAddress, "https");
        var context = new CpuContext(memory, Generation.Gen5);
        var contextId = CreateHttpContext(context);

        try
        {
            var templateId = CreateTemplate(context, contextId);

            context[CpuRegister.Rdi] = unchecked((ulong)(templateId + 1));
            context[CpuRegister.Rsi] = ServerNameAddress;
            context[CpuRegister.Rdx] = SchemeAddress;
            Assert.Equal(InvalidId, HttpExports.HttpCreateConnection(context));

            context[CpuRegister.Rdi] = unchecked((ulong)templateId);
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(InvalidValue, HttpExports.HttpCreateConnection(context));

            context[CpuRegister.Rsi] = ServerNameAddress;
            context[CpuRegister.Rdx] = 0;
            Assert.Equal(InvalidValue, HttpExports.HttpCreateConnection(context));

            Span<byte> unterminated = stackalloc byte[8];
            unterminated.Fill((byte)'x');
            var truncatedAddress = MemoryBase + 0x1000 - (ulong)unterminated.Length;
            Assert.True(memory.TryWrite(truncatedAddress, unterminated));
            context[CpuRegister.Rsi] = truncatedAddress;
            context[CpuRegister.Rdx] = SchemeAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
                HttpExports.HttpCreateConnection(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)contextId);
            Assert.Equal(0, HttpExports.HttpTerm(context));
        }
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void RequestLifecycleIsStatefulAndOffline(Generation generation)
    {
        HttpExports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        memory.WriteCString(UserAgentAddress, "SharpEmu test");
        memory.WriteCString(ServerNameAddress, "telemetry.example.invalid");
        memory.WriteCString(SchemeAddress, "https");
        memory.WriteCString(PathAddress, "/entitlements/query");
        memory.WriteCString(HeaderNameAddress, "Content-Type");
        memory.WriteCString(HeaderValueAddress, "application/json");
        var context = new CpuContext(memory, generation);
        var contextId = CreateHttpContext(context);

        try
        {
            var templateId = CreateTemplate(context, contextId);
            var connectionId = CreateConnection(context, templateId);

            context[CpuRegister.Rdi] = unchecked((ulong)connectionId);
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = PathAddress;
            context[CpuRegister.Rcx] = 0x200;
            Assert.Equal(0, HttpExports.HttpCreateRequest(context));
            var requestId = unchecked((int)context[CpuRegister.Rax]);
            Assert.True(HttpExports.TryGetRequestForTests(
                requestId, out var method, out var path, out var contentLength, out var headerCount));
            Assert.Equal("GET", method);
            Assert.Equal("/entitlements/query", path);
            Assert.Equal(0x200UL, contentLength);
            Assert.Equal(0, headerCount);

            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            context[CpuRegister.Rsi] = HeaderNameAddress;
            context[CpuRegister.Rdx] = HeaderValueAddress;
            context[CpuRegister.Rcx] = 0;
            Assert.Equal(0, HttpExports.HttpAddRequestHeader(context));
            Assert.True(HttpExports.TryGetRequestForTests(
                requestId, out _, out _, out _, out headerCount));
            Assert.Equal(1, headerCount);

            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            context[CpuRegister.Rsi] = 0x345;
            Assert.Equal(0, HttpExports.HttpSetRequestContentLength(context));
            Assert.True(HttpExports.TryGetRequestForTests(
                requestId, out _, out _, out contentLength, out _));
            Assert.Equal(0x345UL, contentLength);

            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            context[CpuRegister.Rsi] = OutputAddress;
            Assert.Equal(BeforeSend, HttpExports.HttpGetStatusCode(context));
            Assert.Equal(0U, ReadUInt32(memory, OutputAddress));

            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;
            Assert.Equal(Timeout, HttpExports.HttpSendRequest(context));

            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            context[CpuRegister.Rsi] = OutputAddress;
            Assert.Equal(Timeout, HttpExports.HttpGetStatusCode(context));
            Assert.Equal(0U, ReadUInt32(memory, OutputAddress));

            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            context[CpuRegister.Rsi] = OutputAddress;
            context[CpuRegister.Rdx] = OutputAddress + 8;
            Assert.Equal(Timeout, HttpExports.HttpGetAllResponseHeaders(context));
            Assert.Equal(0UL, ReadUInt64(memory, OutputAddress));
            Assert.Equal(0UL, ReadUInt64(memory, OutputAddress + 8));

            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            context[CpuRegister.Rsi] = OutputAddress;
            context[CpuRegister.Rdx] = OutputAddress + 8;
            Assert.Equal(Timeout, HttpExports.HttpGetResponseContentLength(context));
            Assert.Equal(uint.MaxValue, ReadUInt32(memory, OutputAddress));
            Assert.Equal(0UL, ReadUInt64(memory, OutputAddress + 8));

            context[CpuRegister.Rdi] = unchecked((ulong)connectionId);
            Assert.Equal(0, HttpExports.HttpDeleteConnection(context));
            context[CpuRegister.Rdi] = unchecked((ulong)requestId);
            Assert.Equal(InvalidId, HttpExports.HttpDeleteRequest(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)contextId);
            Assert.Equal(0, HttpExports.HttpTerm(context));
            HttpExports.ResetRuntimeState();
        }
    }

    [Fact]
    public void CreateRequestValidatesMethodPathAndConnection()
    {
        HttpExports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        memory.WriteCString(UserAgentAddress, "SharpEmu test");
        memory.WriteCString(ServerNameAddress, "host.example.invalid");
        memory.WriteCString(SchemeAddress, "https");
        memory.WriteCString(PathAddress, "/");
        var context = new CpuContext(memory, Generation.Gen5);
        var contextId = CreateHttpContext(context);

        try
        {
            var templateId = CreateTemplate(context, contextId);
            var connectionId = CreateConnection(context, templateId);

            context[CpuRegister.Rdi] = unchecked((ulong)connectionId);
            context[CpuRegister.Rsi] = 8;
            context[CpuRegister.Rdx] = PathAddress;
            Assert.Equal(UnknownMethod, HttpExports.HttpCreateRequest(context));

            context[CpuRegister.Rsi] = 0;
            context[CpuRegister.Rdx] = 0;
            Assert.Equal(InvalidValue, HttpExports.HttpCreateRequest(context));

            context[CpuRegister.Rdi] = unchecked((ulong)(connectionId + 1));
            context[CpuRegister.Rdx] = PathAddress;
            Assert.Equal(InvalidId, HttpExports.HttpCreateRequest(context));
        }
        finally
        {
            context[CpuRegister.Rdi] = unchecked((ulong)contextId);
            Assert.Equal(0, HttpExports.HttpTerm(context));
            HttpExports.ResetRuntimeState();
        }
    }

    [Theory]
    [InlineData("P6A3ytpsiYc", "sceHttpDeleteConnection")]
    [InlineData("tsGVru3hCe8", "sceHttpCreateRequest")]
    [InlineData("qe7oZ+v4PWA", "sceHttpDeleteRequest")]
    [InlineData("PTiFIUxCpJc", "sceHttpSetRequestContentLength")]
    [InlineData("EY28T2bkN7k", "sceHttpAddRequestHeader")]
    [InlineData("1e2BNwI-XzE", "sceHttpSendRequest")]
    [InlineData("0a2TBNfE3BU", "sceHttpGetStatusCode")]
    [InlineData("aCYPMSUIaP8", "sceHttpGetAllResponseHeaders")]
    [InlineData("yuO2H2Uvnos", "sceHttpGetResponseContentLength")]
    public void RequestExportsRegisterWithCatalogIdentity(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceHttp", export.LibraryName);
    }

    private static int CreateHttpContext(CpuContext context)
    {
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 2;
        context[CpuRegister.Rdx] = 0x4000;
        Assert.Equal(0, HttpExports.HttpInit(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CreateTemplate(CpuContext context, int contextId)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        context[CpuRegister.Rsi] = UserAgentAddress;
        context[CpuRegister.Rdx] = 1;
        context[CpuRegister.Rcx] = 0;
        Assert.Equal(0, HttpExports.HttpCreateTemplate(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static int CreateConnection(CpuContext context, int templateId)
    {
        context[CpuRegister.Rdi] = unchecked((ulong)templateId);
        context[CpuRegister.Rsi] = ServerNameAddress;
        context[CpuRegister.Rdx] = SchemeAddress;
        context[CpuRegister.Rcx] = 443;
        context[CpuRegister.R8] = 1;
        Assert.Equal(0, HttpExports.HttpCreateConnection(context));
        return unchecked((int)context[CpuRegister.Rax]);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }
}
