// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

[Collection(SchedulingStateCollection.Name)]
public sealed class LinkedShaderProgramCacheTests
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong TableAddress = PipelineTestGuest.MemoryBase + 0x10000;
    private const ulong FunctionAddress = PipelineTestGuest.MemoryBase + 0x20000;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedFrontend_PreservesNormalizationAndBufferLengths(bool deferred)
    {
        using var fatal = new FatalScope();
        var guest = CreateGuest();
        var frontend = new NormalizingFrontend();
        var original = guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(TableAddress, 16, 1, 0));
        var source = original with { Registered = original.Registered with { Frontend = frontend } };
        var programs = new ShaderProgramCache(guest.Context, guest.Compiler, guest.Host) { DeferLinking = deferred };
        var deadline = DateTime.UtcNow.AddSeconds(30);
        ShaderStageResources stage;
        string rejection;
        bool ready;
        do
        {
            var cursor = 0u;
            ready = programs.TryGetProgram(source, PipelineTestGuest.ComputeOptions(32), true,
                ref cursor, out _, out stage, out rejection);
            if (!ready) Thread.Sleep(1);
        } while (!ready && rejection.Length == 0 && DateTime.UtcNow < deadline);
        Assert.True(ready, rejection);
        var linked = Assert.Single(programs.Entries, entry => !entry.HasShaderCalls);
        Assert.Same(frontend.NormalizeBufferDescriptor, linked.Plan.NormalizeBufferDescriptor);
        Assert.Same(frontend.NormalizeImageDescriptor, linked.Plan.NormalizeImageDescriptor);
        Assert.Same(frontend.ImageNormalizationVersion, linked.Plan.ImageNormalizationVersion);
        var request = Assert.Single(guest.Compiler.Requests);
        Assert.True(request.Bindings.UsesBufferLengths);
        Assert.Equal(1u, request.Bindings.CallArgumentCount);
        Assert.Equal([0x3000ul], stage.CallArguments!);
    }

    [Fact]
    public void LinkedLibrary_ReusesModulesAndTracksTableCodeAndCallerBase()
    {
        using var fatal = new FatalScope();
        var guest = CreateGuest();
        var source = guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(TableAddress, 16, 1, 0));
        var first = Compile(guest, source, out var firstStage);
        Assert.Equal(first, Compile(guest, source));
        Assert.Equal([0x3000ul], firstStage.CallArguments!);
        var request = Assert.Single(guest.Compiler.Requests);
        Assert.DoesNotContain(request.Program.Instructions, instruction => instruction.Opcode == "SSwappcB64");
        Assert.Equal(CodeAddress + 32, Assert.Single(request.Program.Instructions.Select(instruction => instruction.Control).OfType<ShaderCallEntryControl>()).ReturnAddress);
        Assert.True(request.Resources.Info.UsesDeviceAddresses);
        Assert.NotNull(request.Bindings.Find(DescriptorBindingKind.FaultBuffer));
        Assert.Equal(2, guest.Programs.Entries.Count());
        Assert.True(Assert.Single(guest.Programs.Entries, entry =>
            entry.Program.Instructions.Any(instruction => instruction.Opcode == "SSwappcB64")).HasShaderCalls);
        Assert.False(Assert.Single(guest.Programs.Entries, entry =>
            entry.Program.Instructions.All(instruction => instruction.Opcode != "SSwappcB64")).HasShaderCalls);
        // A table rebuilt with its argument elsewhere keeps the program: the plan reads the argument from user data and
        // the module compares it relative to the base the dispatch pushes.
        WriteRecord(guest, 0x4000);
        Assert.Equal(first, Compile(guest, source, out var movedStage));
        Assert.Equal([0x4000ul], movedStage.CallArguments!);
        Assert.Equal(0x4000u, movedStage.Resources.UserData[(int)(ScalarValueGraph.CallArgumentRegisterBase - movedStage.Program!.UserDataBase)]);
        guest.WriteWords(FunctionAddress, 0xBF800001, 0xBE80200E);
        Assert.NotEqual(first, Compile(guest, source));
        RegisterCaller(guest, CodeAddress + 0x400, HeaderAddress + 0x100);
        var relocated = guest.Source(CodeAddress + 0x400, ShaderStage.Compute, source.UserData);
        Assert.Equal(source.Hash, relocated.Hash);
        Compile(guest, relocated);
        Assert.Equal(CodeAddress + 0x400 + 32,
            Assert.Single(guest.Compiler.Requests[^1].Program.Instructions.Select(instruction => instruction.Control).OfType<ShaderCallEntryControl>()).ReturnAddress);
        Assert.Equal(3, guest.Compiler.Requests.Count);
        Assert.Equal(3, guest.Host.Modules.Count);
    }

    // A cache that defers linking links a new layout on a worker: the program is not ready, without a rejection, until
    // the link is done, and then compiles as a synchronous link would.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeferredLinking_ReportsTheProgramPendingUntilItsLinkIsDone(bool strict)
    {
        using var fatal = new FatalScope();
        var guest = CreateGuest();
        var programs = new ShaderProgramCache(guest.Context, guest.Compiler, guest.Host) { DeferLinking = true };
        var source = guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(TableAddress, 16, 1, 0));
        var cursor = 0u;
        Assert.False(programs.TryGetProgram(source, PipelineTestGuest.ComputeOptions(32), strict, ref cursor, out _, out _, out var rejection));
        Assert.Empty(rejection);
        Assert.Empty(guest.Compiler.Requests);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        bool ready;
        ShaderStageResources stage;
        do
        {
            cursor = 0;
            ready = programs.TryGetProgram(source, PipelineTestGuest.ComputeOptions(32), strict, ref cursor, out _, out stage, out rejection);
            if (!ready) Thread.Sleep(1);
        } while (!ready && rejection.Length == 0 && DateTime.UtcNow < deadline);
        Assert.True(ready, rejection);
        Assert.Equal([0x3000ul], stage.CallArguments!);
        var request = Assert.Single(guest.Compiler.Requests);
        Assert.DoesNotContain(request.Program.Instructions, instruction => instruction.Opcode == "SSwappcB64");
    }

    // The linked program's translation runs on a worker too, and the program stays pending until it is done.
    [Fact]
    public void DeferredLinking_TranslatesTheLinkedProgramOnAWorker()
    {
        using var fatal = new FatalScope();
        using var release = new ManualResetEventSlim();
        var testThread = Environment.CurrentManagedThreadId;
        var translationThread = -1;
        var guest = CreateGuest(request =>
        {
            Volatile.Write(ref translationThread, Environment.CurrentManagedThreadId);
            release.Wait(TimeSpan.FromSeconds(30));
            return [];
        });
        var programs = new ShaderProgramCache(guest.Context, guest.Compiler, guest.Host) { DeferLinking = true };
        var source = guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(TableAddress, 16, 1, 0));
        bool TryGet(out ShaderStageResources stage, out string rejection)
        {
            var cursor = 0u;
            return programs.TryGetProgram(source, PipelineTestGuest.ComputeOptions(32), strictShaders: true, ref cursor, out _,
                out stage, out rejection);
        }

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (Volatile.Read(ref translationThread) < 0 && DateTime.UtcNow < deadline)
        {
            Assert.False(TryGet(out _, out var waiting));
            Assert.Empty(waiting);
            Thread.Sleep(1);
        }

        Assert.NotEqual(-1, Volatile.Read(ref translationThread));
        Assert.NotEqual(testThread, Volatile.Read(ref translationThread));
        Assert.False(TryGet(out _, out var translating));
        Assert.Empty(translating);
        release.Set();
        bool ready;
        ShaderStageResources stage;
        string rejection;
        do
        {
            ready = TryGet(out stage, out rejection);
            if (!ready) Thread.Sleep(1);
        } while (!ready && rejection.Length == 0 && DateTime.UtcNow < deadline);
        Assert.True(ready, rejection);
        Assert.Equal([0x3000ul], stage.CallArguments!);
        Assert.Single(guest.Compiler.Requests);
    }

    // A game's tables and functions can lie under an image the GPU wrote before the memory was reused, which refuses
    // clean reads of the game's own bytes: the library reads them as it reads any shader code.
    [Fact]
    public void LinkedLibrary_ReadsTablesAndCodeThatCleanReadsRefuse()
    {
        using var fatal = new FatalScope();
        var guest = CreateGuest();
        guest.Host.RefuseCleanRead = address =>
            address is >= TableAddress and < TableAddress + 16 or >= FunctionAddress and < FunctionAddress + 8;
        var source = guest.Source(CodeAddress, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(TableAddress, 16, 1, 0));
        Compile(guest, source, out var stage);
        var request = Assert.Single(guest.Compiler.Requests);
        Assert.DoesNotContain(request.Program.Instructions, instruction => instruction.Opcode == "SSwappcB64");
        Assert.Equal([FunctionAddress], stage.CallTargets!);
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

    private static PipelineTestGuest CreateGuest(Func<ShaderCompileRequest, byte[]>? compile = null)
    {
        var guest = new PipelineTestGuest(compile);
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

    private static ShaderProgram Compile(PipelineTestGuest guest, ShaderSource source) => Compile(guest, source, out _);

    private static ShaderProgram Compile(PipelineTestGuest guest, ShaderSource source, out ShaderStageResources stage)
    {
        var cursor = 0u;
        return guest.Programs.GetOrCompile(source, PipelineTestGuest.ComputeOptions(32), ref cursor, out stage);
    }

    private sealed class NormalizingFrontend : IShaderFrontend
    {
        public ulong CacheDomain => 0x474E4D;
        public bool RequiresExplicitBufferLengths => true;
        public BufferDescriptorNormalizer NormalizeBufferDescriptor { get; } = static words => words.ToArray();
        public ImageDescriptorNormalizer NormalizeImageDescriptor { get; } = static (words, _, _, _) => words.ToArray();
        public Func<long> ImageNormalizationVersion { get; } = static () => 1;
        public RegisteredShader ReadShader(CpuContext context, ulong codeAddress, string label, ReadOnlySpan<uint> userData = default) =>
            throw new NotSupportedException();
        public bool TryDecode(CpuContext context, RegisteredShader shader, out Gen5ShaderProgram program, out string error) =>
            Gen5ShaderTranslator.TryDecodeProgram(context, shader.CodeAddress, out program, out error);
    }
}
