// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Ampr;
using Xunit;

namespace SharpEmu.Libs.Tests.Ampr;

// PakDirectoryTracker reconstructs the absolute pak offset for a "next sequential chunk" read
// (offset -1) from the requested byte count. When several archived files share that byte count,
// picking by directory order alone mis-resolves out-of-order reads: Quake requested progs/h_ogre.mdl
// (0x3A34 bytes) but the tracker returned bots/navigation/death32c.nav, which shares the size and
// sits earlier in the directory, so the guest parsed NAV2 data as a brush model and aborted.
public sealed class PakDirectoryTrackerTests
{
    private const int EntrySize = 64;
    private const int NameLength = 56;

    private readonly record struct PakEntry(string Name, uint FilePos, uint FileLen);

    [Fact]
    public void ResolveSequentialOffset_SizeCollision_PicksEntryNearestReadCursor()
    {
        const uint fileId = 0x5AA5_0001;
        const uint collidingLen = 0x3A34;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        // "far" collides in size but lives early in the pak; "near" is what the guest actually wants.
        LoadDirectory(ctx, fileId, memory, 0x1_0000_0000, dirFileOffset: 0x400, new[]
        {
            new PakEntry("far.dat", FilePos: 0x1000, FileLen: collidingLen),
            new PakEntry("near.dat", FilePos: 0x9000, FileLen: collidingLen),
        });

        // Advance the read cursor next to the "near" entry, mimicking a burst of nearby asset reads.
        PakDirectoryTracker.OnReadCompleted(ctx, fileId, destination: 0x1_0000_0000, fileOffset: 0x8800, bytesRead: 0x400);

        Assert.Equal(0x9000UL, PakDirectoryTracker.ResolveSequentialOffset(fileId, collidingLen));
        // Once the near entry is consumed, the same size resolves to the remaining match.
        Assert.Equal(0x1000UL, PakDirectoryTracker.ResolveSequentialOffset(fileId, collidingLen));
    }

    [Fact]
    public void ResolveSequentialOffset_ContiguousSameSizeRun_StaysInOrder()
    {
        const uint fileId = 0x5AA5_0002;
        const uint runLen = 0x608;
        var memory = new FakeCpuMemory(0x1_0000_0000, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        // A contiguous run of equal-size lumps (like Quake's gfx/weapons/ww_*.lmp) must still resolve
        // in packed order when the guest streams them sequentially.
        LoadDirectory(ctx, fileId, memory, 0x1_0000_0000, dirFileOffset: 0x400, new[]
        {
            new PakEntry("lump_a", FilePos: 0x1000, FileLen: runLen),
            new PakEntry("lump_b", FilePos: 0x1000 + runLen, FileLen: runLen),
            new PakEntry("lump_c", FilePos: 0x1000 + (runLen * 2), FileLen: runLen),
        });

        var first = PakDirectoryTracker.ResolveSequentialOffset(fileId, runLen);
        PakDirectoryTracker.OnReadCompleted(ctx, fileId, destination: 0x1_0000_0000, fileOffset: first, bytesRead: runLen);
        var second = PakDirectoryTracker.ResolveSequentialOffset(fileId, runLen);
        PakDirectoryTracker.OnReadCompleted(ctx, fileId, destination: 0x1_0000_0000, fileOffset: second, bytesRead: runLen);
        var third = PakDirectoryTracker.ResolveSequentialOffset(fileId, runLen);

        Assert.Equal(0x1000UL, first);
        Assert.Equal(0x1000UL + runLen, second);
        Assert.Equal(0x1000UL + (runLen * 2), third);
    }

    // Feeds the tracker a synthetic PACK header + directory table exactly as the AMPR read path does:
    // first the 12-byte header (which arms directory parsing), then the directory records themselves.
    private static void LoadDirectory(
        CpuContext ctx,
        uint fileId,
        FakeCpuMemory memory,
        ulong destination,
        ulong dirFileOffset,
        PakEntry[] entries)
    {
        Span<byte> header = stackalloc byte[12];
        header[0] = (byte)'P';
        header[1] = (byte)'A';
        header[2] = (byte)'C';
        header[3] = (byte)'K';
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..8], (uint)dirFileOffset);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..12], (uint)(entries.Length * EntrySize));
        memory.TryWrite(destination, header);
        PakDirectoryTracker.OnReadCompleted(ctx, fileId, destination, fileOffset: 0, bytesRead: 12);

        var table = new byte[entries.Length * EntrySize];
        for (var i = 0; i < entries.Length; i++)
        {
            var record = table.AsSpan(i * EntrySize, EntrySize);
            var name = Encoding.ASCII.GetBytes(entries[i].Name);
            name.AsSpan(0, Math.Min(name.Length, NameLength)).CopyTo(record);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(56, 4), entries[i].FilePos);
            BinaryPrimitives.WriteUInt32LittleEndian(record.Slice(60, 4), entries[i].FileLen);
        }

        memory.TryWrite(destination, table);
        PakDirectoryTracker.OnReadCompleted(ctx, fileId, destination, dirFileOffset, (ulong)table.Length);
    }
}
