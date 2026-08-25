// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests.LibcStdio;

public sealed class LibcStdioExportsTests
{
    private const ulong BaseAddress = 0x1_0000_0000;
    private const ulong PathAddress = BaseAddress + 0x100;
    private const ulong ModeAddress = BaseAddress + 0x200;
    private const ulong PositionAddress = BaseAddress + 0x300;
    private const ulong DataAddress = BaseAddress + 0x400;
    private const ulong TlsAddress = BaseAddress + 0x800;
    private const ulong ErrnoAddress = TlsAddress + 0x40;

    [Fact]
    public void Fopen_RejectsEmptyPath()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, string.Empty);
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(2, ReadErrno(memory));
    }

    [Fact]
    public void Fopen_RejectsInvalidMode()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, "/app0/file.bin");
        memory.WriteCString(ModeAddress, "invalid");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(22, ReadErrno(memory));
    }

    [Fact]
    public void Fopen_RejectsUnreadablePathPointer()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = BaseAddress + 0x2000;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(14, ReadErrno(memory));
    }

    [Fact]
    public void Fopen_UnresolvedGuestPathSetsEnoent()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, "/sharpemu-unregistered/file.bin");
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;

        var result = LibcStdioExports.Fopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(2, ReadErrno(memory));
    }

    [Fact]
    public void Freopen_RejectsEmptyPath()
    {
        var context = CreateContext(out var memory);
        memory.WriteCString(PathAddress, string.Empty);
        memory.WriteCString(ModeAddress, "rb");
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;
        context[CpuRegister.Rdx] = 0x1234;

        var result = LibcStdioExports.Freopen(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.Equal(2, ReadErrno(memory));
    }

    [Fact]
    public void Fgetpos_WritesSdkFposLayoutAndFileOffset()
    {
        var fixture = OpenTemporaryFile("rb", new byte[] { 1, 2, 3, 4, 5 });
        try
        {
            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = 0;
            fixture.Context[CpuRegister.Rdx] = 2;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Fseek(fixture.Context));

            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = PositionAddress;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Fgetpos(fixture.Context));

            Span<byte> position = stackalloc byte[24];
            Assert.True(fixture.Memory.TryRead(PositionAddress, position));
            Assert.Equal(5L, BinaryPrimitives.ReadInt64LittleEndian(position));
            Assert.True(position[8..].SequenceEqual(new byte[16]));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Fsetpos_SeeksToSdkFposOffset()
    {
        var fixture = OpenTemporaryFile("rb", new byte[] { 10, 20, 30, 40 });
        try
        {
            Span<byte> position = stackalloc byte[24];
            position.Clear();
            BinaryPrimitives.WriteInt64LittleEndian(position, 2);
            Assert.True(fixture.Memory.TryWrite(PositionAddress, position));

            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = PositionAddress;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Fsetpos(fixture.Context));

            fixture.Context[CpuRegister.Rdi] = DataAddress;
            fixture.Context[CpuRegister.Rsi] = 1;
            fixture.Context[CpuRegister.Rdx] = 2;
            fixture.Context[CpuRegister.Rcx] = fixture.Handle;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Fread(fixture.Context));

            Span<byte> bytes = stackalloc byte[2];
            Assert.True(fixture.Memory.TryRead(DataAddress, bytes));
            Assert.True(bytes.SequenceEqual(new byte[] { 30, 40 }));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Fgetpos_RejectsUnwritablePosition()
    {
        var fixture = OpenTemporaryFile("rb", new byte[] { 1 });
        try
        {
            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = 0x7FFF_FFFF_FFFF_F000;

            var result = LibcStdioExports.Fgetpos(fixture.Context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
            Assert.Equal(unchecked((ulong)(int)-1), fixture.Context[CpuRegister.Rax]);
            Assert.Equal(14, ReadErrno(fixture.Memory));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Setvbuf_NoBufferingFlushesEachWrite()
    {
        var fixture = OpenTemporaryFile("wb", Array.Empty<byte>());
        try
        {
            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rsi] = 0;
            fixture.Context[CpuRegister.Rdx] = 2;
            fixture.Context[CpuRegister.Rcx] = 0;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Setvbuf(fixture.Context));

            fixture.Context[CpuRegister.Rdi] = 0x5A;
            fixture.Context[CpuRegister.Rsi] = fixture.Handle;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Fputc(fixture.Context));

            using var reader = new FileStream(
                fixture.HostPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            Assert.Equal(0x5A, reader.ReadByte());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public void Setvbuf_RejectsUnknownMode()
    {
        var fixture = OpenTemporaryFile("rb", new byte[] { 1 });
        try
        {
            fixture.Context[CpuRegister.Rdi] = fixture.Handle;
            fixture.Context[CpuRegister.Rdx] = 3;

            var result = LibcStdioExports.Setvbuf(fixture.Context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
            Assert.Equal(unchecked((ulong)(int)-1), fixture.Context[CpuRegister.Rax]);
            Assert.Equal(22, ReadErrno(fixture.Memory));
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Fread_BlocksConcurrentSeekUntilTheReadCompletes()
    {
        var contents = Enumerable.Range(0, 64).Select(value => (byte)value).ToArray();
        var fixture = OpenTemporaryFile("rb", contents);
        using var writeEntered = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        try
        {
            fixture.Memory.BeforeWrite = (address, _) =>
            {
                if (address != DataAddress)
                {
                    return;
                }

                writeEntered.Set();
                Assert.True(releaseWrite.Wait(TimeSpan.FromSeconds(5)));
            };

            var readContext = CreateContext(fixture.Memory);
            readContext[CpuRegister.Rdi] = DataAddress;
            readContext[CpuRegister.Rsi] = 1;
            readContext[CpuRegister.Rdx] = 32;
            readContext[CpuRegister.Rcx] = fixture.Handle;
            var readTask = Task.Run(() => LibcStdioExports.Fread(readContext));

            Assert.True(writeEntered.Wait(TimeSpan.FromSeconds(5)));

            var seekContext = CreateContext(fixture.Memory);
            seekContext[CpuRegister.Rdi] = fixture.Handle;
            seekContext[CpuRegister.Rsi] = 48;
            seekContext[CpuRegister.Rdx] = 0;
            var seekTask = Task.Run(() => LibcStdioExports.Fseek(seekContext));

            await Task.Delay(100);
            Assert.False(seekTask.IsCompleted);
            releaseWrite.Set();

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await readTask);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await seekTask);
            var actual = new byte[32];
            Assert.True(fixture.Memory.TryRead(DataAddress, actual));
            Assert.True(actual.SequenceEqual(contents.AsSpan(0, 32)));
        }
        finally
        {
            fixture.Memory.BeforeWrite = null;
            releaseWrite.Set();
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task Fclose_WaitsForAnInFlightReadAndRejectsLaterReads()
    {
        var fixture = OpenTemporaryFile("rb", Enumerable.Range(0, 64).Select(value => (byte)value).ToArray());
        using var writeEntered = new ManualResetEventSlim();
        using var releaseWrite = new ManualResetEventSlim();
        try
        {
            fixture.Memory.BeforeWrite = (address, _) =>
            {
                if (address != DataAddress)
                {
                    return;
                }

                writeEntered.Set();
                Assert.True(releaseWrite.Wait(TimeSpan.FromSeconds(5)));
            };

            var readContext = CreateContext(fixture.Memory);
            readContext[CpuRegister.Rdi] = DataAddress;
            readContext[CpuRegister.Rsi] = 1;
            readContext[CpuRegister.Rdx] = 32;
            readContext[CpuRegister.Rcx] = fixture.Handle;
            var readTask = Task.Run(() => LibcStdioExports.Fread(readContext));

            Assert.True(writeEntered.Wait(TimeSpan.FromSeconds(5)));

            var closeContext = CreateContext(fixture.Memory);
            closeContext[CpuRegister.Rdi] = fixture.Handle;
            var closeTask = Task.Run(() => LibcStdioExports.Fclose(closeContext));

            await Task.Delay(100);
            Assert.False(closeTask.IsCompleted);
            releaseWrite.Set();

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await readTask);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, await closeTask);

            var lateReadContext = CreateContext(fixture.Memory);
            lateReadContext[CpuRegister.Rdi] = DataAddress;
            lateReadContext[CpuRegister.Rsi] = 1;
            lateReadContext[CpuRegister.Rdx] = 1;
            lateReadContext[CpuRegister.Rcx] = fixture.Handle;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
                LibcStdioExports.Fread(lateReadContext));
            Assert.Equal(0UL, lateReadContext[CpuRegister.Rax]);
        }
        finally
        {
            fixture.Memory.BeforeWrite = null;
            releaseWrite.Set();
            fixture.Dispose();
        }
    }

    private static CpuContext CreateContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(BaseAddress, 0x1000);
        return new CpuContext(memory, Generation.Gen5)
        {
            FsBase = TlsAddress,
        };
    }

    private static CpuContext CreateContext(StdioCpuMemory memory) =>
        new(memory, Generation.Gen5)
        {
            FsBase = TlsAddress,
        };

    private static int ReadErrno(ICpuMemory memory)
    {
        Span<byte> value = stackalloc byte[sizeof(int)];
        Assert.True(memory.TryRead(ErrnoAddress, value));
        return BinaryPrimitives.ReadInt32LittleEndian(value);
    }

    private static TemporaryFileFixture OpenTemporaryFile(string mode, byte[] contents)
    {
        var mountPoint = $"/stdio-{Guid.NewGuid():N}";
        var hostRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-stdio-{Guid.NewGuid():N}");
        Directory.CreateDirectory(hostRoot);
        var hostPath = Path.Combine(hostRoot, "data.bin");
        File.WriteAllBytes(hostPath, contents);
        KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, hostRoot);

        var memory = new StdioCpuMemory(BaseAddress, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5)
        {
            FsBase = TlsAddress,
        };
        memory.WriteCString(PathAddress, $"{mountPoint}/data.bin");
        memory.WriteCString(ModeAddress, mode);
        context[CpuRegister.Rdi] = PathAddress;
        context[CpuRegister.Rsi] = ModeAddress;
        var result = LibcStdioExports.Fopen(context);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
        Assert.NotEqual(0UL, context[CpuRegister.Rax]);
        return new TemporaryFileFixture(
            context,
            memory,
            context[CpuRegister.Rax],
            mountPoint,
            hostRoot,
            hostPath);
    }

    private sealed class TemporaryFileFixture(
        CpuContext context,
        StdioCpuMemory memory,
        ulong handle,
        string mountPoint,
        string hostRoot,
        string hostPath) : IDisposable
    {
        public CpuContext Context { get; } = context;
        public StdioCpuMemory Memory { get; } = memory;
        public ulong Handle { get; } = handle;
        public string HostPath { get; } = hostPath;

        public void Dispose()
        {
            Context[CpuRegister.Rdi] = Handle;
            _ = LibcStdioExports.Fclose(Context);
            KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            Directory.Delete(hostRoot, recursive: true);
        }
    }

    private sealed class StdioCpuMemory : ICpuMemory, IGuestAddressSpace
    {
        private readonly ulong _baseAddress;
        private readonly byte[] _storage;
        private readonly Dictionary<ulong, byte[]> _allocations = new();

        public Action<ulong, int>? BeforeWrite { get; set; }

        public StdioCpuMemory(ulong baseAddress, int size)
        {
            _baseAddress = baseAddress;
            _storage = new byte[size];
        }

        public bool TryRead(ulong virtualAddress, Span<byte> destination)
        {
            if (!TryResolve(virtualAddress, destination.Length, out var storage, out var offset))
            {
                return false;
            }

            storage.AsSpan(offset, destination.Length).CopyTo(destination);
            return true;
        }

        public bool TryWrite(ulong virtualAddress, ReadOnlySpan<byte> source)
        {
            BeforeWrite?.Invoke(virtualAddress, source.Length);
            if (!TryResolve(virtualAddress, source.Length, out var storage, out var offset))
            {
                return false;
            }

            source.CopyTo(storage.AsSpan(offset, source.Length));
            return true;
        }

        public bool TryAllocateGuestMemory(ulong size, ulong alignment, out ulong address) =>
            TryAllocateAtOrAbove(_baseAddress + (ulong)_storage.Length, size, false, alignment, out address);

        public bool TryFreeGuestMemory(ulong address) => _allocations.Remove(address);

        public ulong AllocateAt(
            ulong desiredAddress,
            ulong size,
            bool executable = true,
            bool allowAlternative = true)
        {
            if (TryAllocateAtOrAbove(desiredAddress, size, executable, 0x1000, out var address))
            {
                return address;
            }

            return 0;
        }

        public bool TryBackFixedRange(ulong address, ulong size, bool executable) =>
            AllocateAt(address, size, executable, allowAlternative: false) == address;

        public bool TryAllocateAtOrAbove(
            ulong desiredAddress,
            ulong size,
            bool executable,
            ulong alignment,
            out ulong actualAddress)
        {
            actualAddress = 0;
            if (size == 0 || size > int.MaxValue || alignment == 0 || (alignment & (alignment - 1)) != 0)
            {
                return false;
            }

            var minimum = _baseAddress + (ulong)_storage.Length;
            var candidate = Math.Max(desiredAddress, minimum);
            candidate = (candidate + alignment - 1) & ~(alignment - 1);
            while (_allocations.Any(pair => RangesOverlap(candidate, size, pair.Key, (ulong)pair.Value.Length)))
            {
                candidate += alignment;
            }

            _allocations[candidate] = new byte[(int)size];
            actualAddress = candidate;
            return true;
        }

        public bool TryEnsureRangeCommitted(ulong address, ulong size) =>
            TryResolve(address, checked((int)size), out _, out _);

        public bool TryProtect(ulong address, ulong size, GuestPageProtection protection) =>
            TryEnsureRangeCommitted(address, size);

        public ulong WriteCString(ulong virtualAddress, string text)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(text);
            Assert.True(TryWrite(virtualAddress, bytes));
            Assert.True(TryWrite(virtualAddress + (ulong)bytes.Length, stackalloc byte[] { 0 }));
            return virtualAddress;
        }

        private bool TryResolve(
            ulong virtualAddress,
            int length,
            out byte[] storage,
            out int offset)
        {
            if (TryResolveInRegion(virtualAddress, length, _baseAddress, _storage, out offset))
            {
                storage = _storage;
                return true;
            }

            foreach (var pair in _allocations)
            {
                if (TryResolveInRegion(virtualAddress, length, pair.Key, pair.Value, out offset))
                {
                    storage = pair.Value;
                    return true;
                }
            }

            storage = Array.Empty<byte>();
            offset = 0;
            return false;
        }

        private static bool TryResolveInRegion(
            ulong address,
            int length,
            ulong regionAddress,
            byte[] region,
            out int offset)
        {
            offset = 0;
            if (address < regionAddress)
            {
                return false;
            }

            var relative = address - regionAddress;
            if (relative + (ulong)length > (ulong)region.Length)
            {
                return false;
            }

            offset = (int)relative;
            return true;
        }

        private static bool RangesOverlap(ulong left, ulong leftSize, ulong right, ulong rightSize) =>
            left < right + rightSize && right < left + leftSize;
    }
}
