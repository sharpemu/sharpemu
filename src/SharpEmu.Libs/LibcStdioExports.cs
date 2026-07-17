// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.LibcStdio;

public static class LibcStdioExports
{
    private const int MaxPathLength = 4096;
    private const int MaxModeLength = 16;
    private const int ReadChunkSize = 1024 * 1024;
    private const ulong GuestFileObjectSize = 0x100;

    private static readonly ConcurrentDictionary<ulong, FileStream> _fileHandles = new();

    private static readonly bool _traceStdio =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_STDIO"), "1", StringComparison.Ordinal);

    private const int CtypeTableLowerBound = -128;
    private const int CtypeTableUpperBound = 255;
    private const int CtypeTableEntryCount = CtypeTableUpperBound - CtypeTableLowerBound + 1; // 384 entries * 2 bytes = 768 bytes

    // Mirrors the Dinkumware ctype bitmask layout (the CRT Sony's libc is based on;
    // _Getpctype/_Getptolower/_Getptoupper are Dinkumware accessor names). This is NOT the
    // MSVC/UCRT layout: e.g. Dinkumware puts digit at 0x20 where UCRT puts control, so
    // serving a UCRT-shaped table made the game's bundled printf engine misparse every
    // %-directive (fatal-error messages rendered empty) and made its mcpp preprocessor
    // treat 'a'-'f' as control characters (UCRT _HEX=0x80 reads as Dinkumware _BB) and
    // drop them from identifiers ("texture" -> "txtur"). Titles that bundle their own
    // libc bypass this table entirely (see IsSafeLleLibcExport in DirectExecutionBackend);
    // this fallback only serves titles that import _Getpctype without shipping one.
    private const ushort CtypeXDigit = 0x001; // _XD  '0'-'9', 'A'-'F', 'a'-'f'
    private const ushort CtypeUpper = 0x002; // _UP  'A'-'Z'
    private const ushort CtypeSpace = 0x004; // _SP  ' ' (isspace = _CN|_SP|_XS)
    private const ushort CtypePunct = 0x008; // _PU  punctuation
    private const ushort CtypeLower = 0x010; // _LO  'a'-'z'
    private const ushort CtypeDigit = 0x020; // _DI  '0'-'9'
    private const ushort CtypeControlSpace = 0x040; // _CN  '\t','\n','\v','\f','\r'
    private const ushort CtypeControl = 0x080; // _BB  other control characters
    private const ushort CtypeBlank = 0x400; // _XB  ' ' and '\t'

    private static readonly object _ctypeTableGate = new();
    private static nint _ctypeTableBase;

    [SysAbiExport(
        Nid = "xeYO4u7uyJ0",
        ExportName = "fopen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fopen(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var modeAddress = ctx[CpuRegister.Rsi];

        if (pathAddress == 0 || modeAddress == 0 ||
            !KernelMemoryCompatExports.TryReadNullTerminatedUtf8(ctx, pathAddress, MaxPathLength, out var guestPath) ||
            !KernelMemoryCompatExports.TryReadNullTerminatedUtf8(ctx, modeAddress, MaxModeLength, out var mode) ||
            !TryParseFopenMode(mode, out var fileMode, out var fileAccess))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var hostPath = KernelMemoryCompatExports.ResolveGuestPath(guestPath);
        if (fileAccess != FileAccess.Read && KernelMemoryCompatExports.IsReadOnlyGuestMutationPath(guestPath))
        {
            if (_traceStdio)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] fopen: guest='{guestPath}' host='{hostPath}' mode='{mode}' -> PERMISSION_DENIED (read-only path)");
            }

            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        try
        {
            if (fileAccess != FileAccess.Read)
            {
                var parentDirectory = Path.GetDirectoryName(hostPath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }
            }

            var stream = new FileStream(hostPath, fileMode, fileAccess, FileShare.ReadWrite);
            if (mode.StartsWith('a') && fileAccess == FileAccess.ReadWrite)
            {
                stream.Seek(0, SeekOrigin.End);
            }

            // A FILE* can cross between HLE stdio and a title's bundled libc.
            // Back the handle with zeroed guest memory so native helpers such
            // as _Lockfilelock can safely inspect the FILE object instead of
            // dereferencing the old small integer token (0x1000, 0x1008...).
            if (!KernelMemoryCompatExports.TryAllocateHleData(
                    ctx,
                    GuestFileObjectSize,
                    alignment: 0x10,
                    out var handle))
            {
                stream.Dispose();
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
            }

            _fileHandles[handle] = stream;

            if (_traceStdio)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] fopen: guest='{guestPath}' host='{hostPath}' mode='{mode}' -> OK handle=0x{handle:X} length={stream.Length}");
            }

