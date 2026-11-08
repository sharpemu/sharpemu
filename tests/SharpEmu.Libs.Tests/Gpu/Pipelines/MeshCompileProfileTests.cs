// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text.RegularExpressions;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Vulkan;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class MeshCompileProfileTests
{
    [Theory]
    [InlineData(ShaderStage.Mesh, 32u, true)]
    [InlineData(ShaderStage.Mesh, 64u, true)]
    [InlineData(ShaderStage.Compute, 32u, true)]
    [InlineData(ShaderStage.Mesh, 32u, false)]
    public void MeshCompileProfileCountsThePayloadOnlyOnCompilation(ShaderStage stage, uint waveSize, bool spirv)
    {
        using var fatal = new FatalScope();
        var guest = new PipelineTestGuest();
        var code = PipelineTestGuest.MemoryBase + 0x1000;
        guest.RegisterProgram(code, PipelineTestGuest.MemoryBase + 0x8000, PipelineTestGuest.EndProgram);
        guest.Host.MeshShadersSupported = true;
        guest.Host.MeshLimits = new MeshShaderLimits(128, 256, 256, 32768, 65535, 65535, 65535,
            128, 32768, 32768, 32, 32, 128);
        var cache = spirv ? new ShaderProgramCache(guest.Context, new VulkanGuestGpuBackend(), guest.Host) : guest.Programs;
        var options = stage == ShaderStage.Compute ? PipelineTestGuest.ComputeOptions() : new StageCompileOptions
        {
            MeshInfo = new MeshDrawConfiguration
            {
                Geometry = new GuestGeometryConfiguration
                {
                    WaveSize = waveSize, ThreadsPerGroup = waveSize, OutputVertexCapacity = 3,
                    OutputPrimitiveCapacity = 1, InputPrimitiveCountPerWorkgroup = 1, InputVertexCountPerWorkgroup = 3,
                },
                Execution = new MeshExecutionLimits { DeviceSubgroupLaneCount = 32 },
            },
        };
        var previous = Console.Error;
        using var output = new StringWriter();
        Console.SetError(output);
        try
        {
            var cursor = 0u;
            var source = guest.Source(code, stage, []);
            var first = cache.GetOrCompile(source, options, ref cursor, out _);
            cursor = 0;
            Assert.Equal(first.Id, cache.GetOrCompile(source, options, ref cursor, out _).Id);
        }
        finally
        {
            Console.SetError(previous);
        }
        var shader = Assert.Single(guest.Host.CompiledShaders);
        var lines = output.ToString().Split('\n').Where(line => line.StartsWith("[PERF][MESH_COMPILE]", StringComparison.Ordinal)).ToArray();
        if (!RenderPhaseProfile.Enabled || stage != ShaderStage.Mesh || !spirv)
        {
            Assert.Empty(lines);
            return;
        }
        var line = Assert.Single(lines);
        var operations = new List<SpirvOp>();
        var words = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(shader.Payload);
        for (var index = 5; index < words.Length; index += (int)(words[index] >> 16))
        {
            Assert.InRange(words[index] >> 16, 1u, (uint)(words.Length - index));
            operations.Add((SpirvOp)(words[index] & 0xffff));
        }
        void Check(string name, int expected) => Assert.Equal(expected,
            int.Parse(Regex.Match(line, $@"\b{name}=(\d+)").Groups[1].Value));
        Check("bytes", shader.Payload.Length);
        Check("static_instructions", operations.Count);
        foreach (var (name, operation) in new[]
        {
            ("static_barriers", SpirvOp.ControlBarrier), ("static_loops", SpirvOp.LoopMerge),
            ("static_elections", SpirvOp.GroupNonUniformElect), ("static_atomic_or", SpirvOp.AtomicOr),
            ("static_loads", SpirvOp.Load), ("static_stores", SpirvOp.Store),
        }) Check(name, operations.Count(value => value == operation));
        var loop = operations.IndexOf(SpirvOp.LoopMerge);
        var prefix = loop < 0 ? 0 : operations.Take(loop).SkipWhile(value => value != SpirvOp.Function)
            .Count(value => value != SpirvOp.FunctionEnd);
        Check("static_before_first_loop", prefix);
        Assert.Contains("bound_format_specialization=enabled buffer_formats=", line);
        Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(shader.Payload));
    }
}
