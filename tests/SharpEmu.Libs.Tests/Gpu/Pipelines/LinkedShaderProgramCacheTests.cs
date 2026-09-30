// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class LinkedShaderProgramCacheTests
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong TableAddress = PipelineTestGuest.MemoryBase + 0x10000;
    private const ulong FunctionAddress = PipelineTestGuest.MemoryBase + 0x20000;

    [Fact]
    public void LinkedLibrary_ReusesModulesAndTracksTableCodeAndCallerBase()
    {
        using var fatal = new FatalScope();
        var guest = CreateGuest();
        var source = guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(TableAddress, 16, 1, 0));
        var first = Compile(guest, source);
        Assert.Equal(first, Compile(guest, source));
        var request = Assert.Single(guest.Compiler.Requests);
        Assert.DoesNotContain(request.Program.Instructions, instruction => instruction.Opcode == "SSwappcB64");
        Assert.Equal(CodeAddress + 32, Assert.Single(request.Program.Instructions.Select(instruction => instruction.Control).OfType<ShaderCallEntryControl>()).ReturnAddress);
        Assert.True(request.Resources.Info.UsesDeviceAddresses);
        Assert.NotNull(request.Bindings.Find(DescriptorBindingKind.FaultBuffer));
        Assert.Equal(2, guest.Programs.Entries.Count());
        WriteRecord(guest, 0x4000);
        Assert.NotEqual(first, Compile(guest, source));
        guest.WriteWords(FunctionAddress, 0xBF800001, 0xBE80200E);
        Assert.NotEqual(first, Compile(guest, source));
        RegisterCaller(guest, CodeAddress + 0x400, HeaderAddress + 0x100);
        var relocated = guest.Source(CodeAddress + 0x400, ShaderStage.Compute, source.UserData);
        Assert.Equal(source.Hash, relocated.Hash);
        Compile(guest, relocated);
        Assert.Equal(CodeAddress + 0x400 + 32,
            Assert.Single(guest.Compiler.Requests[^1].Program.Instructions.Select(instruction => instruction.Control).OfType<ShaderCallEntryControl>()).ReturnAddress);
        Assert.Equal(4, guest.Compiler.Requests.Count);
        Assert.Equal(4, guest.Host.Modules.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidLibrary_UsesTheRequestedRejectionPolicy(bool strict)
    {
        using var fatal = new FatalScope();
        var guest = CreateGuest();
        guest.WriteWords(FunctionAddress, 0xBE8E0380, 0xBE80200E);
        var source = guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(TableAddress, 16, 1, 0));
        var cursor = 0u;
        if (strict)
        {
            var error = Assert.Throws<SchedulerFatalException>(() => guest.Programs.TryGetProgram(source,
                PipelineTestGuest.ComputeOptions(32), true, ref cursor, out _, out _, out _));
            Assert.Contains("changes its return address", error.Message);
        }
        else
        {
            Assert.False(guest.Programs.TryGetProgram(source, PipelineTestGuest.ComputeOptions(32), false,
                ref cursor, out _, out _, out var rejection));
            Assert.Contains("changes its return address", rejection);
        }
        Assert.Empty(guest.Compiler.Requests);
        Assert.Empty(guest.Host.Modules);
        Assert.Equal(0u, cursor);
    }

    private static PipelineTestGuest CreateGuest()
    {
        var guest = new PipelineTestGuest();
        RegisterCaller(guest, CodeAddress, HeaderAddress);
        guest.WriteWords(FunctionAddress, 0xBF800000, 0xBE80200E);
        WriteRecord(guest, 0x3000);
        return guest;
    }

    private static void RegisterCaller(PipelineTestGuest guest, ulong address, ulong header) =>
        guest.RegisterProgram(address, header, [0xF4280400, 125u << 25, 0xBF8C0000,
            0xBE8E0310, 0xBE8F0311, 0xBE900312, 0xBE910313, 0xBE8E210E, 0xBF810000]);

    private static void WriteRecord(PipelineTestGuest guest, uint argument) =>
        guest.WriteWords(TableAddress, unchecked((uint)FunctionAddress), (uint)(FunctionAddress >> 32), argument, 0);

    private static ShaderProgram Compile(PipelineTestGuest guest, ShaderSource source)
    {
        var cursor = 0u;
        return guest.Programs.GetOrCompile(source, PipelineTestGuest.ComputeOptions(32), ref cursor, out _);
    }
}
