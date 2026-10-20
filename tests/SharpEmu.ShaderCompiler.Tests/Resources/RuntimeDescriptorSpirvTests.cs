// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

// The SPIR-V a runtime-descriptor access compiles to: the heap declarations, the shared lookup
// functions, and the rule that no function asks the driver not to inline it.
public sealed class RuntimeDescriptorSpirvTests
{
    private const uint DontInlineMask = 0x2;

    // Every iteration loads a T# and S# through a table pointer at an offset the loop advances, then
    // reads the image with each of the given opcodes.
    private static Gen5ShaderProgram LoopProgram(params string[] opcodes)
    {
        var instructions = new List<Gen5ShaderInstruction>();
        uint pc = 0;
        void Add(Func<uint, Gen5ShaderInstruction> make)
        {
            var instruction = make(pc);
            instructions.Add(instruction);
            pc += (uint)instruction.Words.Count * sizeof(uint);
        }

        Add(at => ScalarLoad(at, 2, destination: 8, count: 2));
        Add(at => MoveScalar(at, 20, 0));
        var loopHead = pc;
        Add(at => ScalarLoad(at, 8, destination: 16, count: 8, dynamicOffsetRegister: 20));
        Add(at => ScalarLoad(at, 8, destination: 24, count: 4, immediateOffset: 32, dynamicOffsetRegister: 20));
        foreach (var opcode in opcodes)
        {
            Add(at => Image(at, opcode, 16, 24, dimension: 1, dmask: 1));
        }

        Add(at => Sop2(at, "SAddU32", 20, Gen5Operand.Scalar(20), Operand(48)));
        Add(at => Branch(at, "SCbranchScc1", (short)((int)loopHead - (int)(at + 4) >> 2)));
        Add(EndProgram);
        return Program([.. instructions]);
    }

