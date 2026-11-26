// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// The prewarm record must describe the program that was actually compiled, including linked
// callees. The round-trip cases use the real translator and compare payload and layout as well as structure.
[Collection(SchedulingStateCollection.Name)]
public sealed class ShaderPrewarmLinkedReplayTests : IDisposable
{
    private const ulong Caller = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong Table = PipelineTestGuest.MemoryBase + 0x10000;
    private const ulong Callee = PipelineTestGuest.MemoryBase + 0x20000;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(true, true)]
    public void RecordedProgramRestoresItsLinkedCallEntries(bool linked, bool imageCallee = false)
    {
        using var fatal = new FatalScope();
        ShaderCompileRequest runtime;
        byte[] runtimeSpirv;
        using (var list = Open())
        {
            var guest = CreateGuest(list, linked, imageCallee);
            Compile(guest, linked);
            runtime = Assert.Single(guest.Compiler.Requests);
            runtimeSpirv = Assert.Single(guest.Compiler.Shaders).Spirv;
            Assert.DoesNotContain(runtime.Program.Instructions, instruction => instruction.Opcode == "SSwappcB64");
            Assert.Equal(linked ? 1 : 0, CallEntries(runtime).Length);
            if (imageCallee) Assert.NotEmpty(runtime.Resources.Info.Images);
        }

        using var restored = Open();
        var (record, code) = Assert.Single(restored.LoadedComputes());
        var compiler = new FakeShaderCompiler(CompileSpirv);
        Assert.True(ShaderProgramCache.TryCompilePrewarm(record, code, compiler, false, true, false,
            out var compiled, out var layout, out var error), error);
        Assert.NotNull(compiled);
        Assert.NotNull(layout);
        Assert.Equal(runtimeSpirv, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
        Assert.Equal(runtime.Bindings, layout);
        Assert.Equal(runtime.Bindings.AllocationCursor, layout.AllocationCursor);
        var replay = Assert.Single(compiler.Requests);
        Assert.DoesNotContain(replay.Program.Instructions, instruction => instruction.Opcode == "SSwappcB64");
        Assert.Equal(CallEntries(runtime), CallEntries(replay));
        Assert.Equal(runtime.Program.Instructions.Select(instruction => instruction.Opcode).ToArray(),
            replay.Program.Instructions.Select(instruction => instruction.Opcode).ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public void RuntimeRecordModesRoundTripThroughRecordingAndTheTranslator(bool callMarks, bool withCallee, bool gpuThreadCounts = false)
    {
        using var fatal = new FatalScope();
        ShaderCompileRequest runtime;
        byte[] runtimeSpirv;
        using (var list = Open())
        {
            var guest = CreateGuest(list, linked: true);
            guest.Host.RuntimeCallRecordsSupported = true;
            guest.Host.CallMarksSupported = callMarks;
            UseIsolatedCallUnion(guest, withCallee);
            Compile(guest, linked: true, gpuThreadCounts: gpuThreadCounts);
            runtime = Assert.Single(guest.Compiler.Requests);
            runtimeSpirv = Assert.Single(guest.Compiler.Shaders).Spirv;
            Assert.Equal(withCallee ? 1 : 0, CallEntries(runtime).Length);
            Assert.All(runtime.Program.Instructions.Select(instruction => instruction.Control)
                .OfType<ShaderCallEntryControl>(), entry => Assert.True(entry.RuntimeArgument));
            var fault = Assert.Single(runtime.Program.Instructions.Select(instruction => instruction.Control)
                .OfType<ShaderCallFaultControl>());
            Assert.NotNull(fault.ReturnPc);
            Assert.Equal(callMarks, runtime.Resources.Info.UsesCallMarks);
            Assert.Equal(callMarks, runtime.Bindings.Find(DescriptorBindingKind.CallMarks) is not null);
            Assert.Equal(gpuThreadCounts, runtime.Bindings.UsesDispatchThreadLimits);
            if (gpuThreadCounts)
            {
                Assert.False(runtime.Bindings.UsesPushData);
                Assert.NotNull(runtime.Bindings.Find(DescriptorBindingKind.ShaderData));
            }
        }

        using var restored = Open();
        var (record, code) = Assert.Single(restored.LoadedComputes());
        Assert.NotNull(record.Linked);
        Assert.True(record.Linked.RuntimeRecords);
        Assert.Equal(gpuThreadCounts, record.Info.IndirectThreadCounts);
        Assert.Equal(gpuThreadCounts, record.Info.DispatchThreadDimensions);
        Assert.Equal(gpuThreadCounts ? PushData.DwordCount : 0u, record.PushDataCursor);
        Assert.Equal(callMarks, record.Linked.CallMarks);
        Assert.Equal(withCallee ? 1 : 0, record.Linked.Functions.Length);
        Assert.Single(record.Linked.Sites);
        Assert.Equal(withCallee ? 1 : 0, record.Linked.Sites[0].Targets.Length);
        var compiler = new FakeShaderCompiler(CompileSpirv);
        Assert.True(ShaderProgramCache.TryCompilePrewarm(record, code, compiler, false, true, false,
            out var compiled, out var layout, out var error), error);
        Assert.Equal(runtimeSpirv, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
        Assert.Equal(runtime.Bindings, layout);
        var replay = Assert.Single(compiler.Requests);
        Assert.Equal(runtime.Resources.Info.UsesCallMarks, replay.Resources.Info.UsesCallMarks);
        Assert.Equal(runtime.Program.Instructions.Select(instruction => instruction.Control),
            replay.Program.Instructions.Select(instruction => instruction.Control));
    }

    private void UseIsolatedCallUnion(PipelineTestGuest guest, bool withCallee)
    {
        // Runtime linking loads kept callees before its first compile. Inject a store under this test's directory,
        // so this regression never reads or writes the user's shader cache and needs no process-wide environment edits.
        var path = Path.Combine(_directory, "callees.bin");
        if (withCallee)
        {
            var decoded = ShaderFunctionReader.Read(Callee, guest.Host.TryReadGuestWord);
            Assert.True(decoded.Complete, decoded.Error);
            var source = guest.Source(Caller, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(Table, 16, 1, 0));
            var callee = new UnitedCallee(new ShaderCallTarget(Callee, 0, decoded.Program),
                System.Security.Cryptography.SHA256.HashData(decoded.Bytes), decoded.Runs);
            // The fixture's s_swappc is word7 (pc28); its return is Caller+32.
            new ShaderCallUnionStore(path).Save(ShaderStage.Compute, source.Hash, new() { [28] = [callee] });
        }
        var field = typeof(ShaderProgramCache).GetField("_callUnionStore",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(guest.Programs, new ShaderCallUnionStore(path));
    }

    [Fact]
    public void DifferentLinkedCalleeCodeDoesNotCollapseToOnePrewarmRecord()
    {
        using var fatal = new FatalScope();
        using (var list = Open())
        {
            var guest = CreateGuest(list, linked: true);
            var first = Compile(guest, linked: true);
            // This existing linked-cache fixture changes code without changing descriptors or the caller.
            guest.WriteWords(Callee, 0xBF800001, 0xBE80200E);
            var second = Compile(guest, linked: true);
            Assert.NotEqual(first, second);
            Assert.Equal(2, guest.Compiler.Requests.Count);
            Assert.All(guest.Compiler.Requests, request => Assert.Single(CallEntries(request)));
        }

        using var restored = Open();
        var records = restored.LoadedComputes();
        Assert.Equal(2, records.Count);
        Assert.Equal(2, records.Select(item => ShaderPrewarmList.Identity(item.Record)).Distinct().Count());
    }

    private static PipelineTestGuest CreateGuest(ShaderPrewarmList list, bool linked, bool imageCallee = false)
    {
        var guest = new PipelineTestGuest(CompileSpirv);
        guest.Host.ShaderPrewarm = list;
        guest.RegisterProgram(Caller, Header, linked
            ? [0xF4280400, 125u << 25, 0xBF8C0000, 0xBE8E0310, 0xBE8F0311,
                0xBE900312, 0xBE910313, 0xBE8E210E, 0xBF810000]
            : PipelineTestGuest.EndProgram);
        var argument = imageCallee ? PipelineTestGuest.MemoryBase + 0x30000 : 0x3000UL;
        if (imageCallee)
        {
            // Copy the record argument s[16:17] to the descriptor-table base s[0:1]; preserve return s[14:15].
            guest.WriteWords(Callee, [0xBE800410, .. ImageBindingTestProgram.Code()[..^1], 0xBE80200E]);
            ImageBindingTestProgram.WriteDescriptors(guest, argument, ImageBindingTestProgram.Partitions[0]);
        }
        else guest.WriteWords(Callee, 0xBF800000, 0xBE80200E);
        guest.WriteWords(Table, unchecked((uint)Callee), (uint)(Callee >> 32), unchecked((uint)argument), (uint)(argument >> 32));
        return guest;
    }

    [Fact]
    public void LinkedReplayStillRejectsAnInvalidIndirectRoot()
    {
        using var fatal = new FatalScope();
        using (var list = Open()) Compile(CreateGuest(list, linked: true), linked: true);
        using var restored = Open();
        var (record, code) = Assert.Single(restored.LoadedComputes());
        Assert.NotNull(record.Linked);
        record.Specialization.Images.Add(new ImageSpecialization(
            ImageNumericClass.Float, ImageDimension.Dim2D, 1, 0, 0, uint.MaxValue, 0, 0, false));
        var compiler = new FakeShaderCompiler();
        Assert.False(ShaderProgramCache.TryCompilePrewarm(record, code, compiler, false, true, false,
            out _, out _, out var error));
        Assert.Contains("invalid indirect root", error);
        Assert.Empty(compiler.Requests);
    }

    [Fact]
    public void GlobalCacheStampDoesNotCertifyLinkedCaches()
    {
        using var fatal = new FatalScope();
        using (var list = Open()) Compile(CreateGuest(list, linked: true), linked: true);
        using var restored = Open();
        Assert.True(restored.HasLinkedComputes);
        var stamp = restored.CoverageStamp("test-build");
        byte[] cache = [1, 2, 3];
        restored.WriteStamp(ShaderPrewarmList.CacheStamp(stamp, cache));
        var presenter = typeof(SharpEmu.Libs.VideoOut.VulkanVideoPresenter)
            .GetNestedType("Presenter", System.Reflection.BindingFlags.NonPublic)!;
        var skip = presenter.GetMethod("CanSkipShaderPrewarm",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        Assert.False((bool)skip.Invoke(null, [restored, stamp, cache])!);
    }

    private static ShaderProgram Compile(PipelineTestGuest guest, bool linked, bool gpuThreadCounts = false)
    {
        // Match ShaderPipelineCache: GPU-written limits must live outside push constants.
        var cursor = gpuThreadCounts ? PushData.DwordCount : 0u;
        var userData = linked ? PipelineTestGuest.BufferDescriptor(Table, 16, 1, 0) : Array.Empty<uint>();
        var options = gpuThreadCounts ? new StageCompileOptions
        {
            ComputeInfo = new ComputeInputInfo
            {
                ThreadsX = 32, ThreadsY = 1, ThreadsZ = 1, WaveSize = 32, GroupIdX = true, ThreadIdCount = 1,
                DispatchThreadDimensions = true, IndirectThreadCounts = true,
            },
        } : PipelineTestGuest.ComputeOptions(32);
        var program = guest.Programs.GetOrCompile(guest.Source(Caller, ShaderStage.Compute, userData),
            options, ref cursor, out var stage);
        Assert.Equal(gpuThreadCounts, stage.GpuThreadLimits);
        return program;
    }

    private static byte[] CompileSpirv(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private static ulong[] CallEntries(ShaderCompileRequest request) => request.Program.Instructions
        .Select(instruction => instruction.Control).OfType<ShaderCallEntryControl>()
        .Select(entry => entry.ReturnAddress).ToArray();

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory)
        ?? throw new InvalidOperationException("The synthetic prewarm list did not open.");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
