// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.GnmMode;
using SharpEmu.GnmMode.Libraries.Np;
using SharpEmu.GnmMode.Libraries.Codec;
using SharpEmu.HLE;
using SharpEmu.Libs.Np;
using Xunit;

namespace SharpEmu.Libs.Tests.Loader;

public sealed class GnmMovieExportsTests
{
    private const ulong Base = 0x1_0000_0000;

    [Fact]
    public void Ps4ComputeQueueMemorySetupMatchesTheMoviePlayerCallingConvention()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen4);
        var exports = GnmExportRegistry.CreateExports();
        var info = Base + 0x100;
        Assert.True(context.TryWriteUInt64(info, 0x18));
        Assert.True(context.TryWriteUInt64(info + 0x18, 0xAABBCCDDEEFF0011));
        context[CpuRegister.Rdi] = info;
        Assert.Equal(0, Assert.Single(exports, export => export.Nid == "RnDibcGCPKw").Function(context));
        Assert.Equal(0x18UL, Read64(context, info));
        Assert.Equal(16UL * 1024 * 1024, Read64(context, info + 8));
        Assert.Equal(0UL, Read64(context, info + 0x10));
        Assert.Equal(0xAABBCCDDEEFF0011UL, Read64(context, info + 0x18));
        var config = Base + 0x200;
        var queue = Base + 0x300;
        var buffer = Base + 0x800;
        Assert.True(context.TryWriteUInt64(config, 0x10));
        Assert.True(context.TryWriteUInt32(config + 8, 4 | 7u << 16));
        Assert.True(context.TryWriteUInt32(config + 12, 1));
        Assert.True(context.TryWriteUInt64(info + 0x10, buffer));
        Assert.True(context.TryWriteUInt64(queue + 8, 0x1122334455667788));
        context[CpuRegister.Rdi] = config;
        context[CpuRegister.Rsi] = info;
        context[CpuRegister.Rdx] = queue;
        Assert.Equal(0, Assert.Single(exports, export => export.Nid == "eD+X2SmxUt4").Function(context));
        Assert.Equal(buffer, Read64(context, queue));
        Assert.Equal(0x1122334455667788UL, Read64(context, queue + 8));
        context[CpuRegister.Rdi] = Read64(context, queue);
        Assert.Equal(0, Assert.Single(exports, export => export.Nid == "UvtA3FAiF4Y").Function(context));
    }

    [Fact]
    public void GnmRegistersTheWholeExistingDecoderLifecycleWithoutChangingGen5()
    {
        var shared = SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5)
            .Where(export => export.LibraryName == "libSceVideodec2").ToArray();
        Assert.Equal(10, shared.Length);
        var gnm = GnmExportRegistry.CreateExports();
        foreach (var original in shared)
        {
            var adapted = Assert.Single(gnm, export => export.Nid == original.Nid);
            Assert.Equal(original.Name, adapted.Name);
            Assert.Equal(original.LibraryName, adapted.LibraryName);
            SysAbiFunction expected = original.Nid switch
            {
                "852F5+q6+iM" => GnmVideodec2Exports.Decode,
                "l1hXwscLuCY" => GnmVideodec2Exports.Flush,
                "NtXRa3dRzU0" => GnmVideodec2Exports.GetPictureInfo,
                _ => original.Function,
            };
            Assert.Equal(expected, adapted.Function);
            Assert.Equal(Generation.Gen4, adapted.Target);
            Assert.Equal(Generation.Gen5, original.Target);
        }
        Assert.Equal(Generation.Gen4, Assert.Single(gnm, export => export.Nid == "p-o74CnoNzY").Target);
        Assert.DoesNotContain(SharpEmu.Generated.SysAbiExportRegistry.CreateExports(Generation.Gen5),
            export => export.Nid == "p-o74CnoNzY");
    }

    [Fact]
    public void NpIdUsesTheExistingOnlineIdAndClearsTheEntirePs4Payload()
    {
        var memory = new FakeCpuMemory(Base, 0x1000);
        var context = new CpuContext(memory, Generation.Gen4);
        var address = Base + 0x100;
        var expected = Enumerable.Repeat((byte)0xAB, 38).ToArray();
        Assert.True(memory.TryWrite(address - 1, expected));
        context[CpuRegister.Rdi] = 0x10000000;
        context[CpuRegister.Rsi] = address;
        Assert.Equal(0, GnmNpManagerExports.GetNpId(context));
        expected.AsSpan(1, 36).Clear();
        context[CpuRegister.Rsi] = Base + 0x200;
        Assert.Equal(0, NpManagerExports.NpGetOnlineId(context));
        Assert.True(memory.TryRead(Base + 0x200, expected.AsSpan(1, 20)));
        var actual = new byte[38];
        Assert.True(memory.TryRead(address - 1, actual));
        Assert.Equal(expected, actual);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData(0xFFFFFFFFu, 0x1_0000_0100UL, 0x80550003u)]
    [InlineData(0x10000000u, 0UL, 0x80550003u)]
    [InlineData(0x10000000u, 0x1_0000_0FE0UL, 0x80020101u)]
    public void NpIdRejectsInvalidArgumentsAndPartialOutputMappings(uint user, ulong output, uint error)
    {
        var context = new CpuContext(new FakeCpuMemory(Base, 0x1000), Generation.Gen4);
        context[CpuRegister.Rdi] = user;
        context[CpuRegister.Rsi] = output;
        Assert.Equal(unchecked((int)error), GnmNpManagerExports.GetNpId(context));
        Assert.Equal(unchecked((ulong)(int)error), context[CpuRegister.Rax]);
    }

    private static ulong Read64(CpuContext context, ulong address)
    {
        Assert.True(context.TryReadUInt64(address, out var value));
        return value;
    }
}
