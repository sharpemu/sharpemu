// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;
using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderHeaderRegistryTests : IDisposable
{
    private readonly FatalScope _fatal = new();
    private const ulong Code = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;

    public void Dispose() => _fatal.Dispose();

    [Fact]
    public void FrontendHeadersReflectTheCurrentDrawUserDataAtTheSameCodeAddress()
    {
        var guest = new PipelineTestGuest();
        var frontend = new UserDataFrontend();
        var registry = new ShaderHeaderRegistry(guest.Context, _ => 0, _ => null, frontend);
        var first = registry.RequireEntry(Code, "fetch", [1u]);
        var second = registry.RequireEntry(Code, "fetch", [2u]);
        Assert.Equal(4u, first.Shader.CodeSizeBytes);
        Assert.Equal(8u, second.Shader.CodeSizeBytes);
        Assert.Same(frontend, first.Shader.Frontend);
        Assert.Equal(first.Registrations, second.Registrations);
    }

    private sealed class UserDataFrontend : IShaderFrontend
    {
        public ulong CacheDomain => 1;
        public RegisteredShader ReadShader(CpuContext context, ulong codeAddress, string label, ReadOnlySpan<uint> userData = default) =>
            new(codeAddress, 0, userData[0] * 4, 0, 0, 0, 0, 0, 0);

        public bool TryDecode(CpuContext context, RegisteredShader shader, out Gen5ShaderProgram program, out string error)
        {
            program = null!;
            error = string.Empty;
            return false;
        }
    }

    [Theory]
    [InlineData(3, 19)]
    [InlineData(19, 3)]
    [InlineData(0, 65535)]
    public void FusedHeadersKeepEachScratchSize(int entryScratch, int continuationScratch)
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(Code, Header, PipelineTestGuest.EndProgram);
        guest.RegisterProgram(Code + 0x100, Header + 0x100, [0xBF800000, 0xBF810000]);
        guest.Write(Header + 0x54, BitConverter.GetBytes((ushort)entryScratch));
        guest.Write(Header + 0x154, BitConverter.GetBytes((ushort)continuationScratch));
        var registry = new ShaderHeaderRegistry(guest.Context, _ => Header,
            _ => new FusedProgramParts(Code + 0x100, Header + 0x100));
        var registered = registry.Require(Code, "test");
        Assert.Equal((uint)entryScratch, registered.ScratchDwords);
        Assert.Equal((uint)continuationScratch, registered.ContinuationScratchDwords);
        Assert.True(registered.IsFused);
        Assert.Equal(new (ulong, uint)[] { (Code, 4), (Code + 0x100, 8) }, registered.CodeRanges);
        Assert.Equal(12u, registered.TotalCodeSizeBytes);
    }

    [Fact]
    public void OrdinaryShaderHasNoContinuationScratch()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(Code, Header, PipelineTestGuest.EndProgram);
        guest.Write(Header + 0x54, BitConverter.GetBytes((ushort)7));
        var registered = guest.Registry.Require(Code, "test");
        Assert.Equal(7u, registered.ScratchDwords);
        Assert.Equal(0u, registered.ContinuationScratchDwords);
        Assert.False(registered.IsFused);
        Assert.Single(registered.CodeRanges);
    }

    [Fact]
    public void UnreadableContinuationScratchStopsRegistration()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(Code, Header, PipelineTestGuest.EndProgram);
        var continuationHeader = PipelineTestGuest.MemoryBase + PipelineTestGuest.MemorySize - 0x50;
        guest.WriteWords(continuationHeader + 0x44, 4);
        var registry = new ShaderHeaderRegistry(guest.Context, _ => Header,
            _ => new FusedProgramParts(Code + 0x100, continuationHeader));
        var failure = Assert.Throws<SchedulerFatalException>(() => registry.Require(Code, "test"));
        Assert.Contains("continuation shader scratch size is unreadable", failure.Message);
    }
}
