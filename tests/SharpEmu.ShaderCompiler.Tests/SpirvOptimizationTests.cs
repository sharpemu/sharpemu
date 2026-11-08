// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Tests.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.ShaderCompiler.Tests;

[CollectionDefinition("SpirvOptimization", DisableParallelization = true)]
public sealed class SpirvOptimizationCollection : ICollectionFixture<SpirvOptimizationDefaults>;

public sealed class SpirvOptimizationDefaults : IDisposable
{
    private readonly bool _previous = SpirvModuleBuilder.KeepsUnreadVariables;

    public SpirvOptimizationDefaults() => SpirvModuleBuilder.KeepsUnreadVariables = false;

    public void Dispose() => SpirvModuleBuilder.KeepsUnreadVariables = _previous;
}

[Collection("SpirvOptimization")]
public sealed class SpirvOptimizationTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ForwardedRegisterValuesKeepTheirOutputWhenUnreadStorageIsRemoved(bool preserve, bool indexed)
    {
        var previous = SpirvModuleBuilder.KeepsUnreadVariables;
        SpirvModuleBuilder.KeepsUnreadVariables = preserve;
        try
        {
            var module = new SpirvModuleBuilder();
            module.AddCapability(SpirvCapability.Shader);
            var integer = module.TypeInt(32, false);
            var storage = indexed ? module.TypeArray(integer, 2) : integer;
            var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, storage), SpirvStorageClass.Private);
            module.AddName(register, "register");
            var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
            module.AddDecoration(output, SpirvDecoration.Location, 0);
            var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
            module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", [register, output]);
            module.AddLabel();
            var pointer = indexed ? module.AddInstruction(SpirvOp.AccessChain,
                module.TypePointer(SpirvStorageClass.Private, integer), register, module.Constant(integer, 1)) : register;
            var value = module.Constant(integer, 42);
            module.AddStatement(SpirvOp.Store, pointer, value);
            var forwarded = module.AddInstruction(SpirvOp.Load, integer, pointer);
            Assert.Equal(value, forwarded);
            module.AddStatement(SpirvOp.Store, output, forwarded);
            module.AddStatement(SpirvOp.Return);
            module.EndFunction();
            var code = module.Build();
            var inspected = new SpirvModuleInspector(code);
            Assert.Equal(preserve, inspected.VariableStorageClasses.ContainsKey(register));
            Assert.Equal(preserve, inspected.Names.ContainsKey(register));
            var words = new uint[code.Length / sizeof(uint)];
            Buffer.BlockCopy(code, 0, words, 0, code.Length);
            var outputStores = 0;
            for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
            {
                if ((SpirvOp)(words[offset] & 0xffff) == SpirvOp.Store && words[offset + 1] == output)
                {
                    Assert.Equal(value, words[offset + 2]);
                    outputStores++;
                }
            }
            Assert.Equal(1, outputStores);
        }
        finally
        {
            SpirvModuleBuilder.KeepsUnreadVariables = previous;
        }
    }
}
