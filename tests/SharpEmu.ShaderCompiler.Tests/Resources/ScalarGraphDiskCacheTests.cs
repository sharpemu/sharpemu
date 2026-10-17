// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.ShaderCompiler.Tests.Resources.ResourceTestProgram;

namespace SharpEmu.ShaderCompiler.Tests.Resources;

public sealed class ScalarGraphDiskCacheTests
{
    [Fact]
    public void SnapshotPreservesCyclesInterningAndInstructionProvenance()
    {
        var program = Program(ScalarLoad(0, 0, 4), EndProgram(8));
        var graph = ScalarValueGraph.Build(program, 0, 4);
        var phi = graph.Phi(0, ScalarValueType.U32);
        phi.SetPhiOperands([1, 2], [graph.UserData(0), phi]);
        graph.BranchConditions[0] = phi;
        graph.BuilderInstruction = (4, "unsupported");
        var undefined = graph.Undefined(ScalarValueType.U32);
        var scan = graph.FindLowestSetBit(graph.UserData(1), 12);
        graph.BranchConditions[4] = undefined;
        graph.BranchConditions[12] = scan;
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true)) graph.WriteSnapshot(writer);
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        var restored = ScalarValueGraph.ReadSnapshot(reader, program, 0, 4, null, 64);
        var loop = restored.BranchConditions[0];
        Assert.Same(loop, loop.Operands[1]);
        Assert.Equal(new[] { 1, 2 }, loop.PhiPredecessors);
        Assert.Same(restored.UserData(0), loop.Operands[0]);
        Assert.Same(restored.BranchConditions[4], restored.Undefined(ScalarValueType.U32));
        Assert.True(restored.TryGetUndefinedOrigin(restored.BranchConditions[4], out var origin));
        Assert.Equal((4u, "unsupported"), origin);
        Assert.Same(restored.BranchConditions[12], restored.FindLowestSetBit(restored.UserData(1), 12));
        Assert.Equal(graph.Accesses.Length, restored.Accesses.Length);
        Assert.NotSame(graph.Accesses[0]!.Read, restored.Accesses[0]!.Read);
        Assert.True(restored.Equivalent(graph.Accesses[0]!.Read!, restored.Accesses[0]!.Read!));
    }

    [Fact]
    public void CacheReusesSnapshotAndRebuildsTruncatedOrCorruptEntries()
    {
        var directory = Path.Combine(Path.GetTempPath(), "sharpemu-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            var program = Program(ScalarLoad(0, 0, 4), EndProgram(8));
            var first = ScalarGraphDiskCache.Build(program, 0, 4, null, 64, directory);
            var file = Assert.Single(Directory.GetFiles(directory, "*.graph"));
            var bytes = File.ReadAllBytes(file);
            var timestamp = File.GetLastWriteTimeUtc(file);
            var second = ScalarGraphDiskCache.Build(program, 0, 4, null, 64, directory);
            Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
            Assert.NotSame(first, second);
            Assert.Equal(first.Values.Count, second.Values.Count);
            var different = Program(ScalarLoad(0, 2, 4), EndProgram(8));
            var differentKey = ScalarGraphDiskCache.Key(different, 0, 4, null, 64);
            var differentFile = Path.Combine(directory, differentKey + ".graph");
            File.WriteAllBytes(differentFile, bytes);
            _ = ScalarGraphDiskCache.Build(different, 0, 4, null, 64, directory);
            Assert.NotEqual(bytes, File.ReadAllBytes(differentFile));
            foreach (var corrupt in new[] { bytes[..20], bytes.Select((b, i) => i == 40 ? (byte)(b ^ 1) : b).ToArray() })
            {
                File.WriteAllBytes(file, corrupt);
                var rebuilt = ScalarGraphDiskCache.Build(program, 0, 4, null, 64, directory);
                Assert.Equal(first.Values.Count, rebuilt.Values.Count);
                Assert.Equal(bytes, File.ReadAllBytes(file));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public void KeysIncludeEffectiveInstructionsControlsWaveAndFixedFunctionLoads()
    {
        var instruction = ScalarLoad(0, 0, 4);
        var program = Program(instruction, EndProgram(8));
        string Key(Gen5ShaderProgram p, uint wave = 64, IReadOnlySet<uint>? loads = null, uint count = 4) =>
            ScalarGraphDiskCache.Key(p, 0, count, loads, wave);
        var key = Key(program);
        Assert.NotEqual(key, Key(program, 32));
        Assert.NotEqual(key, Key(program, count: 8));
        Assert.NotEqual(key, Key(program, loads: new HashSet<uint> { 0 }));
        Assert.NotEqual(key, Key(Program(instruction with { Opcode = "SBufferLoadDword" }, EndProgram(8))));
        Assert.NotEqual(key, Key(Program(instruction with { Control = new Gen5ScalarMemoryControl(1, 32, null) }, EndProgram(8))));
        Assert.Equal(key, Key(program with { Address = program.Address + 4096 }));
    }
}
