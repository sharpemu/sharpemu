// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Acm;

public static class AcmExports
{
    // Batch terminator command observed in Ghost of Yōtei's NCA audio engine:
    // {opcode 0xB, pointer to the caller's status block}. The status block's
    // +8 field is pre-filled with 0xFFFFFFFF_00000000 and converted to the
    // audio clock (cvtsi2ss) once the batch completes, so completion must
    // overwrite it or the guest computes a garbage timestamp.
    private const ulong BatchTerminatorOpcode = 0xB;
    private const int BatchCommandSize = 0x10;
    private const int BatchDescriptorSize = 0x18;
    private const ulong MaxBatchBufferBytes = 0x100000;
    private static int _nextContextId;
    private static readonly ConcurrentDictionary<uint, AcmContextState> Contexts = new();

    private sealed class AcmContextState
    {
        public AcmContextState(ulong parameterAddress, ulong workMemoryAddress, ulong workMemorySize)
        {
            ParameterAddress = parameterAddress;
            WorkMemoryAddress = workMemoryAddress;
            WorkMemorySize = workMemorySize;
        }

        public ulong ParameterAddress { get; }
        public ulong WorkMemoryAddress { get; }
        public ulong WorkMemorySize { get; }

        // In-flight batches by id; the value is the terminator's status-block
        // address (0 when the submitted buffer had no readable terminator).
        public ConcurrentDictionary<uint, ulong> Batches { get; } = new();

        private int _nextBatchId;

        public uint AllocateBatchId()
        {
            while (true)
            {
                var id = unchecked((uint)Interlocked.Increment(ref _nextBatchId));
                // The guest stores the id in a u32 slot where 0xFFFFFFFF means
                // "no batch in flight"; avoid it and 0 so a written id is
                // always distinguishable from both sentinels.
                if (id != 0 && id != uint.MaxValue)
                {
                    return id;
                }
            }
        }
    }

    [SysAbiExport(
        Nid = "ZIXln2K3XMk",
        ExportName = "sceAcmContextCreate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextCreate(CpuContext ctx)
    {
        var outContextAddress = ctx[CpuRegister.Rdi];
        if (outContextAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var contextId = unchecked((uint)Interlocked.Increment(ref _nextContextId));
        Span<byte> contextBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(contextBytes, contextId);
        if (!ctx.Memory.TryWrite(outContextAddress, contextBytes))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Contexts[contextId] = new AcmContextState(
            parameterAddress: ctx[CpuRegister.Rsi],
            workMemoryAddress: ctx[CpuRegister.Rcx],
            workMemorySize: ctx[CpuRegister.R8]);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "jBgBjAj02R8",
        ExportName = "sceAcmContextDestroy",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmContextDestroy(CpuContext ctx)
    {
        Contexts.TryRemove(unchecked((uint)ctx[CpuRegister.Rdi]), out _);
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    /// <summary>
    /// Submits a built command buffer: (contextId, 0, descriptorList,
    /// 0, out u32 batchId). The descriptor list points at descriptor structs of
    /// {commandBase, usedBytes, capacity}. This host renders batches
    /// synchronously, so submission only records the terminator's status block
    /// and publishes a batch id for the matching sceAcmBatchWait.
    /// </summary>
    [SysAbiExport(
        Nid = "8fe55ktlNVo",
        ExportName = "sceAcmBatchStartBuffers",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchStartBuffers(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var descriptorListAddress = ctx[CpuRegister.Rdx];
        var outBatchIdAddress = ctx[CpuRegister.R8];
        if (!Contexts.TryGetValue(contextId, out var context) ||
            descriptorListAddress == 0 ||
            outBatchIdAddress == 0)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var statusAddress = FindTerminatorStatusAddress(ctx, descriptorListAddress);
        var batchId = context.AllocateBatchId();
        context.Batches[batchId] = statusAddress;

        Span<byte> batchIdBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(batchIdBytes, batchId);
        if (!ctx.Memory.TryWrite(outBatchIdAddress, batchIdBytes))
        {
            context.Batches.TryRemove(batchId, out _);
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAcm($"batch-start context={contextId} batch={batchId} descriptors=0x{descriptorListAddress:X} status=0x{statusAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    /// <summary>
    /// Waits for batch completion: (contextId, batchId, pollArg) with 0 meaning
    /// complete. All batches complete immediately in the synchronous host
    /// model; completion overwrites the status block's sentinel timestamp so
    /// the guest audio clock gets 0 instead of a converted sentinel.
    /// </summary>
    [SysAbiExport(
        Nid = "RLN3gRlXJLE",
        ExportName = "sceAcmBatchWait",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceAcm")]
    public static int AcmBatchWait(CpuContext ctx)
    {
        var contextId = unchecked((uint)ctx[CpuRegister.Rdi]);
        var batchId = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (!Contexts.TryGetValue(contextId, out var context))
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (context.Batches.TryRemove(batchId, out var statusAddress) && statusAddress != 0)
        {
            Span<byte> timestamp = stackalloc byte[sizeof(ulong)];
            timestamp.Clear();
            _ = ctx.Memory.TryWrite(statusAddress + 8, timestamp);
        }

        TraceAcm($"batch-wait context={contextId} batch={batchId} status=0x{statusAddress:X}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static ulong FindTerminatorStatusAddress(CpuContext ctx, ulong descriptorListAddress)
    {
        Span<byte> pointerBytes = stackalloc byte[sizeof(ulong)];
        if (!ctx.Memory.TryRead(descriptorListAddress, pointerBytes))
        {
            return 0;
        }

        var descriptorAddress = BinaryPrimitives.ReadUInt64LittleEndian(pointerBytes);
        Span<byte> descriptor = stackalloc byte[BatchDescriptorSize];
        if (descriptorAddress == 0 || !ctx.Memory.TryRead(descriptorAddress, descriptor))
        {
            return 0;
        }

        var commandBase = BinaryPrimitives.ReadUInt64LittleEndian(descriptor);
        var usedBytes = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[8..]);
        var capacity = BinaryPrimitives.ReadUInt64LittleEndian(descriptor[16..]);
        if (commandBase == 0 ||
            usedBytes < BatchCommandSize ||
            usedBytes > capacity ||
            capacity > MaxBatchBufferBytes)
        {
            return 0;
        }

        Span<byte> command = stackalloc byte[BatchCommandSize];
        if (!ctx.Memory.TryRead(commandBase + usedBytes - BatchCommandSize, command))
        {
            return 0;
        }

        return BinaryPrimitives.ReadUInt64LittleEndian(command) == BatchTerminatorOpcode
            ? BinaryPrimitives.ReadUInt64LittleEndian(command[8..])
            : 0;
    }

    private static void TraceAcm(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_ACM"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] acm.{message}");
        }
    }

    internal static void ResetForTests()
    {
        Contexts.Clear();
        Interlocked.Exchange(ref _nextContextId, 0);
    }
}
