// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Buffers;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Buffers;

public sealed class BufferWriteDependenciesTests
{
    [Fact]
    public void DisjointWritesDoNotReplaceTheEarlierRangesDependency()
    {
        var dependencies = new BufferWriteDependencies();
        dependencies.Record(0, 0x100, 1);
        dependencies.Record(0x100000, 0x100, 2);
        Assert.Equal(1UL, dependencies.Latest(0, 0x100));
        Assert.Equal(2UL, dependencies.Latest(0x100000, 0x100));
        Assert.Equal(0UL, dependencies.Latest(0x100, 0x100));
        Assert.Equal(2UL, dependencies.Latest(0, 0x100100));
    }

    [Fact]
    public void PartialAndWholeWritesPreserveTheNewestDependencyOfEveryByte()
    {
        var dependencies = new BufferWriteDependencies();
        var bytes = new ulong[256];
        var random = new System.Random(437);
        for (ulong tick = 1; tick <= 2000; tick++)
        {
            var offset = random.Next(bytes.Length);
            var size = random.Next(1, bytes.Length - offset + 1);
            dependencies.Record((ulong)offset, (ulong)size, tick);
            Array.Fill(bytes, tick, offset, size);
            for (var check = 0; check < 4; check++)
            {
                var start = random.Next(bytes.Length);
                var length = random.Next(1, bytes.Length - start + 1);
                Assert.Equal(bytes.AsSpan(start, length).ToArray().Max(), dependencies.Latest((ulong)start, (ulong)length));
            }
        }
        dependencies.Record(0, 256, 2001);
        Assert.Equal(2001UL, dependencies.Latest(0, 1));
        Assert.Equal(2001UL, dependencies.Latest(255, 1));
        Assert.Equal(0UL, dependencies.Latest(256, 1));
        Assert.Equal(0UL, dependencies.Latest(0, 0));
    }

    [Fact]
    public void FragmentationFallsBackConservativelyAndLaterWholeWritesReplaceIt()
    {
        var dependencies = new BufferWriteDependencies();
        for (ulong tick = 1; tick <= 1024; tick++)
            dependencies.Record(tick * 2, 1, tick);
        for (ulong tick = 1; tick <= 1024; tick++)
            Assert.True(dependencies.Latest(tick * 2, 1) >= tick);
        dependencies.Record(0, 4096, 1025);
        Assert.Equal(1025UL, dependencies.Latest(2, 1));
        Assert.Equal(1025UL, dependencies.Latest(4095, 1));
    }
}
