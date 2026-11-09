// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DemonsSoulsProbeTests(ITestOutputHelper output)
{
    private const ulong ShaderAddress = 0x1000;

    [Theory]
    [InlineData("59775BB47AD2848A")]
    [InlineData("E005B9EB0E74B0A3")]
    public void Probe(string hash)
    {
        var path = Environment.GetEnvironmentVariable("SHARPEMU_PROBE_DIR");
        if (string.IsNullOrEmpty(path)) return;
        var words = File.ReadAllText(Path.Combine(path, hash + ".words.txt")).Split(',')
            .Select(word => Convert.ToUInt32(word, 16)).ToArray();
        var bytes = new byte[words.Length * 4];
        for (var index = 0; index < words.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * 4), words[index]);
        var memory = new ProbeMemory(bytes);
        Assert.True(Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(memory, Generation.Gen5), ShaderAddress, out var program, out var error), error);
        var writer = new StringWriter();
        var previous = Console.Error;
        Console.SetError(writer);
        try
        {
            var plan = ShaderResourcePlan.Extract(program!, ShaderStage.Compute, Convert.ToUInt64(hash, 16), 0, 2);
            output.WriteLine($"OK images={plan.Info.Images.Count}");
        }
        catch (Exception exception)
        {
            output.WriteLine("FAIL " + exception.Message);
        }
        finally
        {
            Console.SetError(previous);
            output.WriteLine(writer.ToString());
        }
    }

    private sealed class ProbeMemory(byte[] bytes) : ICpuMemory
    {
        public bool TryRead(ulong address, Span<byte> destination)
        {
            if (address < ShaderAddress || address - ShaderAddress + (ulong)destination.Length > (ulong)bytes.Length)
                return false;
            bytes.AsSpan((int)(address - ShaderAddress), destination.Length).CopyTo(destination);
            return true;
        }

        public bool CanRead(ulong address, ulong size) =>
            address >= ShaderAddress && size <= (ulong)bytes.Length && address - ShaderAddress <= (ulong)bytes.Length - size;

        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => false;
    }
}
