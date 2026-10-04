// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

[CollectionDefinition("NpAuthState", DisableParallelization = true)]
public sealed class NpAuthStateCollection
{
    public const string Name = "NpAuthState";
}

[Collection(NpAuthStateCollection.Name)]
public sealed class NpAuthExportsTests : IDisposable
{
    private const int InvalidArgument = unchecked((int)0x80550301);
    private const int InvalidSize = unchecked((int)0x80550302);
    private const int Aborted = unchecked((int)0x80550304);
    private const int RequestMaximum = unchecked((int)0x80550305);
    private const int RequestNotFound = unchecked((int)0x80550306);
    private const int InvalidId = unchecked((int)0x80550307);
    private const int SignedOut = unchecked((int)0x80550006);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong CreateParameter = MemoryBase + 0x100;
    private const ulong AuthorizationParameter = MemoryBase + 0x200;
    private const ulong AuthorizationCode = MemoryBase + 0x400;
    private const ulong IssuerId = MemoryBase + 0x500;
    private const ulong AsyncResult = MemoryBase + 0x600;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x4000);
    private readonly CpuContext _context;

    public NpAuthExportsTests()
    {
        NpAuthExports.ResetRuntimeState();
        _context = new CpuContext(_memory, Generation.Gen5);
    }

    public void Dispose()
    {
        NpAuthExports.ResetRuntimeState();
    }

    [Fact]
    public void CreateAsyncRequest_ValidatesParameterAndRegistersExport()
    {
        _context[CpuRegister.Rdi] = 0;
        Assert.Equal(InvalidArgument, NpAuthExports.NpAuthCreateAsyncRequest(_context));

        WriteCreateParameter(23);
        _context[CpuRegister.Rdi] = CreateParameter;
        Assert.Equal(InvalidSize, NpAuthExports.NpAuthCreateAsyncRequest(_context));

        WriteCreateParameter(24);
        Assert.Equal(0x10000001, NpAuthExports.NpAuthCreateAsyncRequest(_context));

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("N+mr7GjTvr8", out var export));
        Assert.Equal("sceNpAuthCreateAsyncRequest", export.Name);
        Assert.Equal("libSceNpAuth", export.LibraryName);
    }

    [Fact]
    public void RequestLimit_DeleteAndReusePreserveHandleSemantics()
    {
        for (var index = 0; index < 16; index++)
        {
            Assert.Equal(0x10000001 + index, NpAuthExports.NpAuthCreateRequest(_context));
        }

        Assert.Equal(RequestMaximum, NpAuthExports.NpAuthCreateRequest(_context));

        _context[CpuRegister.Rdi] = 0x10000005;
        Assert.Equal(0, NpAuthExports.NpAuthDeleteRequest(_context));
        Assert.Equal(0x10000005, NpAuthExports.NpAuthCreateRequest(_context));
    }

    [Fact]
    public void AsyncOfflineCompletion_IsReportedByPoll()
    {
        WriteCreateParameter(24);
        _context[CpuRegister.Rdi] = CreateParameter;
        var requestId = NpAuthExports.NpAuthCreateAsyncRequest(_context);

        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        _context[CpuRegister.Rsi] = AsyncResult;
        Assert.Equal(InvalidId, NpAuthExports.NpAuthPollAsync(_context));

        WriteAuthorizationParameter();
        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        _context[CpuRegister.Rsi] = AuthorizationParameter;
        _context[CpuRegister.Rdx] = AuthorizationCode;
        _context[CpuRegister.Rcx] = IssuerId;
        Assert.Equal(0, NpAuthExports.NpAuthGetAuthorizationCodeV3(_context));

        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        _context[CpuRegister.Rsi] = AsyncResult;
        Assert.Equal(0, NpAuthExports.NpAuthPollAsync(_context));
        Assert.True(_context.TryReadInt32(AsyncResult, out var result));
        Assert.Equal(SignedOut, result);

        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        Assert.Equal(0, NpAuthExports.NpAuthDeleteRequest(_context));
        Assert.Equal(RequestNotFound, NpAuthExports.NpAuthDeleteRequest(_context));
    }

    [Fact]
    public void AbortRequest_CompletesAsyncWaitWithAbortedResult()
    {
        WriteCreateParameter(24);
        _context[CpuRegister.Rdi] = CreateParameter;
        var requestId = NpAuthExports.NpAuthCreateAsyncRequest(_context);

        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        Assert.Equal(0, NpAuthExports.NpAuthAbortRequest(_context));

        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        _context[CpuRegister.Rsi] = AsyncResult;
        Assert.Equal(0, NpAuthExports.NpAuthWaitAsync(_context));
        Assert.True(_context.TryReadInt32(AsyncResult, out var result));
        Assert.Equal(Aborted, result);
    }

    [Fact]
    public void SyncOfflineOperation_ClearsCredentialsAndReturnsSignedOut()
    {
        var requestId = NpAuthExports.NpAuthCreateRequest(_context);
        WriteAuthorizationParameter();
        Assert.True(_memory.TryWrite(AuthorizationCode, Enumerable.Repeat((byte)0xEE, 136).ToArray()));
        Assert.True(_context.TryWriteInt32(IssuerId, -1));

        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        _context[CpuRegister.Rsi] = AuthorizationParameter;
        _context[CpuRegister.Rdx] = AuthorizationCode;
        _context[CpuRegister.Rcx] = IssuerId;

        Assert.Equal(SignedOut, NpAuthExports.NpAuthGetAuthorizationCodeV3(_context));
        Span<byte> authorizationCode = stackalloc byte[136];
        Assert.True(_memory.TryRead(AuthorizationCode, authorizationCode));
        Assert.True(authorizationCode.SequenceEqual(new byte[136]));
        Assert.True(_context.TryReadInt32(IssuerId, out var issuerId));
        Assert.Equal(0, issuerId);
    }

    [Fact]
    public void SetTimeout_PreservesRequestAndRejectsUnknownHandle()
    {
        var requestId = NpAuthExports.NpAuthCreateRequest(_context);
        _context[CpuRegister.Rdi] = unchecked((uint)requestId);
        _context[CpuRegister.Rsi] = 2;
        _context[CpuRegister.Rdx] = 100;
        _context[CpuRegister.Rcx] = 200;
        _context[CpuRegister.R8] = 300;
        _context[CpuRegister.R9] = 400;
        Assert.Equal(0, NpAuthExports.NpAuthSetTimeout(_context));

        _context[CpuRegister.Rdi] = 0x10000020;
        Assert.Equal(RequestNotFound, NpAuthExports.NpAuthSetTimeout(_context));
    }

    private void WriteCreateParameter(ulong size)
    {
        Assert.True(_context.TryWriteUInt64(CreateParameter, size));
        Assert.True(_context.TryWriteUInt64(CreateParameter + 8, 0xFFFF));
        Assert.True(_context.TryWriteInt32(CreateParameter + 16, 256));
    }

    private void WriteAuthorizationParameter()
    {
        Assert.True(_context.TryWriteUInt64(AuthorizationParameter, 32));
        Assert.True(_context.TryWriteInt32(AuthorizationParameter + 8, 1));
        Assert.True(_context.TryWriteUInt64(AuthorizationParameter + 16, MemoryBase + 0x1000));
        Assert.True(_context.TryWriteUInt64(AuthorizationParameter + 24, MemoryBase + 0x1100));
    }
}
