// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SharpEmu.Libs.Ampr;

public static class AmprExports
{
    private const int CommandBufferHeaderSize = 0x18;
    private const ulong CommandBufferTypeOffset = 0x00;
    private const ulong CommandBufferOffsetOffset = 0x04;
    private const ulong CommandBufferNumOffset = 0x08;
    private const ulong CommandBufferSizeOffset = 0x0C;
    private const ulong CommandBufferDataOffset = 0x10;
    private const ulong CommandBufferAux0Offset = 0x18;
    private const ulong CommandBufferAux1Offset = 0x20;
    private const ulong ReadFileRecordSize = 0x14;
    private const ulong ReadFileRecordSizeExtended = 0x18;
    private const ulong ReadGatherRecordSize = 0x08;
    private const ulong ReadGatherRecordSizeExtended = 0x0C;
    private const ulong ReadScatterRecordSize = 0x0C;
    private const ulong ReadGatherScatterRecordSize = 0x10;
    private const ulong ReadGatherScatterRecordSizeExtended = 0x14;
    private const ulong ResetGatherScatterRecordSize = 0x04;
    private const uint AprTypeGatherScatterValid = 0x00010000;
    private const ulong AprMaxReadLength = 0x0000000100000000;
    private const ulong AprMaxFileOffset = 0x0000010000000000;
    private const ulong AprMaxAppAddress = 0x0000f00000000000;
    private const ulong KernelEventQueueRecordSize = 0x20;
    private const ulong WriteAddressRecordSize = 0x20;
    private const ulong WaitAddressRecordSizeWithoutReference = 0x08;
    private const ulong WaitAddressRecordSizeWith32BitReference = 0x0C;
    private const ulong WaitAddressRecordSizeWith64BitReference = 0x10;
    private const uint ReadFileRecordType = 0x17;
    private const uint KernelEventQueueRecordType = 2;
    private const uint WriteAddressRecordType = 3;
    private static readonly ConcurrentDictionary<ulong, CommandBufferState> _commandBuffers = new();
    private static readonly ConcurrentDictionary<ulong, ulong> _commandBufferAliases = new();
    private static readonly bool _traceAmpr =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR"), "1", StringComparison.Ordinal);
    private static readonly bool _traceAmprReads =
        _traceAmpr ||
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_AMPR_READS"), "1", StringComparison.Ordinal);

    private sealed class CommandBufferState
    {
        public ulong Buffer;
        public ulong Size;
        public ulong WriteOffset;
        public ulong CommandCount;
        public readonly List<ReadFileCommand> ReadFileCommands = [];
        public readonly List<KernelEventCommand> KernelEventCommands = [];
        public readonly List<WriteAddressCommand> WriteAddressCommands = [];
        public readonly List<WaitAddressCommand> WaitAddressCommands = [];
        public bool GatherScatterValid;
        public uint GatherScatterFileId;
        public ulong GatherScatterDestination;
        public ulong GatherScatterFileOffset;
    }

    private sealed class ReadFileCommand
    {
        public ulong RecordOffset;
        public ulong RecordSize;
        public uint FileId;
        public ulong Destination;
        public ulong Size;
        public ulong FileOffset;
    }

    private sealed class KernelEventCommand
    {
        public ulong RecordOffset;
        public ulong Equeue;
        public int Id;
        public ulong Data;
    }

    private sealed class WriteAddressCommand
    {
        public ulong RecordOffset;
        public ulong Address;
        public ulong Value;
    }

    private sealed class WaitAddressCommand
    {
        public ulong RecordOffset;
        public ulong Address;
        public ulong Reference;
        public uint Compare;
        public uint Flush;
    }

    private sealed class CachedHostFile : IDisposable
    {
        public CachedHostFile(string path)
        {
            Handle = File.OpenHandle(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.RandomAccess);
            Length = RandomAccess.GetLength(Handle);
        }

        public SafeFileHandle Handle { get; }
        public long Length { get; }

        public void Dispose() => Handle.Dispose();
    }

    private sealed class CachedHostFileEntry
    {
        public required string Path { get; init; }
        public required CachedHostFile File { get; init; }
    }

    // Keep a bounded LRU of open host files. An unbounded cache exhausts the
    // process FD limit (~10k on macOS) during large asset storms, after
    // which every new open throws IOException and surfaces as NOT_FOUND — the
    // guest then reports InvalidFileFourCC on empty buffers.
    private const int MaxCachedHostFiles = 1536;
    private static readonly object _hostFileCacheGate = new();
    private static readonly Dictionary<string, LinkedListNode<CachedHostFileEntry>> _hostFileByPath =
        new(HostFsPath.Comparer);
    private static readonly LinkedList<CachedHostFileEntry> _hostFileLru = new();

