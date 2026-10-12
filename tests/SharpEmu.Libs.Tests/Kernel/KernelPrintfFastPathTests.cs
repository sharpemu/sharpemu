// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Kernel;

// The allocation-free snprintf/vsnprintf path must match the general formatter byte for
// byte, including the return value and everything written to the destination buffer.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelPrintfFastPathTests
{
    private const ulong Base = 0x1_0000_0000;
    private const ulong Dest = Base + 0x100;
    private const int DestBytes = 128;
    private const ulong FormatAddress = Base + 0x400;
    private const ulong StringArea = Base + 0x800;
    private const ulong VaList = Base + 0x2000;
    private const ulong RegSave = Base + 0x2100;

    private readonly ITestOutputHelper _output;

    public KernelPrintfFastPathTests(ITestOutputHelper output) => _output = output;

    private static readonly object?[] NoArgs = [];

    private static ulong Neg(long value) => unchecked((ulong)value);

    public static IEnumerable<object?[]> Cases()
    {
        var cases = new List<(string Format, ulong Size, object?[] Args)>
        {
            ("", 64, NoArgs),
            ("", 0, NoArgs),
            ("", 1, NoArgs),
            ("plain text", 64, NoArgs),
            ("plain text", 0, NoArgs),
            ("plain text", 1, NoArgs),
            ("plain text", 10, NoArgs),
            ("plain text", 11, NoArgs),
            ("plain text", 5, NoArgs),
            ("100%%", 64, NoArgs),
            ("%%%%", 64, NoArgs),
            ("%s", 64, new object?[] { "hello" }),
            ("%s", 0, new object?[] { "hello" }),
            ("%s", 1, new object?[] { "hello" }),
            ("%s", 5, new object?[] { "hello" }),
            ("%s", 6, new object?[] { "hello" }),
            ("%s", 3, new object?[] { "hello" }),
            ("%s", 64, new object?[] { "" }),
            ("[%s]", 64, new object?[] { "" }),
            ("%s", 64, new object?[] { null }),
            ("<%s>", 64, new object?[] { null }),
            ("%10s", 64, new object?[] { null }),
            ("%-10s|", 64, new object?[] { null }),
            ("%10s", 64, new object?[] { "abc" }),
            ("%-10s|", 64, new object?[] { "abc" }),
            ("%010s", 64, new object?[] { "abc" }),
            ("%2s", 64, new object?[] { "abcdef" }),
            ("%.3s", 64, new object?[] { "abcdef" }),
            ("%.0s", 64, new object?[] { "abcdef" }),
            ("%.s", 64, new object?[] { "abcdef" }),
            ("%.10s", 64, new object?[] { "abc" }),
            ("%8.3s|", 64, new object?[] { "abcdef" }),
            ("%-8.3s|", 64, new object?[] { "abcdef" }),
            ("%s/%s.%s", 64, new object?[] { "data", "model", "bin" }),
            ("%s/%s.%s", 12, new object?[] { "data", "model", "bin" }),
            ("%s/%s.%s", 0, new object?[] { "data", "model", "bin" }),
            ("%s%08X", 64, new object?[] { "id_", 0xBEEFUL }),
            ("%s%08X", 8, new object?[] { "id_", 0xBEEFUL }),
            ("%s%08x", 64, new object?[] { "id_", 0xBEEFUL }),
            ("%d", 64, new object?[] { 0UL }),
            ("%d", 64, new object?[] { 42UL }),
            ("%d", 64, new object?[] { Neg(-42) }),
            ("%d", 64, new object?[] { 0xFFFFFFFFUL }),
            ("%d", 64, new object?[] { 0x80000000UL }),
            ("%i", 64, new object?[] { Neg(-7) }),
            ("%ld", 64, new object?[] { Neg(long.MinValue) }),
            ("%lld", 64, new object?[] { Neg(long.MinValue) }),
            ("%lld", 64, new object?[] { Neg(long.MaxValue) }),
            ("%zd", 64, new object?[] { Neg(-1) }),
            ("%hd", 64, new object?[] { 0x18000UL }),
            ("%hhd", 64, new object?[] { 0x1FFUL }),
            ("%hhd", 64, new object?[] { 0x80UL }),
            ("%5d|", 64, new object?[] { 42UL }),
            ("%-5d|", 64, new object?[] { 42UL }),
            ("%05d|", 64, new object?[] { 42UL }),
            ("%05d|", 64, new object?[] { Neg(-42) }),
            ("%-05d|", 64, new object?[] { Neg(-42) }),
            ("%2d|", 64, new object?[] { 12345UL }),
            ("%020lld|", 64, new object?[] { Neg(long.MinValue) }),
            ("%u", 64, new object?[] { 0xFFFFFFFFUL }),
            ("%u", 64, new object?[] { Neg(-1) }),
            ("%lu", 64, new object?[] { Neg(-1) }),
            ("%llu", 64, new object?[] { ulong.MaxValue }),
            ("%zu", 64, new object?[] { 123456789012UL }),
            ("%hu", 64, new object?[] { 0x12345UL }),
            ("%hhu", 64, new object?[] { 0x1FFUL }),
            ("%08u|", 64, new object?[] { 77UL }),
            ("%-8u|", 64, new object?[] { 77UL }),
            ("%x", 64, new object?[] { 0xDEADBEEFUL }),
            ("%X", 64, new object?[] { 0xDEADBEEFUL }),
            ("%x", 64, new object?[] { 0UL }),
            ("%x", 64, new object?[] { Neg(-1) }),
            ("%lx", 64, new object?[] { Neg(-1) }),
            ("%llX", 64, new object?[] { 0x123456789ABCDEFUL }),
            ("%hx", 64, new object?[] { 0xABCDEUL }),
            ("%hhX", 64, new object?[] { 0xABCUL }),
            ("%016llX", 64, new object?[] { 0xABCUL }),
            ("%-6x|", 64, new object?[] { 0xABUL }),
            ("%3x", 64, new object?[] { 0xABCDEUL }),
            ("%p", 64, new object?[] { 0x7FFF1234UL }),
            ("%p", 64, new object?[] { 0UL }),
            ("%20p|", 64, new object?[] { 0xABCUL }),
            ("%c", 64, new object?[] { (ulong)'A' }),
            ("%c%c%c", 64, new object?[] { (ulong)'x', (ulong)'y', (ulong)'z' }),
            ("%5c|", 64, new object?[] { (ulong)'A' }),
            ("%-5c|", 64, new object?[] { (ulong)'A' }),
            ("%c", 64, new object?[] { 0UL }),
            ("a%cb", 64, new object?[] { 0UL }),
            ("%c", 64, new object?[] { 0x141UL }),
            ("%c", 64, new object?[] { 0xE9UL }),
            ("%s %d %s %u %x %c %p", 64, new object?[] { "a", Neg(-3), "bc", 9UL, 255UL, (ulong)'q', 0x10UL }),
            ("%s %d %s %u %x %c %p", 20, new object?[] { "a", Neg(-3), "bc", 9UL, 255UL, (ulong)'q', 0x10UL }),
            ("value=%d;", 64, new object?[] { 5UL }),
            ("value=%d;", 7, new object?[] { 5UL }),
            ("value=%d;", 8, new object?[] { 5UL }),
            ("value=%d;", 9, new object?[] { 5UL }),
            ("café %s", 64, new object?[] { "x" }),
            ("%s", 64, new object?[] { "café" }),
            ("%.2s", 64, new object?[] { "café" }),
            ("%5.1f", 64, new object?[] { 0UL }),
            ("%+d", 64, new object?[] { 5UL }),
            ("% d", 64, new object?[] { 5UL }),
            ("%#x", 64, new object?[] { 255UL }),
            ("%o", 64, new object?[] { 8UL }),
            ("%ls", 64, new object?[] { "hello" }),
            ("%.3d", 64, new object?[] { 5UL }),
            ("%q", 64, new object?[] { 5UL }),
            ("trailing %", 64, NoArgs),
            ("trailing %5", 64, NoArgs),
            ("%s%s%s%s%s", 64, new object?[] { "a", "b", "c", "d", "e" }),
            ("%d%d%d%d%d", 64, new object?[] { 1UL, 2UL, 3UL, 4UL, 5UL }),
        };

        foreach (var (format, size, args) in cases)
        {
            yield return new object?[] { format, size, args, true };
            yield return new object?[] { format, size, args, false };
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void FastPathMatchesGeneralFormatter(string format, ulong size, object?[] args, bool useVaList)
    {
        if (!useVaList && args.Length > 3)
        {
            // snprintf takes its first three variadic arguments from registers.
            return;
        }

        var fast = Run(format, size, args, useVaList, disableFastPath: false);
        var slow = Run(format, size, args, useVaList, disableFastPath: true);

        Assert.Equal(slow.Status, fast.Status);
        Assert.Equal(slow.Rax, fast.Rax);
        Assert.Equal(slow.Buffer, fast.Buffer);
    }

    [Fact]
    public void FastPathProducesExpectedText()
    {
        var run = Run("%s/%s.%s %08X %5d", 64, new object?[] { "a", "bb", "ccc", 0xBEEFUL, Neg(-12) }, useVaList: true, disableFastPath: false);
        Assert.Equal(0, run.Status);
        Assert.Equal(23UL, run.Rax);
        Assert.Equal("a/bb.ccc 0000BEEF   -12\0", Encoding.ASCII.GetString(run.Buffer, 0, 24));
    }

    [Fact]
    public void FastPathNeverWritesPastTheBufferSize()
    {
        var run = Run("%s", 4, new object?[] { "hello" }, useVaList: true, disableFastPath: false);
        Assert.Equal(5UL, run.Rax);
        Assert.Equal("hel\0", Encoding.ASCII.GetString(run.Buffer, 0, 4));
        Assert.Equal(0xAA, run.Buffer[4]);
    }

    [Fact]
    public void Benchmark()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_BENCH_PRINTF"), "1", StringComparison.Ordinal))
        {
            return;
        }

        var benchmarks = new (string Format, object?[] Args)[]
        {
            ("%s/%s.%s", new object?[] { "data/models", "hero", "bin" }),
            ("%s%08X", new object?[] { "id_", 0xBEEFUL }),
        };

        foreach (var (format, args) in benchmarks)
        {
            foreach (var disable in new[] { true, false })
            {
                var (context, memory) = Prepare(format, 128, args, useVaList: true);
                const int Warmup = 20_000;
                const int Iterations = 200_000;
                KernelMemoryCompatExports.DisablePrintfFastPath = disable;
                try
                {
                    for (var i = 0; i < Warmup; i++)
                    {
                        Invoke(context, useVaList: true);
                    }

                    var sw = Stopwatch.StartNew();
                    for (var i = 0; i < Iterations; i++)
                    {
                        Invoke(context, useVaList: true);
                    }

                    sw.Stop();
                    _output.WriteLine(
                        $"BENCH {(disable ? "general" : "fast   ")} \"{format}\": {sw.Elapsed.TotalMicroseconds / Iterations:F3} us/call");
                }
                finally
                {
                    KernelMemoryCompatExports.DisablePrintfFastPath = false;
                }

                GC.KeepAlive(memory);
            }
        }
    }

    private static (CpuContext Context, FakeCpuMemory Memory) Prepare(string format, ulong size, object?[] args, bool useVaList)
    {
        var memory = new FakeCpuMemory(Base, 0x4000);
        var context = new CpuContext(memory, Generation.Gen5);
        memory.TryWrite(Dest, Enumerable.Repeat((byte)0xAA, DestBytes).ToArray());
        memory.TryWrite(FormatAddress, Encoding.UTF8.GetBytes(format + "\0"));

        var values = new ulong[args.Length];
        var next = StringArea;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case null:
                    values[i] = 0;
                    break;
                case string text:
                    memory.WriteCString(next, text);
                    values[i] = next;
                    next += 0x40;
                    break;
                case ulong number:
                    values[i] = number;
                    break;
                default:
                    throw new InvalidOperationException();
            }
        }

        context[CpuRegister.Rdi] = Dest;
        context[CpuRegister.Rsi] = size;
        context[CpuRegister.Rdx] = FormatAddress;
        if (useVaList)
        {
            Assert.True(context.TryWriteUInt64(VaList, 48UL << 32));
            Assert.True(context.TryWriteUInt64(VaList + 8, RegSave + 0x80));
            Assert.True(context.TryWriteUInt64(VaList + 16, RegSave));
            // Six register slots, then overflow slots after the 176 byte save area.
            for (var i = 0; i < values.Length; i++)
            {
                var slot = i < 6 ? RegSave + (ulong)(i * 8) : RegSave + 0x80 + (ulong)((i - 6) * 8);
                Assert.True(context.TryWriteUInt64(slot, values[i]));
            }

            context[CpuRegister.Rcx] = VaList;
        }
        else
        {
            var registers = new[] { CpuRegister.Rcx, CpuRegister.R8, CpuRegister.R9 };
            for (var i = 0; i < values.Length; i++)
            {
                context[registers[i]] = values[i];
            }
        }

        return (context, memory);
    }

    private static int Invoke(CpuContext context, bool useVaList)
        => useVaList ? KernelMemoryCompatExports.Vsnprintf(context) : KernelMemoryCompatExports.Snprintf(context);

    private static (int Status, ulong Rax, byte[] Buffer) Run(
        string format,
        ulong size,
        object?[] args,
        bool useVaList,
        bool disableFastPath)
    {
        var (context, memory) = Prepare(format, size, args, useVaList);
        KernelMemoryCompatExports.DisablePrintfFastPath = disableFastPath;
        try
        {
            var status = Invoke(context, useVaList);
            var buffer = new byte[DestBytes];
            Assert.True(memory.TryRead(Dest, buffer));
            return (status, context[CpuRegister.Rax], buffer);
        }
        finally
        {
            KernelMemoryCompatExports.DisablePrintfFastPath = false;
        }
    }
}
