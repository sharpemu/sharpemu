// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

[CollectionDefinition("Http2Exports", DisableParallelization = true)]
public sealed class Http2ExportsCollectionDefinition;

[Collection("Http2Exports")]
public sealed class Http2ExportsTests : IDisposable
{
    private const int Http2ErrorInvalidId = unchecked((int)0x80436004);
    private const int Http2ErrorInvalidArgument = unchecked((int)0x80436016);
    private const ulong MemoryBase = 0x0000_7FFF_5000_0000;
    private const ulong MethodAddress = MemoryBase + 0x1000;
    private const ulong UrlAddress = MemoryBase + 0x2000;
    private const ulong AlternateUrlAddress = MemoryBase + 0x4000;
    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x10_000);
    private readonly CpuContext _context;

    public Http2ExportsTests()
    {
        Http2Exports.ResetForTests();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void CreateRequest_StoresBoundedStringsAndContentLengthWithUniquePositiveIds()
    {
        var contextId = CreateContext();
        var templateId = CreateTemplate(contextId);
        _memory.WriteCString(MethodAddress, "POST");
        _memory.WriteCString(UrlAddress, "https://example.invalid/first");
        _memory.WriteCString(AlternateUrlAddress, "https://example.invalid/second");

        var firstRequestId = CreateRequest(
            templateId,
            MethodAddress,
            UrlAddress,
            0xFEDC_BA98_7654_3210UL);
        var secondRequestId = CreateRequest(
            templateId,
            MethodAddress,
            AlternateUrlAddress,
            17);

        Assert.True(firstRequestId > 0);
        Assert.True(secondRequestId > 0);
        Assert.NotEqual(firstRequestId, secondRequestId);
        Assert.True(Http2Exports.TryGetRequestState(firstRequestId, out var first));
        Assert.Equal(templateId, first.TemplateId);
        Assert.Equal("POST", first.Method);
        Assert.Equal("https://example.invalid/first", first.Url);
        Assert.Equal(0xFEDC_BA98_7654_3210UL, first.ContentLength);
        Assert.True(Http2Exports.TryGetRequestState(secondRequestId, out var second));
        Assert.Equal(17UL, second.ContentLength);
    }

    [Fact]
    public void CreateRequest_RejectsUnknownTemplate()
    {
        _memory.WriteCString(MethodAddress, "GET");
        _memory.WriteCString(UrlAddress, "https://example.invalid/");
        SetCreateRequestArguments(int.MaxValue, MethodAddress, UrlAddress, 0);

        var result = Http2Exports.Http2CreateRequestWithUrl(_context);

        Assert.Equal(Http2ErrorInvalidId, result);
        Assert.Equal(unchecked((ulong)Http2ErrorInvalidId), _context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CreateRequest_RejectsNullStringPointers(bool nullMethod, bool nullUrl)
    {
        var templateId = CreateTemplate(CreateContext());
        _memory.WriteCString(MethodAddress, "GET");
        _memory.WriteCString(UrlAddress, "https://example.invalid/");
        SetCreateRequestArguments(
            templateId,
            nullMethod ? 0 : MethodAddress,
            nullUrl ? 0 : UrlAddress,
            0);

        var result = Http2Exports.Http2CreateRequestWithUrl(_context);

        Assert.Equal(Http2ErrorInvalidArgument, result);
        Assert.Equal(unchecked((ulong)Http2ErrorInvalidArgument), _context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateRequest_RejectsUnreadableStrings(bool unreadableMethod)
    {
        var templateId = CreateTemplate(CreateContext());
        _memory.WriteCString(MethodAddress, "GET");
        _memory.WriteCString(UrlAddress, "https://example.invalid/");
        var unreadableAddress = MemoryBase + 0x20_000;
        SetCreateRequestArguments(
            templateId,
            unreadableMethod ? unreadableAddress : MethodAddress,
            unreadableMethod ? UrlAddress : unreadableAddress,
            0);

        Assert.Equal(
            Http2ErrorInvalidArgument,
            Http2Exports.Http2CreateRequestWithUrl(_context));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateRequest_RejectsStringsWithoutTerminatorWithinBound(bool unterminatedMethod)
    {
        var templateId = CreateTemplate(CreateContext());
        _memory.WriteCString(MethodAddress, "GET");
        _memory.WriteCString(UrlAddress, "https://example.invalid/");
        var stringAddress = unterminatedMethod ? MethodAddress : UrlAddress;
        var capacity = unterminatedMethod ? 64 : 4096;
        Assert.True(_memory.TryWrite(stringAddress, Enumerable.Repeat((byte)'X', capacity).ToArray()));
        SetCreateRequestArguments(templateId, MethodAddress, UrlAddress, 0);

        Assert.Equal(
            Http2ErrorInvalidArgument,
            Http2Exports.Http2CreateRequestWithUrl(_context));
    }

    [Fact]
    public void Term_RemovesChildTemplatesAndRequests()
    {
        var contextId = CreateContext();
        var templateId = CreateTemplate(contextId);
        _memory.WriteCString(MethodAddress, "GET");
        _memory.WriteCString(UrlAddress, "https://example.invalid/");
        var requestId = CreateRequest(templateId, MethodAddress, UrlAddress, 0);
        Assert.True(Http2Exports.TryGetRequestState(requestId, out _));

        _context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        Assert.Equal(0, Http2Exports.Http2Term(_context));
        Assert.False(Http2Exports.TryGetRequestState(requestId, out _));

        SetCreateRequestArguments(templateId, MethodAddress, UrlAddress, 0);
        Assert.Equal(
            Http2ErrorInvalidId,
            Http2Exports.Http2CreateRequestWithUrl(_context));
    }

    [Fact]
    public void CreateRequestNid_RegistersAsHttp2Export()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(
            SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("mmyOCxQMVYQ", out var export));
        Assert.Equal("sceHttp2CreateRequestWithURL", export.Name);
        Assert.Equal("libSceHttp2", export.LibraryName);
    }

    public void Dispose() => Http2Exports.ResetForTests();

    private int CreateContext()
    {
        _context[CpuRegister.Rdi] = 1;
        _context[CpuRegister.Rsi] = 2;
        _context[CpuRegister.Rdx] = 0x20_000;
        _context[CpuRegister.Rcx] = 8;
        Assert.Equal(0, Http2Exports.Http2Init(_context));
        return checked((int)_context[CpuRegister.Rax]);
    }

    private int CreateTemplate(int contextId)
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)contextId);
        _context[CpuRegister.Rsi] = 0;
        _context[CpuRegister.Rdx] = 2;
        _context[CpuRegister.Rcx] = 0;
        Assert.Equal(0, Http2Exports.Http2CreateTemplate(_context));
        return checked((int)_context[CpuRegister.Rax]);
    }

    private int CreateRequest(
        int templateId,
        ulong methodAddress,
        ulong urlAddress,
        ulong contentLength)
    {
        SetCreateRequestArguments(templateId, methodAddress, urlAddress, contentLength);
        Assert.Equal(0, Http2Exports.Http2CreateRequestWithUrl(_context));
        return checked((int)_context[CpuRegister.Rax]);
    }

    private void SetCreateRequestArguments(
        int templateId,
        ulong methodAddress,
        ulong urlAddress,
        ulong contentLength)
    {
        _context[CpuRegister.Rdi] = unchecked((ulong)templateId);
        _context[CpuRegister.Rsi] = methodAddress;
        _context[CpuRegister.Rdx] = urlAddress;
        _context[CpuRegister.Rcx] = contentLength;
    }
}
