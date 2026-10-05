// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using Xunit.Abstractions;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

public sealed class WaveShuffleDeviceTests(HeadlessVulkanFixture fixture, ITestOutputHelper output)
    : IClassFixture<HeadlessVulkanFixture>
{
    [Theory]
    [InlineData("Dpp8")]
    [InlineData("Dpp16")]
    [InlineData("Permlane16")]
    [InlineData("Permlanex16")]
    [InlineData("Reduction")]
    public void Wave64Shuffle_PreservesUpperHalf(string operation)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Gen5ShaderInstruction instruction)
        {
            instructions.Add(instruction with { Pc = pc });
            pc += 8;
        }

        Add(Vop2(0, "VLshlrevB32", 2, Operand(3), Gen5Operand.Vector(1)));
        Add(Vop2(0, "VAddI32", 2, Gen5Operand.Vector(0), Gen5Operand.Vector(2)));
        Add(Vop2(0, "VAddI32", 3, Operand(1), Gen5Operand.Vector(2)));
        switch (operation)
        {
            case "Dpp8":
                Add(Vop1(0, "VMovB32", 4, Gen5Operand.Vector(3)) with
                {
                    Control = new Gen5Dpp8Control(0x053977, FetchInactive: true), // Reverse each eight lanes.
                });
                break;
            case "Dpp16":
                Add(Vop1(0, "VMovB32", 4, Gen5Operand.Vector(3)) with
                {
                    Control = new Gen5DppControl(0x140, true, false, 0, 0, 15, 15),
                });
                break;
            case "Permlane16":
            case "Permlanex16":
                Add(Vop3(0, operation == "Permlane16" ? "VPermlane16B32" : "VPermlanex16B32",
                    4, Gen5Operand.Vector(3), Operand(uint.MaxValue), Operand(uint.MaxValue)));
                break;
            case "Reduction":
                // The game's tile histogram sums four rows, then combines lanes 31 and 63.
                Add(Vop1(0, "VMovB32", 4, Gen5Operand.Vector(3)));
                foreach (var shift in new uint[] { 1, 2, 4, 8 })
                    Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(4), Gen5Operand.Vector(4)) with
                    {
                        Control = new Gen5DppControl(0x110 + shift, false, false, 0, 0, 15, 15),
                    });
                Add(Vop3(0, "VPermlanex16B32", 3,
                    Gen5Operand.Vector(4), Operand(uint.MaxValue), Operand(uint.MaxValue)));
                Add(Vop2(0, "VAddU32", 4, Gen5Operand.Vector(4), Gen5Operand.Vector(3)));
                Add(ReadLane(0, 20, 4, 31));
                Add(ReadLane(0, 21, 4, 63));
                Add(Sop2(0, "SAddU32", 22, Gen5Operand.Scalar(20), Gen5Operand.Scalar(21)));
                Add(MoveVectorFromScalar(0, 4, 22));
                break;
        }
        Add(Vop2(0, "VLshlrevB32", 5, Operand(2), Gen5Operand.Vector(2)));
        Add(BufferAccess(0, "BufferStoreDword", 4, vectorData: 4, offsetEnabled: true, vectorAddress: 5));
        Add(EndProgram(0));
        var (plan, resources, layout) = Prepare(Program([.. instructions]));
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            LocalSizeX = 8, LocalSizeY = 8, ThreadCountX = 8, ThreadCountY = 8, WaveSize = 64,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        using var harness = new ImageTestHarness(vulkan);
        using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
        var result = runner.CreateBuffer(256);
        var registers = new uint[256];
        registers[6] = 256;
        harness.Run(() => runner.Dispatch(registers,
            new Dictionary<DescriptorBindingKind, GpuBuffer[]> { [DescriptorBindingKind.Buffers] = [result] }, 1));
        var actual = runner.ReadBack(result, 0, 256);
        for (uint lane = 0; lane < 64; lane++)
        {
            var expected = operation switch
            {
                "Dpp8" => (lane ^ 7) + 1,
                "Dpp16" => (lane ^ 15) + 1,
                "Permlane16" => (lane & ~15u) + 16,
                "Permlanex16" => ((lane & ~15u) ^ 16) + 16,
                "Reduction" => 64u * 65 / 2,
                _ => throw new ArgumentOutOfRangeException(nameof(operation)),
            };
            Assert.Equal(expected, BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan((int)lane * 4)));
        }
        harness.AssertNoValidationMessages();
        output.WriteLine($"Verified {operation} on {vulkan.DeviceName}.");
    }
}
