// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

// Internal resolution scaling: the module has to translate the pixel position, integer
// image coordinates and size queries between guest pixels and host texels.
public sealed class Gen5RenderScaleTests
{
    private const uint PositionXBit = 1u << 8;
    private const uint PositionYBit = 1u << 9;
    private const uint PositionZBit = 1u << 10;

    [Fact]
    public void RenderScaleDwordsFollowTheDispatchLimits()
    {
        var info = new ShaderResourceInfo();
        var plain = BindingLayout.Allocate(info, [0u], false, false, false);
        var scaled = BindingLayout.Allocate(info, [0u], false, false, false, 0, false, usesRenderScale: true);
        var both = BindingLayout.Allocate(info, [0u], false, false, false, 0, true, usesRenderScale: true);

        Assert.False(plain.UsesRenderScale);
        Assert.True(scaled.UsesRenderScale);
        Assert.Equal(plain.ShaderDataDwordCount + BindingLayout.RenderScaleDwordCount, scaled.ShaderDataDwordCount);
        Assert.Equal(plain.ShaderDataDwordCount, scaled.RenderScaleDword);
        Assert.Equal(plain.ShaderDataDwordCount + 3, both.RenderScaleDword);
        Assert.NotEqual(plain, scaled);

        // The validator recomputes the layout from the flag, so declarations never drift.
        BindingLayoutValidator.Validate(plain, info, [0u], false, false, false, 0, ShaderStage.Pixel);
        BindingLayoutValidator.Validate(scaled, info, [0u], false, false, false, 0, ShaderStage.Pixel);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PixelPositionIsDividedByTheAttachmentFactor(bool usesRenderScale)
    {
        var spirv = CompilePixelPosition(usesRenderScale);
        var instructions = Parse(spirv);
        var fragCoord = Load(instructions, FragCoordVariable(instructions));

        // Only the two scaled axes move; depth and 1/w keep their values.
        Assert.Equal(usesRenderScale, FeedsOperation(instructions, fragCoord, 0, SpirvOp.FMul));
        Assert.Equal(usesRenderScale, FeedsOperation(instructions, fragCoord, 1, SpirvOp.FMul));
        Assert.False(FeedsOperation(instructions, fragCoord, 2, SpirvOp.FMul));
        ValidateWhenAvailable(spirv);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IntegerImageCoordinatesAreMappedOntoHostTexels(bool usesRenderScale)
    {
        var spirv = CompileImageAccess("ImageLoad", usesRenderScale);
        var instructions = Parse(spirv);
        var fetch = Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageFetch);
        var coordinates = Definition(instructions, fetch.Operands[3]);

        // Two coordinates, each either the raw register or that register mapped through
        // the image's factor; a selected factor of one leaves an unscaled image alone.
        Assert.Equal(SpirvOp.CompositeConstruct, coordinates.Opcode);
        foreach (var component in coordinates.Operands.Skip(2))
        {
            Assert.Equal(usesRenderScale ? SpirvOp.ConvertFToS : SpirvOp.Bitcast, Definition(instructions, component).Opcode);
        }

        if (usesRenderScale)
        {
            // The factor is selected per image, so an unscaled binding costs only the select.
            Assert.Contains(instructions, item => item.Opcode == SpirvOp.Select);
            Assert.Contains(instructions, item => item.Opcode == SpirvOp.ConvertSToF);
        }
        else
        {
            Assert.DoesNotContain(instructions, item => item.Opcode == SpirvOp.ConvertSToF);
        }
        ValidateWhenAvailable(spirv);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SizeQueriesReportGuestSizes(bool usesRenderScale)
    {
        var spirv = CompileImageAccess("ImageGetResinfo", usesRenderScale);
        var instructions = Parse(spirv);
        var query = Assert.Single(instructions, item => item.Opcode == SpirvOp.ImageQuerySizeLod);
        var extracts = instructions
            .Where(item => item.Opcode == SpirvOp.CompositeExtract && item.Operands[2] == query.Operands[1])
            .ToArray();

        Assert.NotEmpty(extracts);
        foreach (var extract in extracts)
        {
            Assert.Equal(usesRenderScale, FeedsOpcode(instructions, extract.Operands[1], SpirvOp.ConvertSToF));
        }

        ValidateWhenAvailable(spirv);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DynamicSampleOffsetsRemainInGuestTexels(bool usesRenderScale)
    {
        var spirv = CompileImageAccess("ImageSampleLzO", usesRenderScale);
        var instructions = Parse(spirv);
        var divide = Assert.Single(instructions, item => item.Opcode == SpirvOp.FDiv);
        var offset = Definition(instructions, divide.Operands[2]);
        Assert.Equal(usesRenderScale ? SpirvOp.CompositeConstruct : SpirvOp.ConvertSToF, offset.Opcode);
        if (usesRenderScale)
        {
            foreach (var component in offset.Operands.Skip(2))
            {
                var multiply = Definition(instructions, component);
                Assert.Equal(SpirvOp.FMul, multiply.Opcode);
                // Each image selects its factor; unscaled bindings select one.
                Assert.Equal(SpirvOp.Select, Definition(instructions, multiply.Operands[3]).Opcode);
            }
        }
        ValidateWhenAvailable(spirv);
    }

    private static byte[] CompilePixelPosition(bool usesRenderScale)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.MoveVector(0, 0, 0),
            ResourceTestProgram.EndProgram(4));
        var request = BuildRequest(program, ShaderStage.Pixel, usesRenderScale,
            PositionXBit | PositionYBit | PositionZBit);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static byte[] CompileImageAccess(string opcode, bool usesRenderScale)
    {
        var program = ResourceTestProgram.Program(
            ResourceTestProgram.Image(0, opcode, resourceRegister: 8, dimension: 1, dmask: 0x3),
            ResourceTestProgram.EndProgram(8));
        var request = BuildRequest(program, ShaderStage.Compute, usesRenderScale);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static ShaderCompileRequest BuildRequest(Gen5ShaderProgram program, ShaderStage stage, bool usesRenderScale,
        uint pixelInputs = 0)
    {
        var plan = ShaderResourcePlan.Extract(program, stage, ResourceTestProgram.Hash, 0, 64);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 64),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            0,
            false,
            usesRenderScale);
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : [],
            PixelInputAddress = pixelInputs,
            PixelInputEnable = pixelInputs,
        };
    }

