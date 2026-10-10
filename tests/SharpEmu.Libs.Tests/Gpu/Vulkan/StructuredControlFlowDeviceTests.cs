// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Tests.Gpu.Images;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.Libs.Tests.Gpu.Vulkan;

// Runs each control-flow program through both the structured form and the PC dispatcher on the
// device, and checks both against the reference model.
public sealed class StructuredControlFlowDeviceTests(HeadlessVulkanFixture fixture) : IClassFixture<HeadlessVulkanFixture>
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var name in new[]
                 {
                     nameof(Gen5StructuredControlFlowTests.LoopWithIfElse),
                     nameof(Gen5StructuredControlFlowTests.NestedLoopsWithEarlyExit),
                     nameof(Gen5StructuredControlFlowTests.NestedIfSharedMerge),
                     nameof(Gen5StructuredControlFlowTests.IrreducibleCycle),
                 })
        {
            yield return [name];
        }
    }

    private static readonly (uint Input0, uint Input1)[] Inputs =
    [
        (0, 0), (1, 0), (2, 1), (3, 3), (4, 6), (5, 7), (6, 9), (7, 20), (8, 1000), (9, 16),
    ];

    [Theory]
    [MemberData(nameof(Cases))]
    public void StructuredAndDispatcherFormsMatchTheModel(string name)
    {
        var vulkan = fixture.Vulkan;
        if (!GatePrerequisites.Ready(vulkan, shaderInt64: true)) return;
        foreach (var forceDispatcher in new[] { false, true })
        {
            var (plan, resources, layout) = Prepare(Gen5StructuredControlFlowTests.Create(name));
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                LocalSizeX = 1,
                ThreadCountX = 1,
                ForceDispatcher = forceDispatcher,
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            using var harness = new ImageTestHarness(vulkan);
            using var runner = new LayoutComputeRunner(harness, request, shader.Spirv);
            var output = runner.CreateBuffer(64);
            // Every store names the same V#; the planner may still give each path its own binding.
            var bindings = new Dictionary<DescriptorBindingKind, GpuBuffer[]>
            {
                [DescriptorBindingKind.Buffers] = Enumerable.Repeat(output, Math.Max(1, plan.Info.Buffers.Count)).ToArray(),
            };
            var registers = new uint[256];
            registers[6] = 64;
            foreach (var (input0, input1) in Inputs)
            {
                registers[8] = input0;
                registers[9] = input1;
                harness.Run(() => runner.Dispatch(registers, bindings, 1));
                var actual = runner.ReadBack(output, 0, 8);
                var expected = Gen5StructuredControlFlowTests.Expected(name, input0, input1);
                var form = forceDispatcher ? "dispatcher" : "structured";
                Assert.True(
                    expected.Acc == BinaryPrimitives.ReadUInt32LittleEndian(actual) &&
                    expected.Second == BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4)),
                    $"{name} {form} inputs=({input0},{input1}) expected=({expected.Acc},{expected.Second}) " +
                    $"actual=({BinaryPrimitives.ReadUInt32LittleEndian(actual)},{BinaryPrimitives.ReadUInt32LittleEndian(actual.AsSpan(4))})");
            }

            harness.AssertNoValidationMessages();
        }
    }
}
