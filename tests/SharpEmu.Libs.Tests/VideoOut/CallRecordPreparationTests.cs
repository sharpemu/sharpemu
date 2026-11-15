// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

public class CallRecordPreparationTests
{
    // Off: the original preparation. The derived classes run every case with call record spans and with their verify.
    internal virtual VulkanVideoPresenter.CallRecordSpanMode Mode => VulkanVideoPresenter.CallRecordSpanMode.Off;

    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Type Presenter = typeof(VulkanVideoPresenter).GetNestedType("Presenter", BindingFlags.NonPublic)!;
    private const ulong Target = 0x2000;
    private const ulong Argument = 0x1000;

    // Exercise the actual row construction without creating a Vulkan device. Empty image/sampler fields make this a
    // CPU-only preparation path, while the resulting words are the table the call-record shader search consumes.
    private (uint[] Words, DeviceAddressRange[] Ranges) Prepare(FakeCpuMemory memory, CallRecordBuffer[] buffers,
        params (ulong Target, ulong Argument)[] records)
    {
        VulkanVideoPresenter.CallRecordSpanOverride = Mode;
        try
        {
            return PrepareIn(memory, buffers, records);
        }
        finally
        {
            VulkanVideoPresenter.CallRecordSpanOverride = null;
        }
    }

    private static (uint[] Words, DeviceAddressRange[] Ranges) PrepareIn(FakeCpuMemory memory, CallRecordBuffer[] buffers,
        (ulong Target, ulong Argument)[] records)
    {
        var presenter = RuntimeHelpers.GetUninitializedObject(Presenter);
        Presenter.GetField("_guestMemory", Members)!.SetValue(presenter, memory);
        var program = new ShaderProgramInfo { CallRecordCallees = [new CallRecordCallee([], buffers, 64)] };
        var stage = new ShaderStageResources(program, new ResourceSnapshot()) { CallTargets = [Target], CallRecords = records };
        return ((uint[] Words, DeviceAddressRange[] Ranges))Presenter.GetMethod("PrepareCallRecords", Members)!.Invoke(presenter, [program, stage])!;
    }

    private static void WriteDescriptor(FakeCpuMemory memory, ulong argument, ulong address, uint size)
    {
        Span<byte> bytes = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)address);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[4..], (uint)(address >> 32));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[8..], size);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes[12..], 0);
        Assert.True(memory.TryWrite(argument, bytes));
    }

    [Fact]
    public void ARecordWithoutBufferFieldsKeepsItsKeyAndArgumentRange()
    {
        var memory = new FakeCpuMemory(Argument, 0x2000);
        var result = Prepare(memory, [], (Target, Argument));
        Assert.Equal(1u, result.Words[0]);
        Assert.Equal((uint)Argument, result.Words[1]);
        Assert.Equal(0u, result.Words[2]);
        Assert.Equal(0u, result.Words[3]);
        Assert.Contains(result.Ranges, range => range.Base == Argument && range.Size == 64 && !range.Written);
    }

    [Theory]
    [InlineData(0x8000ul, 64u, false, true, true)]
    [InlineData(0x8000ul, 64u, true, true, true)]
    [InlineData(1ul << 40, 64u, false, true, false)]
    [InlineData(1ul << 40, 64u, true, false, false)]
    [InlineData(0ul, 64u, false, true, false)]
    [InlineData(0ul, 64u, true, true, false)]
    [InlineData(0x8000ul, 0u, false, true, false)]
    [InlineData(0x8000ul, 0u, true, true, false)]
    public void BufferPolicyControlsTheUploadedRowAndResidentRange(ulong address, uint size, bool written,
        bool keep, bool resident)
    {
        var memory = new FakeCpuMemory(Argument, 0x2000);
        WriteDescriptor(memory, Argument, address, size);
        var result = Prepare(memory, [new CallRecordBuffer(0, written)], (Target, Argument));
        Assert.Equal(keep ? 1u : 0u, result.Words[0]);
        if (!keep)
        {
            Assert.Empty(result.Ranges);
            return;
        }

        Assert.Equal((uint)Argument, result.Words[1]);
        Assert.Equal(0u, result.Words[2]);
        Assert.Equal(0u, result.Words[3]);
        Assert.Contains(result.Ranges, range => range.Base == Argument && range.Size == 64 && !range.Written);
        Assert.Equal(resident, result.Ranges.Any(range => range.Base == address && range.Size == size && range.Written == written));
    }

    [Fact]
    public void AnInvalidWritableRecordDoesNotRemoveTheValidRecordBesideIt()
    {
        var memory = new FakeCpuMemory(Argument, 0x2000);
        WriteDescriptor(memory, Argument, 0x8000, 64);
        WriteDescriptor(memory, Argument + 0x100, 1ul << 40, 64);
        var result = Prepare(memory, [new CallRecordBuffer(0, true)],
            (Target, Argument + 0x100), (Target, Argument));
        Assert.Equal(1u, result.Words[0]);
        Assert.Equal((uint)Argument, result.Words[1]);
        Assert.Contains(result.Ranges, range => range.Base == 0x8000 && range.Written);
        Assert.DoesNotContain(result.Ranges, range => range.Base == Argument + 0x100);
    }

    [Fact]
    public void AnUnreadableFieldPreservesTheExistingKeepRecordPolicy()
    {
        var memory = new FakeCpuMemory(Argument, 0x2000);
        var result = Prepare(memory, [new CallRecordBuffer(0, true)], (Target, 0x5000));
        Assert.Equal(1u, result.Words[0]);
        Assert.Equal(0x5000u, result.Words[1]);
        Assert.Single(result.Ranges);
        Assert.Equal(0x5000ul, result.Ranges[0].Base);
    }
}

public sealed class SpannedCallRecordPreparationTests : CallRecordPreparationTests
{
    internal override VulkanVideoPresenter.CallRecordSpanMode Mode => VulkanVideoPresenter.CallRecordSpanMode.On;
}
