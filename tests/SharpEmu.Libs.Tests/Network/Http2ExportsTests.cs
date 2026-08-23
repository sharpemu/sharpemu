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
}
