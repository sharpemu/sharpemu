// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class MeshResourceWaveSizeTests
{
    [Theory]
    [InlineData(32u, false)]
    [InlineData(64u, false)]
    [InlineData(32u, true)]
    [InlineData(64u, true)]
    public void MeshResourcePlanUsesTheGuestWaveSize(uint waveSize, bool linked)
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        var address = PipelineTestGuest.MemoryBase + 0x1000;
        var table = PipelineTestGuest.MemoryBase + 0x10000;
        var function = PipelineTestGuest.MemoryBase + 0x20000;
        guest.RegisterProgram(address, PipelineTestGuest.MemoryBase + 0x8000, linked
            ? [0xF4280404, 125u << 25, 0xBF8C0000, 0xBE8E0310, 0xBE8F0311,
                0xBE900312, 0xBE910313, 0xBE8E210E, 0xBF810000]
            : PipelineTestGuest.EndProgram);
        if (linked)
        {
            guest.WriteWords(function, 0xBF800000, 0xBE80200E);
            guest.WriteWords(table, (uint)function, (uint)(function >> 32), 0x3000, 0);
        }
        guest.Host.MeshShadersSupported = true;
        guest.Host.MeshLimits = new MeshShaderLimits(128, 256, 256, 32768, 65535, 65535, 65535,
            128, 32768, 32768, 32, 32, 128);
        var options = new StageCompileOptions
        {
            MeshInfo = new MeshDrawConfiguration
            {
                Execution = new MeshExecutionLimits { DeviceSubgroupLaneCount = 32 },
                Geometry = new GuestGeometryConfiguration
                {
                    WaveSize = waveSize, ThreadsPerGroup = waveSize,
                    OutputVertexCapacity = 3, OutputPrimitiveCapacity = 1,
                    InputPrimitiveCountPerWorkgroup = 1, InputVertexCountPerWorkgroup = 3,
                },
            },
        };
        var cursor = 0u;
        var userData = linked ? PipelineTestGuest.BufferDescriptor(table, 16, 1, 0) : [];
        _ = guest.Programs.GetOrCompile(guest.Source(address, ShaderStage.Mesh, userData, linked ? 8u : 0u),
            options, ref cursor, out _);
        var request = Assert.Single(guest.Compiler.Requests);
        Assert.Equal(waveSize, request.WaveSize);
        Assert.True(request.Bindings.UsesMeshDrawParameters);
        Assert.Equal(linked ? 2 : 1, guest.Programs.Entries.Count());
        Assert.All(guest.Programs.Entries, entry => Assert.Equal(waveSize, entry.Plan.Graph.WaveSize));
        if (linked)
        {
            Assert.DoesNotContain(request.Program.Instructions, instruction => instruction.Opcode == "SSwappcB64");
            Assert.Contains(request.Program.Instructions,
                instruction => instruction.Control is SharpEmu.ShaderCompiler.ShaderCallEntryControl);
        }
    }
}
