// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelUtimesTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong PathAddress = MemoryBase + 0x100;
    private const ulong TimesAddress = MemoryBase + 0x400;

    // times[0] is the access time and times[1] the modification time, each seconds and microseconds.
    [Fact]
    public void KernelUtimes_SetsTheAccessAndModificationTimes()
    {
        var identity = Guid.NewGuid().ToString("N");
        var mountPoint = $"/utimes-{identity}";
        var hostRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-utimes-{identity}");
        var hostPath = Path.Combine(hostRoot, "SAVE.DAT");
        Directory.CreateDirectory(hostRoot);
        File.WriteAllBytes(hostPath, [1]);
        KernelMemoryCompatExports.RegisterGuestPathMount(mountPoint, hostRoot);
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        try
        {
            memory.WriteCString(PathAddress, $"{mountPoint}/SAVE.DAT");
            WriteTimeval(memory, TimesAddress, 1_600_000_000, 250_000);
            WriteTimeval(memory, TimesAddress + 16, 1_700_000_000, 500_000);
            context[CpuRegister.Rdi] = PathAddress;
            context[CpuRegister.Rsi] = TimesAddress;

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.KernelUtimes(context));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_600_000_000).AddMilliseconds(250).UtcDateTime,
                File.GetLastAccessTimeUtc(hostPath));
            Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000).AddMilliseconds(500).UtcDateTime,
                File.GetLastWriteTimeUtc(hostPath));

            WriteTimeval(memory, TimesAddress, 1_600_000_000, 1_000_000);
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, KernelMemoryCompatExports.KernelUtimes(context));

            memory.WriteCString(PathAddress, $"{mountPoint}/MISSING.DAT");
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, KernelMemoryCompatExports.KernelUtimes(context));
        }
        finally
        {
            _ = KernelMemoryCompatExports.UnregisterGuestPathMount(mountPoint);
            Directory.Delete(hostRoot, recursive: true);
        }
    }

    private static void WriteTimeval(FakeCpuMemory memory, ulong address, long seconds, long microseconds)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteInt64LittleEndian(bytes, seconds);
        BinaryPrimitives.WriteInt64LittleEndian(bytes[8..], microseconds);
        Assert.True(memory.TryWrite(address, bytes));
    }
}
