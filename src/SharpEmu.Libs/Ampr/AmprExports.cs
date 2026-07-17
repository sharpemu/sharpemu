// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;

namespace SharpEmu.Libs.Ampr;

public static class AmprExports
{
    private const int CommandBufferHeaderSize = 0x28;
    private const ulong CommandBufferSelfOffset = 0x00;
    private const ulong CommandBufferDataOffset = 0x08;
    private const ulong CommandBufferSizeOffset = 0x10;
    private const ulong CommandBufferAux0Offset = 0x18;
    private const ulong CommandBufferAux1Offset = 0x20;
    private const ulong ReadFileRecordSize = 0x30;
    private const ulong KernelEventQueueRecordSize = 0x30;
    private const ulong WriteAddressRecordSize = 0x20;
    private const uint ReadFileRecordType = 1;
    private const uint KernelEventQueueRecordType = 2;
    private const uint WriteAddressRecordType = 3;
    private static readonly ConcurrentDictionary<ulong, CommandBufferState> _commandBuffers = new();
    private static readonly ConcurrentDictionary<string, Lazy<CachedHostFile>> _hostFileCache = new(StringComparer.OrdinalIgnoreCase);
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
    }

    private sealed class CachedHostFile
    {
        public CachedHostFile(string path)
        {
            Stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 1024 * 1024,
                FileOptions.RandomAccess);
        }

        public object Gate { get; } = new();
        public FileStream Stream { get; }
    }

    [SysAbiExport(
        Nid = "8aI7R7WaOlc",
        ExportName = "sceAmprCommandBufferConstructor",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int CommandBufferConstructor(CpuContext ctx)
    {
        var commandBuffer = ctx[CpuRegister.Rdi];
        var buffer = ctx[CpuRegister.Rsi];
        var size = ctx[CpuRegister.Rdx];

        if (commandBuffer == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (!InitializeCommandBuffer(ctx, commandBuffer, buffer, size, aux0: 0, aux1: 0, clear: true))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "ctor", commandBuffer, buffer, size);
        ctx[CpuRegister.Rax] = commandBuffer;
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

        if (!InitializeCommandBuffer(ctx, commandBuffer, buffer: 0, size: 0, aux0, aux1, clear: false))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "apr_ctor", commandBuffer, aux0, aux1);
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

        Span<byte> auxiliaryPointers = stackalloc byte[sizeof(ulong) * 2];
        auxiliaryPointers.Clear();
        if (!ctx.Memory.TryWrite(commandBuffer + CommandBufferAux0Offset, auxiliaryPointers))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
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

        if (!WriteVisibleCommandBufferPointers(ctx, commandBuffer, buffer: 0, size: 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        _commandBuffers.TryRemove(commandBuffer, out _);
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

        Span<byte> bufferPointers = stackalloc byte[sizeof(ulong) * 2];
        if (!ctx.Memory.TryRead(commandBuffer + CommandBufferDataOffset, bufferPointers))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var buffer = BinaryPrimitives.ReadUInt64LittleEndian(bufferPointers);
        var size = BinaryPrimitives.ReadUInt64LittleEndian(bufferPointers[sizeof(ulong)..]);
        if (
            !WriteCommandBufferPointers(ctx, commandBuffer, buffer, size, writeOffset: 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "reset", commandBuffer, buffer, size);
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

        _commandBuffers.TryRemove(commandBuffer, out _);
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

        if (commandBuffer == 0 || (destination == 0 && size != 0))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var fileOffset))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (!AmprFileRegistry.TryGetHostPath(fileId, out var hostPath))
        {
            TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead: 0, hostPath, (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var result = TryReadFileToGuestMemory(ctx, hostPath, fileOffset, destination, size, out var bytesRead);
        if (result != (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead, hostPath, result);
            return result;
        }

        if (!AppendReadFileRecord(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmprRead(ctx, commandBuffer, fileId, destination, size, fileOffset, bytesRead, hostPath, (int)OrbisGen2Result.ORBIS_GEN2_OK);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "vWU-odnS+fU",
        ExportName = "sceAmprMeasureCommandSizeReadFile",
        Target = Generation.Gen5,
        LibraryName = "libSceAmpr")]
    public static int MeasureCommandSizeReadFile(CpuContext ctx)
    {
        TraceAmpr(ctx, "measure_read_file", 0, ReadFileRecordSize, 0);
        ctx[CpuRegister.Rax] = ReadFileRecordSize;
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
        var completionToken = ctx[CpuRegister.Rcx];
        var userData = ctx[CpuRegister.R8];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendKernelEventQueueRecord(
                ctx,
                commandBuffer,
                equeue,
                ident,
                completionToken,
                userData))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_equeue", commandBuffer, ident, completionToken);
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
        var completionToken = ctx[CpuRegister.Rcx];
        var userData = ctx[CpuRegister.R8];

        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!AppendKernelEventQueueRecord(
                ctx,
                commandBuffer,
                equeue,
                ident,
                completionToken,
                userData))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        TraceAmpr(ctx, "write_equeue_complete", commandBuffer, ident, completionToken);
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

    public static int CompleteCommandBuffer(CpuContext ctx, ulong commandBuffer)
    {
        if (commandBuffer == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetCommandBufferState(ctx, commandBuffer, out var buffer, out _, out var state) || state is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ulong writeOffset;
        lock (state)
        {
            writeOffset = state.WriteOffset;
        }

        var offset = 0UL;
        while (offset < writeOffset)
        {
            if (!TryReadUInt32(ctx, buffer + offset, out var recordType))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            switch (recordType)
            {
                case ReadFileRecordType:
                    offset += ReadFileRecordSize;
                    break;

                case KernelEventQueueRecordType:
                    if (!CompleteKernelEventQueueRecord(ctx, buffer + offset))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    }

                    offset += KernelEventQueueRecordSize;
                    break;

                case WriteAddressRecordType:
                    if (!CompleteWriteAddressRecord(ctx, buffer + offset))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                    }

                    offset += WriteAddressRecordSize;
                    break;

                default:
                    TraceAmpr(ctx, "complete_unknown", commandBuffer, recordType, offset);
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
        }

        TraceAmpr(ctx, "complete", commandBuffer, buffer, writeOffset);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool InitializeCommandBuffer(
        CpuContext ctx,
        ulong commandBuffer,
        ulong buffer,
        ulong size,
        ulong aux0,
        ulong aux1,
        bool clear)
    {
        Span<byte> header = stackalloc byte[CommandBufferHeaderSize];
        if (clear)
        {
            header.Clear();
        }
        else
        {
            if (!ctx.Memory.TryRead(commandBuffer, header))
            {
                return false;
            }

            buffer = BinaryPrimitives.ReadUInt64LittleEndian(header[(int)CommandBufferDataOffset..]);
            size = BinaryPrimitives.ReadUInt64LittleEndian(header[(int)CommandBufferSizeOffset..]);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(header[(int)CommandBufferSelfOffset..], commandBuffer);
        BinaryPrimitives.WriteUInt64LittleEndian(header[(int)CommandBufferDataOffset..], buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(header[(int)CommandBufferSizeOffset..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(header[(int)CommandBufferAux0Offset..], aux0);
        BinaryPrimitives.WriteUInt64LittleEndian(header[(int)CommandBufferAux1Offset..], aux1);
        if (!ctx.Memory.TryWrite(commandBuffer, header))
        {
            return false;
        }

        UpdateCommandBufferState(commandBuffer, buffer, size, writeOffset: 0);
        return true;
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size)
    {
        return WriteCommandBufferPointers(ctx, commandBuffer, buffer, size, writeOffset: 0);
    }

    private static bool WriteCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size, ulong writeOffset)
    {
        if (!WriteVisibleCommandBufferPointers(ctx, commandBuffer, buffer, size))
        {
            return false;
        }

        UpdateCommandBufferState(commandBuffer, buffer, size, writeOffset);

        return true;
    }

    private static bool WriteVisibleCommandBufferPointers(CpuContext ctx, ulong commandBuffer, ulong buffer, ulong size)
    {
        Span<byte> pointers = stackalloc byte[sizeof(ulong) * 3];
        BinaryPrimitives.WriteUInt64LittleEndian(pointers, commandBuffer);
        BinaryPrimitives.WriteUInt64LittleEndian(pointers[sizeof(ulong)..], buffer);
        BinaryPrimitives.WriteUInt64LittleEndian(pointers[(sizeof(ulong) * 2)..], size);
        return ctx.Memory.TryWrite(commandBuffer + CommandBufferSelfOffset, pointers);
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
        }
    }

    private static bool TryGetCommandBufferState(
        CpuContext ctx,
        ulong commandBuffer,
        out ulong buffer,
        out ulong size,
        out CommandBufferState? state)
    {
        if (_commandBuffers.TryGetValue(commandBuffer, out state))
        {
            lock (state)
            {
                buffer = state.Buffer;
                size = state.Size;
            }

            return true;
        }

        Span<byte> pointers = stackalloc byte[sizeof(ulong) * 2];
        if (ctx.Memory.TryRead(commandBuffer + CommandBufferDataOffset, pointers))
        {
            buffer = BinaryPrimitives.ReadUInt64LittleEndian(pointers);
            size = BinaryPrimitives.ReadUInt64LittleEndian(pointers[sizeof(ulong)..]);
            state = _commandBuffers.GetOrAdd(commandBuffer, static _ => new CommandBufferState());
            lock (state)
            {
                state.Buffer = buffer;
                state.Size = size;
                state.WriteOffset = 0;
                state.CommandCount = 0;
            }

            return true;
        }

        buffer = 0;
        size = 0;
        state = null;
        return false;
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

        const int ChunkSize = 1024 * 1024;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min((ulong)ChunkSize, size));

        try
        {
            if (!TryGetCachedHostFile(hostPath, out var cachedFile, out var openResult))
            {
                return openResult;
            }

            long fileLength;
            lock (cachedFile.Gate)
            {
                fileLength = cachedFile.Stream.Length;
            }

            if (fileOffset >= (ulong)fileLength)
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
                int read;
                lock (cachedFile.Gate)
                {
                    cachedFile.Stream.Position = unchecked((long)absoluteOffset);
                    read = cachedFile.Stream.Read(buffer, 0, request);
                }

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

        var lazy = _hostFileCache.GetOrAdd(
            cachePath,
            static path => new Lazy<CachedHostFile>(() => new CachedHostFile(path), isThreadSafe: true));

        try
        {
            file = lazy.Value;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            _hostFileCache.TryRemove(cachePath, out _);
            result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
            return false;
        }
        catch (IOException)
        {
            _hostFileCache.TryRemove(cachePath, out _);
            result = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            return false;
        }
    }

    private static bool AppendReadFileRecord(
        CpuContext ctx,
        ulong commandBuffer,
        uint fileId,
        ulong destination,
        ulong size,
        ulong fileOffset,
        ulong bytesRead)
    {
        Span<byte> record = stackalloc byte[(int)ReadFileRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], ReadFileRecordType);
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x04..], fileId);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], destination);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], size);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], fileOffset);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], bytesRead);

        return AppendCommandBufferRecord(ctx, commandBuffer, record);
    }

    private static bool AppendKernelEventQueueRecord(
        CpuContext ctx,
        ulong commandBuffer,
        ulong equeue,
        ulong ident,
        ulong completionToken,
        ulong userData)
    {
        Span<byte> record = stackalloc byte[(int)KernelEventQueueRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], KernelEventQueueRecordType);
        BinaryPrimitives.WriteInt16LittleEndian(record[0x04..], KernelEventQueueCompatExports.KernelEventFilterAmpr);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], equeue);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], ident);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x18..], userData);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x20..], completionToken);

        return AppendCommandBufferRecord(ctx, commandBuffer, record);
    }

    private static bool AppendWriteAddressRecord(CpuContext ctx, ulong commandBuffer, ulong address, ulong value)
    {
        Span<byte> record = stackalloc byte[(int)WriteAddressRecordSize];
        record.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(record[0x00..], WriteAddressRecordType);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x08..], address);
        BinaryPrimitives.WriteUInt64LittleEndian(record[0x10..], value);

        return AppendCommandBufferRecord(ctx, commandBuffer, record);
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
        }

        return true;
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
