// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode.Libraries.Ajm;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using SharpEmu.Libs.Tests.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

[Collection(AjmStateCollection.Name)]
public sealed class GnmAjmGaplessLoopTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong ControlReset = 1UL << 13;
    private const ulong ControlInitialize = 1UL << 14;
    private const ulong Stream = AjmExports.AjmJobSidebandFlagStream | AjmExports.AjmJobRunFlagMultipleFrames;
    private const ulong Config = Base + 0x1000;
    private const ulong Window = Base + 0x1010;
    private const ulong Superframe = Base + 0x1100;
    private const ulong Output = Base + 0x2000;
    private const ulong Sideband = Base + 0x400;

    [Fact]
    public void AGaplessLoopInstanceStartsItsWindowAgainOnceTheLastSampleIsOut()
    {
        var (context, contextId, instance) = Create();
        try
        {
            var next = Prepare(context, Base + 0x600, instance);
            next = Run(context, next, instance, Stream, Superframe, 192, 32);
            Start(context, contextId, Base + 0x600, next);
            Assert.Equal(0u, Read32(context, Sideband));
            Assert.Equal(192u, Read32(context, Sideband + 8));
            Assert.Equal(504u, Read32(context, Sideband + 12));

            next = Run(context, Base + 0x700, instance, Stream, Superframe, 192, 32);
            Start(context, contextId, Base + 0x700, next);
            Assert.Equal(0u, Read32(context, Sideband));
            Assert.Equal(192u, Read32(context, Sideband + 8));
            Assert.Equal(504u, Read32(context, Sideband + 12));
        }
        finally
        {
            Destroy(context, contextId, instance);
        }
    }

    [Fact]
    public void ARunWithoutInputReportsNoPartialInput()
    {
        var (context, contextId, instance) = Create();
        try
        {
            var next = Prepare(context, Base + 0x600, instance);
            next = Run(context, next, instance, AjmExports.AjmJobRunFlagGetCodecInfo, 0, 0, 24);
            Start(context, contextId, Base + 0x600, next);
            Assert.Equal(0u, Read32(context, Sideband));
            Assert.Equal(192u, Read32(context, Sideband + 8));

            next = Run(context, Base + 0x700, instance, Stream, 0, 0, 32);
            Start(context, contextId, Base + 0x700, next);
            Assert.Equal(0u, Read32(context, Sideband));
            Assert.Equal(0u, Read32(context, Sideband + 12));
        }
        finally
        {
            Destroy(context, contextId, instance);
        }
    }

    private static (CpuContext Context, uint ContextId, uint Instance) Create()
    {
        AjmExports.ResetForTests();
        var context = new CpuContext(new FakeCpuMemory(Base, 0x10000), Generation.Gen4);
        context[CpuRegister.Rsp] = Base + 0x800;
        context[CpuRegister.Rsi] = Base;
        Assert.Equal(0, AjmExports.AjmInitialize(context));
        var contextId = Read32(context, Base);
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = 1;
        Assert.Equal(0, AjmExports.AjmModuleRegister(context));
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = 0x409;
        context[CpuRegister.Rcx] = Base + 4;
        Assert.Equal(0, GnmAjmExports.InstanceCreate(context));
        Assert.True(context.Memory.TryWrite(Config, new byte[] { 0xFE, 0x70, 0x17, 0xE0, 0, 0, 0, 0 }));
        Assert.True(context.TryWriteUInt32(Window, 252));
        Assert.True(context.TryWriteUInt32(Window + 4, 4));
        var superframe = new byte[192];
        new byte[] { 0x00, 0x84, 0x00, 0x40 }.CopyTo(superframe, 0);
        Assert.True(context.Memory.TryWrite(Superframe, superframe));
        return (context, contextId, Read32(context, Base + 4));
    }

    private static ulong Prepare(CpuContext context, ulong address, uint instance)
    {
        var next = Control(context, address, instance, ControlReset | ControlInitialize, Config, Base + 0x480);
        return Control(context, next, instance, ControlReset | AjmExports.AjmJobSidebandFlagGaplessDecode, Window, Base + 0x488);
    }

    private static void Destroy(CpuContext context, uint contextId, uint instance)
    {
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = instance;
        GnmAjmExports.InstanceDestroy(context);
        AjmExports.ResetForTests();
    }

    private static ulong Control(CpuContext context, ulong address, uint instance, ulong flags, ulong input, ulong output)
    {
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = instance;
        context[CpuRegister.Rdx] = flags;
        context[CpuRegister.Rcx] = input;
        context[CpuRegister.R8] = 8;
        context[CpuRegister.R9] = output;
        Assert.True(context.TryWriteUInt64(Base + 0x808, 8));
        Assert.True(context.TryWriteUInt64(Base + 0x810, 0));
        Assert.Equal(0, GnmAjmExports.BatchJobControlBufferRa(context));
        return context[CpuRegister.Rax];
    }

    private static ulong Run(CpuContext context, ulong address, uint instance, ulong flags, ulong input, ulong inputSize,
        ulong sidebandSize)
    {
        Assert.True(context.Memory.TryWrite(Sideband, new byte[32]));
        context[CpuRegister.Rdi] = address;
        context[CpuRegister.Rsi] = instance;
        context[CpuRegister.Rdx] = flags;
        context[CpuRegister.Rcx] = input;
        context[CpuRegister.R8] = inputSize;
        context[CpuRegister.R9] = inputSize == 0 ? 0 : Output;
        Assert.True(context.TryWriteUInt64(Base + 0x808, inputSize == 0 ? 0 : 1024UL));
        Assert.True(context.TryWriteUInt64(Base + 0x810, Sideband));
        Assert.True(context.TryWriteUInt64(Base + 0x818, sidebandSize));
        Assert.True(context.TryWriteUInt64(Base + 0x820, 0));
        Assert.Equal(0, GnmAjmExports.BatchJobRunBufferRa(context));
        return context[CpuRegister.Rax];
    }

    private static void Start(CpuContext context, uint contextId, ulong address, ulong end)
    {
        context[CpuRegister.Rdi] = contextId;
        context[CpuRegister.Rsi] = address;
        context[CpuRegister.Rdx] = end - address;
        context[CpuRegister.Rcx] = 40;
        context[CpuRegister.R8] = 0;
        context[CpuRegister.R9] = Base + 8;
        Assert.Equal(0, GnmAjmExports.BatchStartBuffer(context));
    }

    private static uint Read32(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt32(address, out var value));
        return value;
    }
}
