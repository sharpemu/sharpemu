// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

public static partial class KernelMemoryCompatExports
{
    private const int MemoryBadDescriptor = unchecked((int)0x80020009);
    private const int MemoryNoDevice = unchecked((int)0x80020013);
    private const uint MmapShared = 0x1;
    private const uint MmapAnonymous = 0x1000;
    private static int _sharedWritableFileMapWarned;

    [SysAbiExport(Nid = "BPE9s9vQQXo", ExportName = "mmap", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int PosixMmap(CpuContext ctx)
    {
        var result = MmapCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], (int)ctx[CpuRegister.Rdx],
            (uint)ctx[CpuRegister.Rcx], (int)ctx[CpuRegister.R8], ctx[CpuRegister.R9], out var address);
        if (result != 0)
            return PosixFailure(ctx, result, result & 0xFFFF);
        ctx[CpuRegister.Rax] = address;
        return 0;
    }

    // sceKernelMmap(addr, len, prot, flags, fd, offset, void** res): same mapping as mmap(2),
    // but returns an Orbis error code and stores the address through the 7th (stack) argument.
    [SysAbiExport(Nid = "PGhQHd-dzv8", ExportName = "sceKernelMmap", Target = Generation.Gen4 | Generation.Gen5, LibraryName = "libKernel")]
    public static int KernelMmap(CpuContext ctx)
    {
        if (!ctx.TryReadUInt64(ctx[CpuRegister.Rsp] + sizeof(ulong), out var resultAddress) || resultAddress == 0)
            return MemoryInvalidArgument;
        var result = MmapCore(ctx, ctx[CpuRegister.Rdi], ctx[CpuRegister.Rsi], (int)ctx[CpuRegister.Rdx],
            (uint)ctx[CpuRegister.Rcx], (int)ctx[CpuRegister.R8], ctx[CpuRegister.R9], out var address);
        if (result != 0)
            return result;
        return ctx.TryWriteUInt64(resultAddress, address) ? 0 : MemoryFault;
    }

    private static int MmapCore(CpuContext ctx, ulong requested, ulong length, int protection, uint flags,
        int descriptor, ulong offset, out ulong address)
    {
        address = 0;
        var anonymous = (flags & MmapAnonymous) != 0;
        // Anonymous mappings stay private with fd -1; file mappings (e.g. il2cpp mapping
        // global-metadata.dat; without it every class/method lookup returns null) may be shared.
        var validFlags = anonymous
            ? (flags & 3) == 2 && (flags & ~0xFF001012u) == 0 && descriptor == -1 && offset == 0
            : ((flags & 3) is 1 or 2) && (flags & ~0xFF0E0013u) == 0 && descriptor >= 0 &&
              IsAligned(offset, OrbisPageSize);
        if (length == 0 || length > ulong.MaxValue - (OrbisPageSize - 1) || !validFlags ||
            !TryDecodeMappedProtection(protection, out var mode))
            return MemoryInvalidArgument;
        length = (length + OrbisPageSize - 1) & ~(OrbisPageSize - 1);
        var shift = flags >> 24;
        if (shift != 0 && shift is < 14 or > 31)
            return MemoryInvalidArgument;
        var alignment = shift == 0 ? OrbisPageSize : 1UL << (int)shift;
        if ((flags & OrbisKernelMapFixed) != 0 &&
            (requested == 0 || !IsAligned(requested, alignment) || requested > ulong.MaxValue - length))
            return MemoryInvalidArgument;

        byte[]? fileBytes = null;
        if (!anonymous)
        {
            var read = TryReadMappedFileBytes(descriptor, offset, length, out fileBytes);
            if (read != 0)
                return read;
            if ((flags & MmapShared) != 0 && (mode & GuestPageProtection.Write) != 0 &&
                Interlocked.Exchange(ref _sharedWritableFileMapWarned, 1) == 0)
                Console.Error.WriteLine(
                    "[LOADER][WARN] mmap: shared writable file mappings are not written back to the file");
        }

        var mapped = 0UL;
        var result = RunMappingTransaction(() =>
        {
            lock (_memoryGate)
            {
                // A file mapping is filled while writable, then switched to the requested protection.
                var mapResult = MapFlexibleRangeLocked(ctx, requested, length,
                    anonymous ? protection : 3, flags & ~0x1003u, alignment,
                    anonymous ? mode : GuestPageProtection.Read | GuestPageProtection.Write, out mapped);
                if (mapResult != 0 || anonymous)
                    return mapResult;
                if (fileBytes!.Length > 0 && !ctx.Memory.TryWrite(mapped, fileBytes))
                    return MemoryFault;
                return protection == 3 ? 0 : ProtectMappedRange(ctx, mapped, length, protection);
            }
        });
        if (result == 0)
            address = mapped;
        return result;
    }

    // Reads min(length, fileLength - offset) bytes without touching the descriptor's own position.
    private static int TryReadMappedFileBytes(int descriptor, ulong offset, ulong length, out byte[]? bytes)
    {
        bytes = null;
        Stream? stream;
        lock (_fdGate)
        {
            if (!_openFiles.TryGetValue(descriptor, out stream))
                return _openDirectories.ContainsKey(descriptor) || _randomDeviceDescriptors.Contains(descriptor)
                    ? MemoryNoDevice
                    : MemoryBadDescriptor;
        }
        try
        {
            if (stream is FileStream file)
            {
                // Separate handle: the guest's descriptor keeps its position.
                using var reader = new FileStream(file.Name, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return ReadMappedBytes(reader, offset, length, out bytes);
            }
            if (!stream.CanSeek || !stream.CanRead)
                return MemoryNoDevice;
            lock (stream)
            {
                var saved = stream.Position;
                try { return ReadMappedBytes(stream, offset, length, out bytes); }
                finally { stream.Position = saved; }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return MemoryBadDescriptor;
        }
    }

    private static int ReadMappedBytes(Stream stream, ulong offset, ulong length, out byte[]? bytes)
    {
        var fileLength = (ulong)stream.Length;
        var count = offset >= fileLength ? 0UL : Math.Min(length, fileLength - offset);
        bytes = new byte[count];
        if (count == 0)
            return 0;
        stream.Position = (long)offset;
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read <= 0)
                break;
            total += read;
        }
        if (total < bytes.Length)
            Array.Resize(ref bytes, total);
        return 0;
    }

    internal static void RegisterOpenFileForTests(int descriptor, Stream stream)
    {
        lock (_fdGate)
            _openFiles[descriptor] = stream;
    }

    internal static void UnregisterOpenFileForTests(int descriptor)
    {
        lock (_fdGate)
            _openFiles.Remove(descriptor);
    }
}
