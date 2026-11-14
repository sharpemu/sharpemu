// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[Collection("SpirvOptimization")]
public sealed class SpirvExtInstUseTests
{
    [Fact]
    public void OperationLiteralDoesNotKeepUnreadPrivateStorageOrItsMetadata()
    {
        var (module, integer, glsl, input, output, function) = BeginIntegerModule();
        var one = module.Constant(integer, 1);
        var pointerType = module.TypePointer(SpirvStorageClass.Private, integer);
        ReserveIdsBefore(module, 38);
        var variable = module.AddGlobalVariable(pointerType, SpirvStorageClass.Private);
        Assert.Equal(38u, variable);
        module.AddName(variable, "unreadLiteralCollision");
        module.AddStatement(SpirvOp.Store, variable, one);
        var loaded = module.AddInstruction(SpirvOp.Load, integer, input);
        var minimum = module.AddInstruction(SpirvOp.ExtInst, integer, glsl, 38, loaded, one);
        module.AddStatement(SpirvOp.Store, output, minimum);
        var words = Finish(module, function, [input, output, variable]);
        Assert.DoesNotContain(words, value => (SpirvOp)(value[0] & 0xffff) == SpirvOp.Variable && value[2] == variable);
        Assert.DoesNotContain(words, value => (SpirvOp)(value[0] & 0xffff) == SpirvOp.Store && value[1] == variable);
        Assert.DoesNotContain(words, value => (SpirvOp)(value[0] & 0xffff) == SpirvOp.Name && value[1] == variable);
        Assert.DoesNotContain(variable, Assert.Single(words.Where(value =>
            (SpirvOp)(value[0] & 0xffff) == SpirvOp.EntryPoint)));
        Assert.Contains(words, value => (SpirvOp)(value[0] & 0xffff) == SpirvOp.ExtInst && value[2] == minimum);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiteralUseAndItsRemovalDoNotChangeARealDefinitionUse(bool keepDefinition)
    {
        var (module, integer, glsl, input, output, function) = BeginIntegerModule();
        var loaded = module.AddInstruction(SpirvOp.Load, integer, input);
        var divisor = module.Constant(integer, 2);
        ReserveIdsBefore(module, 38);
        var divided = module.AddInstruction(SpirvOp.UDiv, integer, loaded, divisor);
        Assert.Equal(38u, divided);
        module.AddName(divided, "definitionLiteralCollision");
        var minimum = module.AddInstruction(SpirvOp.ExtInst, integer, glsl, 38, loaded, divisor);
        var selected = keepDefinition ? divided : minimum;
        module.AddStatement(SpirvOp.Store, output, selected);
        var words = Finish(module, function, [input, output]);
        Assert.Equal(keepDefinition, words.Any(value =>
            (SpirvOp)(value[0] & 0xffff) == SpirvOp.UDiv && value[2] == divided));
        Assert.Equal(keepDefinition, words.Any(value =>
            (SpirvOp)(value[0] & 0xffff) == SpirvOp.Name && value[1] == divided));
        Assert.Equal(!keepDefinition, words.Any(value =>
            (SpirvOp)(value[0] & 0xffff) == SpirvOp.ExtInst && value[2] == minimum));
        Assert.Contains(words, value => (SpirvOp)(value[0] & 0xffff) == SpirvOp.Store &&
            value[1] == output && value[2] == selected);
    }

    [Theory]
    [InlineData(35u)]
    [InlineData(51u)]
    public void ActualOutputPointerKeepsStorageEvenWhenItsIdEqualsTheOperation(uint operation)
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var glsl = module.ImportExtInst("GLSL.std.450");
        var floating = module.TypeFloat(32);
        var valueType = operation == 35 ? floating : module.TypeInt(32, true);
        var privatePointer = module.TypePointer(SpirvStorageClass.Private, valueType);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, valueType),
            SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var input = module.ConstantFloat(floating, 1.5f);
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddLabel();
        ReserveIdsBefore(module, operation);
        var variable = module.AddGlobalVariable(privatePointer, SpirvStorageClass.Private);
        Assert.Equal(operation, variable);
        // No load follows: the output-pointer argument is the variable's only function use.
        module.AddInstruction(SpirvOp.ExtInst, floating, glsl, operation, input, variable);
        var keptOutput = operation == 35 ? module.ConstantFloat(floating, 0) : module.Constant(valueType, 0);
        module.AddStatement(SpirvOp.Store, output, keptOutput);
        var words = Finish(module, function, [output, variable]);
        Assert.Contains(words, value => (SpirvOp)(value[0] & 0xffff) == SpirvOp.Variable && value[2] == variable);
        Assert.Contains(words, value => (SpirvOp)(value[0] & 0xffff) == SpirvOp.ExtInst &&
            value[4] == operation && value[6] == variable);
        Assert.Contains(variable, Assert.Single(words.Where(value =>
            (SpirvOp)(value[0] & 0xffff) == SpirvOp.EntryPoint)));
    }

    private static (SpirvModuleBuilder Module, uint Integer, uint Glsl, uint Input, uint Output, uint Function)
        BeginIntegerModule()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var glsl = module.ImportExtInst("GLSL.std.450");
        var integer = module.TypeInt(32, false);
        var input = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, integer), SpirvStorageClass.Input);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.AddDecoration(input, SpirvDecoration.Location, 0);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddLabel();
        return (module, integer, glsl, input, output, function);
    }

    private static uint[][] Finish(SpirvModuleBuilder module, uint function, uint[] interfaces)
    {
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", interfaces);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();
        var code = module.Build();
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var instructions = new List<uint[]>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            var count = (int)(words[offset] >> 16);
            instructions.Add(words[offset..(offset + count)]);
        }
        return instructions.ToArray();
    }

    private static void ReserveIdsBefore(SpirvModuleBuilder module, uint target)
    {
        uint reserved;
        do
        {
            reserved = module.AllocateId();
        }
        while (reserved < target - 1);
        Assert.Equal(target - 1, reserved);
    }
}
