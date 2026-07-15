// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

// In-memory pipe and socketpair channels. Endpoints live in their own table
// (mirroring how emulated sockets are layered) and are consulted by the
// shared read/write/close/poll paths in KernelMemoryCompatExports.
//
// Semantics notes:
// - A read on an empty pipe whose write end is still open does a short bounded
//   wait (pumping the guest scheduler) and then fails with the try-again code
//   instead of blocking forever; once every write end is closed it returns 0
//   (EOF) like the real kernel.
// - Writes fill a bounded buffer; a full buffer accepts a partial write, and a
//   write with no remaining read end fails.
public static class KernelPipeCompatExports
{
    private const int PipeBufferCapacity = 64 * 1024;
    private const int EmptyReadWaitIterations = 10;
    private const int EmptyReadWaitMilliseconds = 1;

    private static readonly object _pipeGate = new();
    private static readonly Dictionary<int, PipeEndpoint> _pipeEndpoints = new();

    // One direction of byte flow. `WriterClosed` turns empty reads into EOF.
    private sealed class PipeBuffer
    {
        public readonly Queue<byte> Bytes = new();
        public bool WriterClosed;
        public bool ReaderClosed;
    }

    private sealed class PipeEndpoint
    {
        public PipeBuffer? ReadBuffer;
        public PipeBuffer? WriteBuffer;
    }

