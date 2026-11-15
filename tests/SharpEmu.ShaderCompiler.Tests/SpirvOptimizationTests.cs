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

    [Fact]
    public void ExplicitNoneLoadKeepsThePrivateStoreItReadsBeforeALaterStore()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var integer = module.TypeInt(32, false);
        var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, integer), SpirvStorageClass.Private);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", [register, output]);
        module.AddLabel();
        var first = module.Constant(integer, 41);
        var second = module.Constant(integer, 73);
        module.AddStatement(SpirvOp.Store, register, first);
        var loaded = module.AddInstruction(SpirvOp.Load, integer, register, 0); // Memory Access: None.
        module.AddStatement(SpirvOp.Store, register, second);
        module.AddStatement(SpirvOp.Store, output, loaded);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var instructions = Instructions(module.Build());
        var stores = instructions.Where(words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == register).ToArray();
        Assert.Collection(stores,
            words => Assert.Equal(new uint[] { ((uint)SpirvOp.Store) | (3u << 16), register, first }, words),
            words => Assert.Equal(new uint[] { ((uint)SpirvOp.Store) | (3u << 16), register, second }, words));
        var load = Assert.Single(instructions.Where(words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Load && words[2] == loaded));
        Assert.Equal(new uint[] { ((uint)SpirvOp.Load) | (5u << 16), integer, loaded, register, 0 }, load);
        Assert.True(Array.IndexOf(instructions, stores[0]) < Array.IndexOf(instructions, load));
        Assert.True(Array.IndexOf(instructions, load) < Array.IndexOf(instructions, stores[1]));
        Assert.Contains(instructions, words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == output && words[2] == loaded);
    }

    [Fact]
    public void ExplicitNoneElementStoreInvalidatesThePreviouslyForwardedValue()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var integer = module.TypeInt(32, false);
        var array = module.TypeArray(integer, 2);
        var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, array), SpirvStorageClass.Private);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", [register, output]);
        module.AddLabel();
        var pointer = module.AddInstruction(SpirvOp.AccessChain,
            module.TypePointer(SpirvStorageClass.Private, integer), register, module.Constant(integer, 1));
        var first = module.Constant(integer, 41);
        var second = module.Constant(integer, 73);
        module.AddStatement(SpirvOp.Store, pointer, first);
        module.AddStatement(SpirvOp.Store, pointer, second, 0); // Memory Access: None.
        var loaded = module.AddInstruction(SpirvOp.Load, integer, pointer);
        Assert.NotEqual(first, loaded);
        module.AddStatement(SpirvOp.Store, output, loaded);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var instructions = Instructions(module.Build());
        var explicitStore = Assert.Single(instructions.Where(words =>
            (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words.Length == 4 && words[1] == pointer));
        Assert.Equal(new uint[] { ((uint)SpirvOp.Store) | (4u << 16), pointer, second, 0 }, explicitStore);
        var load = Assert.Single(instructions.Where(words =>
            (SpirvOp)(words[0] & 0xffff) == SpirvOp.Load && words[2] == loaded));
        Assert.Equal(new uint[] { ((uint)SpirvOp.Load) | (4u << 16), integer, loaded, pointer }, load);
        Assert.True(Array.IndexOf(instructions, explicitStore) < Array.IndexOf(instructions, load));
        Assert.Contains(instructions, words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == output && words[2] == loaded);
    }

    [Fact]
    public void VolatileLoadKeepsItsStoreWhileAnUntrackedExplicitLoadKeepsPrivateForwarding()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var integer = module.TypeInt(32, false);
        var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, integer), SpirvStorageClass.Private);
        var input = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Input, integer), SpirvStorageClass.Input);
        module.AddDecoration(input, SpirvDecoration.Location, 0);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var inputOutput = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.AddDecoration(inputOutput, SpirvDecoration.Location, 1);
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", [register, input, output, inputOutput]);
        module.AddLabel();
        var first = module.Constant(integer, 41);
        var second = module.Constant(integer, 73);
        module.AddStatement(SpirvOp.Store, register, first);
        var inputValue = module.AddInstruction(SpirvOp.Load, integer, input, 0); // Untracked Input, explicit None.
        var forwarded = module.AddInstruction(SpirvOp.Load, integer, register);
        Assert.Equal(first, forwarded);
        var loaded = module.AddInstruction(SpirvOp.Load, integer, register, 1); // Memory Access: Volatile.
        module.AddStatement(SpirvOp.Store, register, second);
        module.AddStatement(SpirvOp.Store, output, loaded);
        module.AddStatement(SpirvOp.Store, inputOutput, inputValue);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var instructions = Instructions(module.Build());
        var stores = instructions.Where(words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == register).ToArray();
        Assert.Collection(stores,
            words => Assert.Equal(new uint[] { ((uint)SpirvOp.Store) | (3u << 16), register, first }, words),
            words => Assert.Equal(new uint[] { ((uint)SpirvOp.Store) | (3u << 16), register, second }, words));
        var volatileLoad = Assert.Single(instructions.Where(words =>
            (SpirvOp)(words[0] & 0xffff) == SpirvOp.Load && words[2] == loaded));
        Assert.Equal(new uint[] { ((uint)SpirvOp.Load) | (5u << 16), integer, loaded, register, 1 }, volatileLoad);
        Assert.DoesNotContain(instructions, words =>
            (SpirvOp)(words[0] & 0xffff) == SpirvOp.Load && words.Length == 4 && words[3] == register);
        var untrackedLoad = Assert.Single(instructions.Where(words =>
            (SpirvOp)(words[0] & 0xffff) == SpirvOp.Load && words[2] == inputValue));
        Assert.Equal(new uint[] { ((uint)SpirvOp.Load) | (5u << 16), integer, inputValue, input, 0 }, untrackedLoad);
        Assert.True(Array.IndexOf(instructions, stores[0]) < Array.IndexOf(instructions, volatileLoad));
        Assert.True(Array.IndexOf(instructions, volatileLoad) < Array.IndexOf(instructions, stores[1]));
        Assert.Contains(instructions, words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == output && words[2] == loaded);
        Assert.Contains(instructions, words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == inputOutput && words[2] == inputValue);
    }

    [Fact]
    public void ExplicitAccessInAnUndeclaredLoopHeaderKeepsTheInheritedReadGuard()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var integer = module.TypeInt(32, false);
        var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, integer), SpirvStorageClass.Private);
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.GLCompute, function, "main", [register]);
        module.AddExecutionMode(function, SpirvExecutionMode.LocalSize, 1, 1, 1);
        module.AddLabel();
        var header = module.AllocateId();
        var latch = module.AllocateId();
        var done = module.AllocateId();
        module.AddStatement(SpirvOp.Store, register, module.Constant(integer, 1));
        module.AddStatement(SpirvOp.Branch, header);
        module.AddLabel(header); // No DeclareLoopHeader: the header inherits register = 1.
        var inherited = module.AddInstruction(SpirvOp.Load, integer, register);
        module.AddInstruction(SpirvOp.Load, integer, register, 0); // Explicit None: resets the memory state.
        var again = module.AddInstruction(SpirvOp.INotEqual, module.TypeBool(), inherited, module.Constant(integer, 2));
        module.AddStatement(SpirvOp.LoopMerge, done, latch, 0);
        module.AddStatement(SpirvOp.BranchConditional, again, latch, done);
        module.AddLabel(latch);
        module.AddStatement(SpirvOp.Store, register, module.Constant(integer, 2));
        module.AddStatement(SpirvOp.Branch, header);
        module.AddLabel(done);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        // The inherited read happened before the reset, so the header must still request the retry.
        Assert.False(module.InheritedBlocksHeld);
    }

    [Fact]
    public void ElementLoadKeepsTheWholePrivateStoreItReadsBeforeALaterWholeStore()
    {
        var module = new SpirvModuleBuilder();
        module.AddCapability(SpirvCapability.Shader);
        var integer = module.TypeInt(32, false);
        var array = module.TypeArray(integer, 2);
        var register = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Private, array), SpirvStorageClass.Private);
        var output = module.AddGlobalVariable(module.TypePointer(SpirvStorageClass.Output, integer), SpirvStorageClass.Output);
        module.AddDecoration(output, SpirvDecoration.Location, 0);
        var first = module.ConstantComposite(array, module.Constant(integer, 41), module.Constant(integer, 42));
        var second = module.ConstantComposite(array, module.Constant(integer, 73), module.Constant(integer, 74));
        var function = module.BeginFunction(module.TypeVoid(), module.TypeFunction(module.TypeVoid()));
        module.AddEntryPoint(SpirvExecutionModel.Vertex, function, "main", [register, output]);
        module.AddLabel();
        var pointer = module.AddInstruction(SpirvOp.AccessChain,
            module.TypePointer(SpirvStorageClass.Private, integer), register, module.Constant(integer, 1));
        module.AddStatement(SpirvOp.Store, register, first);
        var loaded = module.AddInstruction(SpirvOp.Load, integer, pointer);
        module.AddStatement(SpirvOp.Store, register, second);
        module.AddStatement(SpirvOp.Store, output, loaded);
        module.AddStatement(SpirvOp.Return);
        module.EndFunction();

        var instructions = Instructions(module.Build());
        var stores = instructions.Where(words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == register).ToArray();
        Assert.Collection(stores,
            words => Assert.Equal(new uint[] { ((uint)SpirvOp.Store) | (3u << 16), register, first }, words),
            words => Assert.Equal(new uint[] { ((uint)SpirvOp.Store) | (3u << 16), register, second }, words));
        var load = Assert.Single(instructions.Where(words =>
            (SpirvOp)(words[0] & 0xffff) == SpirvOp.Load && words[2] == loaded));
        Assert.Equal(new uint[] { ((uint)SpirvOp.Load) | (4u << 16), integer, loaded, pointer }, load);
        Assert.True(Array.IndexOf(instructions, stores[0]) < Array.IndexOf(instructions, load));
        Assert.True(Array.IndexOf(instructions, load) < Array.IndexOf(instructions, stores[1]));
        Assert.Contains(instructions, words => (SpirvOp)(words[0] & 0xffff) == SpirvOp.Store && words[1] == output && words[2] == loaded);
    }

    private static uint[][] Instructions(byte[] code)
    {
        var words = new uint[code.Length / sizeof(uint)];
        Buffer.BlockCopy(code, 0, words, 0, code.Length);
        var instructions = new List<uint[]>();
        for (var offset = 5; offset < words.Length; offset += (int)(words[offset] >> 16))
        {
            instructions.Add(words.AsSpan(offset, (int)(words[offset] >> 16)).ToArray());
        }

        return instructions.ToArray();
    }
}
