// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

public sealed class Gen5VertexInstanceIdSpirvTests
{
    [Theory]
    [InlineData(false, -1, 0)]
    [InlineData(true, -1, 1)]
    [InlineData(true, 2, 1)]
    public void TheInstanceIdCountsFromTheDrawsBaseInstanceWhenTheDeviceExposesIt(bool drawParameters, int startInstanceUserData, int expectedBaseInstanceReads)
    {
        var end = new Gen5ShaderInstruction(0, Gen5ShaderEncoding.Sopp, "SEndpgm", [], [], [], null);
        var program = new Gen5ShaderProgram(0, [end]);
        var plan = ShaderResourcePlan.Extract(program, ShaderStage.Vertex, 1, 8, 4, dynamicUserData: startInstanceUserData);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(resources.Info, [], false, false, false, 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            SupportsDrawParameters = drawParameters,
            StartInstanceUserData = startInstanceUserData,
        };
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);

        var module = ParseModule(shader.Spirv);
        var builtIns = module
            .Where(instruction => instruction.Opcode == SpirvOp.Decorate && instruction.Operands.Length == 3 &&
                instruction.Operands[1] == (uint)SpirvDecoration.BuiltIn)
            .ToDictionary(instruction => instruction.Operands[2], instruction => instruction.Operands[0]);
        var capabilities = module.Where(instruction => instruction.Opcode == SpirvOp.Capability).Select(instruction => instruction.Operands[0]).ToHashSet();
        var loads = module.Where(instruction => instruction.Opcode == SpirvOp.Load).ToDictionary(instruction => instruction.Operands[1], instruction => instruction.Operands[2]);
        var instanceIndex = builtIns[(uint)SpirvBuiltIn.InstanceIndex];

        if (!drawParameters)
        {
            Assert.False(builtIns.ContainsKey((uint)SpirvBuiltIn.BaseInstance));
            Assert.DoesNotContain((uint)SpirvCapability.DrawParameters, capabilities);
            return;
        }

        var baseInstance = builtIns[(uint)SpirvBuiltIn.BaseInstance];
        Assert.Contains((uint)SpirvCapability.DrawParameters, capabilities);
        Assert.Equal(expectedBaseInstanceReads, loads.Values.Count(pointer => pointer == baseInstance));
        Assert.Contains(module, instruction => instruction.Opcode == SpirvOp.ISub &&
            loads.TryGetValue(instruction.Operands[2], out var left) && left == instanceIndex &&
            loads.TryGetValue(instruction.Operands[3], out var right) && right == baseInstance);
    }

    private static IReadOnlyList<ParsedInstruction> ParseModule(byte[] spirv)
    {
        var instructions = new List<ParsedInstruction>();
        for (var offset = 5; offset < spirv.Length / sizeof(uint);)
        {
            var header = BitConverter.ToUInt32(spirv, offset * sizeof(uint));
            var wordCount = (int)(header >> 16);
            Assert.True(wordCount > 0);
            var operands = new uint[wordCount - 1];
            for (var index = 0; index < operands.Length; index++)
            {
                operands[index] = BitConverter.ToUInt32(spirv, (offset + index + 1) * sizeof(uint));
            }

            instructions.Add(new ParsedInstruction((SpirvOp)(ushort)header, operands));
            offset += wordCount;
        }

        return instructions;
    }

    private sealed record ParsedInstruction(SpirvOp Opcode, uint[] Operands);
}
