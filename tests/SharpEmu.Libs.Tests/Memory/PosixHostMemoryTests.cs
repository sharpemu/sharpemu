// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.Core.Memory;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.HLE.Host.Posix;
using Xunit;

namespace SharpEmu.Libs.Tests.Memory;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PosixHostMemoryStateCollection
{
    public const string Name = "PosixHostMemoryState";
}

[Collection(PosixHostMemoryStateCollection.Name)]
public sealed unsafe class PosixHostMemoryTests
{
    private const int ProtRead = 0x1;
    private const int ProtWrite = 0x2;
    private const int MapPrivate = 0x02;
    private const int MapAnonymousDarwin = 0x1000;
    private const uint PageExecuteWriteCopy = 0x80;
    private const uint PageGuard = 0x100;
    private static readonly nint MapFailed = -1;

    [Fact]
    public void LegacyFacadeAndProcessBackendShareMappings()
    {
        if (!SupportsNativePosixBackend())
        {
            return;
        }

        var size = checked((nuint)Environment.SystemPageSize);
        var backend = HostPlatform.Current.Memory;
        var facadeAddress = HostMemory.Alloc(
            null,
            size,
            HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT,
            HostMemory.PAGE_READWRITE);
        Assert.NotEqual(0UL, (ulong)facadeAddress);

        var facadeFreed = false;
        try
        {
            Assert.True(backend.Query((ulong)facadeAddress, out var facadeRegion));
            Assert.Equal(HostRegionState.Committed, facadeRegion.State);
            Assert.Equal((ulong)facadeAddress, facadeRegion.AllocationBase);
            Assert.True(backend.Protect(
                (ulong)facadeAddress,
                (ulong)size,
                HostPageProtection.ReadOnly,
                out var facadeOldProtection));
            Assert.Equal(HostMemory.PAGE_READWRITE, facadeOldProtection);
            Assert.True(backend.Free((ulong)facadeAddress));
            facadeFreed = true;
        }
        finally
        {
            if (!facadeFreed)
            {
                _ = HostMemory.Free(facadeAddress, 0, HostMemory.MEM_RELEASE);
            }
        }

        var backendAddress = backend.Allocate(0, (ulong)size, HostPageProtection.ReadWrite);
        Assert.NotEqual(0UL, backendAddress);

        var backendFreed = false;
        try
        {
            Assert.NotEqual(0U, HostMemory.Query((void*)backendAddress, out var backendRegion));
            Assert.Equal(backendAddress, backendRegion.AllocationBase);
            Assert.Equal(HostMemory.MEM_COMMIT, backendRegion.State);
            Assert.True(HostMemory.Protect(
                (void*)backendAddress,
                size,
                HostMemory.PAGE_READONLY,
                out var backendOldProtection));
            Assert.Equal(HostMemory.PAGE_READWRITE, backendOldProtection);
            Assert.True(HostMemory.Free((void*)backendAddress, 0, HostMemory.MEM_RELEASE));
            backendFreed = true;
        }
        finally
        {
            if (!backendFreed)
            {
                _ = backend.Free(backendAddress);
            }
        }
    }

    [Fact]
    public void LegacyFacadePreservesRawAllocationProtection()
    {
        if (!SupportsNativePosixBackend())
        {
            return;
        }

        var size = checked((nuint)Environment.SystemPageSize);
        var rawProtection = PageExecuteWriteCopy | PageGuard;
        var address = HostMemory.Alloc(
            null,
            size,
            HostMemory.MEM_RESERVE | HostMemory.MEM_COMMIT,
            rawProtection);
        Assert.NotEqual(0UL, (ulong)address);

        try
        {
            Assert.True(HostPlatform.Current.Memory.Query((ulong)address, out var region));
            Assert.Equal(HostPageProtection.ExecuteWriteCopy, region.Protection);
            Assert.Equal(rawProtection, region.RawProtection);
            Assert.Equal(rawProtection, region.RawAllocationProtection);

            Assert.NotEqual(0U, HostMemory.Query(address, out var facadeInfo));
            Assert.Equal(rawProtection, facadeInfo.Protect);
            Assert.Equal(rawProtection, facadeInfo.AllocationProtect);
        }
        finally
        {
            Assert.True(HostMemory.Free(address, 0, HostMemory.MEM_RELEASE));
        }
    }

    [Fact]
    public void DefaultPhysicalMemoryUsesProcessBackendForExactMappings()
    {
        if (!SupportsNativePosixBackend())
        {
            return;
        }

        const int maxAttempts = 16;
        var size = checked((ulong)Environment.SystemPageSize);
        var backend = HostPlatform.Current.Memory;
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var candidate = backend.Allocate(0, size, HostPageProtection.ReadWrite);
            Assert.NotEqual(0UL, candidate);
            Assert.True(backend.Free(candidate));

            using var memory = new PhysicalVirtualMemory();
            if (!memory.TryAllocateAtExact(candidate, size, executable: false, out var actualAddress))
            {
                continue;
            }

            Assert.Equal(candidate, actualAddress);
            Assert.True(backend.Query(candidate, out var region));
            Assert.Equal(HostRegionState.Committed, region.State);
            Assert.Equal(candidate, region.AllocationBase);
            Assert.True(memory.TryWrite(candidate, [0x5A]));
            return;
        }

        Assert.Fail($"Could not reclaim any of {maxAttempts} released candidate mappings exactly.");
    }

    [Fact]
    public void ExactAllocationDoesNotReplaceUntrackedDarwinMapping()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        var size = checked((nuint)Environment.SystemPageSize);
        var externalMapping = mmap(
            0,
            size,
            ProtRead | ProtWrite,
            MapPrivate | MapAnonymousDarwin,
            -1,
            0);
        Assert.NotEqual(MapFailed, externalMapping);

        try
        {
            var sentinel = new Span<byte>((void*)externalMapping, checked((int)size));
            sentinel.Fill(0xA5);

            var hostMemory = new PosixHostMemory();
            var result = hostMemory.Allocate(
                (ulong)externalMapping,
                (ulong)size,
                HostPageProtection.ReadWrite);

            Assert.Equal(0UL, result);
            Assert.All(sentinel.ToArray(), value => Assert.Equal(0xA5, value));
        }
        finally
        {
            Assert.Equal(0, munmap(externalMapping, size));
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern nint mmap(nint addr, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", SetLastError = true)]
    private static extern int munmap(nint addr, nuint length);

    private static bool SupportsNativePosixBackend() =>
        !OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;
}
