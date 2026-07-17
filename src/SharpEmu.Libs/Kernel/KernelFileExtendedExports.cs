// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Positional and scatter/gather file I/O, synchronization, renaming, fcntl,
/// polling and asynchronous I/O (AIO) that complement the base file exports.
/// These share the same guest FD table and path-resolution helpers via the
/// partial <see cref="KernelMemoryCompatExports"/> class.
/// </summary>
public static partial class KernelMemoryCompatExports
{
    // fcntl commands (FreeBSD numbering used by the PS4/PS5 libc).
    private const int F_DUPFD = 0;
    private const int F_GETFD = 1;
    private const int F_SETFD = 2;
    private const int F_GETFL = 3;
    private const int F_SETFL = 4;

    // poll event bits.
    private const short POLLIN = 0x0001;
    private const short POLLOUT = 0x0004;

    // AIO request state (SCE_KERNEL_AIO_STATE_*).
    private const uint AioStateCompleted = 3;
    private const int SizeofAioRwRequest = 40;

    private static long _nextAioSubmitId = 1;
    private static readonly ConcurrentDictionary<uint, int> _aioResults = new();

    private static FileStream? GetOpenFile(int fd)
    {
        lock (_fdGate)
        {
            return _openFiles.TryGetValue(fd, out var stream) ? stream : null;
        }
    }

    // ---- Positional read/write (do not move the file offset) ----

