// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.Tests.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class LoadExecArgumentsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitArgumentsPreserveEmptyAndSpacedValuesAndHaveANullTerminator(bool empty)
    {
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_ARGS");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_ARGS", "legacy arguments");
            string[] arguments = empty ? [] : ["one", "two words", "", "Türkçe"];
            var context = Initialize(arguments);
            Assert.True(context.TryReadUInt64(context[CpuRegister.Rdi], out var count));
            string[] expected = empty ? ["big2-ps4_Shipping.elf"] : arguments;
            Assert.Equal((ulong)expected.Length, count);
            for (var index = 0; index < expected.Length; index++)
            {
                Assert.True(context.TryReadUInt64(context[CpuRegister.Rdi] + 8 + (ulong)index * 8, out var pointer));
                Assert.True(context.TryReadNullTerminatedUtf8(pointer, 1024, out var actual));
                Assert.Equal(expected[index], actual);
            }
            Assert.True(context.TryReadUInt64(context[CpuRegister.Rdi] + 8 + (ulong)expected.Length * 8, out var terminator));
            Assert.Equal(0UL, terminator);
            Assert.Equal(expected.Length, LibcProcessArgumentExports.GetArgc(context));
            LibcProcessArgumentExports.GetArgv(context);
            Assert.Equal(context[CpuRegister.Rdi] + 8, context[CpuRegister.Rax]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_ARGS", previous);
            HleDataSymbols.ConfigureProcessImageName("eboot.bin");
        }
    }

    [Fact]
    public void CollectionLoadExecPreservesTheSuppliedArgvZeroAndFlagPosition()
    {
        try
        {
            var context = Initialize(["eboot.bin", "ispartofuccollection"]);
            var entry = context[CpuRegister.Rdi];
            Assert.True(context.TryReadUInt64(entry, out var count));
            Assert.Equal(2UL, count);
            Assert.True(context.TryReadUInt64(entry + 8, out var image));
            Assert.True(context.TryReadNullTerminatedUtf8(image, 1024, out var name));
            Assert.Equal("eboot.bin", name);
            Assert.True(context.TryReadUInt64(entry + 16, out var flag));
            Assert.True(context.TryReadNullTerminatedUtf8(flag, 1024, out var value));
            Assert.Equal("ispartofuccollection", value);
            Assert.True(context.TryReadUInt64(entry + 24, out var terminator));
            Assert.Equal(0UL, terminator);
            Assert.Equal(2, LibcProcessArgumentExports.GetArgc(context));
        }
        finally
        {
            HleDataSymbols.ConfigureProcessImageName("eboot.bin");
        }
    }

    [Fact]
    public void DefaultEntryKeepsTheExistingCompatibilityArguments()
    {
        var previous = Environment.GetEnvironmentVariable("SHARPEMU_GUEST_ARGS");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_ARGS", "first second ignored");
            var context = Initialize(null);
            Assert.True(context.TryReadUInt64(context[CpuRegister.Rdi], out var count));
            Assert.Equal(3UL, count);
            Assert.Equal(1, HleDataSymbols.ProcessArgumentCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_GUEST_ARGS", previous);
            HleDataSymbols.ConfigureProcessImageName("eboot.bin");
        }
    }

    private static CpuContext Initialize(IReadOnlyList<string>? arguments)
    {
        HleDataSymbols.ConfigureProcessImageName("big2-ps4_Shipping.elf");
        const ulong address = 0x1_0000_0000;
        var context = new CpuContext(new FakeCpuMemory(address, 0x10000), Generation.Gen4);
        context[CpuRegister.Rsp] = address + 0xFF00;
        var initialize = typeof(CpuDispatcher).GetMethod("InitializeProcessEntryFrame", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.True((bool)initialize.Invoke(null, [context, "big2-ps4_Shipping.elf", 123UL, arguments])!);
        return context;
    }
}
