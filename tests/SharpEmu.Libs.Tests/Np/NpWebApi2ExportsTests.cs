// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Np;

public sealed class NpWebApi2ExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int InvalidArgument = unchecked((int)0x80553402);

    [Fact]
    public void PushEventRegisterCallback_ValidatesStateAndReturnsCallbackId()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        NpWebApi2Exports.ResetRuntimeState();
        try
        {
            context[CpuRegister.Rdi] = 1;
            context[CpuRegister.Rsi] = 0x1000;
            var libraryContextId = NpWebApi2Exports.NpWebApi2Initialize(context);
            Assert.True(libraryContextId > 0);

            context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
            context[CpuRegister.Rsi] = 0x1000_0000;
            var userContextId = NpWebApi2Exports.NpWebApi2CreateUserContext(context);
            Assert.True(userContextId > 0);

            context[CpuRegister.Rdi] = unchecked((ulong)libraryContextId);
            var filterId = NpWebApi2Exports.NpWebApi2PushEventCreateFilter(context);
            Assert.True(filterId > 0);

            context[CpuRegister.Rdi] = unchecked((ulong)userContextId);
            context[CpuRegister.Rsi] = unchecked((ulong)filterId);
            context[CpuRegister.Rdx] = 0x8000_1000;
            context[CpuRegister.Rcx] = 0x1234;
            Assert.True(NpWebApi2Exports.NpWebApi2PushEventRegisterCallback(context) > 0);

            context[CpuRegister.Rdx] = 0;
            Assert.Equal(
                InvalidArgument,
                NpWebApi2Exports.NpWebApi2PushEventRegisterCallback(context));
        }
        finally
        {
            NpWebApi2Exports.ResetRuntimeState();
        }
    }

    [Fact]
    public void PushEventRegisterCallback_RegistersForGen5()
    {
        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));

        Assert.True(manager.TryGetExport("fY3QqeNkF8k", out var export));
        Assert.Equal("sceNpWebApi2PushEventRegisterCallback", export.Name);
        Assert.Equal("libSceNpWebApi2", export.LibraryName);
    }

    [Fact]
    public void PushEventDeleteHandle_IsIdempotentAndRegisteredForGen5()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 1;
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(0, NpWebApi2Exports.NpWebApi2PushEventDeleteHandle(context));

        var manager = new ModuleManager();
        manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5));
        Assert.True(manager.TryGetExport("fIATVMo4Y1w", out var export));
        Assert.Equal("sceNpWebApi2PushEventDeleteHandle", export.Name);
        Assert.Equal("libSceNpWebApi2", export.LibraryName);
    }
}
