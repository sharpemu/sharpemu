// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
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
    private const ulong ConfigAddress = MemoryBase + 0xB00;
    private const ulong ConfigInfoAddress = MemoryBase + 0xB10;

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
    public void Initialize_Gen5AcceptsOpaqueInitializationValueAndWritesContext()
    {
        _ctx[CpuRegister.Rdi] = 0x0000000300000000;
        _ctx[CpuRegister.Rsi] = ContextAddress;

        Assert.Equal(0, AjmExports.AjmInitialize(_ctx));
        var contextId = ReadUInt32(ContextAddress);
        Assert.NotEqual(0u, contextId);
        Assert.Equal(0, RegisterCodec(contextId, 1));
    }

    [Fact]
    public void Initialize_Gen4StillRejectsNonzeroReservedValue()
    {
        var context = new CpuContext(_memory, Generation.Gen4);
        const uint sentinel = 0xCCCCCCCC;
        WriteUInt32(ContextAddress, sentinel);
        context[CpuRegister.Rdi] = 0x0000000300000000;
        context[CpuRegister.Rsi] = ContextAddress;

        Assert.Equal(unchecked((int)0x806A0001), AjmExports.AjmInitialize(context));
        Assert.Equal(sentinel, ReadUInt32(ContextAddress));
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

    [Fact]
    public void ParseConfigData_LayoutSevenIsReportedAsStereo()
    {
        WriteBytes(ConfigAddress, [0xFE, 0x4E, 0x00, 0x40]);

        _ctx[CpuRegister.Rdi] = ConfigAddress;
        _ctx[CpuRegister.Rsi] = ConfigInfoAddress;
        Assert.Equal(0, AjmExports.AjmDecAt9ParseConfigData(_ctx));

        Assert.Equal(3u, ReadUInt32(ConfigInfoAddress));
        Assert.Equal(1u, ReadUInt32(ConfigInfoAddress + 4));
        Assert.Equal(2u, ReadUInt32(ConfigInfoAddress + 8));
        Assert.Equal(128u, ReadUInt32(ConfigInfoAddress + 12));
        Assert.Equal(3u, ReadUInt32(ConfigInfoAddress + 16));
    }

    [Fact]
    public void ParseConfigData_Ps5MultichannelLayoutReportsFixedSuperframe()
    {
        WriteBytes(ConfigAddress, [0x30, 0x62, 0xC0, 0x40]);

        _ctx[CpuRegister.Rdi] = ConfigAddress;
        _ctx[CpuRegister.Rsi] = ConfigInfoAddress;
        Assert.Equal(0, AjmExports.AjmDecAt9ParseConfigData(_ctx));

        Assert.Equal(204u, ReadUInt32(ConfigInfoAddress));
        Assert.Equal(1u, ReadUInt32(ConfigInfoAddress + 4));
        Assert.Equal(12u, ReadUInt32(ConfigInfoAddress + 8));
        Assert.Equal(256u, ReadUInt32(ConfigInfoAddress + 12));
        Assert.Equal(17u, ReadUInt32(ConfigInfoAddress + 16));
    }

    [Fact]
    public void ParseConfigData_UsesSuperframeIndexInMonoDecoderConfig()
    {
        var decoder = new Atrac9DecodeState();
        Assert.True(decoder.TryInitialize([0x30, 0x62, 0xC0, 0x42]));

        var configField = typeof(Atrac9DecodeState).GetField(
            "_extendedDecoderConfig",
            BindingFlags.Instance | BindingFlags.NonPublic);
        var monoConfig = Assert.IsType<byte[]>(configField?.GetValue(decoder));

        // frameBytes=17 and sfIndex=2: the low config field is 16 << 5 | 2 << 3.
        Assert.Equal(new byte[] { 0xFE, 0x60, 0x02, 0x10 }, monoConfig);
    }

    [Fact]
    public void ExtendedBlocks_AdvanceByConsumedBytesAndSkipPadding()
    {
        // Two channels, two frames: the consumed block lengths differ and the
        // second frame has low-bit padding before its second channel block.
        ReadOnlySpan<byte> compressed =
        [
            0x80, 0x01,       // frame 0, channel 0: used = 2
            0x80, 0x82,       // frame 0, channel 1: used = 2
            0x80,             // frame 1, channel 0: used = 1
            0x03, 0x04, 0x80, // padding before channel 1
            0x80, 0x05,       // frame 1, channel 1: used = 2
        ];
        var usedBytes = new[] { 2, 2, 1, 2 };
        var position = 0;
        var usedIndex = 0;

        for (var frame = 0; frame < 2; frame++)
        {
            for (var channel = 0; channel < 2; channel++)
            {
                Assert.True(Atrac9DecodeState.TryBeginExtendedBlock(compressed, frame, ref position));
                position += usedBytes[usedIndex++];
            }
        }

        Assert.Equal(9, position);
    }

    [Fact]
    public void UnsupportedStandardLayoutProducesSilentSuperframeWithoutError()
    {
        // Layout 6 is outside LibAtrac9's standard channel table. It must not
        // be handed to the decoder, but the header still describes a complete
        // stereo-duration superframe so the caller can keep its timeline.
        var decoder = new Atrac9DecodeState();
        Assert.True(decoder.TryInitialize([0xFE, 0x4C, 0x00, 0x40]));

        var output = new byte[128 * 2 * sizeof(short)];
        var result = decoder.Decode(
            [0x00, 0x00, 0x00],
            output,
            Atrac9PcmEncoding.Signed16,
            requestedChannels: 2,
            multipleFrames: false);

        Assert.Equal(0, result.Status);
        Assert.Equal(output.Length, result.OutputWritten);
        Assert.Equal(128u, result.TotalDecodedSamples);
        Assert.All(output, value => Assert.Equal(0, value));
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

    [Fact]
    public void BatchJobDecodeSplit_InvalidInstanceWritesResultThroughSeventhArgument()
    {
        const ulong resultAddress = MemoryBase + 0xB00;
        const ulong outputAddress = MemoryBase + 0xC00;
        InitializeBatch(BatchBufferAddress, 0x100, BatchInfoAddress);
        WriteUInt64(outputAddress, 0x1122334455667788);
        WriteUInt64(resultAddress + 32, 0x8877665544332211);
        WriteStackArgs(resultAddress, 0, 0);
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = uint.MaxValue;
        _ctx[CpuRegister.Rdx] = MemoryBase + 0xD00;
        _ctx[CpuRegister.Rcx] = 1;
        _ctx[CpuRegister.R8] = outputAddress;
        _ctx[CpuRegister.R9] = 1;

        Assert.Equal(0, AjmExports.AjmBatchJobDecodeSplit(_ctx));
        Assert.NotEqual(0u, ReadUInt32(resultAddress));
        Assert.Equal(0u, ReadUInt32(resultAddress + 8));
        Assert.Equal(0u, ReadUInt32(resultAddress + 12));
        Assert.Equal(0UL, ReadUInt64(resultAddress + 16));
        Assert.Equal(0u, ReadUInt32(resultAddress + 24));
        Assert.Equal(0x1122334455667788UL, ReadUInt64(outputAddress));
        Assert.Equal(0x8877665544332211UL, ReadUInt64(resultAddress + 32));
    }

    [Fact]
    public void BatchJobDecodeSplit_GathersAFrameAndScattersPcmWithoutOverwritingBoundaries()
    {
        const ulong configAddress = MemoryBase + 0x600;
        const ulong inputDescriptors = MemoryBase + 0x620;
        const ulong outputDescriptors = MemoryBase + 0x660;
        const ulong inputData = MemoryBase + 0x700;
        const ulong outputData = MemoryBase + 0x800;
        const ulong resultAddress = MemoryBase + 0xB00;
        var contextId = Initialize();
        Assert.Equal(0, RegisterCodec(contextId, 1));
        Assert.Equal(0, CreateInstance(contextId, 1, 0x401, InstanceAddress));
        var instanceId = ReadUInt32(InstanceAddress);
        InitializeBatch(BatchBufferAddress, 0x100, BatchInfoAddress);
        Assert.True(_memory.TryWrite(configAddress, new byte[] { 0xFE, 0x70, 0x17, 0xE0 }));
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = configAddress;
        _ctx[CpuRegister.Rcx] = 4;
        _ctx[CpuRegister.R8] = resultAddress;
        Assert.Equal(0, AjmExports.AjmBatchJobInitialize(_ctx));
        Assert.Equal(0u, ReadUInt32(resultAddress));

        var input = new byte[192];
        new byte[] { 0x00, 0x84, 0x00, 0x40 }.CopyTo(input, 0);
        Assert.True(_memory.TryWrite(inputData, input));
        WriteUInt64(inputDescriptors, inputData);
        WriteUInt64(inputDescriptors + 8, 2);
        WriteUInt64(inputDescriptors + 16, inputData + 2);
        WriteUInt64(inputDescriptors + 24, 190);
        WriteUInt64(outputDescriptors, outputData);
        WriteUInt64(outputDescriptors + 8, 128);
        WriteUInt64(outputDescriptors + 16, outputData + 144);
        WriteUInt64(outputDescriptors + 24, 384);
        var sentinel = new byte[544];
        Array.Fill(sentinel, (byte)0xCC);
        Assert.True(_memory.TryWrite(outputData, sentinel));
        _ctx[CpuRegister.Rsp] = MemoryBase + 0xF00;
        WriteUInt64(MemoryBase + 0xF08, resultAddress);
        _ctx[CpuRegister.Rdi] = BatchInfoAddress;
        _ctx[CpuRegister.Rsi] = instanceId;
        _ctx[CpuRegister.Rdx] = inputDescriptors;
        _ctx[CpuRegister.Rcx] = 2;
        _ctx[CpuRegister.R8] = outputDescriptors;
        _ctx[CpuRegister.R9] = 2;
        Assert.Equal(0, AjmExports.AjmBatchJobDecodeSplit(_ctx));
        Assert.Equal(0u, ReadUInt32(resultAddress));
        Assert.Equal(192u, ReadUInt32(resultAddress + 8));
        Assert.Equal(512u, ReadUInt32(resultAddress + 12));
        Assert.Equal(256UL, ReadUInt64(resultAddress + 16));
        Assert.All(ReadBytes(outputData, 128), value => Assert.Equal(0, value));
        Assert.All(ReadBytes(outputData + 128, 16), value => Assert.Equal(0xCC, value));
        Assert.All(ReadBytes(outputData + 144, 384), value => Assert.Equal(0, value));
        Assert.All(ReadBytes(outputData + 528, 16), value => Assert.Equal(0xCC, value));
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

    private void WriteBytes(ulong address, ReadOnlySpan<byte> value) => Assert.True(_memory.TryWrite(address, value));

    private void WriteUInt32(ulong address, uint value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        Assert.True(_memory.TryWrite(address, bytes));
    }
}
