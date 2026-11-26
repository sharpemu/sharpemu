// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using Mode = SharpEmu.Libs.VideoOut.VulkanVideoPresenter.CallRecordSpanMode;

namespace SharpEmu.Libs.Tests.VideoOut;

// Call record spans (VulkanVideoPresenter.CallRecordSpans.cs): on the same memory the preparation hands the bindless heap the
// same descriptor words in the same order, and writes the same words table and merged ranges, as the original; verify keeps
// the separate read and counts a difference.
public sealed class CallRecordSpanTests
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Presenter = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;
    private const ulong Base = 0x10000;
    private const int Size = 0x10000;

    private sealed record Prepared(uint[] Words, DeviceAddressRange[] Ranges, List<string> Elements);

    // The elements stand in for the bindless heap (which needs a device): each call is listed with its slot and words and
    // gets the next number.
    private static Prepared Prepare(Mode mode, ICpuMemory memory, CallRecordCallee[] callees, ulong[] targets,
        (ulong Target, ulong Argument)[] records, DeviceAddressRange[]? stageRanges = null)
    {
        var elements = new List<string>();
        VulkanVideoPresenter.CallRecordSpanOverride = mode;
        VulkanVideoPresenter.CallRecordElementForTests = (slot, words) =>
        {
            elements.Add($"{slot}:{string.Join(",", words)}");
            return (uint)elements.Count;
        };
        try
        {
            var presenter = RuntimeHelpers.GetUninitializedObject(Presenter);
            Presenter.GetField("_guestMemory", Members)!.SetValue(presenter, memory);
            var program = new ShaderProgramInfo { CallRecordCallees = callees };
            var stage = new ShaderStageResources(program, new ResourceSnapshot())
            {
                CallTargets = targets,
                CallRecords = records,
                CallRecordRanges = stageRanges,
            };
            (uint[] Words, DeviceAddressRange[] Ranges) prepared;
            try
            {
                prepared = ((uint[], DeviceAddressRange[]))Presenter.GetMethod("PrepareCallRecords", Members)!.Invoke(presenter, [program, stage])!;
            }
            catch (TargetInvocationException exception) when (exception.InnerException is not null)
            {
                throw exception.InnerException;
            }

            return new(prepared.Words, prepared.Ranges, elements);
        }
        finally
        {
            VulkanVideoPresenter.CallRecordSpanOverride = null;
            VulkanVideoPresenter.CallRecordElementForTests = null;
        }
    }

    private static void Same(Prepared expected, Prepared actual)
    {
        Assert.Equal(expected.Words, actual.Words);
        Assert.Equal(expected.Ranges, actual.Ranges);
        Assert.Equal(expected.Elements, actual.Elements);
    }

    // Callees whose fields and V#s lie within one span, one whose span is too wide to read whole, one that reads nothing.
    private static readonly CallRecordCallee[] Callees =
    [
        new([new CallRecordField(0, false, 1), new CallRecordField(32, true, 7)], [new CallRecordBuffer(48, false)], 64),
        new([], [new CallRecordBuffer(0, true), new CallRecordBuffer(16, false)], 32),
        new([new CallRecordField(0x100, false, 2)], [new CallRecordBuffer(0, false)], 0),
        new([new CallRecordField(0, false, 3)], [new CallRecordBuffer(0x300, false)], 16),
        new([], [], 16),
    ];

    private static readonly ulong[] Targets = [0x2000, 0x3000, 0x4000, 0x5000, 0x6000];

    private static void Descriptor(FakeCpuMemory memory, ulong at, ulong address, uint records, uint stride = 0)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)(address >> 32) | stride << 16);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], records);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], 0);
        memory.TryWrite(at, bytes);
    }

    // Random memory with valid, null and invalid V#s, and records whose spans lie inside the region, cross its end or lie past
    // it, with unknown targets and repeated arguments.
    private static (FakeCpuMemory Memory, (ulong, ulong)[] Records) RandomRecords(int seed, int count)
    {
        var random = new System.Random(seed);
        var memory = new FakeCpuMemory(Base, Size);
        var noise = new byte[Size];
        random.NextBytes(noise);
        memory.TryWrite(Base, noise);
        var records = new List<(ulong, ulong)>();
        for (var index = 0; index < count; index++)
        {
            var argument = Base + (ulong)random.Next(0, Size + 0x400) / 16 * 16;
            if (random.Next(4) == 0 && records.Count != 0) argument = records[random.Next(records.Count)].Item2;
            var target = random.Next(12) == 0 ? 0x9000ul : Targets[random.Next(Targets.Length)];
            if (random.Next(3) == 0)
            {
                // A V# that describes a buffer at each of the callees' buffer immediates.
                foreach (var immediate in new uint[] { 0, 16, 48, 0x300 })
                    Descriptor(memory, argument + immediate, 0x8000 + (ulong)random.Next(0x1000) * 16, (uint)random.Next(1, 64), 16);
            }
            else if (random.Next(3) == 0)
            {
                // Null V#s, and a written one that describes no buffer.
                Descriptor(memory, argument, 0, 0);
                Descriptor(memory, argument + 16, 1ul << 40, 64);
            }

            if (!records.Contains((target, argument))) records.Add((target, argument));
        }

        return (memory, [.. records]);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(2, 64)]
    [InlineData(3, 300)]
    [InlineData(4, 2000)]
    public void SpansPrepareWhatTheOriginalPrepares(int seed, int count)
    {
        var (memory, records) = RandomRecords(seed, count);
        DeviceAddressRange[] stageRanges = [new(3, 0x8000, 0x40, true, false), new(4, 0x9000, 0x20, true, true)];
        var original = Prepare(Mode.Off, memory, Callees, Targets, records, stageRanges);
        Same(original, Prepare(Mode.On, memory, Callees, Targets, records, stageRanges));
        Assert.NotEmpty(original.Elements);
        Assert.True(original.Words[0] > 0);
    }

    // A record whose span crosses the end of mapped memory is read field by field: the fields inside still read, and the
    // ones past the end take the null element, as in the original.
    [Fact]
    public void ASpanPastMappedMemoryReadsFieldByField()
    {
        var memory = new FakeCpuMemory(Base, Size);
        var argument = Base + Size - 40;
        Descriptor(memory, argument + 16, 0x8000, 4, 16);
        var records = new[] { (Targets[1], argument), (Targets[0], argument) };
        var original = Prepare(Mode.Off, memory, Callees, Targets, records);
        var spanned = Prepare(Mode.On, memory, Callees, Targets, records);
        Same(original, spanned);
        Assert.Contains(spanned.Ranges, range => range.Base == 0x8000 && range.Size == 64);
        Assert.Single(spanned.Elements);
        Assert.Equal(BindlessHeap.CallRecordNullIndex, spanned.Words[^1]);
    }

    [Fact]
    public void SpansAreOnUnlessConfigured()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_CALL_RECORD_SPANS") is not null) return;
        Assert.Equal(Mode.On, VulkanVideoPresenter.CallRecordSpans);
    }
}
