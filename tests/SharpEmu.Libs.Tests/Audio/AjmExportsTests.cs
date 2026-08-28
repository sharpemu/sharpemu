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
    private const ulong BatchInfoAddress = MemoryBase + 0x300;
    private const ulong StatisticsAddress = MemoryBase + 0x400;
    private const ulong BatchBufferAddress = MemoryBase + 0x500;

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
    public void ModuleUnregister_RemovesRegisteredCodecAndRejectsUnknownContext()
    {
        var contextId = Initialize();

        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(0, UnregisterCodec(contextId, 1));
        // The codec is actually gone, not just a no-op stub: it's unusable
        // for a new instance, and re-registering no longer hits
        // CodecAlreadyRegistered.
        Assert.Equal(CodecNotRegistered, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        Assert.Equal(0, RegisterCodec(contextId, 1));

        Assert.Equal(InvalidContext, UnregisterCodec(contextId + 1, 1));
    }

    [Fact]
    public void ModuleUnregister_UnknownCodecIsToleratedAsANoOp()
    {
        var contextId = Initialize();

        Assert.Equal(0, UnregisterCodec(contextId, 1));
    }

    [Fact]
    public void MemoryRegistration_TracksValidContextAndToleratesRepeatedUnregister()
    {
        var contextId = Initialize();
        const ulong address = 0x4_D4E0_0000;

        Assert.Equal(0, RegisterMemory(contextId, address, 4));
        Assert.Equal(0, UnregisterMemory(contextId, address));
        Assert.Equal(0, UnregisterMemory(contextId, address));
        Assert.Equal(InvalidContext, RegisterMemory(contextId + 1, address, 4));
        Assert.Equal(InvalidContext, UnregisterMemory(contextId + 1, address));
        Assert.Equal(InvalidParameter, RegisterMemory(contextId, 0, 4));
        Assert.Equal(InvalidParameter, RegisterMemory(contextId, address, 0));
        Assert.Equal(InvalidParameter, UnregisterMemory(contextId, 0));
    }

    [Fact]
    public void BatchInitializeAndStatistics_WriteExpectedAbiStructures()
    {
        Span<byte> sentinel = stackalloc byte[48];
        sentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(StatisticsAddress, sentinel));

        _ctx[CpuRegister.Rdi] = BatchBufferAddress;
        _ctx[CpuRegister.Rsi] = 0x200;
        _ctx[CpuRegister.Rdx] = BatchInfoAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));

        Assert.Equal(BatchBufferAddress, ReadUInt64(BatchInfoAddress));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 8));
        Assert.Equal(0x200ul, ReadUInt64(BatchInfoAddress + 16));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 24));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 32));

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = StatisticsAddress;
        _ctx.SetXmmRegister(0, BitConverter.SingleToUInt32Bits(0.25f), 0);
        Assert.Equal(0, AjmExports.AjmBatchJobGetStatistics(_ctx));

        Assert.Equal(88ul, ReadUInt64(BatchInfoAddress + 8));
        Assert.Equal(BatchBufferAddress, ReadUInt64(BatchInfoAddress + 24));
        Assert.Equal(0ul, ReadUInt64(BatchInfoAddress + 32));
        Assert.All(ReadBytes(StatisticsAddress, 48), value => Assert.Equal(0, value));
        Assert.All(ReadBytes(BatchBufferAddress, 88), value => Assert.Equal(0, value));
    }

    /// <summary>
    /// Demon's Souls creates ATRAC9 voices with low flag words 1, 2, 4 and 8 —
    /// the channel counts of the streams they carry. Rejecting any of them left
    /// the title holding SCE_AJM_INSTANCE_INVALID for that voice, so its 4- and
    /// 8-channel movie stems played silence.
    /// </summary>
    [Theory]
    [InlineData(0x1_0000_0001ul)]
    [InlineData(0x1_0000_0002ul)]
    [InlineData(0x1_0000_0004ul)]
    [InlineData(0x1_0000_0008ul)]
    public void InstanceCreate_AcceptsEveryChannelCountFlagWord(ulong flags)
    {
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));

        Assert.Equal(0, CreateInstance(contextId, 1, flags, InstanceAddress));
        Assert.Equal(0x4001u, ReadUInt32(InstanceAddress));
    }

    /// <summary>
    /// sceAjmBatchJobRunSplit gathers arrays of AjmBuffer descriptors rather than
    /// a single pointer/size pair, and its sideband layout is positional: result,
    /// then only the blocks the job flags asked for.
    /// </summary>
    [Fact]
    public void BatchJobRunSplit_GathersDescriptorsAndWritesFlaggedSideband()
    {
        const ulong inputDescriptors = MemoryBase + 0x600;
        const ulong outputDescriptors = MemoryBase + 0x640;
        const ulong inputData = MemoryBase + 0x700;
        const ulong outputData = MemoryBase + 0x800;
        const ulong sideband = MemoryBase + 0x900;
        const ulong streamSideband = 1ul << 47;
        const ulong multipleFrames = 1ul << 12;

        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 2));
        Assert.Equal(0, CreateInstance(contextId, 2, 0x2_0000_0002, InstanceAddress));
        var instanceId = ReadUInt32(InstanceAddress);

        InitializeBatch(BatchBufferAddress, 0x200, BatchInfoAddress);

        // Two input descriptors totalling 0x30 bytes, one output of 0x40.
        WriteUInt64(inputDescriptors, inputData);
        WriteUInt64(inputDescriptors + 8, 0x20);
        WriteUInt64(inputDescriptors + 16, inputData + 0x20);
        WriteUInt64(inputDescriptors + 24, 0x10);
        WriteUInt64(outputDescriptors, outputData);
        WriteUInt64(outputDescriptors + 8, 0x40);

        Span<byte> dirty = stackalloc byte[0x40];
        dirty.Fill(0xAB);
        Assert.True(_memory.TryWrite(outputData, dirty));

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = streamSideband | multipleFrames;
        _ctx[CpuRegister.Rcx] = inputDescriptors;
        _ctx[CpuRegister.R8] = 2;
        _ctx[CpuRegister.R9] = outputDescriptors;
        WriteStackArgs(outputCount: 1, sidebandAddress: sideband, sidebandSize: 0x20);

        Assert.Equal(0, AjmExports.AjmBatchJobRunSplit(_ctx));

        // A non-ATRAC9 instance stays a silence stub: the output is cleared and
        // the whole gathered input is reported consumed.
        Assert.All(ReadBytes(outputData, 0x40), value => Assert.Equal(0, value));

        var result = ReadBytes(sideband, 0x20);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result));            // AjmSidebandResult
        Assert.Equal(0x30, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(8)));  // stream.input_consumed
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(result.AsSpan(12)));    // stream.output_written
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(24)));  // mframe.num_frames

        // SCE_AJM_JOB_RUN_SPLIT_SIZE(3) = 32 + (16 * 3).
        Assert.Equal(80ul, ReadUInt64(BatchInfoAddress + 8));
    }

    [Fact]
    public void BatchJobSetGaplessDecode_UsesFifthArgumentForResult()
    {
        const ulong gaplessAddress = MemoryBase + 0x600;
        const ulong resultAddress = MemoryBase + 0x680;

        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        var instanceId = ReadUInt32(InstanceAddress);

        InitializeBatch(BatchBufferAddress, 48, BatchInfoAddress);
        Span<byte> sentinel = stackalloc byte[8];
        sentinel.Fill(0xCC);
        Assert.True(_memory.TryWrite(resultAddress, sentinel));

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = gaplessAddress;
        _ctx[CpuRegister.Rcx] = 1; // iReset, not a result pointer.
        _ctx[CpuRegister.R8] = resultAddress;

        Assert.Equal(0, AjmExports.AjmBatchJobSetGaplessDecode(_ctx));
        Assert.Equal(48ul, ReadUInt64(BatchInfoAddress + 8));
        Assert.All(ReadBytes(resultAddress, 8), value => Assert.Equal(0, value));
    }

    [Fact]
    public void BatchJobRunSplit_OmitsSidebandBlocksTheJobFlagsDidNotRequest()
    {
        const ulong inputDescriptors = MemoryBase + 0x600;
        const ulong outputDescriptors = MemoryBase + 0x640;
        const ulong inputData = MemoryBase + 0x700;
        const ulong outputData = MemoryBase + 0x800;
        const ulong sideband = MemoryBase + 0x900;

        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 2));
        Assert.Equal(0, CreateInstance(contextId, 2, 0x2_0000_0002, InstanceAddress));
        var instanceId = ReadUInt32(InstanceAddress);

        InitializeBatch(BatchBufferAddress, 0x200, BatchInfoAddress);
        WriteUInt64(inputDescriptors, inputData);
        WriteUInt64(inputDescriptors + 8, 0x20);
        WriteUInt64(outputDescriptors, outputData);
        WriteUInt64(outputDescriptors + 8, 0x40);

        Span<byte> sentinel = stackalloc byte[0x20];
        sentinel.Fill(0x5A);
        Assert.True(_memory.TryWrite(sideband, sentinel));

        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = 0; // No stream sideband, no multiple-frames.
        _ctx[CpuRegister.Rcx] = inputDescriptors;
        _ctx[CpuRegister.R8] = 1;
        _ctx[CpuRegister.R9] = outputDescriptors;
        WriteStackArgs(outputCount: 1, sidebandAddress: sideband, sidebandSize: 0x20);

        Assert.Equal(0, AjmExports.AjmBatchJobRunSplit(_ctx));

        // Only the 8-byte result block is written; the rest keeps its sentinel.
        var written = ReadBytes(sideband, 0x20);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(written));
        Assert.All(written.AsSpan(8).ToArray(), value => Assert.Equal(0x5A, value));
    }

    [Theory]
    [InlineData(23u)]
    [InlineData(24u)]
    public void Gen5CodecTypesCanRegisterAndCreateInstances(uint codecType)
    {
        var contextId = Initialize();

        Assert.Equal(0, RegisterCodec(contextId, codecType));
        Assert.Equal(
            0,
            CreateInstance(contextId, codecType, 0x401, InstanceAddress));
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
            Assert.True(manager.TryGetExport("bkRHEYG6lEM", out var memoryRegister));
            Assert.Equal("sceAjmMemoryRegister", memoryRegister.Name);
            Assert.True(manager.TryGetExport("pIpGiaYkHkM", out var memoryUnregister));
            Assert.Equal("sceAjmMemoryUnregister", memoryUnregister.Name);
            Assert.True(manager.TryGetExport("3cAg7xN995U", out var statistics));
            Assert.Equal("sceAjmBatchJobGetStatistics", statistics.Name);
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

    private int UnregisterCodec(uint contextId, uint codecType)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = codecType;
        return AjmExports.AjmModuleUnregister(_ctx);
    }

    private int RegisterMemory(uint contextId, ulong address, ulong pages)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = address;
        _ctx[CpuRegister.Rdx] = pages;
        return AjmExports.AjmMemoryRegister(_ctx);
    }

    private int UnregisterMemory(uint contextId, ulong address)
    {
        _ctx[CpuRegister.Rdi] = contextId;
        _ctx[CpuRegister.Rsi] = address;
        return AjmExports.AjmMemoryUnregister(_ctx);
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

    private void InitializeBatch(ulong bufferAddress, ulong bufferSize, ulong infoAddress)
    {
        _ctx[CpuRegister.Rdi] = bufferAddress;
        _ctx[CpuRegister.Rsi] = bufferSize;
        _ctx[CpuRegister.Rdx] = infoAddress;
        Assert.Equal(0, AjmExports.AjmBatchInitialize(_ctx));
    }

    /// <summary>
    /// Places the seventh, eighth and ninth SysV arguments where the export reads
    /// them: just past the return address slot at [rsp].
    /// </summary>
    private void WriteStackArgs(ulong outputCount, ulong sidebandAddress, ulong sidebandSize)
    {
        const ulong stackAddress = MemoryBase + 0xA00;
        _ctx[CpuRegister.Rsp] = stackAddress;
        WriteUInt64(stackAddress + 8, outputCount);
        WriteUInt64(stackAddress + 16, sidebandAddress);
        WriteUInt64(stackAddress + 24, sidebandSize);
    }

    private void WriteUInt64(ulong address, ulong value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }

    private ulong ReadUInt64(ulong address)
    {
        Span<byte> value = stackalloc byte[sizeof(ulong)];
        Assert.True(_memory.TryRead(address, value));
        return BinaryPrimitives.ReadUInt64LittleEndian(value);
    }

    private byte[] ReadBytes(ulong address, int length)
    {
        var value = new byte[length];
        Assert.True(_memory.TryRead(address, value));
        return value;
    }

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