    [SysAbiExport(
        Nid = "-Jp7F+pXxNg",
        ExportName = "pipe",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixPipe(CpuContext ctx)
    {
        var descriptorArrayAddress = ctx[CpuRegister.Rdi];
        if (descriptorArrayAddress == 0)
        {
            return FailPosix(ctx, errno: 14 /* EFAULT */);
        }

        var buffer = new PipeBuffer();
        int readFd;
        int writeFd;
        lock (_pipeGate)
        {
            readFd = KernelMemoryCompatExports.AllocateGuestFileDescriptor();
            writeFd = KernelMemoryCompatExports.AllocateGuestFileDescriptor();
            _pipeEndpoints[readFd] = new PipeEndpoint { ReadBuffer = buffer };
            _pipeEndpoints[writeFd] = new PipeEndpoint { WriteBuffer = buffer };
        }

        if (!ctx.TryWriteInt32(descriptorArrayAddress, readFd) ||
            !ctx.TryWriteInt32(descriptorArrayAddress + sizeof(int), writeFd))
        {
            lock (_pipeGate)
            {
                _pipeEndpoints.Remove(readFd);
                _pipeEndpoints.Remove(writeFd);
            }

            return FailPosix(ctx, errno: 14 /* EFAULT */);
        }

        TracePipe($"pipe read_fd={readFd} write_fd={writeFd}");
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    [SysAbiExport(
        Nid = "MZb0GKT3mo8",
        ExportName = "socketpair",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int PosixSocketpair(CpuContext ctx)
    {
        var descriptorArrayAddress = ctx[CpuRegister.Rcx];
        if (descriptorArrayAddress == 0)
        {
            return FailPosix(ctx, errno: 14 /* EFAULT */);
        }

        var firstToSecond = new PipeBuffer();
        var secondToFirst = new PipeBuffer();
        int firstFd;
        int secondFd;
        lock (_pipeGate)
        {
            firstFd = KernelMemoryCompatExports.AllocateGuestFileDescriptor();
            secondFd = KernelMemoryCompatExports.AllocateGuestFileDescriptor();
            _pipeEndpoints[firstFd] = new PipeEndpoint { ReadBuffer = secondToFirst, WriteBuffer = firstToSecond };
            _pipeEndpoints[secondFd] = new PipeEndpoint { ReadBuffer = firstToSecond, WriteBuffer = secondToFirst };
        }

        if (!ctx.TryWriteInt32(descriptorArrayAddress, firstFd) ||
            !ctx.TryWriteInt32(descriptorArrayAddress + sizeof(int), secondFd))
        {
            lock (_pipeGate)
            {
                _pipeEndpoints.Remove(firstFd);
                _pipeEndpoints.Remove(secondFd);
            }

            return FailPosix(ctx, errno: 14 /* EFAULT */);
        }

        TracePipe($"socketpair first_fd={firstFd} second_fd={secondFd}");
        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    internal static bool IsPipeFd(int fd)
    {
        lock (_pipeGate)
        {
            return _pipeEndpoints.ContainsKey(fd);
        }
    }

    internal static bool TryClosePipeFd(int fd)
    {
        lock (_pipeGate)
        {
            if (!_pipeEndpoints.Remove(fd, out var endpoint))
            {
                return false;
            }

            if (endpoint.ReadBuffer is { } readBuffer)
            {
                readBuffer.ReaderClosed = true;
            }

            if (endpoint.WriteBuffer is { } writeBuffer)
            {
                writeBuffer.WriterClosed = true;
            }

            TracePipe($"close fd={fd}");
            return true;
        }
    }

    // Read path used by _read/read/sceKernelRead. Returns false when the fd is
    // not a pipe endpoint; otherwise the out parameters carry the outcome.
    internal static bool TryReadPipeFd(
        CpuContext ctx,
        int fd,
        ulong bufferAddress,
        int requested,
        out int bytesRead,
        out OrbisGen2Result error)
    {
        bytesRead = 0;
        error = OrbisGen2Result.ORBIS_GEN2_OK;

        PipeBuffer? buffer;
        lock (_pipeGate)
        {
            if (!_pipeEndpoints.TryGetValue(fd, out var endpoint))
            {
                return false;
            }

            buffer = endpoint.ReadBuffer;
        }

        if (buffer is null)
        {
            // Write-only end.
            error = OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            return true;
        }

        for (var attempt = 0; ; attempt++)
        {
            lock (buffer)
            {
                if (buffer.Bytes.Count > 0)
                {
                    var take = Math.Min(requested, buffer.Bytes.Count);
                    var payload = GC.AllocateUninitializedArray<byte>(take);
                    for (var i = 0; i < take; i++)
                    {
                        payload[i] = buffer.Bytes.Dequeue();
                    }

                    if (take > 0 && !ctx.Memory.TryWrite(bufferAddress, payload))
                    {
                        error = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
                        return true;
                    }

                    bytesRead = take;
                    TracePipe($"read fd={fd} req={requested} read={take}");
                    return true;
                }

                if (buffer.WriterClosed)
                {
                    // EOF.
                    TracePipe($"read-eof fd={fd}");
                    return true;
                }
            }

            if (attempt >= EmptyReadWaitIterations)
            {
                // Bounded wait elapsed with the writer still open; report
                // try-again instead of blocking the guest thread forever.
                TracePipe($"read-again fd={fd}");
                error = OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
                return true;
            }

            GuestThreadExecution.Scheduler?.Pump(ctx, "pipe_read");
            Thread.Sleep(EmptyReadWaitMilliseconds);
        }
    }

    // Write path used by _write/write/sceKernelWrite; mirrors TryReadPipeFd.
    internal static bool TryWritePipeFd(
        int fd,
        ReadOnlySpan<byte> payload,
        out int bytesWritten,
        out OrbisGen2Result error)
    {
        bytesWritten = 0;
        error = OrbisGen2Result.ORBIS_GEN2_OK;

        PipeBuffer? buffer;
        lock (_pipeGate)
        {
            if (!_pipeEndpoints.TryGetValue(fd, out var endpoint))
            {
                return false;
            }

            buffer = endpoint.WriteBuffer;
        }

        if (buffer is null)
        {
            // Read-only end.
            error = OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
            return true;
        }

        lock (buffer)
        {
            if (buffer.ReaderClosed)
            {
                // EPIPE territory: nobody can ever read this.
                TracePipe($"write-epipe fd={fd}");
                error = OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
                return true;
            }

            var space = PipeBufferCapacity - buffer.Bytes.Count;
            if (space <= 0)
            {
                TracePipe($"write-full fd={fd}");
                error = OrbisGen2Result.ORBIS_GEN2_ERROR_TRY_AGAIN;
                return true;
            }

            var take = Math.Min(space, payload.Length);
            for (var i = 0; i < take; i++)
            {
                buffer.Bytes.Enqueue(payload[i]);
            }

            bytesWritten = take;
            TracePipe($"write fd={fd} req={payload.Length} written={take}");
            return true;
        }
    }

    // Poll/select support: readable when buffered bytes exist or EOF is
    // observable; writable while the peer's read end is open and space remains.
    internal static bool TryQueryPipePollState(int fd, out bool readable, out bool writable)
    {
        readable = false;
        writable = false;

        PipeEndpoint? endpoint;
        lock (_pipeGate)
        {
            if (!_pipeEndpoints.TryGetValue(fd, out endpoint))
            {
                return false;
            }
        }

        if (endpoint.ReadBuffer is { } readBuffer)
        {
            lock (readBuffer)
            {
                readable = readBuffer.Bytes.Count > 0 || readBuffer.WriterClosed;
            }
        }

        if (endpoint.WriteBuffer is { } writeBuffer)
        {
            lock (writeBuffer)
            {
                writable = !writeBuffer.ReaderClosed && writeBuffer.Bytes.Count < PipeBufferCapacity;
            }
        }

        return true;
    }

    private static int FailPosix(CpuContext ctx, int errno)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = ulong.MaxValue;
        return -1;
    }

    private static void TracePipe(string message)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_LOG_PIPES"), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"[LOADER][TRACE] pipe.{message}");
        }
    }
}