    [SysAbiExport(
        Nid = "8aI7R7WaOlc",
        ExportName = "sceAmprCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferConstructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];

        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        Span<byte> header = stackalloc byte[CommandBufferHeaderSize];
        header.Clear();
        if (!ctx.Memory.TryWrite(commandBuffer, header))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        RemoveCommandBufferState(commandBuffer);
        TraceAmpr(ctx, "ctor", commandBuffer, 0, 0);
        TryPreindexApp0();
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "a8uLzYY--tM",
        ExportName = "sceAmprAprCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferConstructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var aux0 = ctx[CpuRegister.Rsi];
        var aux1 = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var reserved0 = aux0 != 0 ? aux0 : commandBuffer + CommandBufferAux0Offset;
        var reserved1 = aux1 != 0 ? aux1 : commandBuffer + CommandBufferAux1Offset;
        Span<byte> zero = stackalloc byte[sizeof(ulong)];
        zero.Clear();
        if (!ctx.Memory.TryWrite(reserved0, zero) ||
            (reserved1 != reserved0 && !ctx.Memory.TryWrite(reserved1, zero)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "apr_ctor", commandBuffer, aux0, aux1);
        TryPreindexApp0();
        ctx[CpuRegister.Rax] = commandBuffer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Qs1xtplKo0U",
        ExportName = "sceAmprAprCommandBufferDestructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferDestructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        TraceAmpr(ctx, "apr_dtor", commandBuffer, 0, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GuchCTefuZw",
        ExportName = "sceAmprCommandBufferDestructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferDestructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        RemoveCommandBufferState(commandBuffer);
        TraceAmpr(ctx, "dtor", commandBuffer, 0, 0);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "N-FSPA4S3nI",
        ExportName = "sceAmprCommandBufferSetBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferSetBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var buffer = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!WriteCommandBufferPointers(ctx, commandBuffer, buffer, size))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "set_buffer", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "baQO9ez2gL4",
        ExportName = "sceAmprCommandBufferReset",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferReset(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(commandBuffer + CommandBufferDataOffset, out var buffer) ||
            !TryReadUInt32(ctx, commandBuffer + CommandBufferSizeOffset, out var size32))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!WriteCommandBufferPointers(ctx, commandBuffer, buffer, size32, writeOffset: 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "reset", commandBuffer, buffer, size32);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ULvXMDz56po",
        ExportName = "sceAmprCommandBufferClearBuffer",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferClearBuffer(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out var buffer, out var size, out _) ||
            !WriteVisibleCommandBufferPointers(ctx, commandBuffer, buffer: 0, size: 0))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        RemoveCommandBufferState(commandBuffer);
        TraceAmpr(ctx, "clear_buffer", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = buffer;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mQ16-QdKv7k",
        ExportName = "sceAmprAprCommandBufferReadFile",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferReadFile(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var fileId = unchecked((uint)ctx[CpuRegister.Rcx]);
        var destination = ctx[CpuRegister.R8];
        var size = ctx[CpuRegister.R9];

        if (commandBuffer == 0 || !IsValidAprReadRange(destination, size))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryGetImportStackArgument(0, out var fileOffset) &&
            !ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out fileOffset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        // APR records the read here and performs the I/O only when the command
        // buffer is submitted. Keep the sequential-offset sentinel as a SharpEmu
        // compatibility extension and resolve it in CompleteReadFileRecord so
        // queued reads observe command order.
        if (fileOffset != ulong.MaxValue && !IsValidAprFileOffset(fileOffset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        return AppendReadFileRecord(ctx, commandBuffer, fileId, destination, size, fileOffset)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "vWU-odnS+fU",
        ExportName = "sceAmprMeasureCommandSizeReadFile",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeReadFile(CpuContext ctx)
    {
        var fileOffset = ctx[CpuRegister.Rcx];
        var recordSize = GetReadFileRecordSize(fileOffset);
        TraceAmpr(ctx, "measure_read_file", 0, recordSize, fileOffset);
        ctx[CpuRegister.Rax] = recordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "qesF88X4DRg",
        ExportName = "sceAmprMeasureCommandSizeReadFileGather",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeReadFileGather(CpuContext ctx)
    {
        var size = ctx[CpuRegister.Rdi];
        var fileOffset = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = IsValidAprReadSize(size) && IsValidAprFileOffset(fileOffset)
            ? GetReadGatherRecordSize(fileOffset)
            : unchecked((uint)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "7nXGDGMXSqo",
        ExportName = "sceAmprMeasureCommandSizeReadFileScatter",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeReadFileScatter(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var size = ctx[CpuRegister.Rsi];
        ctx[CpuRegister.Rax] = IsValidAprReadRange(destination, size)
            ? ReadScatterRecordSize
            : unchecked((uint)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "DXmgc5op8Yw",
        ExportName = "sceAmprMeasureCommandSizeReadFileGatherScatter",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeReadFileGatherScatter(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var size = ctx[CpuRegister.Rsi];
        var fileOffset = ctx[CpuRegister.Rdx];
        ctx[CpuRegister.Rax] = IsValidAprReadRange(destination, size) && IsValidAprFileOffset(fileOffset)
            ? GetReadGatherScatterRecordSize(fileOffset)
            : unchecked((uint)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "mZSbNJVJpV8",
        ExportName = "sceAmprAprCommandBufferReadFileGather",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferReadFileGather(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var size = ctx[CpuRegister.Rcx];
        var fileOffset = ctx[CpuRegister.R8];
        if (commandBuffer == 0 || !IsValidAprReadSize(size) || !IsValidAprFileOffset(fileOffset) ||
            !TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        uint fileId;
        ulong destination;
        lock (state)
        {
            if (!state.GatherScatterValid || !IsValidAprReadRange(state.GatherScatterDestination, size))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
            fileId = state.GatherScatterFileId;
            destination = state.GatherScatterDestination;
        }

        return AppendReadFileRecord(ctx, commandBuffer, 0x18, fileId, destination, size, fileOffset,
            GetReadGatherRecordSize(fileOffset))
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "Jg-AgkdJHkk",
        ExportName = "sceAmprAprCommandBufferReadFileScatter",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferReadFileScatter(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var destination = ctx[CpuRegister.Rcx];
        var size = ctx[CpuRegister.R8];
        if (commandBuffer == 0 || !IsValidAprReadRange(destination, size) ||
            !TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        uint fileId;
        ulong fileOffset;
        lock (state)
        {
            if (!state.GatherScatterValid || !IsValidAprFileOffset(state.GatherScatterFileOffset))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
            fileId = state.GatherScatterFileId;
            fileOffset = state.GatherScatterFileOffset;
        }

        return AppendReadFileRecord(ctx, commandBuffer, 0x19, fileId, destination, size, fileOffset,
            ReadScatterRecordSize)
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "BVmR1H8l+XI",
        ExportName = "sceAmprAprCommandBufferReadFileGatherScatter",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferReadFileGatherScatter(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var destination = ctx[CpuRegister.Rcx];
        var size = ctx[CpuRegister.R8];
        var fileOffset = ctx[CpuRegister.R9];
        if (commandBuffer == 0 || !IsValidAprReadRange(destination, size) || !IsValidAprFileOffset(fileOffset) ||
            !TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        uint fileId;
        lock (state)
        {
            if (!state.GatherScatterValid)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
            fileId = state.GatherScatterFileId;
        }

        return AppendReadFileRecord(ctx, commandBuffer, 0x1A, fileId, destination, size, fileOffset,
            GetReadGatherScatterRecordSize(fileOffset))
            ? (int)OrbisGen2Result.ORBIS_GEN2_OK
            : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
    }

    [SysAbiExport(
        Nid = "YPxkUDhgoNI",
        ExportName = "sceAmprAprCommandBufferResetGatherScatterState",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int AprCommandBufferResetGatherScatterState(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0 || !AppendNoOpRecord(ctx, commandBuffer, ResetGatherScatterRecordSize) ||
            !TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        lock (state)
        {
            state.GatherScatterValid = false;
            state.GatherScatterFileId = 0;
            state.GatherScatterDestination = 0;
            state.GatherScatterFileOffset = 0;
        }
        if (ctx.TryReadUInt32(commandBuffer + CommandBufferTypeOffset, out var type))
        {
            _ = ctx.TryWriteUInt32(commandBuffer + CommandBufferTypeOffset, type & ~AprTypeGatherScatterValid);
        }
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "sSAUCCU1dv4",
        ExportName = "sceAmprMeasureCommandSizeWriteKernelEventQueue_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteKernelEventQueue0400(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_equeue", 0, KernelEventQueueRecordSize, 0);
        ctx[CpuRegister.Rax] = KernelEventQueueRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Zi3dBUjgyXI",
        ExportName = "sceAmprMeasureCommandSizeWriteKernelEventQueueOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteKernelEventQueueOnCompletion(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_equeue_complete", 0, KernelEventQueueRecordSize, 0);
        ctx[CpuRegister.Rax] = KernelEventQueueRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "C+IEj+BsAFM",
        ExportName = "sceAmprMeasureCommandSizeWriteAddressOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteAddressOnCompletion(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_address_complete", 0, WriteAddressRecordSize, 0);
        ctx[CpuRegister.Rax] = WriteAddressRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "4fgtGfXDrFc",
        ExportName = "sceAmprMeasureCommandSizeWriteAddress_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWriteAddress0400(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_write_address", 0, WriteAddressRecordSize, 0);
        ctx[CpuRegister.Rax] = WriteAddressRecordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "jIlc4p5dSD0",
        ExportName = "sceAmprMeasureCommandSizeWaitOnAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWaitOnAddress(CpuContext ctx) => MeasureCommandSizeWaitOnAddress(ctx, legacy: false);

    [SysAbiExport(
        Nid = "0BMj1hgG+kE",
        ExportName = "sceAmprMeasureCommandSizeWaitOnAddress_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeWaitOnAddress0400(CpuContext ctx) => MeasureCommandSizeWaitOnAddress(ctx, legacy: true);

    private static int MeasureCommandSizeWaitOnAddress(CpuContext ctx, bool legacy)
    {
        var address = ctx[CpuRegister.Rdi];
        var reference = ctx[CpuRegister.Rsi];
        var compare = ctx[CpuRegister.Rdx];
        var flush = ctx[CpuRegister.Rcx];
        if (!IsValidWaitAddressParameters(address, compare, flush, legacy))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var recordSize = GetWaitAddressRecordSize(reference);
        TraceAmpr(ctx, legacy ? "measure_wait_address_04_00" : "measure_wait_address", address, recordSize, reference);
        ctx[CpuRegister.Rax] = recordSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "tZDDEo2tE5k",
        ExportName = "sceAmprCommandBufferGetSize",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferGetSize(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out var size, out _))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "get_size", commandBuffer, size, 0);
        ctx[CpuRegister.Rax] = size;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "GnxKOHEawhk",
        ExportName = "sceAmprCommandBufferGetCurrentOffset",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferGetCurrentOffset(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferOffset(ctx, commandBuffer, out var offset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "get_offset", commandBuffer, offset, 0);
        ctx[CpuRegister.Rax] = offset;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gzndltBEzWc",
        ExportName = "sceAmprCommandBufferGetNumCommands",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferGetNumCommands(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ulong commandCount;
        lock (state)
        {
            commandCount = state.CommandCount;
        }

        TraceAmpr(ctx, "get_num_commands", commandBuffer, commandCount, 0);
        ctx[CpuRegister.Rax] = commandCount;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "H896Pt-yB4I",
        ExportName = "sceAmprCommandBufferWriteKernelEventQueue_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteKernelEventQueue0400(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var equeue = ctx[CpuRegister.Rsi];
        var ident = ctx[CpuRegister.Rdx];
        var data = ctx[CpuRegister.Rcx];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendKernelEventQueueRecord(
                ctx,
                commandBuffer,
                equeue,
                ident,
                data))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_equeue", commandBuffer, ident, data);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "o67gODLFpls",
        ExportName = "sceAmprCommandBufferWriteKernelEventQueueOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteKernelEventQueueOnCompletion(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var equeue = ctx[CpuRegister.Rsi];
        var ident = ctx[CpuRegister.Rdx];
        var data = ctx[CpuRegister.Rcx];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendKernelEventQueueRecord(
                ctx,
                commandBuffer,
                equeue,
                ident,
                data))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_equeue_complete", commandBuffer, ident, data);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "sJXyWHjP-F8",
        ExportName = "sceAmprCommandBufferWriteAddressOnCompletion",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteAddressOnCompletion(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        var value = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0 || address == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendWriteAddressRecord(ctx, commandBuffer, address, value))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_address_complete", commandBuffer, address, value);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "j0+3uJMxYJY",
        ExportName = "sceAmprCommandBufferWriteAddress_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWriteAddress0400(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        var value = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0 || address == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendWriteAddressRecord(ctx, commandBuffer, address, value))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_address", commandBuffer, address, value);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "V7GQTEeUfhw",
        ExportName = "sceAmprCommandBufferWaitOnAddress",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWaitOnAddress(CpuContext ctx) => CommandBufferWaitOnAddress(ctx, legacy: false);

    [SysAbiExport(
        Nid = "DLfoNxTFNVk",
        ExportName = "sceAmprCommandBufferWaitOnAddress_04_00",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferWaitOnAddress0400(CpuContext ctx) => CommandBufferWaitOnAddress(ctx, legacy: true);

    private static int CommandBufferWaitOnAddress(CpuContext ctx, bool legacy)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var address = ctx[CpuRegister.Rsi];
        var reference = ctx[CpuRegister.Rdx];
        var compare = ctx[CpuRegister.Rcx];
        var flush = ctx[CpuRegister.R8];
        if (commandBuffer == 0 || !IsValidWaitAddressParameters(address, compare, flush, legacy))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        if (!AppendWaitAddressRecord(ctx, commandBuffer, address, reference, (uint)compare, (uint)flush))
        {
            return SetAmprResult(ctx, OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        TraceAmpr(ctx, legacy ? "wait_address_04_00" : "wait_address", commandBuffer, address, reference);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    public static int CompleteCommandBuffer(CpuContext ctx, ulong commandBuffer)
    {
        return CompleteCommandBuffer(ctx, commandBuffer, out _, out _);
    }

    public static int CompleteCommandBuffer(
        CpuContext ctx,
        ulong commandBuffer,
        out int executionResult,
        out uint errorOffset)
    {
        executionResult = (int)OrbisGen2Result.ORBIS_GEN2_OK;
        errorOffset = 0;

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out var buffer, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ulong writeOffset;
        ReadFileCommand[] readCommands;
        KernelEventCommand[] kernelEventCommands;
        WriteAddressCommand[] writeAddressCommands;
        WaitAddressCommand[] waitAddressCommands;
        lock (state)
        {
            writeOffset = state.WriteOffset;
            readCommands = state.ReadFileCommands.ToArray();
            kernelEventCommands = state.KernelEventCommands.ToArray();
            writeAddressCommands = state.WriteAddressCommands.ToArray();
            waitAddressCommands = state.WaitAddressCommands.ToArray();
        }

        var commands = new List<(ulong Offset, int Kind, int Index)>(
            readCommands.Length + kernelEventCommands.Length + writeAddressCommands.Length + waitAddressCommands.Length);
        for (var i = 0; i < readCommands.Length; i++)
        {
            commands.Add((readCommands[i].RecordOffset, 0, i));
        }
        for (var i = 0; i < kernelEventCommands.Length; i++)
        {
            commands.Add((kernelEventCommands[i].RecordOffset, 1, i));
        }
        for (var i = 0; i < writeAddressCommands.Length; i++)
        {
            commands.Add((writeAddressCommands[i].RecordOffset, 2, i));
        }
        for (var i = 0; i < waitAddressCommands.Length; i++)
        {
            commands.Add((waitAddressCommands[i].RecordOffset, 3, i));
        }
        commands.Sort(static (left, right) => left.Offset.CompareTo(right.Offset));

        foreach (var commandEntry in commands)
        {
            switch (commandEntry.Kind)
            {
                case 0:
                    {
                        var readCommand = readCommands[commandEntry.Index];
                        var readResult = CompleteReadFileRecord(ctx, commandBuffer, readCommand);
                        if (readResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
                        {
                            executionResult = readResult;
                            errorOffset = checked((uint)commandEntry.Offset);
                            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                        }
                        break;
                    }

                case 1:
                    var eventCommand = kernelEventCommands[commandEntry.Index];
                    if (!KernelEventQueueCompatExports.TriggerAmprEvent(
                            eventCommand.Equeue,
                            unchecked((uint)eventCommand.Id),
                            eventCommand.Data))
                    {
                        executionResult = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                        errorOffset = checked((uint)commandEntry.Offset);
                        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                    }
                    break;

                case 2:
                    var writeCommand = writeAddressCommands[commandEntry.Index];
                    if (!ctx.TryWriteUInt64(writeCommand.Address, writeCommand.Value))
                    {
                        executionResult = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                        errorOffset = checked((uint)commandEntry.Offset);
                        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                    }
                    break;

                case 3:
                    var waitCommand = waitAddressCommands[commandEntry.Index];
                    var waitResult = WaitForAddress(ctx, waitCommand);
                    if (waitResult != (int)OrbisGen2Result.ORBIS_GEN2_OK)
                    {
                        executionResult = waitResult;
                        errorOffset = checked((uint)commandEntry.Offset);
                        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
                    }
                    break;
            }
        }

        TraceAmpr(ctx, "complete", commandBuffer, buffer, writeOffset);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int CompleteReadFileRecord(CpuContext ctx, ulong commandBuffer, ReadFileCommand command)
    {
        var fileId = command.FileId;
        var destination = command.Destination;
        var size = command.Size;
        var fileOffset = command.FileOffset;

        if (!AmprFileRegistry.TryGetHostPath(fileId, out var hostPath))
        {
            var app0Root = KernelMemoryCompatExports.ResolveGuestPath("$/");
            if (!string.IsNullOrEmpty(app0Root))
            {
                AmprFileRegistry.EnsureApp0Indexed(app0Root);
            }

            if (!AmprFileRegistry.TryGetHostPath(fileId, out hostPath))
            {
                TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead: 0, hostPath, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }
        }

        // Resolve the sequential-read sentinel at execution time to preserve command order.
        if (fileOffset == unchecked((ulong)(long)-1))
        {
            fileOffset = PakDirectoryTracker.ResolveSequentialOffset(fileId, size);
        }
        else if (fileOffset > long.MaxValue)
        {
            fileOffset = 0;
        }

        var result = TryReadFileToGuestMemory(ctx, hostPath, fileOffset, destination, size, out var bytesRead);
        TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead, hostPath, result);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return result;
        }

        PakDirectoryTracker.OnReadCompleted(ctx, fileId, destination, fileOffset, bytesRead);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size)
    {
        return WriteCommandBufferPointers(ctx, commandBuffer, buffer, size, writeOffset: 0);
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size, ulong writeOffset)
    {
        if (size > uint.MaxValue || writeOffset > uint.MaxValue ||
            !WriteVisibleCommandBufferPointers(ctx, commandBuffer, buffer, size) ||
            !ctx.TryWriteUInt32(commandBuffer + CommandBufferOffsetOffset, (uint)writeOffset) ||
            !ctx.TryWriteUInt32(commandBuffer + CommandBufferNumOffset, 0))
        {
            return false;
        }

        UpdateCommandBufferState(commandBuffer, buffer, size, writeOffset);

        return true;
    }

    private static bool WriteVisibleCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size)
    {
        if (size > uint.MaxValue)
        {
            return false;
        }

        return ctx.TryWriteUInt32(commandBuffer + CommandBufferSizeOffset, (uint)size) &&
               ctx.TryWriteUInt64(commandBuffer + CommandBufferDataOffset, buffer);
    }

    private static void UpdateCommandBufferState(
        ulong commandBuffer,
        ulong buffer,
        ulong size,
        ulong writeOffset)
    {
        var state = _commandBuffers.GetOrAdd(commandBuffer, static _ => new CommandBufferState());
        lock (state)
        {
            state.Buffer = buffer;
            state.Size = size;
            state.WriteOffset = writeOffset;
            state.CommandCount = 0;
            state.ReadFileCommands.Clear();
            state.KernelEventCommands.Clear();
            state.WriteAddressCommands.Clear();
            state.WaitAddressCommands.Clear();
            state.GatherScatterValid = false;
            state.GatherScatterFileId = 0;
            state.GatherScatterDestination = 0;
            state.GatherScatterFileOffset = 0;
        }

        RegisterCommandBufferAlias(commandBuffer, buffer);
    }

    private static bool TryGetCommandBufferState(
        CpuContext ctx,
        ulong commandBuffer,
        out ulong buffer,
        out ulong size,
        out CommandBufferState? state)
    {
        if (_commandBuffers.TryGetValue(commandBuffer, out state) && HasQueuedCommands(state))
        {
            lock (state)
            {
                buffer = state.Buffer;
                size = state.Size;
            }

            return true;
        }

        if (_commandBufferAliases.TryGetValue(commandBuffer, out var owner) &&
            _commandBuffers.TryGetValue(owner, out state))
        {
            lock (state)
            {
                buffer = state.Buffer;
                size = state.Size;
            }

            return true;
        }

        if (state is not null)
        {
            lock (state)
            {
                buffer = state.Buffer;
                size = state.Size;
            }

            return true;
        }

        if (ctx.TryReadUInt64(commandBuffer + CommandBufferDataOffset, out buffer) &&
            TryReadUInt32(ctx, commandBuffer + CommandBufferSizeOffset, out var size32) &&
            TryReadUInt32(ctx, commandBuffer + CommandBufferOffsetOffset, out var offset32) &&
            TryReadUInt32(ctx, commandBuffer + CommandBufferNumOffset, out var count32))
        {
            size = size32;
            state = _commandBuffers.GetOrAdd(commandBuffer, static _ => new CommandBufferState());
            lock (state)
            {
                state.Buffer = buffer;
                state.Size = size;
                state.WriteOffset = offset32;
                state.CommandCount = count32;
                state.ReadFileCommands.Clear();
                state.KernelEventCommands.Clear();
                state.WriteAddressCommands.Clear();
                state.WaitAddressCommands.Clear();
                state.GatherScatterValid = false;
                state.GatherScatterFileId = 0;
                state.GatherScatterDestination = 0;
                state.GatherScatterFileOffset = 0;
            }

            return true;
        }

        buffer = 0;
        size = 0;
        state = null;
        return false;
    }

    private static bool HasQueuedCommands(CommandBufferState state)
    {
        lock (state)
        {
            return state.ReadFileCommands.Count != 0 ||
                   state.KernelEventCommands.Count != 0 ||
                   state.WriteAddressCommands.Count != 0 ||
                   state.WaitAddressCommands.Count != 0;
        }
    }

    private static void RegisterCommandBufferAlias(ulong commandBuffer, ulong buffer)
    {
        foreach (var alias in _commandBufferAliases)
        {
            if (alias.Value == commandBuffer)
            {
                _commandBufferAliases.TryRemove(alias.Key, out _);
            }
        }

        if (buffer != 0 && buffer != commandBuffer)
        {
            _commandBufferAliases[buffer] = commandBuffer;
        }
    }

    private static void RemoveCommandBufferState(ulong commandBuffer)
    {
        _commandBuffers.TryRemove(commandBuffer, out _);
        foreach (var alias in _commandBufferAliases)
        {
            if (alias.Key == commandBuffer || alias.Value == commandBuffer)
            {
                _commandBufferAliases.TryRemove(alias.Key, out _);
            }
        }
    }

    private static bool TryGetCommandBufferOffset(CpuContext ctx, ulong commandBuffer, out ulong offset)
    {
        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            offset = 0;
            return false;
        }

        lock (state)
        {
            offset = state.WriteOffset;
        }

        return true;
    }

    private static int TryReadFileToGuestMemory(
        CpuContext ctx,
        string hostPath,
        ulong fileOffset,
        ulong destination,
        ulong size,
        out ulong bytesRead)
    {
        bytesRead = 0;
        if (size == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (fileOffset > long.MaxValue)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // 4 MiB chunks cut syscall/Rosetta round-trips on DeS' large sequential
        // APR reads without blowing the ArrayPool for small probes.
        const int ChunkSize = 4 * 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min((ulong)ChunkSize, size));

        try
        {
            if (!TryGetCachedHostFile(hostPath, out var cachedFile, out var openResult))
            {
                return openResult;
            }

            if (fileOffset >= (ulong)cachedFile.Length)
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            while (bytesRead < size)
            {
                if (bytesRead > ulong.MaxValue - fileOffset)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                var absoluteOffset = fileOffset + bytesRead;
                if (absoluteOffset > long.MaxValue)
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                var request = (int)Math.Min((ulong)buffer.Length, size - bytesRead);
                var read = RandomAccess.Read(
                    cachedFile.Handle,
                    buffer.AsSpan(0, request),
                    unchecked((long)absoluteOffset));

                if (read <= 0)
                {
                    break;
                }

                if (!ctx.Memory.TryWrite(destination + bytesRead, buffer.AsSpan(0, read)))
                {
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                bytesRead += (ulong)read;
            }
        }
        catch (UnauthorizedAccessException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryGetCachedHostFile(string hostPath, out CachedHostFile file, out int result)
    {
        file = null!;
        result = (int)OrbisGen2Result.ORBIS_GEN2_OK;

        string cachePath;
        try
        {
            cachePath = Path.GetFullPath(hostPath);
        }
        catch
        {
            cachePath = hostPath;
        }

        lock (_hostFileCacheGate)
        {
            if (_hostFileByPath.TryGetValue(cachePath, out var existing))
            {
                _hostFileLru.Remove(existing);
                _hostFileLru.AddFirst(existing);
                file = existing.Value.File;
                return true;
            }
        }

        CachedHostFile opened;
        try
        {
            opened = new CachedHostFile(cachePath);
        }
        catch (UnauthorizedAccessException)
        {
            result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            return false;
        }
        catch (IOException)
        {
            // Likely EMFILE from a prior unbounded cache, or a transient miss.
            // Evict everything we hold and retry once so a full FD table can
            // recover without restarting the process.
            EvictAllCachedHostFiles();
            try
            {
                opened = new CachedHostFile(cachePath);
            }
            catch (UnauthorizedAccessException)
            {
                result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
                return false;
            }
            catch (IOException)
            {
                result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                return false;
            }
        }

        lock (_hostFileCacheGate)
        {
            if (_hostFileByPath.TryGetValue(cachePath, out var raced))
            {
                opened.Dispose();
                _hostFileLru.Remove(raced);
                _hostFileLru.AddFirst(raced);
                file = raced.Value.File;
                return true;
            }

            while (_hostFileByPath.Count >= MaxCachedHostFiles)
            {
                EvictLeastRecentlyUsedHostFileLocked();
            }

            var entry = new CachedHostFileEntry { Path = cachePath, File = opened };
            var node = _hostFileLru.AddFirst(entry);
            _hostFileByPath[cachePath] = node;
            file = opened;
            return true;
        }
    }

    private static void EvictAllCachedHostFiles()
    {
        List<CachedHostFile> doomed;
        lock (_hostFileCacheGate)
        {
            doomed = _hostFileLru.Select(entry => entry.File).ToList();
            _hostFileLru.Clear();
            _hostFileByPath.Clear();
        }

        foreach (var cached in doomed)
        {
            cached.Dispose();
        }
    }

    private static void EvictLeastRecentlyUsedHostFileLocked()
    {
        var last = _hostFileLru.Last;
        if (last is null)
        {
            return;
        }

        _hostFileLru.RemoveLast();
        _hostFileByPath.Remove(last.Value.Path);
        last.Value.File.Dispose();
    }

    private static bool AppendReadFileRecord(
        CpuContext ctx,
        ulong commandBuffer,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset)
    {
        return AppendReadFileRecord(
            ctx,
            commandBuffer,
            opcode: 0x17,
            fileId,
            destination,
            size,
            fileOffset,
            GetReadFileRecordSize(fileOffset));
    }

    private static bool AppendReadFileRecord(
        CpuContext ctx,
        ulong commandBuffer,
        byte opcode,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset,
        ulong recordSize)
    {
        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return false;
        }

        Span<byte> record = stackalloc byte[(int)recordSize];
        record.Clear();
        record[0] = opcode;

        lock (state)
        {
            if (state.Buffer == 0 ||
                state.WriteOffset > state.Size ||
                recordSize > state.Size - state.WriteOffset)
            {
                return false;
            }

            var recordOffset = state.WriteOffset;
            if (!ctx.Memory.TryWrite(state.Buffer + recordOffset, record))
            {
                return false;
            }

            state.ReadFileCommands.Add(new ReadFileCommand
            {
                RecordOffset = recordOffset,
                RecordSize = recordSize,
                FileId = fileId,
                Destination = destination,
                Size = size,
                FileOffset = fileOffset,
            });
            state.WriteOffset += recordSize;
            state.CommandCount++;
            if (!WriteCommandBufferProgress(ctx, commandBuffer, state))
            {
                state.ReadFileCommands.RemoveAt(state.ReadFileCommands.Count - 1);
                state.WriteOffset -= recordSize;
                state.CommandCount--;
                return false;
            }

            state.GatherScatterFileId = fileId;
            if (destination > ulong.MaxValue - size)
            {
                state.GatherScatterValid = false;
                return false;
            }

            state.GatherScatterDestination = destination + size;
            if (fileOffset == ulong.MaxValue)
            {
                // -1 means continue after the previous read of this file id.
                // Its concrete file offset is not known until command execution,
                // so it cannot seed a gather/scatter continuation here.
                state.GatherScatterValid = false;
                state.GatherScatterFileOffset = 0;
            }
            else
            {
                if (fileOffset > ulong.MaxValue - size)
                {
                    state.GatherScatterValid = false;
                    return false;
                }

                state.GatherScatterValid = true;
                state.GatherScatterFileOffset = fileOffset + size;
            }
        }

        if (ctx.TryReadUInt32(commandBuffer + CommandBufferTypeOffset, out var type))
        {
            var updatedType = state.GatherScatterValid
                ? type | AprTypeGatherScatterValid
                : type & ~AprTypeGatherScatterValid;
            _ = ctx.TryWriteUInt32(commandBuffer + CommandBufferTypeOffset, updatedType);
        }

        return true;
    }

    private static ulong GetReadFileRecordSize(ulong fileOffset)
    {
        return (fileOffset >> 32) != 0 ? ReadFileRecordSizeExtended : ReadFileRecordSize;
    }

    private static ulong GetReadGatherRecordSize(ulong fileOffset)
    {
        return fileOffset > 0x3FFFF ? ReadGatherRecordSizeExtended : ReadGatherRecordSize;
    }

    private static ulong GetReadGatherScatterRecordSize(ulong fileOffset)
    {
        return (fileOffset >> 32) != 0 ? ReadGatherScatterRecordSizeExtended : ReadGatherScatterRecordSize;
    }

    private static bool IsValidAprFileOffset(ulong fileOffset) => fileOffset < AprMaxFileOffset;

    private static bool IsValidAprReadSize(ulong size) => size != 0 && size <= AprMaxReadLength;

    private static bool IsValidAprReadRange(ulong destination, ulong size)
    {
        return IsValidAprReadSize(size) &&
               destination <= AprMaxAppAddress &&
               AprMaxAppAddress - destination >= size;
    }

    private static bool AppendNoOpRecord(CpuContext ctx, ulong commandBuffer, ulong recordSize)
    {
        if (recordSize == 0 || recordSize > int.MaxValue)
        {
            return false;
        }

        Span<byte> record = stackalloc byte[(int)recordSize];
        record.Clear();
        return AppendCommandBufferRecord(ctx, commandBuffer, record);
    }

    private static bool AppendKernelEventQueueRecord(
        CpuContext ctx,
        ulong commandBuffer,
        ulong equeue,
        ulong ident,
        ulong data)
    {
        Span<byte> record = stackalloc byte[(int)KernelEventQueueRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], KernelEventQueueRecordType);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], equeue);
        BinaryPrimitives.WriteInt32LittleEndian(record[0x10..], unchecked((int)ident));
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], data);

        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return false;
        }

        ulong recordOffset;
        lock (state)
        {
            recordOffset = state.WriteOffset;
        }

        if (!AppendCommandBufferRecord(ctx, commandBuffer, record))
        {
            return false;
        }

        lock (state)
        {
            state.KernelEventCommands.Add(new KernelEventCommand
            {
                RecordOffset = recordOffset,
                Equeue = equeue,
                Id = unchecked((int)ident),
                Data = data,
            });
        }

        return true;
    }

    private static bool AppendWriteAddressRecord(CpuContext ctx, ulong commandBuffer, ulong address, ulong value)
    {
        Span<byte> record = stackalloc byte[(int)WriteAddressRecordSize];
        record.Clear();

        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return false;
        }

        ulong recordOffset;
        lock (state)
        {
            recordOffset = state.WriteOffset;
        }

        if (!AppendCommandBufferRecord(ctx, commandBuffer, record))
        {
            return false;
        }

        lock (state)
        {
            state.WriteAddressCommands.Add(new WriteAddressCommand
            {
                RecordOffset = recordOffset,
                Address = address,
                Value = value,
            });
        }

        return true;
    }

    private static bool AppendWaitAddressRecord(
        CpuContext ctx,
        ulong commandBuffer,
        ulong address,
        ulong reference,
        uint compare,
        uint flush)
    {
        var recordSize = GetWaitAddressRecordSize(reference);
        Span<byte> record = stackalloc byte[(int)recordSize];
        record.Clear();

        var dwordCount = (uint)(recordSize / sizeof(uint));
        var header = (uint)((address >> 16) & 0xFFFF0000UL);
        header |= (((dwordCount << 8) + 0xF00u) & 0xEF00u) |
                  ((compare & 7u) << 13) |
                  ((flush & 1u) << 12) |
                  1u;
        BinaryPrimitives.WriteUInt32LittleEndian(record, header);
        BinaryPrimitives.WriteUInt32LittleEndian(record[sizeof(uint)..], (uint)address);
        if (reference != 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(record[(2 * sizeof(uint))..], (uint)reference);
            if (dwordCount == 4)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(record[(3 * sizeof(uint))..], (uint)(reference >> 32));
            }
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return false;
        }

        lock (state)
        {
            if (state.Buffer == 0 ||
                state.WriteOffset > state.Size ||
                recordSize > state.Size - state.WriteOffset ||
                !ctx.Memory.TryWrite(state.Buffer + state.WriteOffset, record))
            {
                return false;
            }

            var recordOffset = state.WriteOffset;
            state.WaitAddressCommands.Add(new WaitAddressCommand
            {
                RecordOffset = recordOffset,
                Address = address,
                Reference = reference,
                Compare = compare,
                Flush = flush,
            });
            state.WriteOffset += recordSize;
            state.CommandCount++;
            if (!WriteCommandBufferProgress(ctx, commandBuffer, state))
            {
                state.WaitAddressCommands.RemoveAt(state.WaitAddressCommands.Count - 1);
                state.WriteOffset -= recordSize;
                state.CommandCount--;
                return false;
            }
        }

        return true;
    }

    private static int WaitForAddress(CpuContext ctx, WaitAddressCommand command)
    {
        var spinWait = new SpinWait();
        while (true)
        {
            if (command.Flush != 0)
            {
                Thread.MemoryBarrier();
            }

            if (!ctx.TryReadUInt64(command.Address, out var value))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            if (IsWaitConditionSatisfied(value, command.Reference, command.Compare))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            spinWait.SpinOnce();
        }
    }

    private static bool IsWaitConditionSatisfied(ulong value, ulong reference, uint compare) => compare switch
    {
        0 => value == reference,
        1 => value > reference,
        2 => value < reference,
        3 => value != reference,
        4 => unchecked((long)(value - reference)) >= 0,
        5 => unchecked((long)value) > unchecked((long)reference),
        6 => unchecked((long)value) < unchecked((long)reference),
        _ => false,
    };

    private static bool IsValidWaitAddressParameters(ulong address, ulong compare, ulong flush, bool legacy)
    {
        var maxCompare = legacy ? 6UL : 3UL;
        return (address & 7UL) == 0 &&
               address <= AprMaxAppAddress &&
               AprMaxAppAddress - address >= sizeof(ulong) &&
               (!legacy || address != 0) &&
               compare <= maxCompare &&
               flush <= 1;
    }

    private static ulong GetWaitAddressRecordSize(ulong reference)
    {
        if (reference == 0)
        {
            return WaitAddressRecordSizeWithoutReference;
        }

        return (reference >> 32) == 0
            ? WaitAddressRecordSizeWith32BitReference
            : WaitAddressRecordSizeWith64BitReference;
    }

    private static int SetAmprResult(CpuContext ctx, OrbisGen2Result result)
    {
        var value = (int)result;
        ctx[CpuRegister.Rax] = unchecked((ulong)(long)value);
        return value;
    }

    private static bool AppendCommandBufferRecord(CpuContext ctx, ulong commandBuffer, ReadOnlySpan<byte> record)
    {
        if (!TryGetCommandBufferState(ctx, commandBuffer, out _, out _, out var state) || state is null)
        {
            return false;
        }

        var recordSize = (ulong)record.Length;
        lock (state)
        {
            if (state.Buffer == 0 ||
                state.WriteOffset > state.Size ||
                recordSize > state.Size - state.WriteOffset)
            {
                return false;
            }

            if (!ctx.Memory.TryWrite(state.Buffer + state.WriteOffset, record))
            {
                return false;
            }

            state.WriteOffset += recordSize;
            state.CommandCount++;
            if (!WriteCommandBufferProgress(ctx, commandBuffer, state))
            {
                state.WriteOffset -= recordSize;
                state.CommandCount--;
                return false;
            }
        }

        return true;
    }

    private static bool WriteCommandBufferProgress(CpuContext ctx, ulong commandBuffer, CommandBufferState state)
    {
        return state.WriteOffset <= uint.MaxValue &&
               state.CommandCount <= uint.MaxValue &&
               ctx.TryWriteUInt32(commandBuffer + CommandBufferOffsetOffset, (uint)state.WriteOffset) &&
               ctx.TryWriteUInt32(commandBuffer + CommandBufferNumOffset, (uint)state.CommandCount);
    }

    private static bool CompleteKernelEventQueueRecord(CpuContext ctx, ulong recordAddress)
    {
        Span<byte> record = stackalloc byte[(int)KernelEventQueueRecordSize];
        if (!ctx.Memory.TryRead(recordAddress, record))
        {
            return false;
        }

        var filter = unchecked((short)BinaryPrimitives.ReadUInt32LittleEndian(record[0x04..]));
        var equeue = BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]);
        var ident = BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]);
        var userData = BinaryPrimitives.ReadUInt64LittleEndian(record[0x18..]);
        var data = BinaryPrimitives.ReadUInt64LittleEndian(record[0x20..]);
        var extra = BinaryPrimitives.ReadUInt64LittleEndian(record[0x28..]);

        var queuedEvent = new KernelEventQueueCompatExports.KernelQueuedEvent(
            ident,
            filter,
            0x20,
            unchecked((uint)extra),
            data,
            userData);

        _ = KernelEventQueueCompatExports.EnqueueEvent(equeue, queuedEvent);
        TraceAmpr(ctx, "complete_equeue", equeue, ident, data);
        return true;
    }

    private static bool CompleteWriteAddressRecord(CpuContext ctx, ulong recordAddress)
    {
        Span<byte> record = stackalloc byte[(int)WriteAddressRecordSize];
        if (!ctx.Memory.TryRead(recordAddress, record))
        {
            return false;
        }

        var address = BinaryPrimitives.ReadUInt64LittleEndian(record[0x08..]);
        var value = BinaryPrimitives.ReadUInt64LittleEndian(record[0x10..]);
        if (!ctx.TryWriteUInt64(address, value))
        {
            return false;
        }

        TraceAmpr(ctx, "complete_write_address", address, value, 0);
        return true;
    }

    private static bool TryReadUInt32(CpuContext ctx, ulong address, out uint value)
    {
        Span<byte> buffer = stackalloc byte[sizeof(uint)];
        if (!ctx.Memory.TryRead(address, buffer))
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
        return true;
    }

    private static void TryPreindexApp0()
    {
        var app0Root = KernelMemoryCompatExports.ResolveGuestPath("$/");
        if (!string.IsNullOrEmpty(app0Root))
        {
            AmprFileRegistry.EnsureApp0Indexed(app0Root);
        }
    }

    private static void TraceAmpr(CpuContext ctx, string operation, ulong commandBuffer, ulong arg0, ulong arg1)
    {
        if (!_traceAmpr)
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] ampr.{operation}: cmd=0x{commandBuffer:X16} arg0=0x{arg0:X16} arg1=0x{arg1:X16} ret=0x{returnRip:X16}");
    }

    private static void TraceAmprRead(
        CpuContext ctx,
        ulong commandBuffer,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset,
        ulong bytesRead,
        string? hostPath,
        int result)
    {
        if (!_traceAmprReads)
        {
            return;
        }

        var returnRip = 0UL;
        _ = ctx.TryReadUInt64(ctx[CpuRegister.Rsp], out returnRip);
        Console.Error.WriteLine(
            $"[LOADER][TRACE] ampr.read_file: cmd=0x{commandBuffer:X16} id=0x{fileId:X8} dst=0x{destination:X16} size=0x{size:X16} offset=0x{fileOffset:X16} read=0x{bytesRead:X16} result=0x{result:X8} path='{hostPath ?? string.Empty}' ret=0x{returnRip:X16}");
    }
}
