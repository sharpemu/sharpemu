// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ScalarValueInterningTests
{
    private static ScalarValueGraph Graph() => ScalarValueGraph.Build(Program(EndProgram(0)), 0, 0);

    [Fact]
    public void IdentityIncludesTypesOriginsAndOperandOrder()
    {
        var graph = Graph();
        Assert.NotSame(graph.Constant(1u), graph.Constant(1ul));
        Assert.NotSame(graph.Constant(1u), graph.Constant(true));
        var left = graph.UserData(0);
        var right = graph.UserData(1);
        var pair = graph.Handle(ScalarValueKind.AddressHandle, left, right);
        Assert.Same(pair, graph.Handle(ScalarValueKind.AddressHandle, left, right));
        Assert.NotSame(pair, graph.Handle(ScalarValueKind.AddressHandle, right, left));
        Assert.NotSame(pair, graph.Handle(ScalarValueKind.BufferHandle, left, right));
        Assert.NotSame(graph.MemoryRead(ScalarValueKind.ScalarAddressWord, pair, left, 0),
            graph.MemoryRead(ScalarValueKind.ScalarAddressWord, pair, left, 1));

        graph.BuilderInstruction = default;
        var unknown = graph.Undefined(ScalarValueType.U32);
        graph.BuilderInstruction = (0, "unknown");
        var atZero = graph.Undefined(ScalarValueType.U32);
        Assert.NotSame(unknown, atZero);
        graph.BuilderInstruction = (4, "unknown");
        Assert.NotSame(atZero, graph.Undefined(ScalarValueType.U32));
        graph.BuilderInstruction = (0, "unknown");
        Assert.Same(atZero, graph.Undefined(ScalarValueType.U32));
        Assert.NotSame(graph.FindLowestSetBit(left, 0), graph.FindLowestSetBit(left, 4));
        Assert.NotSame(graph.FindLowestSetBit(left, 0),
            graph.Operation(ScalarOperation.FindLowestBit32, ScalarValueType.U32, left));
    }

    [Fact]
    public void RetainedOperandsAreIndependentOfCallerScratch()
    {
        var graph = Graph();
        var operands = Enumerable.Range(0, 12).Select(i => graph.UserData((uint)i)).ToArray();
        var saved = operands.ToArray();
        var handle = graph.Handle(ScalarValueKind.ImageHandle, operands);
        operands[11] = graph.Constant(99u);
        Assert.Same(handle, graph.Handle(ScalarValueKind.ImageHandle, saved));
        Assert.NotSame(handle, graph.Handle(ScalarValueKind.ImageHandle, operands));
        Assert.Same(saved[11], handle.Operands[11]);
    }

    [Fact]
    public void RepeatedLookupsAndFoldedOperationsAllocateNothing()
    {
        var graph = Graph();
        void Lookup()
        {
            var left = graph.UserData(0);
            var right = graph.Constant(12u);
            var sum = graph.Operation(ScalarOperation.IAdd32, ScalarValueType.U32, left, right);
            var address = graph.Handle(ScalarValueKind.AddressHandle, sum, graph.Constant(0u));
            _ = graph.MemoryRead(ScalarValueKind.ScalarAddressWord, address, right, 7);
            _ = graph.Operation(ScalarOperation.And32, ScalarValueType.U32, sum, graph.Constant(0u));
        }
        for (var i = 0; i < 1000; i++) Lookup();
        var bytes = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) Lookup();
        bytes = GC.GetAllocatedBytesForCurrentThread() - bytes;
        Assert.Equal(0, bytes);
    }
}
