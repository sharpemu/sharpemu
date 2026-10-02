// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class DescriptorReadTests
{
    private delegate bool RangeRead(ReadOnlySpan<uint> descriptor, uint dynamicOffset,
        uint immediateOffset, ResourceRuntimeInputs inputs, Span<uint> words);

    private static RangeRead Reader => typeof(ResourceMaterializer).Assembly
        .GetType("SharpEmu.ShaderCompiler.Resources.ScalarBufferRangeRead")!
        .GetMethod("TryRead", BindingFlags.Static | BindingFlags.NonPublic)!.CreateDelegate<RangeRead>();

    [Fact]
    public void ContiguousDescriptor_UsesOneReadAndPreservesWords()
    {
        var calls = 0;
        var inputs = new ResourceRuntimeInputs
        {
            ReadCleanWords = (ulong address, Span<uint> words) =>
            {
                Assert.Equal(0x1020UL, address);
                calls++;
                for (var i = 0; i < words.Length; i++) words[i] = (uint)(100 + i);
                return true;
            },
        };
        var result = new uint[8];
        Assert.True(Reader([0x1000, 0, 64, 0], 32, 0, inputs, result));
        Assert.Equal(1, calls);
        Assert.Equal(Enumerable.Range(100, 8).Select(i => (uint)i), result);
    }

    [Theory]
    [InlineData(48u, 0u)] // Partial OOB must retain individual zero-filling semantics.
    [InlineData(0u, uint.MaxValue)] // Immediate offsets must not wrap.
    public void OutOfBoundsRange_DoesNotReadGuestMemory(uint offset, uint immediate)
    {
        var calls = 0;
        var inputs = new ResourceRuntimeInputs { ReadCleanWords = (ulong _, Span<uint> _) => { calls++; return true; } };
        Assert.False(Reader([0x1000, 0, 64, 0], offset, immediate, inputs, new uint[8]));
        Assert.Equal(0, calls);
    }

    [Fact]
    public void GpuOwnedRange_RefusesBulkRead()
    {
        var inputs = new ResourceRuntimeInputs { ReadCleanWords = (ulong _, Span<uint> _) => false };
        Assert.False(Reader([0x1000, 0, 64, 0], 0, 0, inputs, new uint[8]));
    }

    [Fact]
    public void ChangingObservation_IsUncacheableAndRepeatedReadsAreDeduplicated()
    {
        var type = typeof(ResourceMaterializationCache).GetNestedType("ReadRecorder", BindingFlags.NonPublic)!;
        var recorder = Activator.CreateInstance(type, nonPublic: true)!;
        uint value = 1;
        GuestWordReader source = (ulong _, out uint word) => { word = value; return true; };
        var reader = (GuestWordReader)type.GetMethod("Wrap", [typeof(GuestWordReader), typeof(bool)])!
            .Invoke(recorder, [source, true])!;
        Assert.True(reader(0x1000, out _));
        Assert.True(reader(0x1000, out _));
        var reads = (System.Collections.ICollection)type.GetProperty("Reads")!.GetValue(recorder)!;
        Assert.Single(reads.Cast<object>());
        Assert.False((bool)type.GetProperty("Failed")!.GetValue(recorder)!);
        value = 2;
        Assert.True(reader(0x1000, out _));
        Assert.True((bool)type.GetProperty("Failed")!.GetValue(recorder)!);
    }
}
