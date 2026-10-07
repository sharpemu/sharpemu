// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Network;
using Xunit;

namespace SharpEmu.Libs.Tests.Network;

public sealed class Http2ExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;

    [Fact]
    public void RequestLifecycle_RemainsLocalAndBlocksTransmission()
    {
        Http2Exports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 0x10000;
        ctx[CpuRegister.Rcx] = 4;
        Assert.Equal(0, Http2Exports.Http2Init(ctx));
        var contextId = unchecked((int)ctx[CpuRegister.Rax]);

        var userAgentAddress = memory.WriteCString(MemoryBase + 0x100, "SharpEmu test");
        ctx[CpuRegister.Rdi] = unchecked((uint)contextId);
        ctx[CpuRegister.Rsi] = userAgentAddress;
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(contextId + 0x1000, Http2Exports.Http2CreateTemplate(ctx));
        var templateId = unchecked((int)ctx[CpuRegister.Rax]);

        var methodAddress = memory.WriteCString(MemoryBase + 0x200, "GET");
        var urlAddress = memory.WriteCString(MemoryBase + 0x300, "https://example.invalid/");
        ctx[CpuRegister.Rdi] = unchecked((uint)templateId);
        ctx[CpuRegister.Rsi] = methodAddress;
        ctx[CpuRegister.Rdx] = urlAddress;
        ctx[CpuRegister.Rcx] = 0;
        Assert.Equal(templateId + 0x1000, Http2Exports.Http2CreateRequestWithUrl(ctx));
        var requestId = unchecked((int)ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = 0;
        ctx[CpuRegister.Rdx] = 0;
        Assert.Equal(unchecked((int)0x817B5224), Http2Exports.Http2SendRequest(ctx));

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        Assert.Equal(0, Http2Exports.Http2DeleteRequest(ctx));
        Assert.Equal(unchecked((int)0x817B1100), Http2Exports.Http2DeleteRequest(ctx));
    }

    [Fact]
    public void CreateTemplate_RejectsInvalidVersion()
    {
        Http2Exports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 0x1000;
        ctx[CpuRegister.Rcx] = 1;
        _ = Http2Exports.Http2Init(ctx);
        var contextId = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = memory.WriteCString(MemoryBase + 0x100, "test");
        ctx[CpuRegister.Rdx] = 4;
        ctx[CpuRegister.Rcx] = 0;

        Assert.Equal(unchecked((int)0x817B106A), Http2Exports.Http2CreateTemplate(ctx));
    }

    [Fact]
    public void SetMinSslVersion_AcceptsLiveIdsAndRejectsDeletedIds()
    {
        Http2Exports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 0x1000;
        ctx[CpuRegister.Rcx] = 2;
        Assert.Equal(0, Http2Exports.Http2Init(ctx));
        var contextId = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = contextId;
        ctx[CpuRegister.Rsi] = memory.WriteCString(MemoryBase + 0x100, "test");
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 0;
        Assert.True(Http2Exports.Http2CreateTemplate(ctx) > 0);
        var templateId = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = templateId;
        ctx[CpuRegister.Rsi] = 4;
        Assert.Equal(0, Http2Exports.Http2SetMinSslVersion(ctx));

        ctx[CpuRegister.Rdi] = templateId;
        ctx[CpuRegister.Rsi] = memory.WriteCString(MemoryBase + 0x200, "GET");
        ctx[CpuRegister.Rdx] = memory.WriteCString(MemoryBase + 0x300, "https://example.invalid/");
        ctx[CpuRegister.Rcx] = 0;
        Assert.True(Http2Exports.Http2CreateRequestWithUrl(ctx) > 0);
        var requestId = ctx[CpuRegister.Rax];

        ctx[CpuRegister.Rdi] = requestId;
        ctx[CpuRegister.Rsi] = 4;
        Assert.Equal(0, Http2Exports.Http2SetMinSslVersion(ctx));

        ctx[CpuRegister.Rdi] = templateId;
        Assert.Equal(0, Http2Exports.Http2DeleteTemplate(ctx));
        Assert.Equal(unchecked((int)0x817B1100), Http2Exports.Http2SetMinSslVersion(ctx));

        ctx[CpuRegister.Rdi] = requestId;
        Assert.Equal(unchecked((int)0x817B1100), Http2Exports.Http2SetMinSslVersion(ctx));
    }

    [Fact]
    public void SetMinSslVersion_RegistersPs5Nid()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("09tk+kIA1Ns", out var export));
        Assert.Equal("sceHttp2SetMinSslVersion", export.Name);
        Assert.Equal("libSceHttp2", export.LibraryName);
    }

    [Fact]
    public void Options_AreStoredInheritedAndIndependentlyUpdated()
    {
        var (memory, ctx, templateId) = CreateTemplate();

        ctx[CpuRegister.Rdi] = unchecked((uint)templateId);
        ctx[CpuRegister.Rsi] = 0x0F;
        Assert.Equal(0, Http2Exports.Http2SslEnableOption(ctx));

        ctx[CpuRegister.Rsi] = 0x05;
        Assert.Equal(0, Http2Exports.Http2SslDisableOption(ctx));

        ctx[CpuRegister.Rsi] = 0x1234_5678;
        ctx[CpuRegister.Rdx] = 0x8765_4321;
        Assert.Equal(0, Http2Exports.Http2SetRedirectCallback(ctx));

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, Http2Exports.Http2SetAutoRedirect(ctx));

        ctx[CpuRegister.Rsi] = 101;
        Assert.Equal(0, Http2Exports.Http2SetConnectTimeOut(ctx));
        ctx[CpuRegister.Rsi] = 202;
        Assert.Equal(0, Http2Exports.Http2SetRecvTimeOut(ctx));
        ctx[CpuRegister.Rsi] = 303;
        Assert.Equal(0, Http2Exports.Http2SetSendTimeOut(ctx));

        ctx[CpuRegister.Rsi] = 7;
        Assert.Equal(0, Http2Exports.Http2SetAuthEnabled(ctx));

        ctx[CpuRegister.Rsi] = 0x1111_2222;
        ctx[CpuRegister.Rdx] = 0x3333_4444;
        Assert.Equal(0, Http2Exports.Http2SetSslCallback(ctx));

        Assert.True(Http2Exports.TryGetOptionsForTests(templateId, out var templateOptions));
        Assert.Equal(0x0Au, templateOptions.SslOptions);
        Assert.Equal(0x1234_5678UL, templateOptions.RedirectCallback);
        Assert.Equal(0x8765_4321UL, templateOptions.RedirectUserArgument);
        Assert.False(templateOptions.AutoRedirect);
        Assert.Equal(101u, templateOptions.ConnectTimeoutMicroseconds);
        Assert.Equal(202u, templateOptions.ReceiveTimeoutMicroseconds);
        Assert.Equal(303u, templateOptions.SendTimeoutMicroseconds);
        Assert.True(templateOptions.AuthEnabled);
        Assert.Equal(0x1111_2222UL, templateOptions.SslCallback);
        Assert.Equal(0x3333_4444UL, templateOptions.SslUserArgument);

        var requestId = CreateRequest(memory, ctx, templateId);
        Assert.True(Http2Exports.TryGetOptionsForTests(requestId, out var inheritedOptions));
        Assert.Equal(templateOptions, inheritedOptions);

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = 404;
        Assert.Equal(0, Http2Exports.Http2SetRecvTimeOut(ctx));
        Assert.True(Http2Exports.TryGetOptionsForTests(requestId, out var requestOptions));
        Assert.Equal(404u, requestOptions.ReceiveTimeoutMicroseconds);
        Assert.True(Http2Exports.TryGetOptionsForTests(templateId, out var unchangedTemplateOptions));
        Assert.Equal(202u, unchangedTemplateOptions.ReceiveTimeoutMicroseconds);
    }

    [Fact]
    public void OptionSetters_RejectUnknownHandles()
    {
        Http2Exports.ResetRuntimeState();
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0x7FFF_FFFF;
        ctx[CpuRegister.Rsi] = 1;
        ctx[CpuRegister.Rdx] = 2;

        Func<CpuContext, int>[] setters =
        [
            Http2Exports.Http2SslDisableOption,
            Http2Exports.Http2SslEnableOption,
            Http2Exports.Http2SetRedirectCallback,
            Http2Exports.Http2SetAutoRedirect,
            Http2Exports.Http2SetCookieBox,
            Http2Exports.Http2SetConnectTimeOut,
            Http2Exports.Http2SetRecvTimeOut,
            Http2Exports.Http2SetSendTimeOut,
            Http2Exports.Http2SetAuthEnabled,
            Http2Exports.Http2SetSslCallback,
        ];

        foreach (var setter in setters)
        {
            Assert.Equal(unchecked((int)0x817B1100), setter(ctx));
        }
    }

    [Fact]
    public void AddRequestHeader_ValidatesPointersAndTracksGuestStrings()
    {
        var (memory, ctx, templateId) = CreateTemplate();
        var requestId = CreateRequest(memory, ctx, templateId);
        var nameAddress = memory.WriteCString(MemoryBase + 0x500, "X-SharpEmu-Test");
        var valueAddress = memory.WriteCString(MemoryBase + 0x600, "enabled");

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = nameAddress;
        ctx[CpuRegister.Rdx] = valueAddress;
        ctx[CpuRegister.Rcx] = 1;
        Assert.Equal(0, Http2Exports.Http2AddRequestHeader(ctx));

        var header = Assert.Single(Http2Exports.GetRequestHeadersForTests(requestId));
        Assert.Equal("X-SharpEmu-Test", header.Name);
        Assert.Equal("enabled", header.Value);
        Assert.Equal(1u, header.Mode);

        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(unchecked((int)0x817B1225), Http2Exports.Http2AddRequestHeader(ctx));

        ctx[CpuRegister.Rsi] = MemoryBase + 0x3000;
        Assert.Equal(unchecked((int)0x817B11FE), Http2Exports.Http2AddRequestHeader(ctx));

        ctx[CpuRegister.Rdi] = 0x7FFF_FFFF;
        ctx[CpuRegister.Rsi] = nameAddress;
        Assert.Equal(unchecked((int)0x817B1100), Http2Exports.Http2AddRequestHeader(ctx));
    }

    [Fact]
    public void CookieBox_IsCreatedBoundInheritedAndCanBeDetached()
    {
        var (memory, ctx, templateId) = CreateTemplate();

        ctx[CpuRegister.Rdi] = 0x80000; // Deliberately stale sceHttp2Init register state.
        Assert.Equal(0x3001, Http2Exports.Http2CreateCookieBox(ctx));
        var cookieBoxId = unchecked((int)ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = unchecked((uint)templateId);
        ctx[CpuRegister.Rsi] = unchecked((uint)cookieBoxId);
        Assert.Equal(0, Http2Exports.Http2SetCookieBox(ctx));
        Assert.True(Http2Exports.TryGetOptionsForTests(templateId, out var templateOptions));
        Assert.Equal(cookieBoxId, templateOptions.CookieBoxId);

        var requestId = CreateRequest(memory, ctx, templateId);
        Assert.True(Http2Exports.TryGetOptionsForTests(requestId, out var inheritedOptions));
        Assert.Equal(cookieBoxId, inheritedOptions.CookieBoxId);

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = 0;
        Assert.Equal(0, Http2Exports.Http2SetCookieBox(ctx));
        Assert.True(Http2Exports.TryGetOptionsForTests(requestId, out var detachedOptions));
        Assert.Equal(0, detachedOptions.CookieBoxId);

        ctx[CpuRegister.Rsi] = 0x7FFF_FFFF;
        Assert.Equal(unchecked((int)0x817B1100), Http2Exports.Http2SetCookieBox(ctx));
    }

    [Fact]
    public void SetRequestContentLength_UpdatesOnlyLiveRequests()
    {
        var (memory, ctx, templateId) = CreateTemplate();
        var requestId = CreateRequest(memory, ctx, templateId);

        ctx[CpuRegister.Rdi] = unchecked((uint)requestId);
        ctx[CpuRegister.Rsi] = 0x1_0000_0042;
        Assert.Equal(0, Http2Exports.Http2SetRequestContentLength(ctx));
        Assert.True(Http2Exports.TryGetRequestContentLengthForTests(requestId, out var contentLength));
        Assert.Equal(0x1_0000_0042UL, contentLength);

        ctx[CpuRegister.Rdi] = 0x7FFF_FFFF;
        Assert.Equal(unchecked((int)0x817B1100), Http2Exports.Http2SetRequestContentLength(ctx));
    }

    [Theory]
    [InlineData("B37SruheQ5Y", "sceHttp2SslDisableOption")]
    [InlineData("EWcwMpbr5F8", "sceHttp2SslEnableOption")]
    [InlineData("BJgi0CH7al4", "sceHttp2SetRedirectCallback")]
    [InlineData("b9AvoIaOuHI", "sceHttp2SetAutoRedirect")]
    [InlineData("N4UfjvWJsMw", "sceHttp2CreateCookieBox")]
    [InlineData("jrVHsKCXA0g", "sceHttp2SetCookieBox")]
    [InlineData("FSAFOzi0FpM", "sceHttp2SetRequestContentLength")]
    [InlineData("-HIO4VT87v8", "sceHttp2SetConnectTimeOut")]
    [InlineData("izvHhqgDt44", "sceHttp2SetRecvTimeOut")]
    [InlineData("XPtW45xiLHk", "sceHttp2SetSendTimeOut")]
    [InlineData("jjFahkBPCYs", "sceHttp2SetAuthEnabled")]
    [InlineData("nrPfOE8TQu0", "sceHttp2AddRequestHeader")]
    [InlineData("YrWX+DhPHQY", "sceHttp2SetSslCallback")]
    public void CompatibilityExports_RegisterPs5Nids(string nid, string name)
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport(nid, out var export));
        Assert.Equal(name, export.Name);
        Assert.Equal("libSceHttp2", export.LibraryName);
    }

    private static (FakeCpuMemory Memory, CpuContext Context, int TemplateId) CreateTemplate()
    {
        Http2Exports.ResetRuntimeState();
        var memory = new FakeCpuMemory(MemoryBase, 0x2000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        ctx[CpuRegister.Rdi] = 1;
        ctx[CpuRegister.Rsi] = 2;
        ctx[CpuRegister.Rdx] = 0x1000;
        ctx[CpuRegister.Rcx] = 2;
        Assert.Equal(0, Http2Exports.Http2Init(ctx));
        var contextId = unchecked((int)ctx[CpuRegister.Rax]);

        ctx[CpuRegister.Rdi] = unchecked((uint)contextId);
        ctx[CpuRegister.Rsi] = memory.WriteCString(MemoryBase + 0x100, "test");
        ctx[CpuRegister.Rdx] = 3;
        ctx[CpuRegister.Rcx] = 0;
        Assert.True(Http2Exports.Http2CreateTemplate(ctx) > 0);
        return (memory, ctx, unchecked((int)ctx[CpuRegister.Rax]));
    }

    private static int CreateRequest(FakeCpuMemory memory, CpuContext ctx, int templateId)
    {
        ctx[CpuRegister.Rdi] = unchecked((uint)templateId);
        ctx[CpuRegister.Rsi] = memory.WriteCString(MemoryBase + 0x200, "GET");
        ctx[CpuRegister.Rdx] = memory.WriteCString(MemoryBase + 0x300, "https://example.invalid/");
        ctx[CpuRegister.Rcx] = 0;
        Assert.True(Http2Exports.Http2CreateRequestWithUrl(ctx) > 0);
        return unchecked((int)ctx[CpuRegister.Rax]);
    }
}