    [SysAbiExport(Nid = "ezv-RSBNKqI", ExportName = "pread",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPread(CpuContext ctx) => KernelPreadCore(ctx);

    [SysAbiExport(Nid = "+r3rMFwItV4", ExportName = "sceKernelPread",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelPread(CpuContext ctx) => KernelPreadCore(ctx);

    private static int KernelPreadCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var offset = unchecked((long)ctx[CpuRegister.Rcx]);
        if (requested < 0 || (requested > 0 && bufferAddress == 0) || offset < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (requested == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(requested);
        int read;
        try
        {
            read = RandomAccess.Read(stream.SafeFileHandle, buffer, offset);
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (read > 0 && !ctx.Memory.TryWrite(bufferAddress, buffer.AsSpan(0, read)))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)read);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "C2kJ-byS5rM", ExportName = "pwrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPwrite(CpuContext ctx) => KernelPwriteCore(ctx);

    [SysAbiExport(Nid = "nKWi-N2HBV4", ExportName = "sceKernelPwrite",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelPwrite(CpuContext ctx) => KernelPwriteCore(ctx);

    private static int KernelPwriteCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var bufferAddress = ctx[CpuRegister.Rsi];
        var requested = (int)Math.Min(ctx[CpuRegister.Rdx], int.MaxValue);
        var offset = unchecked((long)ctx[CpuRegister.Rcx]);
        if (requested < 0 || (requested > 0 && bufferAddress == 0) || offset < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (requested == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var payload = GC.AllocateUninitializedArray<byte>(requested);
        if (!ctx.Memory.TryRead(bufferAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        try
        {
            RandomAccess.Write(stream.SafeFileHandle, payload, offset);
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)requested);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Synchronization ----

    [SysAbiExport(Nid = "juWbTNM+8hw", ExportName = "fsync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFsync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "fTx66l5iWIA", ExportName = "sceKernelFsync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFsync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "KIbJFQ0I1Cg", ExportName = "fdatasync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFdatasync(CpuContext ctx) => KernelFsyncCore(ctx);

    [SysAbiExport(Nid = "30Rh4ixbKy4", ExportName = "sceKernelFdatasync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFdatasync(CpuContext ctx) => KernelFsyncCore(ctx);

    private static int KernelFsyncCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            if (fd is 0 or 1 or 2)
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            stream.Flush(flushToDisk: true);
        }
        catch (IOException)
        {
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "Y2OqwJQ3lr8", ExportName = "sync",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSync(CpuContext ctx)
    {
        lock (_fdGate)
        {
            foreach (var stream in _openFiles.Values)
            {
                try { stream.Flush(flushToDisk: true); } catch (IOException) { }
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Truncation ----

    [SysAbiExport(Nid = "ih4CD9-gghM", ExportName = "ftruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFtruncate(CpuContext ctx) => KernelFtruncateCore(ctx);

    [SysAbiExport(Nid = "VW3TVZiM4-E", ExportName = "sceKernelFtruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFtruncate(CpuContext ctx) => KernelFtruncateCore(ctx);

    private static int KernelFtruncateCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var length = unchecked((long)ctx[CpuRegister.Rsi]);
        if (length < 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            stream.SetLength(length);
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "ayrtszI7GBg", ExportName = "truncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixTruncate(CpuContext ctx) => KernelTruncateCore(ctx);

    [SysAbiExport(Nid = "WlyEA-sLDf0", ExportName = "sceKernelTruncate",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelTruncate(CpuContext ctx) => KernelTruncateCore(ctx);

    private static int KernelTruncateCore(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var length = unchecked((long)ctx[CpuRegister.Rsi]);
        if (length < 0 || !TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        var hostPath = ResolveGuestPath(guestPath);
        try
        {
            using var stream = new FileStream(hostPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(length);
        }
        catch (FileNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Rename ----

    [SysAbiExport(Nid = "NN01qLRhiqU", ExportName = "rename",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixRename(CpuContext ctx) => KernelRenameCore(ctx);

    [SysAbiExport(Nid = "52NcYU9+lEo", ExportName = "sceKernelRename",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelRename(CpuContext ctx) => KernelRenameCore(ctx);

    private static int KernelRenameCore(CpuContext ctx)
    {
        if (!TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rdi], MaxGuestStringLength, out var fromGuest) ||
            !TryReadNullTerminatedUtf8(ctx, ctx[CpuRegister.Rsi], MaxGuestStringLength, out var toGuest))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        if (IsReadOnlyGuestMutationPath(fromGuest) || IsReadOnlyGuestMutationPath(toGuest))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        var fromHost = ResolveGuestPath(fromGuest);
        var toHost = ResolveGuestPath(toGuest);
        try
        {
            if (Directory.Exists(fromHost))
            {
                Directory.Move(fromHost, toHost);
            }
            else
            {
                File.Move(fromHost, toHost, overwrite: true);
            }
        }
        catch (FileNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (DirectoryNotFoundException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        catch (IOException)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        InvalidateNegativeStatCacheForPathAndAncestors(toGuest);
        AddNegativeStatCacheForGuestPath(fromGuest);
        InvalidateAprFileSizeCache(fromHost);
        InvalidateAprFileSizeCache(toHost);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Descriptor duplication ----

    [SysAbiExport(Nid = "iiQjzvfWDq0", ExportName = "dup",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixDup(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(fd, out var stream))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            // POSIX dup shares the open file description (and offset), which is
            // exactly the shared FileStream reference.
            var newFd = (int)Interlocked.Increment(ref _nextFileDescriptor);
            _openFiles[newFd] = stream;
            ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "wdUufa9g-D8", ExportName = "dup2",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixDup2(CpuContext ctx)
    {
        var oldFd = unchecked((int)ctx[CpuRegister.Rdi]);
        var newFd = unchecked((int)ctx[CpuRegister.Rsi]);
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(oldFd, out var stream))
            {
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            if (oldFd == newFd)
            {
                ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }

            // If newFd names an open file, dup2 closes it first.
            if (_openFiles.TryGetValue(newFd, out var existing) && !ReferenceEquals(existing, stream))
            {
                try { existing.Dispose(); } catch (IOException) { }
            }

            _openFiles[newFd] = stream;
            ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- fcntl ----

    [SysAbiExport(Nid = "8nY19bKoiZk", ExportName = "fcntl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixFcntl(CpuContext ctx) => KernelFcntlCore(ctx);

    [SysAbiExport(Nid = "SoZkxZkCHaw", ExportName = "sceKernelFcntl",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelFcntl(CpuContext ctx) => KernelFcntlCore(ctx);

    private static int KernelFcntlCore(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var command = unchecked((int)ctx[CpuRegister.Rsi]);
        var argument = unchecked((int)ctx[CpuRegister.Rdx]);

        switch (command)
        {
            case F_DUPFD:
                lock (_fdGate)
                {
                    if (!_openFiles.TryGetValue(fd, out var stream))
                    {
                        return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
                    }

                    var newFd = Math.Max((int)Interlocked.Increment(ref _nextFileDescriptor), argument);
                    _openFiles[newFd] = stream;
                    ctx[CpuRegister.Rax] = unchecked((ulong)newFd);
                }

                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            case F_GETFD:
            case F_GETFL:
                // No close-on-exec / status flags are tracked; report cleared.
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            case F_SETFD:
            case F_SETFL:
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;

            default:
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
    }

    // ---- poll / select ----
    // Regular files are always ready for both read and write, so report every
    // requested descriptor as immediately ready.

    [SysAbiExport(Nid = "ku7D4q1Y9PI", ExportName = "poll",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixPoll(CpuContext ctx)
    {
        var fdsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((uint)ctx[CpuRegister.Rsi]);
        if (fdsAddress == 0 || count == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // struct pollfd { int fd; short events; short revents; } == 8 bytes.
        var ready = 0;
        Span<byte> buffer = stackalloc byte[8];
        for (uint i = 0; i < count && i < 4096; i++)
        {
            var entry = fdsAddress + i * 8;
            if (!ctx.Memory.TryRead(entry, buffer))
            {
                break;
            }

            var events = BinaryPrimitives.ReadInt16LittleEndian(buffer[4..]);
            var revents = (short)(events & (POLLIN | POLLOUT));
            BinaryPrimitives.WriteInt16LittleEndian(buffer[6..], revents);
            if (revents != 0)
            {
                ready++;
            }

            _ = ctx.Memory.TryWrite(entry, buffer);
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)ready);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "T8fER+tIGgk", ExportName = "select",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixSelect(CpuContext ctx)
    {
        // nfds in Rdi; the fd_sets are left as-is (all reported ready) and the
        // ready count returned is nfds so callers proceed without blocking.
        var nfds = unchecked((int)ctx[CpuRegister.Rdi]);
        ctx[CpuRegister.Rax] = unchecked((ulong)Math.Max(nfds, 0));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ---- Asynchronous I/O (executed synchronously) ----

    [SysAbiExport(Nid = "HgX7+AORI58", ExportName = "sceKernelAioSubmitReadCommands",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitReadCommands(CpuContext ctx) => KernelAioSubmit(ctx, write: false);

    [SysAbiExport(Nid = "lXT0m3P-vs4", ExportName = "sceKernelAioSubmitReadCommandsMultiple",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitReadCommandsMultiple(CpuContext ctx) => KernelAioSubmit(ctx, write: false);

    [SysAbiExport(Nid = "XQ8C8y+de+E", ExportName = "sceKernelAioSubmitWriteCommands",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioSubmitWriteCommands(CpuContext ctx) => KernelAioSubmit(ctx, write: true);

    private static int KernelAioSubmit(CpuContext ctx, bool write)
    {
        var requestsAddress = ctx[CpuRegister.Rdi];
        var count = unchecked((int)ctx[CpuRegister.Rsi]);
        var outIdAddress = ctx[CpuRegister.Rcx];
        if (requestsAddress == 0 || count <= 0 || count > 0x10000)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        // Each SceKernelAioRWRequest: offset(8) nbyte(8) buf(8) result(8) fd(4).
        Span<byte> request = stackalloc byte[SizeofAioRwRequest];
        Span<byte> result = stackalloc byte[16];
        for (var i = 0; i < count; i++)
        {
            var entry = requestsAddress + (ulong)(i * SizeofAioRwRequest);
            if (!ctx.Memory.TryRead(entry, request))
            {
                break;
            }

            var offset = BinaryPrimitives.ReadInt64LittleEndian(request[0..]);
            var nbyte = (int)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(request[8..]), int.MaxValue);
            var buf = BinaryPrimitives.ReadUInt64LittleEndian(request[16..]);
            var resultPtr = BinaryPrimitives.ReadUInt64LittleEndian(request[24..]);
            var fd = BinaryPrimitives.ReadInt32LittleEndian(request[32..]);

            long transferred = KernelAioTransfer(ctx, fd, offset, nbyte, buf, write);
            if (resultPtr != 0)
            {
                BinaryPrimitives.WriteInt64LittleEndian(result[0..], transferred);
                BinaryPrimitives.WriteUInt32LittleEndian(result[8..], AioStateCompleted);
                _ = ctx.Memory.TryWrite(resultPtr, result);
            }
        }

        var submitId = unchecked((uint)Interlocked.Increment(ref _nextAioSubmitId));
        _aioResults[submitId] = 0;
        if (outIdAddress != 0)
        {
            Span<byte> idBuffer = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(idBuffer, submitId);
            _ = ctx.Memory.TryWrite(outIdAddress, idBuffer);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static long KernelAioTransfer(CpuContext ctx, int fd, long offset, int nbyte, ulong buf, bool write)
    {
        if (nbyte <= 0 || buf == 0 || offset < 0)
        {
            return 0;
        }

        var stream = GetOpenFile(fd);
        if (stream is null)
        {
            return -1;
        }

        var scratch = GC.AllocateUninitializedArray<byte>(nbyte);
        try
        {
            if (write)
            {
                if (!ctx.Memory.TryRead(buf, scratch))
                {
                    return -1;
                }

                RandomAccess.Write(stream.SafeFileHandle, scratch, offset);
                return nbyte;
            }

            var read = RandomAccess.Read(stream.SafeFileHandle, scratch, offset);
            if (read > 0 && !ctx.Memory.TryWrite(buf, scratch.AsSpan(0, read)))
            {
                return -1;
            }

            return read;
        }
        catch (IOException)
        {
            return -1;
        }
    }

    [SysAbiExport(Nid = "o7O4z3jwKzo", ExportName = "sceKernelAioPollRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioPollRequests(CpuContext ctx) => KernelAioComplete(ctx);

    [SysAbiExport(Nid = "lgK+oIWkJyA", ExportName = "sceKernelAioWaitRequests",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioWaitRequests(CpuContext ctx) => KernelAioComplete(ctx);

    private static int KernelAioComplete(CpuContext ctx)
    {
        // Submission already performed the I/O synchronously, so every request
        // reports completed. Rsi points at the state-out array, Rdx = count.
        var statesAddress = ctx[CpuRegister.Rsi];
        var count = unchecked((int)ctx[CpuRegister.Rdx]);
        if (statesAddress != 0 && count > 0 && count <= 0x10000)
        {
            Span<byte> state = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(state, AioStateCompleted);
            for (var i = 0; i < count; i++)
            {
                _ = ctx.Memory.TryWrite(statesAddress + (ulong)(i * sizeof(uint)), state);
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "fR521KIGgb8", ExportName = "sceKernelAioCancelRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioCancelRequest(CpuContext ctx)
    {
        // Already completed; report the completed state if an out pointer given.
        var stateAddress = ctx[CpuRegister.Rsi];
        if (stateAddress != 0)
        {
            Span<byte> state = stackalloc byte[sizeof(uint)];
            BinaryPrimitives.WriteUInt32LittleEndian(state, AioStateCompleted);
            _ = ctx.Memory.TryWrite(stateAddress, state);
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(Nid = "5TgME6AYty4", ExportName = "sceKernelAioDeleteRequest",
        Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelAioDeleteRequest(CpuContext ctx)
    {
        var submitId = unchecked((uint)ctx[CpuRegister.Rdi]);
        _aioResults.TryRemove(submitId, out _);
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
