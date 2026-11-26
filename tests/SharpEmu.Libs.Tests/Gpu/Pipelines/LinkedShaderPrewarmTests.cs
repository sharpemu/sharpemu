// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

public sealed class LinkedShaderPrewarmTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RestoresCapturedCodeCallOrderAndModes(bool runtimeRecords, bool callMarks)
    {
        const ulong callerAddress = 0x1000, functionAddress = 0x2000;
        uint[] words = [0xBF800000, 0xBE80200E];
        var function = ShaderFunctionReader.Read(functionAddress, (ulong address, out uint word) =>
        {
            var offset = address - functionAddress;
            word = offset < 8 ? words[(int)(offset / 4)] : 0;
            return offset < 8;
        });
        Assert.True(function.Complete, function.Error);
        var caller = new Gen5ShaderProgram(callerAddress,
        [
            new(0, Gen5ShaderEncoding.Sop1, "SSwappcB64", [0xBE8E210E], [Gen5Operand.Scalar(14)], [Gen5Operand.Scalar(14)], null),
            new(4, Gen5ShaderEncoding.Sopp, "SEndpgm", [0xBF810000], [], [], null),
        ]);
        var site = new ShaderCallSite(0, 14, 16, 14, callerAddress + 4,
            [new(functionAddress, 0x3000, function.Program), new(functionAddress, 0x4000, function.Program)]);
        var library = new ShaderCallLibrary("test", [site], "test", [0x3000, 0x4000])
        {
            CodeRuns = new() { [functionAddress] = function.Runs },
        };
        var expected = Gen5ShaderCallLinker.Link(caller, library.Calls, runtimeRecords);
        var captured = LinkedShaderPrewarm.Capture(library, runtimeRecords, callMarks, bindlessHeap: false);
        Assert.Single(captured.Functions);
        // The library's backing bytes may be reused later; the record must keep the exact earlier code.
        function.Runs[0].Bytes[0] ^= 1;
        var restored = Read(Write(captured));
        Assert.Equal(runtimeRecords, restored.RuntimeRecords);
        Assert.Equal(callMarks, restored.CallMarks);
        Assert.False(restored.BindlessHeap);
        Assert.Equal(new ulong[] { 0x3000, 0x4000 }, restored.Sites[0].Targets.Select(target => target.Argument));
        var actual = restored.Link(caller);
        Assert.Equal(expected.Instructions.Select(instruction => instruction.Opcode), actual.Instructions.Select(instruction => instruction.Opcode));
        Assert.Equal(expected.Instructions.Select(instruction => instruction.Control), actual.Instructions.Select(instruction => instruction.Control));
        Assert.Equal(expected.Instructions.SelectMany(instruction => instruction.Words), actual.Instructions.SelectMany(instruction => instruction.Words));
    }

    [Fact]
    public void CapturePreservesTheLibrarySiteOrder()
    {
        var first = new ShaderCallSite(8, 14, 16, 14, 0x100CUL, []);
        var second = new ShaderCallSite(0, 14, 16, 14, 0x1004UL, []);
        var library = new ShaderCallLibrary("test", [first, second], "test", []);
        var restored = Read(Write(LinkedShaderPrewarm.Capture(library, false, false, false)));
        Assert.Equal(new uint[] { 8, 0 }, restored.Sites.Select(site => site.Pc));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void RejectsImpossibleFunctionCountsBeforeAllocation(int count)
    {
        var bytes = Write(new LinkedShaderPrewarm(false, false, false, [], []));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(3), count);
        Assert.Throws<ArgumentException>(() => Read(bytes));
    }

    [Fact]
    public void RejectsACallWhoseCodeIsMissing()
    {
        var record = new LinkedShaderPrewarm(false, false, false, [],
            [new(0, 14, 16, 14, 0x1004, [new(0x2000, 0x3000)])]);
        Assert.Throws<ArgumentException>(() => Read(Write(record)));
    }

    private static byte[] Write(LinkedShaderPrewarm record)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        record.Write(writer);
        return stream.ToArray();
    }

    private static LinkedShaderPrewarm Read(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        return LinkedShaderPrewarm.Read(reader);
    }
}
