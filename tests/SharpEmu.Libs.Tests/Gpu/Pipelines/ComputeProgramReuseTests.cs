// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class ComputeProgramReuseTests
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;

    [Fact]
    public void RepeatedRegisterWritesWithTheSameValuesKeepTheProgram()
    {
        using var fatal = new FatalScope();
        var (cache, guest) = CreateCache();
        var banks = Banks();
        var first = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1);
        banks.MarkWritten(RegisterStamps.AllBits);
        var second = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1);

        Assert.True(first.Available);
        Assert.Equal(first.Program, second.Program);
        Assert.Single(guest.Host.Modules);
    }

    [Fact]
    public void ChangedUserDataResolvesTheResourcesAgainOnTheSameProgram()
    {
        using var fatal = new FatalScope();
        var (cache, guest) = CreateCache();
        var banks = Banks();
        var first = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1);
        banks.Shader.Compute.UserScalars.Set(0, 0x1234, UserScalarKind.Unknown);
        var second = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1);

        Assert.True(second.Available);
        Assert.NotSame(first, second);
        Assert.Equal(0x1234u, second.Input.Stage.Resources.UserData[0]);
        Assert.Single(guest.Host.Modules);
    }

    [Fact]
    public void ChangedDispatchShapeIsNotKept()
    {
        using var fatal = new FatalScope();
        var (cache, _) = CreateCache();
        var banks = Banks();
        var first = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1);
        banks.Shader.Compute.ThreadsX = 64;
        var second = cache.GetComputeProgram(banks, 0x8001, 1, 1, 1);

        Assert.NotSame(first, second);
        Assert.Equal(64u, second.Input.ThreadsX);
    }

    private static (ShaderPipelineCache Cache, PipelineTestGuest Guest) CreateCache()
    {
        var guest = new PipelineTestGuest();
        guest.RegisterProgram(CodeAddress, HeaderAddress, PipelineTestGuest.EndProgram);
        return (new ShaderPipelineCache(guest.Context, guest.Host, guest.Compiler, guest.Registry), guest);
    }

    private static RegisterBanks Banks()
    {
        var banks = new RegisterBanks(static message => new InvalidOperationException(message));
        var compute = banks.Shader.Compute;
        compute.Address = CodeAddress;
        compute.ThreadsX = 1;
        compute.ThreadsY = 1;
        compute.ThreadsZ = 1;
        compute.UserScalarCount = 8;
        return banks;
    }
}