    private static ShaderCompileRequest RuntimeRequest(Gen5ShaderProgram program, ShaderStage stage = ShaderStage.Compute)
    {
        var plan = ShaderResourcePlan.Extract(program, stage, Hash, 0, 64, runtimeDescriptors: RuntimeDescriptorMode.OnDemand);
        var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
        var layout = BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, 0, 64),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program));
        return new ShaderCompileRequest(plan, resources, layout)
        {
            PixelOutputs = stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : [],
        };
    }

    private static (ShaderCompileRequest Request, SpirvModuleInspector Module, byte[] Spirv) Compile(Gen5ShaderProgram program, ShaderStage stage = ShaderStage.Compute)
    {
        var request = RuntimeRequest(program, stage);
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        Gen5MipStatisticsTests.ValidateWhenAvailable(shader.Spirv);
        return (request, new SpirvModuleInspector(shader.Spirv), shader.Spirv);
    }

    private static uint FunctionNamed(SpirvModuleInspector module, string name) =>
        module.Names.Single(pair => pair.Value == name).Key;

    [Fact]
    public void SampleDeclaresTheHeapAndSharesOneLookupPerTable()
    {
        var (request, module, _) = Compile(LoopProgram("ImageSample", "ImageSample", "ImageSampleLz"));

        Assert.True(request.Resources.Info.UsesRuntimeDescriptors);
        var expected = request.Bindings.Descriptors
            .Select(descriptor => (0u, BindingLayout.NativeBindingIndex(ShaderStage.Compute, descriptor.Kind)))
            .Append((RuntimeDescriptorTable.HeapDescriptorSet, RuntimeDescriptorTable.HeapSampledImageBinding))
            .Append((RuntimeDescriptorTable.HeapDescriptorSet, RuntimeDescriptorTable.HeapSamplerBinding))
            .ToHashSet();
        Assert.Equal(expected, module.DescriptorBindings);
        Assert.Equal(BindingLayout.NativeBindingIndex(ShaderStage.Compute, DescriptorBindingKind.RuntimeDescriptorTable),
            module.BindingOf("runtimeDescriptorTable"));
        Assert.Equal(BindingLayout.NativeBindingIndex(ShaderStage.Compute, DescriptorBindingKind.RuntimeDescriptorMisses),
            module.BindingOf("runtimeDescriptorMisses"));
        Assert.Equal(RuntimeDescriptorTable.HeapSampledImageBinding, module.BindingOf("runtimeImagesDim2DFloat"));
        Assert.Equal(RuntimeDescriptorTable.HeapSamplerBinding, module.BindingOf("runtimeSamplers"));
        Assert.Contains((uint)SpirvCapability.RuntimeDescriptorArray, module.Capabilities);
        Assert.Contains((uint)SpirvCapability.ShaderNonUniform, module.Capabilities);
        Assert.Contains((uint)SpirvCapability.SampledImageArrayNonUniformIndexing, module.Capabilities);

        // Three accesses call two shared functions: the compare against every probe is not repeated per access.
        Assert.Equal(3, module.CallCounts[FunctionNamed(module, "lookupRuntimeImage")]);
        Assert.Equal(3, module.CallCounts[FunctionNamed(module, "lookupRuntimeSampler")]);
        Assert.Equal(3, module.FunctionControls.Count);
    }

    // The driver inlines each lookup at every access, so its body is what a sample costs to compile:
    // one probe in a loop that is not unrolled, not ProbeCount copies of the compare (the image
    // lookup was 4713 words unrolled).
    [Fact]
    public void LookupsProbeTheTableInOneLoop()
    {
        var (_, module, _) = Compile(LoopProgram("ImageSample"));

        foreach (var name in new[] { "lookupRuntimeImage", "lookupRuntimeSampler" })
        {
            var function = FunctionNamed(module, name);
            Assert.Equal([LoopControlDontUnroll], module.FunctionLoopControls[function]);
            Assert.True(module.FunctionWords[function] < 1000, $"{name}: {module.FunctionWords[function]} words");
        }
    }

    private const uint LoopControlDontUnroll = 0x2;

    [Fact]
    public void ImageLoadFetchesThroughTheArrayOfItsNumericView()
    {
        var (_, module, _) = Compile(LoopProgram("ImageLoad"));

        foreach (var view in new[] { "Float", "Uint", "Sint" })
        {
            Assert.Equal(RuntimeDescriptorTable.HeapSampledImageBinding, module.BindingOf($"runtimeImagesDim2D{view}"));
        }

        Assert.Contains(module.Names.Values, name => name == "runtimeImageNumericClasses");
        Assert.Contains(module.Names.Values, name => name == "lookupRuntimeImage");
        Assert.DoesNotContain(module.Names.Values, name => name == "lookupRuntimeSampler");
        Assert.DoesNotContain(module.Names.Values, name => name == "runtimeSamplers");
    }

    [Fact]
    public void PixelStageOffsetsItsTableBindingsAndKeepsTheHeapSet()
    {
        var (request, module, _) = Compile(LoopProgram("ImageSample"), ShaderStage.Pixel);

        Assert.Equal(BindingLayout.NativeBindingIndex(ShaderStage.Pixel, DescriptorBindingKind.RuntimeDescriptorTable),
            module.BindingOf("runtimeDescriptorTable"));
        Assert.Contains(request.Bindings.Descriptors, descriptor => descriptor.Kind == DescriptorBindingKind.RuntimeDescriptorMisses);
        // The heap is one set shared by the stages of a pipeline: its bindings are not offset by the stage.
        Assert.Equal(RuntimeDescriptorTable.HeapSamplerBinding, module.BindingOf("runtimeSamplers"));
    }

    [Fact]
    public void PlannedImagesDeclareNoHeapAndNoLookupFunction()
    {
        var program = Program(
            Image(0, "ImageSample", 8, 16),
            EndProgram(8));
        var (_, module, _) = Compile(program);

        Assert.DoesNotContain(module.DescriptorBindings, binding => binding.Set != 0);
        Assert.Single(module.FunctionControls);
        Assert.DoesNotContain((uint)SpirvCapability.RuntimeDescriptorArray, module.Capabilities);
    }

    // A shader the planner can bind statically compiles to the very same module whether or not the host serves
    // runtime descriptors: the feature costs nothing where it is not needed.
    [Theory]
    [InlineData(ShaderStage.Compute)]
    [InlineData(ShaderStage.Pixel)]
    public void StaticallyBoundShaderCompilesIdenticallyWhateverTheMode(ShaderStage stage)
    {
        var program = Program(
            Image(0, "ImageSample", 8, 16),
            Image(8, "ImageLoad", 24),
            BufferLoad(16, 4),
            EndProgram(24));
        byte[] CompileIn(RuntimeDescriptorMode mode)
        {
            var plan = ShaderResourcePlan.Extract(program, stage, Hash, 0, 64, runtimeDescriptors: mode);
            var resources = ResourceMaterializer.ApplyTo(plan, ResourceSpecialization.Default(plan.Info));
            var layout = BindingLayout.Allocate(resources.Info, BindingLayout.CollectUserDataRegisters(program, 0, 64), false, false, false);
            var request = new ShaderCompileRequest(plan, resources, layout)
            {
                PixelOutputs = stage == ShaderStage.Pixel ? [new Gen5PixelOutputBinding(0, 0, Gen5PixelOutputKind.Float)] : [],
            };
            Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
            return shader.Spirv;
        }

        var baseline = CompileIn(RuntimeDescriptorMode.Disabled);

        Assert.Equal(baseline, CompileIn(RuntimeDescriptorMode.OnDemand));
        Assert.Equal(baseline, CompileIn(RuntimeDescriptorMode.Preferred));
    }

    // NVIDIA returns the first call's result for repeated calls of one DontInline function that sit in a
    // uniform loop and index a descriptor array, so the runtime lookup (called per access, inside the
    // guest's loops) and every other function must leave the inlining decision to the driver.
    [Theory]
    [InlineData(ShaderStage.Compute)]
    [InlineData(ShaderStage.Pixel)]
    public void NoFunctionIsDontInline(ShaderStage stage)
    {
        var programs = new[]
        {
            LoopProgram("ImageSample"),
            LoopProgram("ImageSample", "ImageSample", "ImageSampleLz", "ImageLoad", "ImageLoad"),
            LoopProgram("ImageLoadMip"),
        };
        foreach (var program in programs)
        {
            var (_, module, _) = Compile(program, stage);
            foreach (var (function, control) in module.FunctionControls)
            {
                Assert.Equal(0u, control & DontInlineMask);
            }

            // The module really calls its lookups, so the check above is not vacuous.
            Assert.NotEmpty(module.CallCounts);
        }
    }
}
