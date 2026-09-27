// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Images;
using Silk.NET.Vulkan;
using Xunit;
using Xunit.Abstractions;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class NativeHalfConversionProbeDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    // The probe is what decides whether the translator may use GLSL UnpackHalf2x16 /
    // PackHalf2x16, so the gate is that it completes and reports a count - not that this
    // device passes it. The verdict goes to the test output, where a port to another GPU
    // can read it.
    [Fact]
    public void ProbeRunsAndReportsWhetherNativeHalfConversionIsExact()
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        using var harness = new ImageTestHarness(vulkan);

        var result = default(NativeHalfConversionProbe.Result);
        harness.Run(() => result = NativeHalfConversionProbe.Run(
            harness.Device,
            harness.Scheduler,
            () => new CommandBuffer(harness.Scheduler.Current.Handle)));

        output.WriteLine(
            $"[VK][F16] native half conversion exact={result.Exact} mismatches={result.Mismatches} " +
            $"tested={result.Tested} device={vulkan.DeviceName} first={result.Detail}");
        Assert.Null(result.Note);
        Assert.True(result.Tested >= 2 * 0x1_0000, $"tested={result.Tested}");
        Assert.InRange(result.Mismatches, 0, result.Tested);
        Assert.Equal(result.Mismatches == 0, result.Exact);
        harness.AssertNoValidationMessages();
    }

    // Every test vector must agree with System.Half, which is what proves the integer
    // sequences the probe compares against are the right reference in the first place.
    [Fact]
    public void ReferenceConversionsMatchSystemHalfForEveryTestVector()
    {
        foreach (var value in HalfConversionReference.BuildTestVectors())
        {
            var half = BitConverter.UInt16BitsToHalf((ushort)value);
            var widened = BitConverter.SingleToUInt32Bits((float)half);
            var wide = HalfConversionReference.HalfToFloatBits(value & 0xFFFFu);
            Assert.True(
                HalfConversionReference.Matches(widened, wide, asHalf: false),
                $"half 0x{value & 0xFFFF:X4} widened to 0x{wide:X8}, System.Half says 0x{widened:X8}");

            var single = BitConverter.UInt32BitsToSingle(value);
            var narrowed = BitConverter.HalfToUInt16Bits((Half)single);
            var narrow = HalfConversionReference.FloatToHalfBits(value);
            Assert.True(
                HalfConversionReference.Matches(narrowed, narrow, asHalf: true),
                $"float 0x{value:X8} narrowed to 0x{narrow:X4}, System.Half says 0x{narrowed:X4}");
        }
    }
}