    private static uint FragCoordVariable(IReadOnlyList<Parsed> instructions) => Assert.Single(instructions,
        item => item.Opcode == SpirvOp.Decorate && item.Operands.Length >= 3 &&
                item.Operands[1] == (uint)SpirvDecoration.BuiltIn && item.Operands[2] == (uint)SpirvBuiltIn.FragCoord).Operands[0];

    private static uint Load(IReadOnlyList<Parsed> instructions, uint variable) =>
        Assert.Single(instructions, item => item.Opcode == SpirvOp.Load && item.Operands[2] == variable).Operands[1];

    // True when the given component of the composite is extracted and consumed by the opcode.
    private static bool FeedsOperation(IReadOnlyList<Parsed> instructions, uint composite, uint component, SpirvOp opcode) =>
        instructions.Any(item => item.Opcode == SpirvOp.CompositeExtract && item.Operands[2] == composite &&
                                 item.Operands.Length >= 4 && item.Operands[3] == component &&
                                 FeedsOpcode(instructions, item.Operands[1], opcode));

    private static bool FeedsOpcode(IReadOnlyList<Parsed> instructions, uint value, SpirvOp opcode) =>
        instructions.Any(item => item.Opcode == opcode && item.Operands.Skip(2).Contains(value));

    // The producer of a value, among the opcodes these tests expect to see one of.
    private static Parsed Definition(IReadOnlyList<Parsed> instructions, uint id) =>
        Assert.Single(instructions, item => ValueOpcodes.Contains(item.Opcode) && item.Operands.Length >= 2 && item.Operands[1] == id);

    private static readonly SpirvOp[] ValueOpcodes =
    [
        SpirvOp.CompositeConstruct, SpirvOp.CompositeExtract, SpirvOp.Bitcast, SpirvOp.Load,
        SpirvOp.ConvertFToS, SpirvOp.ConvertSToF, SpirvOp.FMul, SpirvOp.FAdd, SpirvOp.Select,
    ];

    private readonly record struct Parsed(SpirvOp Opcode, uint[] Operands);

    private static IReadOnlyList<Parsed> Parse(byte[] spirv)
    {
        var instructions = new List<Parsed>();
        for (var offset = 5 * sizeof(uint); offset < spirv.Length;)
        {
            var header = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset));
            var wordCount = checked((int)(header >> 16));
            Assert.InRange(wordCount, 1, (spirv.Length - offset) / sizeof(uint));
            var operands = new uint[wordCount - 1];
            for (var operand = 0; operand < operands.Length; operand++)
            {
                operands[operand] = BinaryPrimitives.ReadUInt32LittleEndian(spirv.AsSpan(offset + (operand + 1) * sizeof(uint)));
            }

            instructions.Add(new Parsed((SpirvOp)(ushort)header, operands));
            offset += wordCount * sizeof(uint);
        }

        return instructions;
    }

    private static void ValidateWhenAvailable(byte[] spirv)
    {
        var sdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (string.IsNullOrEmpty(sdk))
        {
            return;
        }

        var validator = Path.Combine(sdk, "bin", "spirv-val");
        if (!File.Exists(validator))
        {
            return;
        }

        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, spirv);
            using var process = Process.Start(new ProcessStartInfo(validator, ["--target-env", "vulkan1.2", path])
            {
                RedirectStandardError = true,
            })!;
            var errors = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, errors);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
