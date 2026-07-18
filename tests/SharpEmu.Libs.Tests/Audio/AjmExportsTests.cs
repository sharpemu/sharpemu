// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AjmState", DisableParallelization = true)]
public sealed class AjmStateCollection
{
    public const string Name = "AjmState";
}

[Collection(AjmStateCollection.Name)]
public sealed class AjmExportsTests : IDisposable
{
    private const int InvalidContext = unchecked((int)0x80930002);
    private const int InvalidInstance = unchecked((int)0x80930003);
    private const int InvalidParameter = unchecked((int)0x80930005);
    private const int CodecAlreadyRegistered = unchecked((int)0x80930009);
    private const int CodecNotRegistered = unchecked((int)0x8093000A);
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong ContextAddress = MemoryBase + 0x100;
    private const ulong InstanceAddress = MemoryBase + 0x200;

    private readonly FakeCpuMemory _memory = new(MemoryBase, 0x1000);
    private readonly CpuContext _ctx;

    public AjmExportsTests()
    {
        AjmExports.ResetForTests();
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void InstanceLifecycle_RegisteredCodecCreatesAndDestroysInstance()
    {
        var contextId = Initialize();

        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        Assert.Equal(0x4001u, ReadUInt32(InstanceAddress));

        Assert.Equal(0, DestroyInstance(contextId, 0x4001));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 0x4001));
    }

    [Fact]
    public void InstanceCreate_UnregisteredCodecDoesNotWriteOutput()
    {
        var contextId = Initialize();
        WriteUInt32(InstanceAddress, 0xCCCCCCCC);

        Assert.Equal(CodecNotRegistered, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        Assert.Equal(0xCCCCCCCCu, ReadUInt32(InstanceAddress));
    }

    [Fact]
    public void InstanceCreate_FaultingOutputDoesNotAdvanceInstanceId()
    {
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));

        Assert.Equal(InvalidParameter, CreateInstance(contextId, 1, 0x401, MemoryBase + 0x1000));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        Assert.Equal(0x4001u, ReadUInt32(InstanceAddress));
        Assert.Equal(0, DestroyInstance(contextId, 0x4001));
    }

    [Fact]
    public void ModuleRegister_RejectsDuplicateAndUnknownContext()
    {
        var contextId = Initialize();

        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(CodecAlreadyRegistered, RegisterCodec(contextId, 1));
        Assert.Equal(InvalidContext, RegisterCodec(contextId + 1, 1));
    }

    [Fact]
    public void InstanceDestroy_RejectsUnknownContextAndSlot()
    {
        var contextId = Initialize();

        Assert.Equal(InvalidContext, DestroyInstance(contextId + 1, 1));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 0));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 1));
    }

    [Fact]
    public void InstanceDestroy_ResolvesInstanceByMaskedSlot()
    {
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));

        Assert.Equal(0, DestroyInstance(contextId, 0x8001));
        Assert.Equal(InvalidInstance, DestroyInstance(contextId, 0x4001));
    }

    [Fact]
    public void ConcurrentInstanceCreates_ProduceUniqueLiveIds()
    {
        const int count = 32;
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));

        var results = Enumerable.Range(0, count)
            .AsParallel()
            .Select(index =>
            {
                var outputAddress = MemoryBase + 0x300 + unchecked((ulong)(index * sizeof(uint)));
                var context = new CpuContext(_memory, Generation.Gen5)
                {
                    [CpuRegister.Rdi] = contextId,
                    [CpuRegister.Rsi] = 1,
                    [CpuRegister.Rdx] = 0x401,
                    [CpuRegister.Rcx] = outputAddress,
                };
                var result = AjmExports.AjmInstanceCreate(context);
                return (result, instanceId: ReadUInt32(outputAddress));
            })
            .ToArray();

        Assert.All(results, result => Assert.Equal(0, result.result));
        Assert.Equal(count, results.Select(result => result.instanceId).Distinct().Count());
        Assert.All(results, result => Assert.Equal(0, DestroyInstance(contextId, result.instanceId)));
    }

    [Fact]
    public void InstanceLifecycleExports_RegisterForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("AxoDrINp4J8", out var create));
            Assert.Equal("sceAjmInstanceCreate", create.Name);
            Assert.True(manager.TryGetExport("RbLbuKv8zho", out var destroy));
            Assert.Equal("sceAjmInstanceDestroy", destroy.Name);
        }
    }

    public void Dispose()
    {
        AjmExports.ResetForTests();
    }

    private uint Initialize()
    {
        _ctx[CpuRegister.Rdi] = 0;
        _ctx[CpuRegister.Rsi] = ContextAddress;
        Assert.Equal(0, AjmExports.AjmInitialize(_ctx));
        return ReadUInt32(ContextAddress);
    }

    private int RegisterCodec(uint contextId, uint codecType)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        _ctx[CpuRegister.Rdx] = 0;
        return AjmExports.AjmModuleRegister(_ctx);
    }

    private int CreateInstance(uint contextId, uint codecType, ulong flags, ulong outputAddress)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        _ctx[CpuRegister.Rdx] = flags;
        _ctx[CpuRegister.Rcx] = outputAddress;
        return AjmExports.AjmInstanceCreate(_ctx);
    }

    private int DestroyInstance(uint contextId, uint instanceId)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = instanceId;
        return AjmExports.AjmInstanceDestroy(_ctx);
    }

    private uint ReadUInt32(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(uint)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt32LittleEndian(value);
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
