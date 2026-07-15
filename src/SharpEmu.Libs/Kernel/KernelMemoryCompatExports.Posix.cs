// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using System.Buffers.Binary;

namespace SharpEmu.Libs.Kernel;

// POSIX-named libkernel exports (and their sceKernel* twins) that games import
// alongside the underscore-prefixed syscall wrappers: positional file I/O,
// vectored I/O, path mutation, mmap, and small process-level queries.
//
// Convention: POSIX-named exports fail by setting errno and returning -1 in
// RAX (the libkernel "posix" flavor); sceKernel* exports fail by returning an
// SCE error code in RAX.
public static partial class KernelMemoryCompatExports
{
    private const int Eperm = 1;
    private const int Enoent = 2;
    private const int Ebadf = 9;
    private const int Eacces = 13;
    private const int Eexist = 17;
    private const int Enotdir = 20;
    private const int Eisdir = 21;
    private const int Enotempty = 66;

    private const int PosixFOk = 0x0;
    private const int PosixWOk = 0x2;

    private const ulong PosixMapFixed = 0x10;
    private const ulong PosixMapAnonymous = 0x1000;

    private const int MaxIoVectorCount = 1024;
    private const int MmapCopyChunkSize = 1 << 20;

    private static int PosixFail(CpuContext ctx, int errno)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }

    private static int PosixOk(CpuContext ctx, ulong result = 0)
    {
        ctx[CpuRegister.Rax] = result;
        return 0;
    }

    private static bool TryGetOpenFileStream(int fd, out FileStream stream)
    {
        FileStream? found;
        lock (_fdGate)
        {
            _openFiles.TryGetValue(fd, out found);
        }

        stream = found!;
        return found is not null;
    }

    private static int TranslateOrbisResultToErrno(int result) => result switch
    {
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED => Eacces,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND => Enoent,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT => Einval,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_ALREADY_EXISTS => Eexist,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT => Efault,
        (int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY => Enotempty,
        _ => Einval,
    };

    private static int PosixWrapOrbisCall(CpuContext ctx, Func<CpuContext, int> orbisHandler)
    {
        var result = orbisHandler(ctx);
        if (result == (int)OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return 0;
        }

        return PosixFail(ctx, TranslateOrbisResultToErrno(result));
    }

    // ------------------------------------------------------------------
    // Positional file I/O
    // ------------------------------------------------------------------

    private static OrbisGen2Result PreadCore(CpuContext ctx, int fd, ulong bufferAddress, ulong requestedRaw, long offset, out int errno, out long bytesRead)
    {
        errno = 0;
        bytesRead = 0;
        var requested = (int)Math.Min(requestedRaw, int.MaxValue);
        if (offset < 0 || (requested > 0 && bufferAddress == 0))
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetOpenFileStream(fd, out var stream))
        {
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (requested == 0)
        {
            return OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var buffer = GC.AllocateUninitializedArray<byte>(requested);
        int read;
        try
        {
            // Positional read: never disturbs the descriptor's file offset.
            read = RandomAccess.Read(stream.SafeFileHandle, buffer.AsSpan(0, requested), offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            LogIoTrace("pread", stream.Name, $"fd={fd} req={requested} offset={offset} result=io_error ex={ex.Message}");
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (read > 0 && !ctx.Memory.TryWrite(bufferAddress, buffer.AsSpan(0, read)))
        {
            errno = Efault;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        LogIoTrace("pread", stream.Name, $"fd={fd} req={requested} offset={offset} read={read}");
        bytesRead = read;
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static OrbisGen2Result PwriteCore(CpuContext ctx, int fd, ulong bufferAddress, ulong requestedRaw, long offset, out int errno, out long bytesWritten)
    {
        errno = 0;
        bytesWritten = 0;
        var requested = (int)Math.Min(requestedRaw, int.MaxValue);
        if (offset < 0 || (requested > 0 && bufferAddress == 0))
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetOpenFileStream(fd, out var stream))
        {
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (requested == 0)
        {
            return OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var payload = GC.AllocateUninitializedArray<byte>(requested);
        if (!ctx.Memory.TryRead(bufferAddress, payload.AsSpan(0, requested)))
        {
            errno = Efault;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        try
        {
            RandomAccess.Write(stream.SafeFileHandle, payload.AsSpan(0, requested), offset);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            LogIoTrace("pwrite", stream.Name, $"fd={fd} req={requested} offset={offset} result=io_error ex={ex.Message}");
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        LogIoTrace("pwrite", stream.Name, $"fd={fd} req={requested} offset={offset} written={requested}");
        bytesWritten = requested;
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ezv-RSBNKqI",
        ExportName = "pread",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPread(CpuContext ctx)
    {
        var result = PreadCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            unchecked((long)ctx[CpuRegister.Rcx]),
            out var errno,
            out var bytesRead);
        return result == OrbisGen2Result.ORBIS_GEN2_OK
            ? PosixOk(ctx, unchecked((ulong)bytesRead))
            : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "+r3rMFwItV4",
        ExportName = "sceKernelPread",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPread(CpuContext ctx)
    {
        var result = PreadCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            unchecked((long)ctx[CpuRegister.Rcx]),
            out _,
            out var bytesRead);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)bytesRead);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "C2kJ-byS5rM",
        ExportName = "pwrite",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPwrite(CpuContext ctx)
    {
        var result = PwriteCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            unchecked((long)ctx[CpuRegister.Rcx]),
            out var errno,
            out var bytesWritten);
        return result == OrbisGen2Result.ORBIS_GEN2_OK
            ? PosixOk(ctx, unchecked((ulong)bytesWritten))
            : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "nKWi-N2HBV4",
        ExportName = "sceKernelPwrite",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelPwrite(CpuContext ctx)
    {
        var result = PwriteCore(
            ctx,
            unchecked((int)ctx[CpuRegister.Rdi]),
            ctx[CpuRegister.Rsi],
            ctx[CpuRegister.Rdx],
            unchecked((long)ctx[CpuRegister.Rcx]),
            out _,
            out var bytesWritten);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = unchecked((ulong)bytesWritten);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ------------------------------------------------------------------
    // Vectored I/O
    // ------------------------------------------------------------------

    private static bool TryReadIoVector(CpuContext ctx, ulong iovAddress, int index, out ulong baseAddress, out ulong length)
    {
        baseAddress = 0;
        length = 0;
        var entryAddress = iovAddress + ((ulong)index * 16UL);
        return ctx.TryReadUInt64(entryAddress, out baseAddress) &&
            ctx.TryReadUInt64(entryAddress + sizeof(ulong), out length);
    }

    [SysAbiExport(
        Nid = "I7ImcLds-uU",
        ExportName = "readv",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixReadv(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var iovAddress = ctx[CpuRegister.Rsi];
        var iovCount = unchecked((int)ctx[CpuRegister.Rdx]);
        if (iovCount < 0 || iovCount > MaxIoVectorCount || (iovCount > 0 && iovAddress == 0))
        {
            return PosixFail(ctx, Einval);
        }

        if (!TryGetOpenFileStream(fd, out var stream))
        {
            return PosixFail(ctx, Ebadf);
        }

        long total = 0;
        for (var i = 0; i < iovCount; i++)
        {
            if (!TryReadIoVector(ctx, iovAddress, i, out var baseAddress, out var lengthRaw))
            {
                return PosixFail(ctx, Efault);
            }

            var length = (int)Math.Min(lengthRaw, int.MaxValue);
            if (length == 0)
            {
                continue;
            }

            var buffer = GC.AllocateUninitializedArray<byte>(length);
            int read;
            try
            {
                read = stream.Read(buffer, 0, length);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                LogIoTrace("readv", stream.Name, $"fd={fd} iov={i} result=io_error ex={ex.Message}");
                return PosixFail(ctx, Einval);
            }

            if (read > 0 && !ctx.Memory.TryWrite(baseAddress, buffer.AsSpan(0, read)))
            {
                return PosixFail(ctx, Efault);
            }

            total += read;
            if (read < length)
            {
                break;
            }
        }

        LogIoTrace("readv", stream.Name, $"fd={fd} iovcnt={iovCount} read={total}");
        return PosixOk(ctx, unchecked((ulong)total));
    }

    [SysAbiExport(
        Nid = "Z2aKdxzS4KE",
        ExportName = "writev",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixWritev(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var iovAddress = ctx[CpuRegister.Rsi];
        var iovCount = unchecked((int)ctx[CpuRegister.Rdx]);
        if (iovCount < 0 || iovCount > MaxIoVectorCount || (iovCount > 0 && iovAddress == 0))
        {
            return PosixFail(ctx, Einval);
        }

        var isConsole = fd is 1 or 2;
        FileStream? stream = null;
        if (!isConsole && !TryGetOpenFileStream(fd, out stream!))
        {
            return PosixFail(ctx, Ebadf);
        }

        long total = 0;
        for (var i = 0; i < iovCount; i++)
        {
            if (!TryReadIoVector(ctx, iovAddress, i, out var baseAddress, out var lengthRaw))
            {
                return PosixFail(ctx, Efault);
            }

            var length = (int)Math.Min(lengthRaw, int.MaxValue);
            if (length == 0)
            {
                continue;
            }

            var payload = GC.AllocateUninitializedArray<byte>(length);
            if (!ctx.Memory.TryRead(baseAddress, payload.AsSpan(0, length)))
            {
                return PosixFail(ctx, Efault);
            }

            if (isConsole)
            {
                var text = System.Text.Encoding.UTF8.GetString(payload, 0, length);
                if (fd == 1)
                {
                    Console.Out.Write(text);
                }
                else
                {
                    Console.Error.Write(text);
                }
            }
            else
            {
                try
                {
                    stream!.Write(payload, 0, length);
                }
                catch (Exception ex) when (ex is IOException or NotSupportedException)
                {
                    LogIoTrace("writev", stream!.Name, $"fd={fd} iov={i} result=io_error ex={ex.Message}");
                    return PosixFail(ctx, Ebadf);
                }
            }

            total += length;
        }

        if (isConsole)
        {
            (fd == 1 ? Console.Out : Console.Error).Flush();
        }
        else
        {
            stream!.Flush();
        }

        return PosixOk(ctx, unchecked((ulong)total));
    }

    // ------------------------------------------------------------------
    // fsync / ftruncate / truncate
    // ------------------------------------------------------------------

    private static OrbisGen2Result FsyncCore(int fd, out int errno)
    {
        errno = 0;
        if (!TryGetOpenFileStream(fd, out var stream))
        {
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            if (stream.CanWrite)
            {
                stream.Flush(flushToDisk: true);
            }
        }
        catch (IOException)
        {
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        }

        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "juWbTNM+8hw",
        ExportName = "fsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFsync(CpuContext ctx)
    {
        var result = FsyncCore(unchecked((int)ctx[CpuRegister.Rdi]), out var errno);
        return result == OrbisGen2Result.ORBIS_GEN2_OK ? PosixOk(ctx) : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "KIbJFQ0I1Cg",
        ExportName = "fdatasync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFdatasync(CpuContext ctx) => PosixFsync(ctx);

    [SysAbiExport(
        Nid = "fTx66l5iWIA",
        ExportName = "sceKernelFsync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelFsync(CpuContext ctx)
    {
        var result = FsyncCore(unchecked((int)ctx[CpuRegister.Rdi]), out _);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static OrbisGen2Result FtruncateCore(int fd, long length, out int errno)
    {
        errno = 0;
        if (length < 0)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryGetOpenFileStream(fd, out var stream))
        {
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        if (!stream.CanWrite)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        try
        {
            stream.SetLength(length);
        }
        catch (IOException)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        LogIoTrace("ftruncate", stream.Name, $"fd={fd} length={length}");
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ih4CD9-gghM",
        ExportName = "ftruncate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFtruncate(CpuContext ctx)
    {
        var result = FtruncateCore(
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((long)ctx[CpuRegister.Rsi]),
            out var errno);
        return result == OrbisGen2Result.ORBIS_GEN2_OK ? PosixOk(ctx) : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "VW3TVZiM4-E",
        ExportName = "sceKernelFtruncate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelFtruncate(CpuContext ctx)
    {
        var result = FtruncateCore(
            unchecked((int)ctx[CpuRegister.Rdi]),
            unchecked((long)ctx[CpuRegister.Rsi]),
            out _);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static OrbisGen2Result TruncateCore(CpuContext ctx, ulong pathAddress, long length, out int errno)
    {
        errno = 0;
        if (length < 0 || pathAddress == 0)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            errno = Efault;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            errno = Eacces;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        if (Directory.Exists(hostPath))
        {
            errno = Eisdir;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!File.Exists(hostPath))
        {
            errno = Enoent;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        try
        {
            using var stream = new FileStream(hostPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            stream.SetLength(length);
        }
        catch (UnauthorizedAccessException)
        {
            errno = Eacces;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        }

        InvalidateAprFileSizeCache(hostPath);
        LogIoTrace("truncate", guestPath, $"host='{hostPath}' length={length}");
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "ayrtszI7GBg",
        ExportName = "truncate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixTruncate(CpuContext ctx)
    {
        var result = TruncateCore(ctx, ctx[CpuRegister.Rdi], unchecked((long)ctx[CpuRegister.Rsi]), out var errno);
        return result == OrbisGen2Result.ORBIS_GEN2_OK ? PosixOk(ctx) : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "WlyEA-sLDf0",
        ExportName = "sceKernelTruncate",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelTruncate(CpuContext ctx)
    {
        var result = TruncateCore(ctx, ctx[CpuRegister.Rdi], unchecked((long)ctx[CpuRegister.Rsi]), out _);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ------------------------------------------------------------------
    // access / rename
    // ------------------------------------------------------------------

    [SysAbiExport(
        Nid = "8vE6Z6VEYyk",
        ExportName = "access",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixAccess(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var mode = unchecked((int)ctx[CpuRegister.Rsi]);
        if (pathAddress == 0)
        {
            return PosixFail(ctx, Einval);
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            return PosixFail(ctx, Efault);
        }

        var hostPath = ResolveGuestPath(guestPath);
        var exists = File.Exists(hostPath) || Directory.Exists(hostPath);
        LogIoTrace("access", guestPath, $"host='{hostPath}' mode=0x{mode:X} exists={(exists ? 1 : 0)}");
        if (!exists)
        {
            return PosixFail(ctx, Enoent);
        }

        if ((mode & PosixWOk) != 0 && IsReadOnlyGuestMutationPath(guestPath))
        {
            return PosixFail(ctx, Eacces);
        }

        return PosixOk(ctx);
    }

    private static OrbisGen2Result RenameCore(CpuContext ctx, ulong fromAddress, ulong toAddress, out int errno)
    {
        errno = 0;
        if (fromAddress == 0 || toAddress == 0)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, fromAddress, MaxGuestStringLength, out var fromGuestPath) ||
            !TryReadNullTerminatedUtf8(ctx, toAddress, MaxGuestStringLength, out var toGuestPath))
        {
            errno = Efault;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var fromHostPath = ResolveGuestPath(fromGuestPath);
        var toHostPath = ResolveGuestPath(toGuestPath);
        if (IsReadOnlyGuestMutationPath(fromGuestPath) || IsReadOnlyGuestMutationPath(toGuestPath))
        {
            LogIoTrace("rename", fromGuestPath, $"to='{toGuestPath}' result=readonly");
            errno = Eacces;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        try
        {
            if (Directory.Exists(fromHostPath))
            {
                if (File.Exists(toHostPath))
                {
                    errno = Enotdir;
                    return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                if (Directory.Exists(toHostPath))
                {
                    // POSIX only allows replacing an empty directory.
                    if (Directory.EnumerateFileSystemEntries(toHostPath).Any())
                    {
                        errno = Enotempty;
                        return OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
                    }

                    Directory.Delete(toHostPath, recursive: false);
                }

                Directory.Move(fromHostPath, toHostPath);
            }
            else if (File.Exists(fromHostPath))
            {
                if (Directory.Exists(toHostPath))
                {
                    errno = Eisdir;
                    return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                }

                File.Move(fromHostPath, toHostPath, overwrite: true);
            }
            else
            {
                AddNegativeStatCacheForGuestPath(fromGuestPath);
                errno = Enoent;
                return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }
        }
        catch (UnauthorizedAccessException)
        {
            errno = Eacces;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }
        catch (IOException ex)
        {
            LogIoTrace("rename", fromGuestPath, $"to='{toGuestPath}' result=io_error ex={ex.Message}");
            errno = Eacces;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY;
        }

        InvalidateNegativeStatCacheForPathAndAncestors(fromGuestPath);
        InvalidateNegativeStatCacheForPathAndAncestors(toGuestPath);
        AddNegativeStatCacheForGuestPath(fromGuestPath);
        InvalidateAprFileSizeCache(fromHostPath);
        InvalidateAprFileSizeCache(toHostPath);
        LogIoTrace("rename", fromGuestPath, $"to='{toGuestPath}' host_from='{fromHostPath}' host_to='{toHostPath}'");
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "NN01qLRhiqU",
        ExportName = "rename",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixRename(CpuContext ctx)
    {
        var result = RenameCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out var errno);
        return result == OrbisGen2Result.ORBIS_GEN2_OK ? PosixOk(ctx) : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "52NcYU9+lEo",
        ExportName = "sceKernelRename",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelRename(CpuContext ctx)
    {
        var result = RenameCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out _);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ------------------------------------------------------------------
    // POSIX aliases of existing path-mutation exports
    // ------------------------------------------------------------------

    [SysAbiExport(
        Nid = "JGMio+21L4c",
        ExportName = "mkdir",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMkdir(CpuContext ctx) => PosixWrapOrbisCall(ctx, KernelMkdir);

    [SysAbiExport(
        Nid = "c7ZnT7V1B98",
        ExportName = "rmdir",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixRmdir(CpuContext ctx) => PosixWrapOrbisCall(ctx, KernelRmdir);

    [SysAbiExport(
        Nid = "VAzswvTOCzI",
        ExportName = "unlink",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixUnlink(CpuContext ctx) => PosixWrapOrbisCall(ctx, KernelUnlink);

    // ------------------------------------------------------------------
    // mmap family
    // ------------------------------------------------------------------

    private static OrbisGen2Result MmapCore(
        CpuContext ctx,
        ulong requestedAddress,
        ulong length,
        int protection,
        ulong flags,
        int fd,
        long offset,
        out int errno,
        out ulong mappedAddress)
    {
        errno = 0;
        mappedAddress = 0;
        if (length == 0 || offset < 0)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var anonymous = (flags & PosixMapAnonymous) != 0 || fd == -1;
        FileStream? stream = null;
        if (!anonymous && !TryGetOpenFileStream(fd, out stream!))
        {
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        lock (_memoryGate)
        {
            var fixedMapping = (flags & PosixMapFixed) != 0;
            var desiredAddress = requestedAddress != 0
                ? requestedAddress
                : AlignUp(_nextVirtualAddress == 0 ? 0x1_0000_0000UL : _nextVirtualAddress, OrbisPageSize);

            if (fixedMapping && requestedAddress != 0)
            {
                mappedAddress = requestedAddress;
                if (!IsGuestRangeBacked(ctx, requestedAddress, length))
                {
                    TryReserveExactGuestVirtualRange(ctx, requestedAddress, length, protection);
                    if (!IsGuestRangeBacked(ctx, requestedAddress, length))
                    {
                        mappedAddress = 0;
                    }
                }
            }
            else if (!TryReserveGuestVirtualRange(ctx, desiredAddress, length, protection, OrbisPageSize, out mappedAddress))
            {
                mappedAddress = AllocateMappedGuestAddress(ctx, length, OrbisPageSize);
            }

            if (mappedAddress == 0)
            {
                errno = Einval;
                return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            _nextVirtualAddress = Math.Max(_nextVirtualAddress, mappedAddress + length);
            _mappedRegions[mappedAddress] = new MappedRegion(
                mappedAddress,
                length,
                protection,
                IsFlexible: false,
                IsDirect: false,
                DirectStart: 0);
        }

        if (!anonymous)
        {
            CopyFileIntoMappedRange(ctx, stream!, offset, mappedAddress, length);
        }

        if (ShouldTraceDirectMemory())
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] mmap: mapped=0x{mappedAddress:X16} len=0x{length:X} prot=0x{protection:X} " +
                $"flags=0x{flags:X} fd={fd} offset=0x{offset:X}");
        }

        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // Private mappings get a one-shot copy of the file contents; guests use
    // read-only file maps for data archives, so copy-on-map is behaviorally
    // equivalent minus lazy faulting.
    private static void CopyFileIntoMappedRange(CpuContext ctx, FileStream stream, long offset, ulong mappedAddress, ulong length)
    {
        long fileLength;
        try
        {
            fileLength = RandomAccess.GetLength(stream.SafeFileHandle);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            LogIoTrace("mmap", stream.Name, $"result=length_error ex={ex.Message}");
            return;
        }

        if (offset >= fileLength)
        {
            return;
        }

        var remaining = (ulong)Math.Min((ulong)(fileLength - offset), length);
        var cursorOffset = offset;
        var cursorAddress = mappedAddress;
        var chunk = GC.AllocateUninitializedArray<byte>((int)Math.Min(remaining, MmapCopyChunkSize));
        while (remaining > 0)
        {
            var take = (int)Math.Min((ulong)chunk.Length, remaining);
            int read;
            try
            {
                read = RandomAccess.Read(stream.SafeFileHandle, chunk.AsSpan(0, take), cursorOffset);
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                LogIoTrace("mmap", stream.Name, $"result=read_error offset={cursorOffset} ex={ex.Message}");
                return;
            }

            if (read <= 0 || !TryWriteCompat(ctx, cursorAddress, chunk.AsSpan(0, read)))
            {
                return;
            }

            cursorOffset += read;
            cursorAddress += (ulong)read;
            remaining -= (ulong)read;
        }
    }

    [SysAbiExport(
        Nid = "BPE9s9vQQXo",
        ExportName = "mmap",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMmap(CpuContext ctx)
    {
        var result = MmapCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            unchecked((int)ctx[CpuRegister.Rdx]),
            ctx[CpuRegister.Rcx],
            unchecked((int)ctx[CpuRegister.R8]),
            unchecked((long)ctx[CpuRegister.R9]),
            out var errno,
            out var mappedAddress);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            // MAP_FAILED is -1, which PosixFail already leaves in RAX.
            return PosixFail(ctx, errno);
        }

        ctx[CpuRegister.Rax] = mappedAddress;
        return 0;
    }

    [SysAbiExport(
        Nid = "PGhQHd-dzv8",
        ExportName = "sceKernelMmap",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelMmap(CpuContext ctx)
    {
        // int sceKernelMmap(void* addr, size_t len, int prot, int flags, int fd,
        //                   off_t offset, void** res) — the seventh argument
        //                   lives on the guest stack above the return address.
        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var resultAddress) || resultAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var result = MmapCore(
            ctx,
            ctx[CpuRegister.Rdi],
            ctx[CpuRegister.Rsi],
            unchecked((int)ctx[CpuRegister.Rdx]),
            ctx[CpuRegister.Rcx],
            unchecked((int)ctx[CpuRegister.R8]),
            unchecked((long)ctx[CpuRegister.R9]),
            out _,
            out var mappedAddress);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        if (!ctx.TryWriteUInt64(resultAddress, mappedAddress))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "UqDGjXA5yUM",
        ExportName = "munmap",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMunmap(CpuContext ctx)
    {
        var address = ctx[CpuRegister.Rdi];
        var length = ctx[CpuRegister.Rsi];
        if (address == 0 || length == 0)
        {
            return PosixFail(ctx, Einval);
        }

        lock (_memoryGate)
        {
            if (_mappedRegions.TryGetValue(address, out var mappedRegion) && mappedRegion.Length == length)
            {
                _mappedRegions.Remove(address);
                if (mappedRegion.IsFlexible)
                {
                    _allocatedFlexibleBytes = mappedRegion.Length >= _allocatedFlexibleBytes
                        ? 0
                        : _allocatedFlexibleBytes - mappedRegion.Length;
                }
            }
        }

        // Unmapping a range this HLE never tracked still succeeds: the guest is
        // releasing address space, not asking a question.
        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "tZY4+SZNFhA",
        ExportName = "msync",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMsync(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "Jahsnh4KKkg",
        ExportName = "madvise",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMadvise(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "yYiyDoU2IEs",
        ExportName = "posix_madvise",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMadvisePosix(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "mTBZfEal2Bw",
        ExportName = "mlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMlock(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "OG4RsDwLguo",
        ExportName = "munlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixMunlock(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "k+AXqu2-eBc",
        ExportName = "getpagesize",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetpagesize(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = OrbisPageSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ------------------------------------------------------------------
    // Signals / scheduling / system identity
    // ------------------------------------------------------------------

    private static int SigprocmaskCore(CpuContext ctx, int how, ulong oldSetAddress)
    {
        // SIG_BLOCK=1, SIG_UNBLOCK=2, SIG_SETMASK=3 (FreeBSD numbering).
        if (how is < 1 or > 3)
        {
            return PosixFail(ctx, Einval);
        }

        if (oldSetAddress != 0)
        {
            // Report an empty mask; the HLE kernel never blocks guest signals.
            Span<byte> emptyMask = stackalloc byte[16];
            emptyMask.Clear();
            if (!ctx.Memory.TryWrite(oldSetAddress, emptyMask))
            {
                return PosixFail(ctx, Efault);
            }
        }

        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "aPcyptbOiZs",
        ExportName = "sigprocmask",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSigprocmask(CpuContext ctx) =>
        SigprocmaskCore(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rdx]);

    [SysAbiExport(
        Nid = "JZKw5+Wrnaw",
        ExportName = "pthread_sigmask",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadSigmask(CpuContext ctx) =>
        SigprocmaskCore(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rdx]);

    [SysAbiExport(
        Nid = "6XG4B33N09g",
        ExportName = "sched_yield",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSchedYield(CpuContext ctx)
    {
        GuestThreadExecution.Scheduler?.Pump(ctx, "sched_yield");
        Thread.Yield();
        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "mpxAdqW7dKY",
        ExportName = "sceKernelIsProspero",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelIsProspero(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 1;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "VOx8NGmHXTs",
        ExportName = "sceKernelGetCpumode",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetCpumode(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "Mv1zUObHvXI",
        ExportName = "sceKernelGetSystemSwVersion",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetSystemSwVersion(CpuContext ctx)
    {
        // struct { uint64_t size; char version_string[28]; uint32_t version; }
        var infoAddress = ctx[CpuRegister.Rdi];
        if (infoAddress == 0)
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        Span<byte> payload = stackalloc byte[0x28];
        payload.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(payload, 0x28);
        var versionText = "9.008.001"u8;
        versionText.CopyTo(payload.Slice(8, versionText.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(payload[0x24..], 0x09008001);
        if (!TryWriteCompat(ctx, infoAddress, payload))
        {
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ------------------------------------------------------------------
    // Descriptor duplication: dup / dup2 / fcntl
    // ------------------------------------------------------------------

    private const int FcntlDupFd = 0;
    private const int FcntlGetFd = 1;
    private const int FcntlSetFd = 2;
    private const int FcntlGetFl = 3;
    private const int FcntlSetFl = 4;
    private const int FcntlDupFdCloexec = 17;

    private static bool TryDuplicateDescriptor(int fd, out int newFd)
    {
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(fd, out var stream))
            {
                newFd = -1;
                return false;
            }

            newFd = AllocateGuestFileDescriptor();
            _openFiles[newFd] = stream;
            return true;
        }
    }

    [SysAbiExport(
        Nid = "iiQjzvfWDq0",
        ExportName = "dup",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixDup(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!TryDuplicateDescriptor(fd, out var newFd))
        {
            return PosixFail(ctx, Ebadf);
        }

        LogIoTrace("dup", $"fd:{fd}", $"new_fd={newFd}");
        return PosixOk(ctx, unchecked((ulong)newFd));
    }

    [SysAbiExport(
        Nid = "wdUufa9g-D8",
        ExportName = "dup2",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixDup2(CpuContext ctx)
    {
        var oldFd = unchecked((int)ctx[CpuRegister.Rdi]);
        var newFd = unchecked((int)ctx[CpuRegister.Rsi]);
        if (newFd < 0)
        {
            return PosixFail(ctx, Ebadf);
        }

        FileStream? displaced = null;
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(oldFd, out var stream))
            {
                return PosixFail(ctx, Ebadf);
            }

            if (newFd == oldFd)
            {
                return PosixOk(ctx, unchecked((ulong)newFd));
            }

            // Std descriptors keep their console routing (_read/_write check
            // fd 0/1/2 before this table), so aliasing onto them is a no-op.
            if (newFd is 0 or 1 or 2)
            {
                return PosixOk(ctx, unchecked((ulong)newFd));
            }

            if (_openFiles.Remove(newFd, out var existing) &&
                !ReferenceEquals(existing, stream) &&
                !_openFiles.ContainsValue(existing))
            {
                displaced = existing;
            }

            _openDirectories.Remove(newFd);
            _openFiles[newFd] = stream;
        }

        displaced?.Dispose();
        LogIoTrace("dup2", $"fd:{oldFd}", $"new_fd={newFd}");
        return PosixOk(ctx, unchecked((ulong)newFd));
    }

    [SysAbiExport(
        Nid = "8nY19bKoiZk",
        ExportName = "fcntl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFcntl(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        var command = unchecked((int)ctx[CpuRegister.Rsi]);
        switch (command)
        {
            case FcntlDupFd:
            case FcntlDupFdCloexec:
                if (!TryDuplicateDescriptor(fd, out var newFd))
                {
                    return PosixFail(ctx, Ebadf);
                }

                return PosixOk(ctx, unchecked((ulong)newFd));

            case FcntlGetFd:
            case FcntlSetFd:
            case FcntlSetFl:
                if (!TryGetOpenFileStream(fd, out _) && !KernelSocketCompatExports.IsEmulatedSocketFd(fd))
                {
                    return PosixFail(ctx, Ebadf);
                }

                return PosixOk(ctx);

            case FcntlGetFl:
                if (TryGetOpenFileStream(fd, out var stream))
                {
                    var flags = stream.CanRead && stream.CanWrite
                        ? O_RDWR
                        : stream.CanWrite
                            ? O_WRONLY
                            : 0;
                    return PosixOk(ctx, unchecked((ulong)flags));
                }

                if (KernelSocketCompatExports.IsEmulatedSocketFd(fd))
                {
                    return PosixOk(ctx, O_RDWR);
                }

                return PosixFail(ctx, Ebadf);

            default:
                LogIoTrace("fcntl", $"fd:{fd}", $"cmd={command} result=unsupported");
                return PosixFail(ctx, Einval);
        }
    }

    // ------------------------------------------------------------------
    // Working directory bookkeeping
    // ------------------------------------------------------------------

    private static readonly object _cwdGate = new();
    private static string _guestWorkingDirectory = "/app0";

    private static string NormalizeGuestAbsolutePath(string path, string currentDirectory)
    {
        var combined = path.StartsWith('/') ? path : currentDirectory + "/" + path;
        var segments = new List<string>();
        foreach (var segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            switch (segment)
            {
                case ".":
                    break;
                case "..":
                    if (segments.Count > 0)
                    {
                        segments.RemoveAt(segments.Count - 1);
                    }

                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return "/" + string.Join('/', segments);
    }

    [SysAbiExport(
        Nid = "DYivN1nO-JQ",
        ExportName = "getcwd",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetcwd(CpuContext ctx)
    {
        var bufferAddress = ctx[CpuRegister.Rdi];
        var bufferSize = ctx[CpuRegister.Rsi];
        if (bufferAddress == 0)
        {
            return PosixFail(ctx, Einval);
        }

        string currentDirectory;
        lock (_cwdGate)
        {
            currentDirectory = _guestWorkingDirectory;
        }

        var payload = System.Text.Encoding.UTF8.GetBytes(currentDirectory);
        if ((ulong)payload.Length + 1 > bufferSize)
        {
            return PosixFail(ctx, Erange);
        }

        Span<byte> terminated = stackalloc byte[payload.Length + 1];
        payload.CopyTo(terminated);
        terminated[^1] = 0;
        if (!ctx.Memory.TryWrite(bufferAddress, terminated))
        {
            return PosixFail(ctx, Efault);
        }

        return PosixOk(ctx, bufferAddress);
    }

    [SysAbiExport(
        Nid = "6mMQ1MSPW-Q",
        ExportName = "chdir",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixChdir(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        if (pathAddress == 0)
        {
            return PosixFail(ctx, Einval);
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath) ||
            guestPath.Length == 0)
        {
            return PosixFail(ctx, pathAddress == 0 ? Einval : Efault);
        }

        string normalized;
        lock (_cwdGate)
        {
            normalized = NormalizeGuestAbsolutePath(guestPath, _guestWorkingDirectory);
        }

        var hostPath = ResolveGuestPath(normalized);
        if (!Directory.Exists(hostPath))
        {
            return PosixFail(ctx, Enoent);
        }

        lock (_cwdGate)
        {
            _guestWorkingDirectory = normalized;
        }

        LogIoTrace("chdir", guestPath, $"cwd='{normalized}'");
        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "gnyVA6Tj-ak",
        ExportName = "fchdir",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFchdir(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        lock (_fdGate)
        {
            if (!_openDirectories.ContainsKey(fd))
            {
                return PosixFail(ctx, Ebadf);
            }
        }

        // Directory descriptors only carry host paths; guest-relative path
        // resolution never consults the tracked cwd, so accepting is enough.
        return PosixOk(ctx);
    }

    // ------------------------------------------------------------------
    // Process identity, limits, sysconf
    // ------------------------------------------------------------------

    private static readonly object _umaskGate = new();
    private static int _processUmask = 0x12; // 022

    [SysAbiExport(
        Nid = "kg4x8Prhfxw",
        ExportName = "getuid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetuid(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "tvpHe5kBO4E",
        ExportName = "geteuid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGeteuid(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "AfuS23bX6kg",
        ExportName = "getgid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetgid(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "72rYuYoDTWk",
        ExportName = "getegid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetegid(CpuContext ctx) => PosixOk(ctx);

    [SysAbiExport(
        Nid = "AxUhC3zNrhk",
        ExportName = "issetugid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixIssetugid(CpuContext ctx) => PosixOk(ctx, 1);

    [SysAbiExport(
        Nid = "e6ovBo9ZvJc",
        ExportName = "getppid",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetppid(CpuContext ctx) => PosixOk(ctx, 1);

    [SysAbiExport(
        Nid = "Rb6ziJKvCCQ",
        ExportName = "umask",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixUmask(CpuContext ctx)
    {
        var requested = unchecked((int)ctx[CpuRegister.Rdi]) & 0x1FF;
        int previous;
        lock (_umaskGate)
        {
            previous = _processUmask;
            _processUmask = requested;
        }

        return PosixOk(ctx, unchecked((ulong)previous));
    }

    [SysAbiExport(
        Nid = "Wh7HbV7JFqc",
        ExportName = "getrlimit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetrlimit(CpuContext ctx)
    {
        var resource = unchecked((int)ctx[CpuRegister.Rdi]);
        var limitAddress = ctx[CpuRegister.Rsi];
        if (resource is < 0 or > 15 || limitAddress == 0)
        {
            return PosixFail(ctx, Einval);
        }

        // { rlim_cur, rlim_max } both RLIM_INFINITY.
        Span<byte> payload = stackalloc byte[16];
        BinaryPrimitives.WriteUInt64LittleEndian(payload, long.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(payload[8..], long.MaxValue);
        if (!ctx.Memory.TryWrite(limitAddress, payload))
        {
            return PosixFail(ctx, Efault);
        }

        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "4X0QwvuCfjc",
        ExportName = "setrlimit",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSetrlimit(CpuContext ctx)
    {
        var resource = unchecked((int)ctx[CpuRegister.Rdi]);
        if (resource is < 0 or > 15)
        {
            return PosixFail(ctx, Einval);
        }

        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "hHlZQUnlxSM",
        ExportName = "getrusage",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixGetrusage(CpuContext ctx)
    {
        var usageAddress = ctx[CpuRegister.Rsi];
        if (usageAddress == 0)
        {
            return PosixFail(ctx, Efault);
        }

        // struct rusage: 2 timevals + 14 longs = 0x90 bytes, all zero.
        Span<byte> payload = stackalloc byte[0x90];
        payload.Clear();
        if (!ctx.Memory.TryWrite(usageAddress, payload))
        {
            return PosixFail(ctx, Efault);
        }

        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "mkawd0NA9ts",
        ExportName = "sysconf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSysconf(CpuContext ctx)
    {
        // FreeBSD numbering.
        const int ScClkTck = 3;
        const int ScPagesize = 47;
        const int ScNprocessorsConf = 57;
        const int ScNprocessorsOnln = 58;

        var name = unchecked((int)ctx[CpuRegister.Rdi]);
        switch (name)
        {
            case ScPagesize:
                return PosixOk(ctx, OrbisPageSize);
            case ScClkTck:
                return PosixOk(ctx, 128);
            case ScNprocessorsConf:
            case ScNprocessorsOnln:
                return PosixOk(ctx, 14);
            default:
                LogIoTrace("sysconf", $"name:{name}", "result=unsupported");
                return PosixFail(ctx, Einval);
        }
    }

    // ------------------------------------------------------------------
    // Signals redux: sigaction, pthread_atfork
    // ------------------------------------------------------------------

    [SysAbiExport(
        Nid = "KiJEPEWRyUY",
        ExportName = "sigaction",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSigaction(CpuContext ctx)
    {
        var signal = unchecked((int)ctx[CpuRegister.Rdi]);
        var oldActionAddress = ctx[CpuRegister.Rdx];
        if (signal is < 1 or > 128)
        {
            return PosixFail(ctx, Einval);
        }

        if (oldActionAddress != 0)
        {
            // struct sigaction: handler + flags + mask = 32 bytes; report
            // SIG_DFL with an empty mask.
            Span<byte> emptyAction = stackalloc byte[32];
            emptyAction.Clear();
            if (!ctx.Memory.TryWrite(oldActionAddress, emptyAction))
            {
                return PosixFail(ctx, Efault);
            }
        }

        return PosixOk(ctx);
    }

    [SysAbiExport(
        Nid = "U9t5kJAWPnA",
        ExportName = "pthread_atfork",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPthreadAtfork(CpuContext ctx)
    {
        // The HLE kernel never forks, so the registered handlers can never run.
        return PosixOk(ctx);
    }

    // ------------------------------------------------------------------
    // Clock resolution
    // ------------------------------------------------------------------

    private static OrbisGen2Result ClockGetresCore(CpuContext ctx, int clockId, ulong timespecAddress, out int errno)
    {
        errno = 0;
        if (!KernelRuntimeCompatExports.ResolveClockTime(clockId, out _, out _))
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (timespecAddress != 0)
        {
            Span<byte> payload = stackalloc byte[16];
            BinaryPrimitives.WriteInt64LittleEndian(payload, 0);
            BinaryPrimitives.WriteInt64LittleEndian(payload[8..], 100); // 100 ns tick
            if (!ctx.Memory.TryWrite(timespecAddress, payload))
            {
                errno = Efault;
                return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }

        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "smIj7eqzZE8",
        ExportName = "clock_getres",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixClockGetres(CpuContext ctx)
    {
        var result = ClockGetresCore(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi], out var errno);
        return result == OrbisGen2Result.ORBIS_GEN2_OK ? PosixOk(ctx) : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "wRYVA5Zolso",
        ExportName = "sceKernelClockGetres",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClockGetres(CpuContext ctx)
    {
        var result = ClockGetresCore(ctx, unchecked((int)ctx[CpuRegister.Rdi]), ctx[CpuRegister.Rsi], out _);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // ------------------------------------------------------------------
    // poll
    // ------------------------------------------------------------------

    private const short PollIn = 0x0001;
    private const short PollOut = 0x0004;
    private const short PollRdNorm = 0x0040;
    private const short PollWrNorm = 0x0004;
    private const short PollNval = 0x0020;

    [SysAbiExport(
        Nid = "ku7D4q1Y9PI",
        ExportName = "poll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPoll(CpuContext ctx)
    {
        var fdsAddress = ctx[CpuRegister.Rdi];
        var fdCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var timeoutMilliseconds = unchecked((int)ctx[CpuRegister.Rdx]);
        if (fdCount < 0 || fdCount > MaxIoVectorCount)
        {
            return PosixFail(ctx, Einval);
        }

        if (fdCount > 0 && fdsAddress == 0)
        {
            return PosixFail(ctx, Efault);
        }

        var ready = ScanPollDescriptors(ctx, fdsAddress, fdCount, out var faulted);
        if (faulted)
        {
            return PosixFail(ctx, Efault);
        }

        if (ready == 0 && timeoutMilliseconds != 0)
        {
            // Bounded wait: never park the guest thread indefinitely here.
            var sleep = timeoutMilliseconds < 0 ? 10 : Math.Min(timeoutMilliseconds, 10);
            GuestThreadExecution.Scheduler?.Pump(ctx, "poll");
            Thread.Sleep(sleep);
            ready = ScanPollDescriptors(ctx, fdsAddress, fdCount, out faulted);
            if (faulted)
            {
                return PosixFail(ctx, Efault);
            }
        }

        return PosixOk(ctx, unchecked((ulong)ready));
    }

    private static int ScanPollDescriptors(CpuContext ctx, ulong fdsAddress, int fdCount, out bool faulted)
    {
        faulted = false;
        var ready = 0;
        for (var i = 0; i < fdCount; i++)
        {
            // struct pollfd { int fd; short events; short revents; }
            var entryAddress = fdsAddress + ((ulong)i * 8UL);
            if (!ctx.TryReadInt32(entryAddress, out var fd) ||
                !ctx.TryReadUInt16(entryAddress + 4, out var eventsRaw))
            {
                faulted = true;
                return 0;
            }

            var events = unchecked((short)eventsRaw);
            short revents = 0;
            if (fd >= 0)
            {
                if (TryQueryDescriptorReadiness(fd, out var readable, out var writable))
                {
                    if (readable)
                    {
                        revents |= (short)(events & (PollIn | PollRdNorm));
                    }

                    if (writable)
                    {
                        revents |= (short)(events & (PollOut | PollWrNorm));
                    }
                }
                else
                {
                    revents = PollNval;
                }
            }

            if (!ctx.TryWriteUInt16(entryAddress + 6, unchecked((ushort)revents)))
            {
                faulted = true;
                return 0;
            }

            if (revents != 0)
            {
                ready++;
            }
        }

        return ready;
    }

    // Shared readiness query for poll/select. Returns false for descriptors
    // this HLE does not know about at all.
    private static bool TryQueryDescriptorReadiness(int fd, out bool readable, out bool writable)
    {
        if (TryGetOpenFileStream(fd, out _))
        {
            // Regular files never block.
            readable = true;
            writable = true;
            return true;
        }

        if (KernelPipeCompatExports.TryQueryPipePollState(fd, out readable, out writable))
        {
            return true;
        }

        if (KernelSocketCompatExports.TryQuerySocketPollState(fd, out readable, out writable))
        {
            return true;
        }

        if (fd is 0 or 1 or 2)
        {
            // stdin never has data; stdout/stderr always accept writes.
            readable = false;
            writable = fd != 0;
            return true;
        }

        readable = false;
        writable = false;
        return false;
    }

    // ------------------------------------------------------------------
    // select
    // ------------------------------------------------------------------

    private const int FdSetMaxDescriptors = 1024;

    [SysAbiExport(
        Nid = "T8fER+tIGgk",
        ExportName = "select",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSelect(CpuContext ctx)
    {
        var descriptorCount = unchecked((int)ctx[CpuRegister.Rdi]);
        var readSetAddress = ctx[CpuRegister.Rsi];
        var writeSetAddress = ctx[CpuRegister.Rdx];
        var exceptSetAddress = ctx[CpuRegister.Rcx];
        var timeoutAddress = ctx[CpuRegister.R8];
        if (descriptorCount < 0 || descriptorCount > FdSetMaxDescriptors)
        {
            return PosixFail(ctx, Einval);
        }

        var timeoutMilliseconds = -1;
        if (timeoutAddress != 0)
        {
            // struct timeval { int64 sec; int64 usec; }
            if (!ctx.TryReadUInt64(timeoutAddress, out var timeoutSeconds) ||
                !ctx.TryReadUInt64(timeoutAddress + 8, out var timeoutMicros))
            {
                return PosixFail(ctx, Efault);
            }

            timeoutMilliseconds = (int)Math.Min(
                int.MaxValue,
                (timeoutSeconds * 1_000UL) + (timeoutMicros / 1_000UL));
        }

        var ready = ScanSelectSets(ctx, descriptorCount, readSetAddress, writeSetAddress, exceptSetAddress, apply: false, out var badDescriptor, out var faulted);
        if (faulted)
        {
            return PosixFail(ctx, Efault);
        }

        if (badDescriptor)
        {
            return PosixFail(ctx, Ebadf);
        }

        if (ready == 0 && timeoutMilliseconds != 0)
        {
            // Bounded wait like poll: never park the guest thread forever.
            var sleep = timeoutMilliseconds < 0 ? 10 : Math.Min(timeoutMilliseconds, 10);
            GuestThreadExecution.Scheduler?.Pump(ctx, "select");
            Thread.Sleep(sleep);
        }

        ready = ScanSelectSets(ctx, descriptorCount, readSetAddress, writeSetAddress, exceptSetAddress, apply: true, out badDescriptor, out faulted);
        if (faulted)
        {
            return PosixFail(ctx, Efault);
        }

        if (badDescriptor)
        {
            return PosixFail(ctx, Ebadf);
        }

        return PosixOk(ctx, unchecked((ulong)ready));
    }

    // Walks the three fd_sets; with apply=true the surviving bits are written
    // back (except-set members never fire and are always cleared).
    private static int ScanSelectSets(
        CpuContext ctx,
        int descriptorCount,
        ulong readSetAddress,
        ulong writeSetAddress,
        ulong exceptSetAddress,
        bool apply,
        out bool badDescriptor,
        out bool faulted)
    {
        badDescriptor = false;
        faulted = false;
        var ready = 0;
        var wordCount = (descriptorCount + 63) / 64;
        Span<ulong> readWords = stackalloc ulong[16];
        Span<ulong> writeWords = stackalloc ulong[16];
        readWords.Clear();
        writeWords.Clear();

        if (!TryReadFdSetWords(ctx, readSetAddress, wordCount, readWords, ref faulted) ||
            !TryReadFdSetWords(ctx, writeSetAddress, wordCount, writeWords, ref faulted))
        {
            return 0;
        }

        for (var fd = 0; fd < descriptorCount; fd++)
        {
            var word = fd >> 6;
            var bit = 1UL << (fd & 63);
            var wantsRead = readSetAddress != 0 && (readWords[word] & bit) != 0;
            var wantsWrite = writeSetAddress != 0 && (writeWords[word] & bit) != 0;
            if (!wantsRead && !wantsWrite)
            {
                continue;
            }

            if (!TryQueryDescriptorReadiness(fd, out var readable, out var writable))
            {
                badDescriptor = true;
                return 0;
            }

            if (wantsRead && !readable)
            {
                readWords[word] &= ~bit;
            }
            else if (wantsRead)
            {
                ready++;
            }

            if (wantsWrite && !writable)
            {
                writeWords[word] &= ~bit;
            }
            else if (wantsWrite)
            {
                ready++;
            }
        }

        if (apply)
        {
            if (!TryWriteFdSetWords(ctx, readSetAddress, wordCount, readWords, ref faulted) ||
                !TryWriteFdSetWords(ctx, writeSetAddress, wordCount, writeWords, ref faulted))
            {
                return 0;
            }

            // Exceptional conditions never fire in this HLE.
            if (exceptSetAddress != 0)
            {
                Span<ulong> emptyWords = stackalloc ulong[16];
                emptyWords.Clear();
                if (!TryWriteFdSetWords(ctx, exceptSetAddress, wordCount, emptyWords, ref faulted))
                {
                    return 0;
                }
            }
        }

        return ready;
    }

    private static bool TryReadFdSetWords(CpuContext ctx, ulong setAddress, int wordCount, Span<ulong> words, ref bool faulted)
    {
        if (setAddress == 0)
        {
            return true;
        }

        for (var i = 0; i < wordCount; i++)
        {
            if (!ctx.TryReadUInt64(setAddress + ((ulong)i * 8UL), out words[i]))
            {
                faulted = true;
                return false;
            }
        }

        return true;
    }

    private static bool TryWriteFdSetWords(CpuContext ctx, ulong setAddress, int wordCount, ReadOnlySpan<ulong> words, ref bool faulted)
    {
        if (setAddress == 0)
        {
            return true;
        }

        for (var i = 0; i < wordCount; i++)
        {
            if (!ctx.TryWriteUInt64(setAddress + ((ulong)i * 8UL), words[i]))
            {
                faulted = true;
                return false;
            }
        }

        return true;
    }

    // ------------------------------------------------------------------
    // flock / fchmod / utimes
    // ------------------------------------------------------------------

    private static OrbisGen2Result FlockCore(int fd, out int errno)
    {
        errno = 0;
        if (!TryGetOpenFileStream(fd, out _))
        {
            errno = Ebadf;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        // Advisory locks have no cross-process peer inside the emulator.
        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "9eMlfusH4sU",
        ExportName = "flock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFlock(CpuContext ctx)
    {
        var result = FlockCore(unchecked((int)ctx[CpuRegister.Rdi]), out var errno);
        return result == OrbisGen2Result.ORBIS_GEN2_OK ? PosixOk(ctx) : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "YDg-SQj66AQ",
        ExportName = "sceKernelFlock",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelFlock(CpuContext ctx)
    {
        var result = FlockCore(unchecked((int)ctx[CpuRegister.Rdi]), out _);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "n01yNbQO5W4",
        ExportName = "fchmod",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFchmod(CpuContext ctx)
    {
        // POSIX permission bits have no host equivalent here; see sceKernelChmod.
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        return TryGetOpenFileStream(fd, out _) ? PosixOk(ctx) : PosixFail(ctx, Ebadf);
    }

    private static OrbisGen2Result UtimesCore(CpuContext ctx, ulong pathAddress, ulong timesAddress, out int errno)
    {
        errno = 0;
        if (pathAddress == 0)
        {
            errno = Einval;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath))
        {
            errno = Efault;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var hostPath = ResolveGuestPath(guestPath);
        if (IsReadOnlyGuestMutationPath(guestPath))
        {
            errno = Eacces;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        var isDirectory = Directory.Exists(hostPath);
        if (!isDirectory && !File.Exists(hostPath))
        {
            errno = Enoent;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }

        var accessTime = DateTime.UtcNow;
        var writeTime = accessTime;
        if (timesAddress != 0)
        {
            // struct timeval[2]: {access, modification}, each {sec, usec} int64.
            if (!ctx.TryReadUInt64(timesAddress, out var accessSeconds) ||
                !ctx.TryReadUInt64(timesAddress + 8, out var accessMicros) ||
                !ctx.TryReadUInt64(timesAddress + 16, out var writeSeconds) ||
                !ctx.TryReadUInt64(timesAddress + 24, out var writeMicros))
            {
                errno = Efault;
                return OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }

            accessTime = ConvertTimevalToUtc(accessSeconds, accessMicros);
            writeTime = ConvertTimevalToUtc(writeSeconds, writeMicros);
        }

        try
        {
            if (isDirectory)
            {
                Directory.SetLastAccessTimeUtc(hostPath, accessTime);
                Directory.SetLastWriteTimeUtc(hostPath, writeTime);
            }
            else
            {
                File.SetLastAccessTimeUtc(hostPath, accessTime);
                File.SetLastWriteTimeUtc(hostPath, writeTime);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentOutOfRangeException)
        {
            errno = Eacces;
            return OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        return OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static DateTime ConvertTimevalToUtc(ulong seconds, ulong microseconds)
    {
        var clampedSeconds = Math.Min(seconds, 253_402_300_799UL); // year 9999
        var clampedMicros = Math.Min(microseconds, 999_999UL);
        return DateTime.UnixEpoch
            .AddSeconds(clampedSeconds)
            .AddTicks((long)clampedMicros * 10);
    }

    [SysAbiExport(
        Nid = "GDuV00CHrUg",
        ExportName = "utimes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixUtimes(CpuContext ctx)
    {
        var result = UtimesCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out var errno);
        return result == OrbisGen2Result.ORBIS_GEN2_OK ? PosixOk(ctx) : PosixFail(ctx, errno);
    }

    [SysAbiExport(
        Nid = "0Cq8ipKr9n0",
        ExportName = "sceKernelUtimes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelUtimes(CpuContext ctx)
    {
        var result = UtimesCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], out _);
        if (result != OrbisGen2Result.ORBIS_GEN2_OK)
        {
            return (int)result;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "+0EDo7YzcoU",
        ExportName = "futimes",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixFutimes(CpuContext ctx)
    {
        var fd = unchecked((int)ctx[CpuRegister.Rdi]);
        return TryGetOpenFileStream(fd, out _) ? PosixOk(ctx) : PosixFail(ctx, Ebadf);
    }

    // ------------------------------------------------------------------
    // realpath
    // ------------------------------------------------------------------

    [SysAbiExport(
        Nid = "vhtcIgZG-Lk",
        ExportName = "realpath",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixRealpath(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var resolvedAddress = ctx[CpuRegister.Rsi];
        if (pathAddress == 0 || resolvedAddress == 0)
        {
            // realpath(path, NULL) would have to malloc in the guest; unsupported.
            return PosixFail(ctx, Einval);
        }

        if (!TryReadNullTerminatedUtf8(ctx, pathAddress, MaxGuestStringLength, out var guestPath) ||
            guestPath.Length == 0)
        {
            return PosixFail(ctx, guestPath is { Length: 0 } ? Enoent : Efault);
        }

        string normalized;
        lock (_cwdGate)
        {
            normalized = NormalizeGuestAbsolutePath(guestPath, _guestWorkingDirectory);
        }

        var hostPath = ResolveGuestPath(normalized);
        if (!File.Exists(hostPath) && !Directory.Exists(hostPath))
        {
            return PosixFail(ctx, Enoent);
        }

        var payload = System.Text.Encoding.UTF8.GetBytes(normalized);
        Span<byte> terminated = payload.Length < 1024 ? stackalloc byte[payload.Length + 1] : new byte[payload.Length + 1];
        payload.CopyTo(terminated);
        terminated[^1] = 0;
        if (!ctx.Memory.TryWrite(resolvedAddress, terminated))
        {
            return PosixFail(ctx, Efault);
        }

        return PosixOk(ctx, resolvedAddress);
    }

    // ------------------------------------------------------------------
    // POSIX shared memory
    // ------------------------------------------------------------------

    private const int O_EXCL = 0x0800;

    private static string ResolveSharedMemoryHostPath(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length);
        foreach (var ch in name)
        {
            builder.Append(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        }

        var directory = Path.Combine(Path.GetTempPath(), "sharpemu-shm");
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, builder.ToString());
    }

    [SysAbiExport(
        Nid = "QuJYZ2KVGGQ",
        ExportName = "shm_open",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixShmOpen(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var flags = unchecked((int)ctx[CpuRegister.Rsi]);
        if (nameAddress == 0)
        {
            return PosixFail(ctx, Einval);
        }

        if (!TryReadNullTerminatedUtf8(ctx, nameAddress, MaxGuestStringLength, out var name) ||
            name.Length == 0)
        {
            return PosixFail(ctx, name is { Length: 0 } ? Einval : Efault);
        }

        var hostPath = ResolveSharedMemoryHostPath(name);
        var exists = File.Exists(hostPath);
        if ((flags & O_CREAT) == 0 && !exists)
        {
            return PosixFail(ctx, Enoent);
        }

        if ((flags & (O_CREAT | O_EXCL)) == (O_CREAT | O_EXCL) && exists)
        {
            return PosixFail(ctx, Eexist);
        }

        try
        {
            var access = ResolveOpenAccess(flags) == FileAccess.Read ? FileAccess.Read : FileAccess.ReadWrite;
            var stream = new FileStream(hostPath, ResolveOpenMode(flags, access), access, FileShare.ReadWrite);
            int fd;
            lock (_fdGate)
            {
                fd = AllocateGuestFileDescriptor();
                _openFiles[fd] = stream;
            }

            LogIoTrace("shm_open", name, $"host='{hostPath}' flags=0x{flags:X} fd={fd}");
            return PosixOk(ctx, unchecked((ulong)fd));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogIoTrace("shm_open", name, $"host='{hostPath}' flags=0x{flags:X} ex={ex.GetType().Name}");
            return PosixFail(ctx, Eacces);
        }
    }

    [SysAbiExport(
        Nid = "tPWsbOUGO8k",
        ExportName = "shm_unlink",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixShmUnlink(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        if (nameAddress == 0)
        {
            return PosixFail(ctx, Einval);
        }

        if (!TryReadNullTerminatedUtf8(ctx, nameAddress, MaxGuestStringLength, out var name) ||
            name.Length == 0)
        {
            return PosixFail(ctx, name is { Length: 0 } ? Einval : Efault);
        }

        var hostPath = ResolveSharedMemoryHostPath(name);
        if (!File.Exists(hostPath))
        {
            return PosixFail(ctx, Enoent);
        }

        try
        {
            File.Delete(hostPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogIoTrace("shm_unlink", name, $"host='{hostPath}' ex={ex.GetType().Name}");
            return PosixFail(ctx, Eacces);
        }

        return PosixOk(ctx);
    }
}
