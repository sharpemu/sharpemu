// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A linked program plans its resources with the policy of its link (run-time call records and marks), not CreateEntry's.
// Replanning it with heap records, planning it so from the start, recording it and replaying its record all keep that policy.
// The fixture's callee reads no image table, so no heap record is gained here: PreferredHeapRecords and the unchanged call
// plan are what these cases observe; the gain itself is covered by HeapRecordReplanTests on a pixel program.
[Collection(SchedulingStateCollection.Name)]
public sealed class LinkedHeapRecordReplanTests : IDisposable
{
    private const ulong Caller = PipelineTestGuest.MemoryBase + 0x1000;
    private const ulong Header = PipelineTestGuest.MemoryBase + 0x8000;
    private const ulong Table = PipelineTestGuest.MemoryBase + 0x10000;
    private const ulong Callee = PipelineTestGuest.MemoryBase + 0x20000;
    private static readonly uint[] CallerCode = [0xF4280400, 125u << 25, 0xBF8C0000, 0xBE8E0310, 0xBE8F0311,
        0xBE900312, 0xBE910313, 0xBE8E210E, 0xBF810000];
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private ShaderPrewarmList Open() => ShaderPrewarmList.Open(_directory)
        ?? throw new InvalidOperationException("The synthetic prewarm list did not open.");

    private static byte[] CompileSpirv(ShaderCompileRequest request)
    {
        Assert.True(Gen5SpirvTranslator.TryCompileProgram(request, out var shader, out var error), error);
        return shader.Spirv;
    }

    private PipelineTestGuest CreateGuest(ShaderPrewarmList list, bool callMarks)
    {
        var guest = new PipelineTestGuest(CompileSpirv);
        guest.Host.ShaderPrewarm = list;
        guest.Host.SupportsBindlessHeap = true;
        guest.Host.RuntimeCallRecordsSupported = true;
        guest.Host.CallMarksSupported = callMarks;
        guest.RegisterProgram(Caller, Header, CallerCode);
        guest.WriteWords(Callee, 0xBF800000, 0xBE80200E);
        guest.WriteWords(Table, unchecked((uint)Callee), (uint)(Callee >> 32), 0x3000, 0);
        // Runtime linking loads kept callees before its first compile: a store under this test's directory keeps the
        // user's shader cache out of it.
        var path = Path.Combine(_directory, "callees.bin");
        var decoded = ShaderFunctionReader.Read(Callee, guest.Host.TryReadGuestWord);
        Assert.True(decoded.Complete, decoded.Error);
        var callee = new UnitedCallee(new ShaderCallTarget(Callee, 0, decoded.Program),
            System.Security.Cryptography.SHA256.HashData(decoded.Bytes), decoded.Runs);
        new ShaderCallUnionStore(path).Save(ShaderStage.Compute, Source(guest).Hash, new() { [28] = [callee] });
        var field = typeof(ShaderProgramCache).GetField("_callUnionStore",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(guest.Programs, new ShaderCallUnionStore(path));
        return guest;
    }

    private static ShaderSource Source(PipelineTestGuest guest) =>
        guest.Source(Caller, ShaderStage.Compute, PipelineTestGuest.BufferDescriptor(Table, 16, 1, 0));

    private static void Compile(PipelineTestGuest guest)
    {
        var cursor = 0u;
        guest.Programs.GetOrCompile(Source(guest), PipelineTestGuest.ComputeOptions(32), ref cursor, out _);
    }

    private static ProgramSourceEntry LinkedEntry(PipelineTestGuest guest) =>
        Assert.Single(guest.Programs.Entries, entry => entry.RuntimeCallRecords);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplanningALinkedProgramKeepsItsCallRecordPolicy(bool callMarks)
    {
        using var fatal = new FatalScope();
        using var list = Open();
        var guest = CreateGuest(list, callMarks);
        Compile(guest);
        var entry = LinkedEntry(guest);
        Assert.False(entry.Plan.PreferredHeapRecords);

        var replan = Assert.IsType<Func<ShaderSource, StageCompileOptions, ShaderResourcePlan>>(entry.ReplanWithHeapRecords);
        var replanned = replan(Source(guest), PipelineTestGuest.ComputeOptions(32));

        Assert.True(replanned.PreferredHeapRecords);
        Assert.Equal(entry.Plan.CallRecords.Count, replanned.CallRecords.Count);
        Assert.Equal(entry.Plan.CallRecordCallees.Count, replanned.CallRecordCallees.Count);
        Assert.Equal(callMarks, replanned.Info.UsesCallMarks);
    }

    // The launch after a replan: the prewarm list prefers heap records for the program, so its link plans with them at once
    // and records itself with the flag and its linked callees, which the prewarm replays with the same plan.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ALinkedProgramThePrewarmPrefersIsPlannedRecordedAndReplayedWithHeapRecords(bool callMarks)
    {
        using var fatal = new FatalScope();
        ShaderCompileRequest runtime;
        byte[] runtimeSpirv;
        using (var list = Open())
        {
            var guest = CreateGuest(list, callMarks);
            var source = Source(guest);
            list.RecordCompute(Capture(source), new ComputePrewarmRecord
            {
                Hash = source.Hash, CodeSize = source.CodeSize, Address = Caller, UserDataBase = 0, UserDataCount = 4,
                PushDataCursor = 0, Info = new ComputeInputInfo { ThreadsX = 32, ThreadsY = 1, ThreadsZ = 1, WaveSize = 32 },
                Specialization = new ResourceSpecialization(), PreferHeapRecords = true,
            });
            Compile(guest);
            Assert.True(LinkedEntry(guest).Plan.PreferredHeapRecords);
            runtime = Assert.Single(guest.Compiler.Requests);
            runtimeSpirv = Assert.Single(guest.Compiler.Shaders).Spirv;
        }

        using var restored = Open();
        var (record, code) = Assert.Single(restored.LoadedComputes(), item => item.Record.Linked is not null);
        Assert.True(record.PreferHeapRecords);
        Assert.True(record.Linked!.RuntimeRecords);
        Assert.Equal(callMarks, record.Linked.CallMarks);
        var compiler = new FakeShaderCompiler(CompileSpirv);
        Assert.True(ShaderProgramCache.TryCompilePrewarm(record, code, compiler, false, true, false,
            out var compiled, out var layout, out var error, bindlessHeap: true), error);
        Assert.NotNull(layout);
        var replay = Assert.Single(compiler.Requests);
        Assert.Equal(runtime.Resources.Info.UsesCallMarks, replay.Resources.Info.UsesCallMarks);
        Assert.Equal(runtime.Program.Instructions.Select(instruction => instruction.Control),
            replay.Program.Instructions.Select(instruction => instruction.Control));
        Assert.Equal(runtimeSpirv, Assert.IsType<FakeCompiledShader>(compiled).Spirv);
        Assert.Equal(runtime.Bindings, layout);
    }

    private static ShaderCodeCapture Capture(ShaderSource source) => new()
    {
        Hash = source.Hash, CodeSize = source.CodeSize, Address = Caller, Generation = Generation.Gen5,
        Ranges = [new CodeRange(Caller, System.Runtime.InteropServices.MemoryMarshal.AsBytes<uint>(CallerCode).ToArray())],
    };
}
