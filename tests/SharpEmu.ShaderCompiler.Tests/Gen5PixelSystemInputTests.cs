// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

public sealed class Gen5PixelSystemInputTests
{
    private const uint FrontFaceInput = 1u << 12;
    private const uint AncillaryInput = 1u << 13;
    private const uint TargetLayerInputLayout =
        (1u << 1) | // PERSP_CENTER occupies VGPR0..1.
        (1u << 8) | // POS_X occupies VGPR2.
        (1u << 9) | // POS_Y occupies VGPR3.
        AncillaryInput; // ANCILLARY occupies VGPR4.

    [Fact]
    public void EarlyDepthEmitsEarlyFragmentTestsExecutionMode()
    {
        var shader = Compile(0, 0, earlyDepth: true);

        Assert.Contains(
            Instructions(shader.Spirv),
            instruction =>
                instruction.Opcode == SpirvOp.ExecutionMode &&
                instruction.Operands.Length >= 2 &&
                instruction.Operands[1] ==
                    (uint)SpirvExecutionMode.EarlyFragmentTests);

        ValidateWhenAvailable(shader.Spirv);
    }

    [Fact]
    public void FrontFaceInputStoresOneOrZeroIntoGuestRegister()
    {
        var shader = Compile(FrontFaceInput, FrontFaceInput);
        var instructions = Instructions(shader.Spirv);

        var frontFaceDecoration = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] ==
                    (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.FrontFacing);
        var frontFaceInput = frontFaceDecoration.Operands[0];
        var frontFaceVariable = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands[1] == frontFaceInput &&
                instruction.Operands[2] ==
                    (uint)SpirvStorageClass.Input);
        var frontFacePointer = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.TypePointer &&
                instruction.Operands[0] == frontFaceVariable.Operands[0]);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.TypeBool &&
                instruction.Operands[0] == frontFacePointer.Operands[2]);

        var frontFaceLoad = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands[^1] == frontFaceInput);
        var constants = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Constant)
            .ToDictionary(
                instruction => instruction.Operands[1],
                instruction => instruction.Operands[2]);
        var frontFaceSelect = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Select &&
                instruction.Operands[2] == frontFaceLoad.Operands[1] &&
                constants.TryGetValue(instruction.Operands[3], out var front) &&
                front == 0x3f800000u &&
                constants.TryGetValue(instruction.Operands[4], out var back) &&
                back == 0u);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                instruction.Operands[1] == frontFaceSelect.Operands[1]);

        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(FrontFaceInput, 0u)]
    [InlineData(0u, FrontFaceInput)]
    public void InactiveFrontFaceInputDoesNotDeclareBuiltIn(
        uint address,
        uint enable)
    {
        var shader = Compile(address, enable);

        Assert.DoesNotContain(
            Instructions(shader.Spirv),
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] ==
                    (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.FrontFacing);
    }

    [Fact]
    public void AncillaryInputPacksFragmentLayerIntoGuestBits()
    {
        var shader = Compile(
            TargetLayerInputLayout,
            TargetLayerInputLayout);
        var instructions = Instructions(shader.Spirv);

        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Capability &&
                instruction.Operands[0] ==
                    (uint)SpirvCapability.ShaderViewportIndexLayerExt);
        var layerDecoration = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] ==
                    (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.Layer);
        var layerInput = layerDecoration.Operands[0];
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 2 &&
                instruction.Operands[0] == layerInput &&
                instruction.Operands[1] ==
                    (uint)SpirvDecoration.Flat);
        var layerVariable = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Variable &&
                instruction.Operands[1] == layerInput &&
                instruction.Operands[2] ==
                    (uint)SpirvStorageClass.Input);
        var layerPointer = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.TypePointer &&
                instruction.Operands[0] == layerVariable.Operands[0]);
        Assert.Equal(
            (uint)SpirvStorageClass.Input,
            layerPointer.Operands[1]);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.TypeInt &&
                instruction.Operands[0] == layerPointer.Operands[2] &&
                instruction.Operands[1] == 32 &&
                instruction.Operands[2] == 0);

        var layerLoad = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Load &&
                instruction.Operands[^1] == layerInput);
        var constants = instructions
            .Where(instruction => instruction.Opcode == SpirvOp.Constant)
            .ToDictionary(
                instruction => instruction.Operands[1],
                instruction => instruction.Operands[2]);
        bool IsConstant(uint id, uint value) =>
            constants.TryGetValue(id, out var actual) && actual == value;
        var packedLayer = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.ShiftLeftLogical &&
                instruction.Operands[2] == layerLoad.Operands[1] &&
                IsConstant(instruction.Operands[3], 16));
        var ancillaryStore = Assert.Single(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.Store &&
                instruction.Operands[1] == packedLayer.Operands[1]);
        Assert.Contains(
            instructions,
            instruction =>
                instruction.Opcode == SpirvOp.AccessChain &&
                instruction.Operands[1] == ancillaryStore.Operands[0] &&
                IsConstant(instruction.Operands[^1], 4));

        ValidateWhenAvailable(shader.Spirv);
    }

    [Theory]
    [InlineData(AncillaryInput, 0u)]
    [InlineData(0u, AncillaryInput)]
    public void InactiveAncillaryInputDoesNotDeclareLayer(
        uint address,
        uint enable)
    {
        var shader = Compile(address, enable);

        Assert.DoesNotContain(
            Instructions(shader.Spirv),
            instruction =>
                instruction.Opcode == SpirvOp.Decorate &&
                instruction.Operands.Length == 3 &&
                instruction.Operands[1] ==
                    (uint)SpirvDecoration.BuiltIn &&
                instruction.Operands[2] ==
                    (uint)SpirvBuiltIn.Layer);
    }

    private static Gen5SpirvShader Compile(
        uint address,
        uint enable,
        bool earlyDepth = false)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.EndProgram(0));
        var (plan, resources, layout) = ResourceTestProgram.Prepare(
            program,
            ShaderStage.Pixel,
            userDataCount: 0);
        var request = new ShaderCompileRequest(plan, resources, layout)
        {
            PixelInputAddress = address,
            PixelInputEnable = enable,
            PixelEarlyDepth = earlyDepth,
        };

        Assert.True(
            Gen5SpirvTranslator.TryCompileProgram(
                request,
                out var shader,
                out var error),
            error);
        return shader;
    }

    private static List<Instruction> Instructions(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var result = new List<Instruction>();
        for (var index = 5; index < words.Length;)
        {
            var count = (int)(words[index] >> 16);
            result.Add(
                new Instruction(
                    (SpirvOp)(words[index] & 0xffff),
                    words[(index + 1)..(index + count)]));
            index += count;
        }

        return result;
    }

    private static void ValidateWhenAvailable(byte[] code)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (string.IsNullOrWhiteSpace(sdk))
        {
            return;
        }

        var executable = Path.Combine(
            sdk,
            OperatingSystem.IsWindows()
                ? "Bin/spirv-val.exe"
                : "bin/spirv-val");
        if (!File.Exists(executable))
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, code);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add("--target-env");
            start.ArgumentList.Add("vulkan1.2");
            start.ArgumentList.Add(path);
            using var process = Process.Start(start)!;
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed record Instruction(
        SpirvOp Opcode,
        uint[] Operands);
}
