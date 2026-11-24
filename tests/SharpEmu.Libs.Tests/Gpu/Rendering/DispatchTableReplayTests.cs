// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;
using static SharpEmu.Libs.Tests.Gpu.Rendering.RenderExecutorFixtures;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Tests.Gpu.Rendering;

[Collection(SchedulingStateCollection.Name)]
public sealed class DispatchTableReplayTests : IDisposable
{
    private readonly FatalScope _fatal = new();

    public void Dispose() => _fatal.Dispose();

    // A compute program whose only binding is its flattened resource table; device addresses are a second replay refusal.
    private static ShaderProgramInfo TableProgram(bool usesDeviceAddresses = false)
    {
        var info = new ShaderResourceInfo();
        return new ShaderProgramInfo
        {
            Stage = ShaderStageKind.Compute,
            Hash = 0x7AB1_E000,
            UsesDeviceAddresses = usesDeviceAddresses,
            Resources = new SpecializedResourceInfo { Info = info },
            Bindings = BindingLayout.Allocate(info, [], usesGlobalDataShare: false, usesFlattenedTable: true, usesShaderBase: false),
        };
    }

    private static ResolvedDispatch Dispatch(ShaderProgramInfo program, uint[] table, uint[][]? buffers = null) => new()
    {
        Program = ComputeProgram(new ShaderStageResources(program, new ResourceSnapshot { FlattenedResourceTable = table, Buffers = buffers ?? [] })),
        GroupsX = 1,
        GroupsY = 1,
        GroupsZ = 1,
    };

    private static List<string> RecordTwo(ResolvedDispatch first, ResolvedDispatch second)
    {
        var host = new RecordingRenderHost();
        var recorder = new RenderRecorder(host, new FakePipelineProvider(), strictDrawResources: true, returnsRecords: false);
        host.ItemSequence = 1;
        recorder.Record(first);
        host.ItemSequence = 2;
        recorder.Record(second);
        return host.Calls;
    }

    private static int Count(List<string> calls, string prefix) => calls.Count(call => call.StartsWith(prefix, StringComparison.Ordinal));

    [Fact]
    public void ADispatchWhoseTableAloneDiffersReplaysWithItsTableReplaced()
    {
        var program = TableProgram();
        var calls = RecordTwo(Dispatch(program, [1, 2, 3]), Dispatch(program, [4, 5, 6]));

        Assert.Equal(1, Count(calls, "commit Compute"));
        Assert.Contains("replay_dispatch table=True", calls);
        Assert.Equal(2, Count(calls, "dispatch 1 1 1"));
        Assert.Equal(2, Count(calls, "seal_dispatch"));
    }

    [Fact]
    public void ADispatchWithTheSameTableReplaysWithoutReplacingIt()
    {
        var program = TableProgram();
        var calls = RecordTwo(Dispatch(program, [1, 2, 3]), Dispatch(program, [1, 2, 3]));

        Assert.Equal(1, Count(calls, "commit Compute"));
        Assert.Contains("replay_dispatch table=False", calls);
    }

    [Fact]
    public void ATableThatDiffersBesideOtherBindingsTakesTheFullPath()
    {
        var program = TableProgram();
        var calls = RecordTwo(Dispatch(program, [1, 2, 3], [BufferDescriptor(VertexBase, 4, 16)]),
            Dispatch(program, [4, 5, 6], [BufferDescriptor(VertexBase + 0x100, 4, 16)]));

        Assert.Equal(2, Count(calls, "commit Compute"));
        Assert.DoesNotContain(calls, call => call.StartsWith("replay_dispatch", StringComparison.Ordinal));
    }

    [Fact]
    public void AProgramWithAnotherReplayRefusalBesideItsTableSealsNothing()
    {
        var program = TableProgram(usesDeviceAddresses: true);
        var calls = RecordTwo(Dispatch(program, [1, 2, 3]), Dispatch(program, [4, 5, 6]));

        Assert.Equal(2, Count(calls, "commit Compute"));
        Assert.Equal(0, Count(calls, "seal_dispatch"));
    }
}