            ctx[CpuRegister.Rax] = handle;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_traceStdio)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] fopen: guest='{guestPath}' host='{hostPath}' mode='{mode}' -> FAILED {ex.GetType().Name}: {ex.Message}");
            }

            ctx[CpuRegister.Rax] = 0;
            return ex is UnauthorizedAccessException
                ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
    }

    [SysAbiExport(
        Nid = "rQFVBXp-Cxg",
        ExportName = "fseek",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fseek(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var offset = unchecked((long)ctx[CpuRegister.Rsi]);
        var whence = unchecked((int)ctx[CpuRegister.Rdx]);

        if (!_fileHandles.TryGetValue(handle, out var stream) || !TryGetSeekOrigin(whence, out var origin))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        try
        {
            stream.Seek(offset, origin);
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (IOException)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
    }

    [SysAbiExport(
        Nid = "Qazy8LmXTvw",
        ExportName = "ftell",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Ftell(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];

        if (!_fileHandles.TryGetValue(handle, out var stream))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        try
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)stream.Position);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (IOException)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(long)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
    }

    [SysAbiExport(
        Nid = "uodLYyUip20",
        ExportName = "fclose",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fclose(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];

        if (!_fileHandles.TryRemove(handle, out var stream))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        try
        {
            stream.Dispose();
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (IOException)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(int)-1);
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
    }

    [SysAbiExport(
        Nid = "lbB+UlZqVG0",
        ExportName = "fread",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fread(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var elementSize = ctx[CpuRegister.Rsi];
        var elementCount = ctx[CpuRegister.Rdx];
        var handle = ctx[CpuRegister.Rcx];

        if (elementSize == 0 || elementCount == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (destination == 0 || !_fileHandles.TryGetValue(handle, out var stream))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var totalRequested = elementSize * elementCount;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min((ulong)ReadChunkSize, totalRequested));
        ulong totalRead = 0;

        try
        {
            while (totalRead < totalRequested)
            {
                var request = (int)Math.Min((ulong)buffer.Length, totalRequested - totalRead);
                var read = stream.Read(buffer, 0, request);
                if (read <= 0)
                {
                    break;
                }

                if (!ctx.Memory.TryWrite(destination + totalRead, buffer.AsSpan(0, read)))
                {
                    ctx[CpuRegister.Rax] = totalRead / elementSize;
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                totalRead += (ulong)read;
            }
        }
        catch (IOException)
        {
            ctx[CpuRegister.Rax] = totalRead / elementSize;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        if (_traceStdio)
        {
            Console.Error.WriteLine(
                $"[LOADER][TRACE] fread: handle=0x{handle:X} requested={totalRequested} read={totalRead} pos={stream.Position}");
        }

        ctx[CpuRegister.Rax] = totalRead / elementSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "KdP-nULpuGw",
        ExportName = "fgets",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fgets(CpuContext ctx)
    {
        var destination = ctx[CpuRegister.Rdi];
        var maxCount = unchecked((int)ctx[CpuRegister.Rsi]);
        var handle = ctx[CpuRegister.Rdx];

        if (destination == 0 || maxCount <= 0 || !_fileHandles.TryGetValue(handle, out var stream))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(maxCount - 1);
        var count = 0;

        try
        {
            while (count < maxCount - 1)
            {
                var b = stream.ReadByte();
                if (b < 0)
                {
                    break;
                }

                buffer[count++] = (byte)b;
                if (b == '\n')
                {
                    break;
                }
            }

            if (count == 0)
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            }

            Span<byte> withNul = stackalloc byte[count + 1];
            buffer.AsSpan(0, count).CopyTo(withNul);
            withNul[count] = 0;

            if (!ctx.Memory.TryWrite(destination, withNul))
            {
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ctx[CpuRegister.Rax] = destination;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "8Q60JLJ6Rv4",
        ExportName = "getc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Getc(CpuContext ctx) => FgetcCore(ctx);

    [SysAbiExport(
        Nid = "AEuF3F2f8TA",
        ExportName = "fgetc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fgetc(CpuContext ctx) => FgetcCore(ctx);

    private static int FgetcCore(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (!_fileHandles.TryGetValue(handle, out var stream))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        try
        {
            var value = stream.ReadByte();
            ctx[CpuRegister.Rax] = value < 0 ? unchecked((ulong)(-1L)) : (ulong)value;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (IOException)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
    }

    [SysAbiExport(
        Nid = "LxcEU+ICu8U",
        ExportName = "feof",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Feof(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var atEnd = _fileHandles.TryGetValue(handle, out var stream) &&
            stream.CanRead && stream.Position >= stream.Length;
        ctx[CpuRegister.Rax] = atEnd ? 1UL : 0UL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "AHxyhN96dy4",
        ExportName = "ferror",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Ferror(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "3QIPIh-GDjw",
        ExportName = "rewind",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Rewind(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        if (_fileHandles.TryGetValue(handle, out var stream))
        {
            try
            {
                stream.Seek(0, SeekOrigin.Begin);
            }
            catch (IOException)
            {
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "aZK8lNei-Qw",
        ExportName = "fputc",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fputc(CpuContext ctx)
    {
        var character = unchecked((byte)ctx[CpuRegister.Rdi]);
        var handle = ctx[CpuRegister.Rsi];

        if (_fileHandles.TryGetValue(handle, out var stream))
        {
            try
            {
                stream.WriteByte(character);
            }
            catch (IOException)
            {
                ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
        }
        else
        {
            // Unknown streams are the bundled libc's real stdout/stderr FILE objects;
            // mirror the fputs HLE and forward them to the host console.
            Console.Out.Write((char)character);
        }

        ctx[CpuRegister.Rax] = character;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MpxhMh8QFro",
        ExportName = "fwrite",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fwrite(CpuContext ctx)
    {
        var source = ctx[CpuRegister.Rdi];
        var elementSize = ctx[CpuRegister.Rsi];
        var elementCount = ctx[CpuRegister.Rdx];
        var handle = ctx[CpuRegister.Rcx];

        if (elementSize == 0 || elementCount == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var knownHandle = _fileHandles.TryGetValue(handle, out var stream);
        if (source == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        var totalRequested = elementSize * elementCount;
        var buffer = ArrayPool<byte>.Shared.Rent((int)Math.Min((ulong)ReadChunkSize, totalRequested));
        ulong totalWritten = 0;

        try
        {
            while (totalWritten < totalRequested)
            {
                var request = (int)Math.Min((ulong)buffer.Length, totalRequested - totalWritten);
                if (!ctx.Memory.TryRead(source + totalWritten, buffer.AsSpan(0, request)))
                {
                    ctx[CpuRegister.Rax] = totalWritten / elementSize;
                    return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                }

                if (knownHandle)
                {
                    stream!.Write(buffer, 0, request);
                }
                else
                {
                    Console.Out.Write(System.Text.Encoding.UTF8.GetString(buffer, 0, request));
                }

                totalWritten += (ulong)request;
            }
        }
        catch (IOException)
        {
            ctx[CpuRegister.Rax] = totalWritten / elementSize;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        ctx[CpuRegister.Rax] = totalWritten / elementSize;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "MUjC4lbHrK4",
        ExportName = "fflush",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fflush(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];

        try
        {
            if (handle == 0)
            {
                foreach (var stream in _fileHandles.Values)
                {
                    stream.Flush();
                }
            }
            else if (_fileHandles.TryGetValue(handle, out var stream))
            {
                stream.Flush();
            }
        }
        catch (IOException)
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fffwELXNVFA",
        ExportName = "fprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Fprintf(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var formatAddress = ctx[CpuRegister.Rsi];

        if (!KernelMemoryCompatExports.TryReadNullTerminatedUtf8(ctx, formatAddress, MaxPathLength, out var format))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        var rendered = KernelMemoryCompatExports.FormatStringFromVarArgs(ctx, format, firstGpArgIndex: 2);
        return WriteRenderedText(ctx, handle, rendered);
    }

    [SysAbiExport(
        Nid = "pDBDcY6uLSA",
        ExportName = "vfprintf",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Vfprintf(CpuContext ctx)
    {
        var handle = ctx[CpuRegister.Rdi];
        var formatAddress = ctx[CpuRegister.Rsi];
        var vaListAddress = ctx[CpuRegister.Rdx];

        if (!KernelMemoryCompatExports.TryReadNullTerminatedUtf8(ctx, formatAddress, MaxPathLength, out var format))
        {
            ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
        }

        KernelMemoryCompatExports.TryFormatStringFromVaList(ctx, format, vaListAddress, out var rendered);
        return WriteRenderedText(ctx, handle, rendered);
    }

    private static int WriteRenderedText(CpuContext ctx, ulong handle, string rendered)
    {
        var payload = System.Text.Encoding.UTF8.GetBytes(rendered);

        if (_fileHandles.TryGetValue(handle, out var stream))
        {
            try
            {
                stream.Write(payload, 0, payload.Length);
            }
            catch (IOException)
            {
                ctx[CpuRegister.Rax] = unchecked((ulong)(-1L));
                return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            }
        }
        else
        {
            Console.Out.Write(rendered);
        }

        ctx[CpuRegister.Rax] = (ulong)payload.Length;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "gkWgn0p1AfU",
        ExportName = "freopen",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Freopen(CpuContext ctx)
    {
        var pathAddress = ctx[CpuRegister.Rdi];
        var modeAddress = ctx[CpuRegister.Rsi];
        var handle = ctx[CpuRegister.Rdx];

        if (pathAddress == 0 || modeAddress == 0 || handle == 0 ||
            !KernelMemoryCompatExports.TryReadNullTerminatedUtf8(ctx, pathAddress, MaxPathLength, out var guestPath) ||
            !KernelMemoryCompatExports.TryReadNullTerminatedUtf8(ctx, modeAddress, MaxModeLength, out var mode) ||
            !TryParseFopenMode(mode, out var fileMode, out var fileAccess))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
        }

        if (_fileHandles.TryRemove(handle, out var previousStream))
        {
            previousStream.Dispose();
        }

        var hostPath = KernelMemoryCompatExports.ResolveGuestPath(guestPath);
        if (fileAccess != FileAccess.Read && KernelMemoryCompatExports.IsReadOnlyGuestMutationPath(guestPath))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED;
        }

        try
        {
            if (fileAccess != FileAccess.Read)
            {
                var parentDirectory = Path.GetDirectoryName(hostPath);
                if (!string.IsNullOrWhiteSpace(parentDirectory))
                {
                    Directory.CreateDirectory(parentDirectory);
                }
            }

            var stream = new FileStream(hostPath, fileMode, fileAccess, FileShare.ReadWrite);
            // freopen keeps the caller's FILE* identity, so rebind the same handle value.
            _fileHandles[handle] = stream;

            if (_traceStdio)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] freopen: guest='{guestPath}' host='{hostPath}' mode='{mode}' -> OK handle=0x{handle:X}");
            }

            ctx[CpuRegister.Rax] = handle;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (_traceStdio)
            {
                Console.Error.WriteLine(
                    $"[LOADER][TRACE] freopen: guest='{guestPath}' host='{hostPath}' mode='{mode}' -> FAILED {ex.GetType().Name}: {ex.Message}");
            }

            ctx[CpuRegister.Rax] = 0;
            return ex is UnauthorizedAccessException
                ? (int)OrbisGen2Result.ORBIS_GEN2_ERROR_PERMISSION_DENIED
                : (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
        }
    }

    [SysAbiExport(
        Nid = "sUP1hBaouOw",
        ExportName = "_Getpctype",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int GetPctype(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)EnsureCtypeTable());
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static unsafe nint EnsureCtypeTable()
    {
        lock (_ctypeTableGate)
        {
            if (_ctypeTableBase != 0)
            {
                return _ctypeTableBase;
            }

            var storage = Marshal.AllocHGlobal(CtypeTableEntryCount * sizeof(ushort));
            var entries = new Span<ushort>((void*)storage, CtypeTableEntryCount);
            for (var i = 0; i < CtypeTableEntryCount; i++)
            {
                var c = i + CtypeTableLowerBound;
                entries[i] = c is >= 0 and <= 0x7F ? ComputeCtypeFlags(c) : (ushort)0;
            }

            // Table is indexed as base[c] for c in [-128, 255], so the pointer handed to the
            // guest must point at the c == 0 entry, not the start of the allocation.
            _ctypeTableBase = storage - (CtypeTableLowerBound * sizeof(ushort));
            return _ctypeTableBase;
        }
    }

    private static ushort ComputeCtypeFlags(int c)
    {
        var isUpper = c is >= 'A' and <= 'Z';
        var isLower = c is >= 'a' and <= 'z';
        var isDigit = c is >= '0' and <= '9';
        var isHex = isDigit || (c is >= 'A' and <= 'F') || (c is >= 'a' and <= 'f');
        var isAlnum = isUpper || isLower || isDigit;
        // In the Dinkumware table '\t'..'\r' carry _CN (isspace and iscntrl both match via
        // _CN) while plain ' ' carries _SP; keeping them distinct is what keeps isprint(' ')
        // true and isprint('\t') false.
        var isControlSpace = c is >= 0x09 and <= 0x0D;
        var isControl = (c is >= 0x00 and <= 0x08) || (c is >= 0x0E and <= 0x1F) || c == 0x7F;
        var isPrintable = c is >= 0x20 and <= 0x7E;
        var isPunct = isPrintable && !isAlnum && c != ' ';

        ushort flags = 0;
        if (isUpper) flags |= CtypeUpper;
        if (isLower) flags |= CtypeLower;
        if (isDigit) flags |= CtypeDigit;
        if (isHex) flags |= CtypeXDigit;
        if (c == ' ') flags |= CtypeSpace | CtypeBlank;
        if (c == '\t') flags |= CtypeBlank;
        if (isControlSpace) flags |= CtypeControlSpace;
        if (isPunct) flags |= CtypePunct;
        if (isControl) flags |= CtypeControl;
        return flags;
    }

    private static bool TryParseFopenMode(string mode, out FileMode fileMode, out FileAccess fileAccess)
    {
        fileMode = FileMode.Open;
        fileAccess = FileAccess.Read;

        if (string.IsNullOrEmpty(mode))
        {
            return false;
        }

        var plus = mode.Contains('+');
        switch (mode[0])
        {
            case 'r':
                fileMode = FileMode.Open;
                fileAccess = plus ? FileAccess.ReadWrite : FileAccess.Read;
                return true;

            case 'w':
                fileMode = FileMode.Create;
                fileAccess = plus ? FileAccess.ReadWrite : FileAccess.Write;
                return true;

            case 'a':
                if (plus)
                {
                    fileMode = FileMode.OpenOrCreate;
                    fileAccess = FileAccess.ReadWrite;
                }
                else
                {
                    fileMode = FileMode.Append;
                    fileAccess = FileAccess.Write;
                }

                return true;

            default:
                return false;
        }
    }

    private static bool TryGetSeekOrigin(int whence, out SeekOrigin origin)
    {
        switch (whence)
        {
            case 0:
                origin = SeekOrigin.Begin;
                return true;

            case 1:
                origin = SeekOrigin.Current;
                return true;

            case 2:
                origin = SeekOrigin.End;
                return true;

            default:
                origin = default;
                return false;
        }
    }
}
