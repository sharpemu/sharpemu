// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// A guest buffer can span more than 2 GiB (a geometry pool of 0xAB000000 bytes): the program reads a word near the start
// and one near the end of the binding.
public sealed unsafe class LargeStorageBindingDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    private const uint NearOffset = 0x100;
    private const uint NearValue = 0x11223344;
    private const uint FarValue = 0x55667788;

    [Theory]
    [InlineData(0x7FFF_0000UL)]
    [InlineData(0x8001_0000UL)]
    [InlineData(0xAB00_0000UL)]
    public void LoadsReadBothEndsOfTheBinding(ulong range)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan)) return;
        if (vulkan.DeviceInfo.MaxStorageBufferRange < range)
        {
            Assert.False(GatePrerequisites.DeviceRequired && range < 0x8000_0000UL, "The device limits storage bindings below 2 GiB.");
            return;
        }

        var farOffset = (uint)(range - 0x100);
        var program = Program(
            MoveVector(0, 0, farOffset),
            BufferAccess(8, "BufferLoadDword", 4, offset: (int)NearOffset, vectorData: 4),
            BufferAccess(16, "BufferLoadDword", 4, offset: 0, vectorData: 5, offsetEnabled: true, vectorAddress: 0),
            BufferAccess(24, "BufferStoreDword", 8, offset: 0, vectorData: 4),
            BufferAccess(32, "BufferStoreDword", 8, offset: 4, vectorData: 5),
            EndProgram(40));
        var (plan, resources, layout) = Prepare(program);
        var request = new ShaderCompileRequest(plan, resources, layout) { LocalSizeX = 32, ThreadCountX = 32, WaveSize = 32 };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        using var pool = new GpuBuffer(harness.Device, harness.Scheduler, GpuBufferUsage.DeviceLocal, 0, GpuBuffer.AllFlags, range);
        var result = runner.CreateBuffer(64);
        var registers = new uint[256];
        registers[4] = 0x1000; registers[6] = (uint)range;
        registers[8] = 0x2000; registers[10] = 64;
        harness.Run(() =>
        {
            var command = new CommandBuffer(harness.Scheduler.Current.Handle);
            var near = NearValue;
            var far = FarValue;
            harness.Vk.CmdUpdateBuffer(command, pool.Handle, NearOffset, sizeof(uint), &near);
            harness.Vk.CmdUpdateBuffer(command, pool.Handle, farOffset, sizeof(uint), &far);
            runner.Dispatch(registers,
                new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [pool, result] }, 1,
                bufferRanges: [range, 64]);
        });

        var words = runner.ReadBack(result, 0, 8);
        Assert.Equal(NearValue, BinaryPrimitives.ReadUInt32LittleEndian(words));
        Assert.Equal(FarValue, BinaryPrimitives.ReadUInt32LittleEndian(words.AsSpan(4)));
        harness.AssertNoValidationMessages();
    }
}
