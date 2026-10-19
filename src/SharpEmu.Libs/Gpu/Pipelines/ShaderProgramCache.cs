// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using ImageResourceClass = SharpEmu.Libs.Gpu.Rendering.ImageResourceClass;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Gpu.Pipelines;

// One program as a draw names it: the registered code, its identity and the user data of the draw.
public sealed record ShaderSource(RegisteredShader Registered, ulong Hash, uint[] UserData, uint UserDataBase, ShaderStage Stage)
{
    public ulong Address => Registered.CodeAddress;

    public uint CodeSize => Registered.TotalCodeSizeBytes;

    public ulong ContinuationAddressOffset => Registered.IsFused
        ? unchecked(Registered.ContinuationAddress - Address)
        : 0;

    public string Label => Stage switch
    {
        ShaderStage.Vertex => "vertex",
        ShaderStage.Pixel => "pixel",
        ShaderStage.Mesh => "mesh",
        _ => "compute",
    };
}

// The stage-specific compile inputs one lookup carries beside its static key.
public sealed class StageCompileOptions
{
    public VertexInputInfo? VertexInfo { get; init; }
    public int RequiredVertexOutputCount { get; init; }
    public uint? MeshOutputLocationMask { get; init; }
    public PixelInputInfo? PixelInfo { get; init; }
    public IReadOnlyList<Gen5PixelOutputBinding> PixelOutputs { get; init; } = [];
    public uint PixelInputEnable { get; init; }
    public uint PixelInputAddress { get; init; }
    public ComputeInputInfo? ComputeInfo { get; init; }
    public Gen5ComputeSystemRegisters? ComputeSystemRegisters { get; init; }
    public MeshDrawConfiguration? MeshInfo { get; init; }
}

// The key of a program entry: what the emitter reads besides the resource specialization.
public sealed class ProgramKey : IEquatable<ProgramKey>
{
    public ProgramKey(ShaderStage stage, ulong hash, uint userDataCount, uint codeSize,
        ulong continuationAddressOffset, uint[] staticState)
    {
        Stage = stage;
        Hash = hash;
        UserDataCount = userDataCount;
        CodeSize = codeSize;
        ContinuationAddressOffset = continuationAddressOffset;
        StaticState = staticState;
    }

    public ShaderStage Stage { get; }
    public ulong Hash { get; }
    public uint UserDataCount { get; }
    public uint CodeSize { get; }
    public ulong ContinuationAddressOffset { get; }
    public uint[] StaticState { get; }

    public bool Equals(ProgramKey? other) =>
        other is not null && Stage == other.Stage && Hash == other.Hash && UserDataCount == other.UserDataCount &&
        CodeSize == other.CodeSize && ContinuationAddressOffset == other.ContinuationAddressOffset &&
        StaticState.AsSpan().SequenceEqual(other.StaticState);

    public override bool Equals(object? obj) => Equals(obj as ProgramKey);

    // Same-shape variants share a bucket; equality does the one exact comparison of the state words.
    public override int GetHashCode() =>
        HashCode.Combine(Stage, Hash, UserDataCount, CodeSize, ContinuationAddressOffset, StaticState.Length);
}

// A program key as a lookup sees it: the static state words stay in the caller's list, so a draw
// that finds its program does not build a key object or copy the words.
internal readonly ref struct ProgramKeyProbe(ShaderStage stage, ulong hash, uint userDataCount, uint codeSize,
    ulong continuationAddressOffset, ReadOnlySpan<uint> staticState)
{
    public readonly ShaderStage Stage = stage;
    public readonly ulong Hash = hash;
    public readonly uint UserDataCount = userDataCount;
    public readonly uint CodeSize = codeSize;
    public readonly ulong ContinuationAddressOffset = continuationAddressOffset;
    public readonly ReadOnlySpan<uint> StaticState = staticState;
}

internal sealed class ProgramKeyComparer : IEqualityComparer<ProgramKey>, IAlternateEqualityComparer<ProgramKeyProbe, ProgramKey>
{
    public static readonly ProgramKeyComparer Instance = new();

    public bool Equals(ProgramKey? x, ProgramKey? y) => x is null ? y is null : x.Equals(y);

    public int GetHashCode(ProgramKey key) => key.GetHashCode();

    public bool Equals(ProgramKeyProbe alternate, ProgramKey key) =>
        alternate.Stage == key.Stage && alternate.Hash == key.Hash && alternate.UserDataCount == key.UserDataCount &&
        alternate.CodeSize == key.CodeSize && alternate.ContinuationAddressOffset == key.ContinuationAddressOffset &&
        alternate.StaticState.SequenceEqual(key.StaticState);

    // The same value as ProgramKey.GetHashCode.
    public int GetHashCode(ProgramKeyProbe alternate) =>
        HashCode.Combine(alternate.Stage, alternate.Hash, alternate.UserDataCount, alternate.CodeSize,
            alternate.ContinuationAddressOffset, alternate.StaticState.Length);

    public ProgramKey Create(ProgramKeyProbe alternate) =>
        new(alternate.Stage, alternate.Hash, alternate.UserDataCount, alternate.CodeSize,
            alternate.ContinuationAddressOffset, alternate.StaticState.ToArray());
}

// One compiled module of a program entry for one specialization and push-data start.
internal sealed class ProgramPermutation
{
    public required ResourceSpecialization Specialization { get; init; }
    public required ShaderProgramInfo Program { get; init; }
    public required ShaderProgram Handle { get; init; }
    public required IGuestCompiledShader Compiled { get; init; }

    public BindingLayout Bindings => Program.Bindings!;
}

// One decoded program with its resource plan and every permutation compiled from it.
internal sealed class ProgramSourceEntry
{
    public required ShaderResourcePlan Plan { get; init; }
    public required Gen5ShaderProgram Program { get; init; }
    public required bool HasBitwiseExclusiveOr { get; init; }
    public required bool HasShaderCalls { get; init; }
    public ConstantFill? ConstantFill { get; init; }
    public BoundedFill? BoundedFill { get; init; }
    public BoundedCopy? BoundedCopy { get; init; }
    public EmbeddedVertexFetchPlan? EmbeddedFetch { get; init; }
    public ShaderVertexInput[] VertexInputs { get; init; } = [];
    public bool FetchEmbedded { get; init; }
    public int FetchAttributeRegister { get; init; }
    public int FetchBufferRegister { get; init; }
    public List<ProgramPermutation> Permutations { get; } = new(8);
}

internal readonly record struct ShaderCompileHostFlags(
    bool GraphicsSubgroups,
    bool SharedInt64Atomics,
    bool ExecGuardElision,
    bool ClipDistance,
    bool PerVertexPixelInputs,
    bool PortableBuffers,
    bool SignedZeroInfNanPreserveFloat32 = false,
    bool MipStatisticsSubgroupReduction = false,
    bool ComputeWave64SubgroupNative = false)
{
    public static ShaderCompileHostFlags From(IShaderPipelineHost host) => new(
        host.GraphicsSubgroupOperationsEnabled,
        host.SharedInt64AtomicsEnabled,
        host.ExecGuardElisionEnabled,
        host.ClipDistanceEnabled,
        host.PerVertexPixelInputsSupported,
        host.PortableBufferDescriptors,
        host.ShaderSignedZeroInfNanPreserveFloat32Supported,
        host.MipStatisticsSubgroupReduction,
        host.ComputeWave64SubgroupNative);
}

// Programs keyed by identity and static state; each draw materializes its resources and reuses
// the permutation whose specialization and push-data start match, else compiles one more.
internal sealed class ShaderProgramCache
{
    private readonly ImageDescriptorTrace _imageDescriptorTrace = new();
    private const uint MaxInstructionScan = 16384;

    private readonly CpuContext _context;
    private readonly IGuestGpuBackend _compiler;
    private readonly IShaderPipelineHost _host;
    private readonly Dictionary<ProgramKey, ProgramSourceEntry> _programs = new(ProgramKeyComparer.Instance);
    private readonly Dictionary<(ProgramKey Key, string Library), ProgramSourceEntry> _linkedPrograms = new();
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong ContinuationAddressOffset), Gen5ShaderProgram> _decoded = new();
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong ContinuationAddressOffset), ShaderCodeCapture> _codeCaptures = new();
    private readonly Dictionary<ulong, (StageRecord Record, ShaderCodeCapture Code)> _stageRecords = new();
    private readonly List<uint> _staticState = new(StageStaticKey.MaxWords);
    // Draws that re-bind unchanged resources reuse the last materialization.
    // SHARPEMU_RESOURCE_CACHE=0 materializes every draw, for A/B comparisons.
    private readonly ResourceMaterializationCache? _materializations =
        Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_CACHE") == "0" ? null : new();
    private ulong _nextProgramId;

    public ResourceMaterializationCache? Materializations => _materializations;

    // Read once: the draw path asked the environment on every draw and dispatch
    // (~4 % of the Demon's Souls render thread) for a debug dump that is almost never on.
    private readonly bool _spirvDumpEnabled = string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"), "1", StringComparison.Ordinal);

    private readonly GuestWordReader _readGuestWord;
    private readonly GuestWordReader _readCleanGuestWord;
    private readonly ResidentGuestBytesReader _readResidentGuestBytes;
    private readonly ResidentGuestBytesReader? _prefetchResidentGuestBytes;

    private static readonly bool PrefetchEnabled = Environment.GetEnvironmentVariable("SHARPEMU_RESOURCE_PREFETCH") != "0";

    public ShaderProgramCache(CpuContext context, IGuestGpuBackend compiler, IShaderPipelineHost host)
    {
        _context = context;
        _compiler = compiler;
        _host = host;
        _readGuestWord = host.TryReadGuestWord;
        _readCleanGuestWord = host.TryReadCleanGuestWord;
        _readResidentGuestBytes = host.TryReadResidentGuestBytes;
        _prefetchResidentGuestBytes = PrefetchEnabled ? _readResidentGuestBytes : null;
    }

    public int ProgramCount => _programs.Count;

    public IEnumerable<ProgramSourceEntry> Entries => _programs.Values.Concat(_linkedPrograms.Values);

    // The decoded instructions of a program, shared by every static variant of the same code.
    public Gen5ShaderProgram Decode(ShaderSource source)
    {
        var key = (source.Hash, source.CodeSize, source.ContinuationAddressOffset);
        if (_decoded.TryGetValue(key, out var program))
        {
            return program;
        }

        using var decodeScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramDecode);
        var recording = _host.ShaderCache is not null ? new RecordingCpuMemory(_context.Memory) : null;
        var context = recording is null ? _context : new CpuContext(recording, _context.TargetGeneration);
        if (!Gen5ShaderTranslator.TryDecodeProgram(context, source.Address, out program, out var error))
        {
            throw SubmissionScheduler.Fatal($"The shader program cannot be decoded: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.");
        }

        _decoded.Add(key, program);
        if (recording is not null)
        {
            _codeCaptures[key] = new ShaderCodeCapture
            {
                Hash = source.Hash,
                CodeSize = source.CodeSize,
                Address = source.Address,
                Generation = _context.TargetGeneration,
                Fused = Gen5ShaderTranslator.TryGetFusedProgramParts(
                    _context, source.Address, out var entryHeader, out var continuation, out var continuationHeader)
                    ? new FusedCodeParts(entryHeader, continuation, continuationHeader)
                    : null,
                Ranges = recording.TakeRanges(),
            };
        }

        return program;
    }

    public ShaderProgram GetOrCompile(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor, out ShaderStageResources stage)
    {
        try
        {
            return GetOrCompileCore(source, options, ref pushDataCursor, out stage);
        }
        catch (ShaderProgramRejectedException exception)
        {
            throw SubmissionScheduler.Fatal(exception.Message);
        }
    }

    public bool TryGetProgram(ShaderSource source, StageCompileOptions options, bool strictShaders,
        ref uint pushDataCursor, out ShaderProgram program, out ShaderStageResources stage, out string rejection)
    {
        rejection = string.Empty;
        if (strictShaders)
        {
            program = GetOrCompile(source, options, ref pushDataCursor, out stage);
            return true;
        }

        try
        {
            program = GetOrCompileCore(source, options, ref pushDataCursor, out stage);
            return true;
        }
        catch (ShaderProgramRejectedException exception)
        {
            program = default;
            stage = default;
            rejection = exception.Message;
            return false;
        }
    }

    private sealed class ShaderProgramRejectedException(string message) : Exception(message);

    private ShaderProgram GetOrCompileCore(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor, out ShaderStageResources stage)
    {
        ProgramKey? key = null;
        ProgramSourceEntry? entry;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramCacheLookup))
        {
            BuildStaticState(source.Stage, options);
            var probe = new ProgramKeyProbe(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize,
                source.ContinuationAddressOffset, CollectionsMarshal.AsSpan(_staticState));
            _programs.GetAlternateLookup<ProgramKeyProbe>().TryGetValue(probe, out key, out entry);
        }
        var sourceWasCached = entry is not null;
        if (RenderTrace.Enabled && RenderTrace.Pipeline())
        {
            RenderTrace.Write($"ProgramCache lookup stage={source.Label} hash=0x{source.Hash:X16} cached={entry is not null} permutations={entry?.Permutations.Count ?? 0}");
        }

        var inputs = new ResourceRuntimeInputs
        {
            UserData = source.UserData,
            ShaderBase = source.Address,
            ReadMemory = _readGuestWord,
            ReadCleanMemory = _readCleanGuestWord,
            TraceImageCandidate = SharpEmu.Libs.Gpu.Images.ImageClearTrace.Enabled
                ? (descriptorSource, candidateKey, heap, address, words) =>
                    _imageDescriptorTrace.RecordCandidate(source.Hash, descriptorSource, candidateKey, heap, address, words.Span)
                : null,
            ReadResidentMemory = _prefetchResidentGuestBytes,
            PortableBuffers = UsesPortableBuffers(source.Stage),
            ComputeState = source.Stage == ShaderStage.Compute && options.ComputeInfo is { } computeState
                ? new ComputeSelectorState(computeState.WaveSize, Math.Max(computeState.ThreadsX, 1),
                    Math.Max(computeState.ThreadsY, 1), Math.Max(computeState.ThreadsZ, 1), computeState.DispatchThreadDimensions,
                    computeState.LocalDataShareDwords, computeState.ThreadIdCount)
                : null,
        };
        if (entry is null)
        {
            entry = CreateEntry(source, options);
            key = BuildProgramKey(source);
            _programs.Add(key, entry);
            ShaderCacheCounters.CountProgram();
        }

        var hasShaderCalls = entry.HasShaderCalls;
        if (hasShaderCalls && !_compiler.LinkedShaderCallsSupported)
            throw new ShaderProgramRejectedException(
                $"The backend does not support linked shader calls: backend={_compiler.BackendName} stage={source.Label} hash=0x{source.Hash:X16}.");

        // Both are assigned by the materialization that follows; a failed one throws.
        ResourceSnapshot snapshot = null!;
        ResourceSpecialization specialization = null!;
        var captureIndirectImageFailure = _spirvDumpEnabled ? ShaderPermutationDump.CreateFailureCapture(source) : null;
        if (Diagnostics.GpuReadTrace.Enabled)
        {
            Diagnostics.GpuReadTrace.CurrentShader = source.Hash;
            Diagnostics.GpuReadTrace.CurrentStage = source.Label;
        }

        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ResourceMaterialization))
        {
            // Failure capture needs the full walk, so a dump run bypasses the cache.
            var materialized = _materializations is not null && captureIndirectImageFailure is null
                ? _materializations.Materialize(entry.Plan, inputs, _readResidentGuestBytes, ref snapshot, ref specialization,
                    out var materializationFailure)
                : ResourceMaterializer.Materialize(entry.Plan, inputs, ref snapshot, ref specialization, out materializationFailure,
                    captureIndirectImageFailure);
            if (!materialized)
            {
                var message = $"The shader resources could not be materialized: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} reason={materializationFailure}.";
                if (materializationFailure is ResourceMaterializationFailure.IncompatibleImageCandidates or ResourceMaterializationFailure.ImageCapacityExceeded)
                    throw new ShaderProgramRejectedException(message);
                throw SubmissionScheduler.Fatal(message);
            }
        }

        if (hasShaderCalls)
        {
            ShaderResourcePlan? failedLinkedPlan = null;
            var captureLinkedImages = CompiledShaderDump.ShouldWrite(source.Address, source.Hash);
            try
            {
                var caller = entry.Program with { Address = source.Address };
                var library = ShaderCallLibrary.Read(caller, entry.Plan.Memory, snapshot, _host.TryReadCleanGuestWord);
                var linkedKey = (key ?? throw new InvalidOperationException("The cached shader has no program key."), library.Identity);
                if (!_linkedPrograms.TryGetValue(linkedKey, out var linked))
                {
                    var program = Gen5ShaderCallLinker.Link(caller, library.Calls);
                    var plan = ShaderResourcePlan.Extract(program, source.Stage, source.Hash, source.UserDataBase,
                        (uint)source.UserData.Length,
                        beforeResourceTracking: captureLinkedImages ? plan => failedLinkedPlan = plan : null,
                        waveSize: source.Stage == ShaderStage.Mesh
                            ? options.MeshInfo!.Geometry.WaveSize : options.ComputeInfo?.WaveSize ?? 32u);
                    failedLinkedPlan = null;
                    linked = new ProgramSourceEntry { Program = program, Plan = plan,
                        HasShaderCalls = program.Instructions.Any(instruction => instruction.Opcode == "SSwappcB64"),
                        HasBitwiseExclusiveOr = program.Instructions.Any(instruction => instruction.Opcode.Contains("Xor", StringComparison.Ordinal)) };
                    _linkedPrograms.Add(linkedKey, linked);
                }
                entry = linked;
                if (!ResourceMaterializer.Materialize(entry.Plan, inputs, ref snapshot, ref specialization))
                    throw new InvalidOperationException("The linked shader resources could not be materialized.");
            }
            catch (Exception exception) when (exception is InvalidOperationException or ResourcePlanException)
            {
                if (failedLinkedPlan is not null) ShaderImageDescriptorDump.Write(source, failedLinkedPlan, inputs);
                if (CompiledShaderDump.ShouldWrite(source.Address, source.Hash))
                    ShaderCallDump.Write(CompiledShaderDump.GetBasePath(source.Label, source.Address, source.Hash),
                        entry.Program, snapshot, entry.Plan.Memory, _host.TryReadCleanGuestWord, exception as ShaderFunctionReadException);
                throw new ShaderProgramRejectedException($"The shader call library cannot be linked: hash=0x{source.Hash:X16} error={exception.Message}");
            }
        }
        _imageDescriptorTrace.Record(source, entry.Plan, snapshot);
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPermutationLookup))
        {
            foreach (var candidate in entry.Permutations)
            {
                var layout = candidate.Bindings;
                if (layout.PushDataStartDword == PushData.StartFor(pushDataCursor, layout.ShaderDataDwordCount) && candidate.Specialization.Equals(specialization))
                {
                    stage = CreateStageResources(candidate.Program, snapshot, source, options);
                    layout.AdvancePushData(ref pushDataCursor);
                    if (RenderTrace.Enabled && RenderTrace.Pipeline())
                    {
                        RenderTrace.Write($"ProgramCache hit stage={source.Label} hash=0x{source.Hash:X16} id=0x{candidate.Handle.Id:X16}");
                    }

                    return candidate.Handle;
                }
            }
        }

        var permutation = CompilePermutation(source, options, entry, specialization, pushDataCursor, key ?? BuildProgramKey(source), sourceWasCached, snapshot);
        entry.Permutations.Add(permutation);
        ShaderCacheCounters.CountPermutation();
        stage = CreateStageResources(permutation.Program, snapshot, source, options);
        permutation.Bindings.AdvancePushData(ref pushDataCursor);
        if (RenderTrace.Enabled && RenderTrace.Pipeline())
        {
            RenderTrace.Write($"ProgramCache insert stage={source.Label} hash=0x{source.Hash:X16} id=0x{permutation.Handle.Id:X16} permutation={entry.Permutations.Count - 1}");
        }

        return permutation.Handle;
    }

    private bool UsesPortableBuffers(ShaderStage stage) => _host.PortableBufferDescriptors && stage != ShaderStage.Mesh;

    // The key of the entry the static state was just built for.
    private ProgramKey BuildProgramKey(ShaderSource source) =>
        new(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize,
            source.ContinuationAddressOffset, _staticState.ToArray());

    private static ShaderStageResources CreateStageResources(
        ShaderProgramInfo program, ResourceSnapshot snapshot, ShaderSource source, StageCompileOptions options)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramResourceAssembly);
        return new(program, snapshot, source.Address)
        {
            ThreadLimits = source.Stage == ShaderStage.Compute && options.ComputeInfo is { DispatchThreadDimensions: true } compute
                ? new DispatchThreadLimits(compute.DispatchThreadsX, compute.DispatchThreadsY, compute.DispatchThreadsZ)
                : null,
        };
    }

    private void BuildStaticState(ShaderStage stage, StageCompileOptions options)
    {
        switch (stage)
        {
            case ShaderStage.Vertex:
                StageStaticKey.Build(options.VertexInfo ?? throw new ArgumentException("The vertex lookup has no vertex input info."), options.RequiredVertexOutputCount, _staticState);
                break;
            case ShaderStage.Pixel:
                StageStaticKey.Build(options.PixelInfo ?? throw new ArgumentException("The pixel lookup has no pixel input info."), _staticState);
                foreach (var output in options.PixelOutputs)
                {
                    _staticState.Add(output.GuestSlot);
                    _staticState.Add(output.HostLocation);
                    _staticState.Add(output.ExportTarget);
                    _staticState.Add(output.Index);
                    _staticState.Add(output.DualSourceAlpha ? 1u : 0u);
                }
                break;
            case ShaderStage.Mesh:
                StageStaticKey.Build(options.MeshInfo ?? throw new ArgumentException("The mesh lookup has no mesh input info."), options.RequiredVertexOutputCount, _staticState);
                _staticState.Add(options.MeshOutputLocationMask.HasValue ? 1u : 0u);
                _staticState.Add(options.MeshOutputLocationMask.GetValueOrDefault());
                break;
            default:
                StageStaticKey.Build(options.ComputeInfo ?? throw new ArgumentException("The compute lookup has no compute input info."), _staticState);
                break;
        }
    }

    private ProgramSourceEntry CreateEntry(ShaderSource source, StageCompileOptions options)
    {
        using var planningScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPlanning);
        var program = Decode(source);
        var dumpPlanning = CompiledShaderDump.ShouldWrite(source.Address, source.Hash);
        if (dumpPlanning) ShaderPlanningDump.WriteInput(source, program);
        var vertexInfo = source.Stage == ShaderStage.Vertex ? options.VertexInfo : null;
        try
        {
            return BuildEntry(
                program,
                source.Stage,
                source.Hash,
                source.UserDataBase,
                (uint)source.UserData.Length,
                vertexInfo is { FetchEmbedded: true } ? (vertexInfo.FetchAttributeRegister, vertexInfo.FetchBufferRegister) : null,
                fetch => BuildVertexInputs(fetch, vertexInfo!, source),
                // Resource planning must use the guest wave size of shader compilation.
                source.Stage switch
                {
                    ShaderStage.Mesh => options.MeshInfo!.Geometry.WaveSize,
                    ShaderStage.Compute => options.ComputeInfo?.WaveSize ?? 64u,
                    _ => 32u,
                },
                dumpPlanning ? resourcePlan => ShaderPlanningDump.WriteGraph(source, resourcePlan) : null);
        }
        catch (ResourcePlanException exception)
        {
            if (dumpPlanning) ShaderPlanningDump.WriteFailure(source, exception.Message);
            throw new ShaderProgramRejectedException($"The shader resource plan is invalid: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={exception.Message}.");
        }
    }

    private static ProgramSourceEntry BuildEntry(
        Gen5ShaderProgram program,
        ShaderStage stage,
        ulong hash,
        uint userDataBase,
        uint userDataCount,
        (int AttributeRegister, int BufferRegister)? fetchRegisters,
        Func<EmbeddedVertexFetchPlan, ShaderVertexInput[]> buildVertexInputs,
        uint waveSize,
        Action<ShaderResourcePlan>? beforeResourceTracking)
    {
        EmbeddedVertexFetchPlan? fetch = null;
        ShaderVertexInput[] vertexInputs = [];
        if (stage == ShaderStage.Vertex && fetchRegisters is { } registers)
        {
            fetch = EmbeddedVertexFetchDetector.Detect(
                program,
                (int)userDataBase + registers.AttributeRegister,
                (int)userDataBase + registers.BufferRegister,
                userDataBase,
                userDataCount,
                waveSize: 32);
            vertexInputs = buildVertexInputs(fetch);
            program = fetch.RemoveReplacedTableLoads(program);
        }

        var plan = ShaderResourcePlan.Extract(program, stage, hash, userDataBase, userDataCount,
            fetch?.Loads.Select(load => load.Pc).ToHashSet(),
            beforeResourceTracking: beforeResourceTracking,
            waveSize: waveSize);

        var exclusiveOr = false;
        foreach (var instruction in program.Instructions)
        {
            exclusiveOr |= instruction.Opcode.Contains("Xor", StringComparison.Ordinal);
        }

        return new ProgramSourceEntry
        {
            Plan = plan,
            Program = program,
            HasBitwiseExclusiveOr = exclusiveOr,
            HasShaderCalls = program.Instructions.Any(instruction => instruction.Opcode == "SSwappcB64"),
            ConstantFill = stage == ShaderStage.Compute ? ConstantFillDetector.Detect(program) : null,
            BoundedFill = stage == ShaderStage.Compute ? BoundedFillDetector.Detect(program) : null,
            BoundedCopy = stage == ShaderStage.Compute ? BoundedFillDetector.DetectCopy(program) : null,
            EmbeddedFetch = fetch,
            VertexInputs = vertexInputs,
            FetchEmbedded = fetchRegisters is not null,
            FetchAttributeRegister = fetchRegisters?.AttributeRegister ?? 0,
            FetchBufferRegister = fetchRegisters?.BufferRegister ?? 0,
        };
    }

    // One fixed-function input per fetched attribute; later loads of the same attribute alias the first.
    private static ShaderVertexInput[] BuildVertexInputs(EmbeddedVertexFetchPlan fetch, VertexInputInfo info, ShaderSource source)
    {
        var inputs = new List<ShaderVertexInput>();
        var inputByLocation = new Dictionary<int, int>();
        foreach (var load in fetch.Loads)
        {
            var location = -1;
            if ((uint)load.AttributeId < (uint)info.Attributes.Length &&
                info.Attributes[load.AttributeId].AttributeId == load.AttributeId)
            {
                location = load.AttributeId;
            }

            if (location < 0)
            {
                for (var index = 0; index < info.Attributes.Length; index++)
                {
                    if (info.Attributes[index].AttributeId == load.AttributeId &&
                        info.Attributes[index].RegisterCount >= load.Components)
                    {
                        location = index;
                        break;
                    }
                }
            }

            if (location < 0)
            {
                for (var index = 0; index < info.Attributes.Length; index++)
                {
                    if (info.Attributes[index].AttributeId == load.AttributeId)
                    {
                        location = index;
                        break;
                    }
                }
            }

            if (location < 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The vertex program fetches an attribute the tables do not declare: hash=0x{source.Hash:X16} shader=0x{source.Address:X16} attribute={load.AttributeId} pc=0x{load.Pc:X}.");
            }

            var attribute = info.Attributes[location];
            var numberFormat = 0u;
            if (Gfx10UnifiedFormat.TryDecode(attribute.Descriptor.Format, out _, out var decodedNumberFormat))
            {
                numberFormat = decodedNumberFormat;
            }

            var requiredComponents = 0u;
            var destinationSelect = attribute.Descriptor.DestinationSelectXYZW;
            for (uint component = 0; component < load.Components; component++)
            {
                var selector = (destinationSelect >> (int)(component * 3)) & 0x7u;
                if (selector is >= 4u and <= 7u)
                {
                    requiredComponents = Math.Max(requiredComponents, selector - 3u);
                    continue;
                }

                if (selector is 0u or 1u)
                {
                    continue;
                }

                throw SubmissionScheduler.Fatal(
                    $"The vertex program uses an unsupported attribute destination selector: hash=0x{source.Hash:X16} shader=0x{source.Address:X16} attribute={load.AttributeId} pc=0x{load.Pc:X} component={component} selector={selector}.");
            }

            if (requiredComponents == 0)
            {
                throw SubmissionScheduler.Fatal(
                    $"The vertex program fetches an attribute without a memory component: hash=0x{source.Hash:X16} shader=0x{source.Address:X16} attribute={load.AttributeId} pc=0x{load.Pc:X}.");
            }

            if (inputByLocation.TryGetValue(location, out var existingIndex))
            {
                var existing = inputs[existingIndex];
                ((List<uint>)existing.AliasPcs).Add(load.Pc);
                inputs[existingIndex] = existing with
                {
                    FetchComponentCount = Math.Max(existing.FetchComponentCount, load.Components),
                    ComponentCount = Math.Max(existing.ComponentCount, requiredComponents),
                };
                continue;
            }

            var aliasPcs = new List<uint>();
            inputByLocation.Add(location, inputs.Count);
            inputs.Add(new ShaderVertexInput(
                load.Pc,
                (uint)location,
                load.Components,
                requiredComponents,
                numberFormat,
                destinationSelect,
                attribute.FetchIndex != 0,
                aliasPcs));
        }

        return inputs.ToArray();
    }

    private ProgramPermutation CompilePermutation(
        ShaderSource source,
        StageCompileOptions options,
        ProgramSourceEntry entry,
        ResourceSpecialization specialization,
        uint pushDataCursor,
        ProgramKey key,
        bool sourceWasCached,
        ResourceSnapshot snapshot)
    {
        var program = entry.Program;
        var plan = entry.Plan;
        SpecializedResourceInfo resources;
        BindingLayout layout;
        try
        {
            using var layoutScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramLayoutRequest);
            resources = ResourceMaterializer.ApplyTo(plan, specialization);
            layout = AllocateLayout(program, plan, resources, source.UserDataBase, (uint)source.UserData.Length, pushDataCursor,
                source.Stage == ShaderStage.Compute && options.ComputeInfo!.DispatchThreadDimensions,
                usesMeshDrawParameters: source.Stage == ShaderStage.Mesh, portableBuffers: UsesPortableBuffers(source.Stage));
        }
        catch (ResourcePlanException exception)
        {
            throw SubmissionScheduler.Fatal($"The shader binding layout is invalid: stage={source.Label} hash=0x{source.Hash:X16} error={exception.Message}.");
        }

        ShaderCompileRequest request;
        var hostFlags = ShaderCompileHostFlags.From(_host);
        var vertexInputs = source.Stage == ShaderStage.Vertex ? VertexInputsOf(options, entry) : null;
        var pixelInputs = source.Stage == ShaderStage.Pixel ? PixelInputsOf(options) : null;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramLayoutRequest))
        {
            request = source.Stage == ShaderStage.Mesh
                ? BuildMeshRequest(options, entry, resources, layout, hostFlags)
                : BuildRequest(source.Stage, entry.Plan, resources, layout, vertexInputs, pixelInputs, options.ComputeInfo,
                    options.ComputeSystemRegisters, hostFlags, _host.ComputeRunsOn32LaneSubgroups);
        }
        if (request.Mesh is { } mesh)
        {
            var limits = _host.MeshLimits;
            var parameterLocations = MeshShaderConfiguration.ParameterLocations(request);
            var parameterCount = parameterLocations.Length;
            var outputLocations = Math.Max(parameterCount + 3, parameterCount == 0 ? 0 : (int)parameterLocations[^1] + 1);
            if ((ulong)outputLocations * 4 > limits.MaxOutputComponents)
                throw new ShaderProgramRejectedException($"The mesh output exceeds the device component limit: " +
                    $"components={outputLocations * 4} limit={limits.MaxOutputComponents}.");
            var outputBytes = mesh.OutputMemoryBytes(parameterCount,
                limits.OutputPerVertexGranularity, limits.OutputPerPrimitiveGranularity);
            if (outputBytes > limits.MaxOutputMemoryBytes || outputBytes > limits.MaxPayloadAndOutputMemoryBytes)
                throw new ShaderProgramRejectedException($"The mesh output exceeds device memory limits: bytes={outputBytes} " +
                    $"output_limit={limits.MaxOutputMemoryBytes} payload_output_limit={limits.MaxPayloadAndOutputMemoryBytes}.");
        }
        var permutationDump = ShaderPermutationDump.WriteInputs(
            source, key, sourceWasCached, _programs.Keys, entry.Permutations,
            specialization, request, pushDataCursor, _nextProgramId + 1);
        IGuestCompiledShader? compiled;
        string error;
        bool translated;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramTranslation))
        {
            translated = _compiler.TryCompileProgram(request, out compiled, out error);
        }
        if (!translated || compiled is null)
        {
            ShaderCallDump.Write(permutationDump, program, snapshot, plan.Memory, _host.TryReadCleanGuestWord);
            throw new ShaderProgramRejectedException($"The shader program cannot be compiled: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.");
        }

        ShaderCacheCounters.CountCompile();
        if (RenderPhaseProfile.Enabled && request.Mesh is not null && compiled.PayloadFileExtension == "spv")
        {
            var payload = compiled.Payload;
            var barriers = 0;
            var loops = 0;
            var elections = 0;
            var atomicOrs = 0;
            var loads = 0;
            var stores = 0;
            var instructions = 0;
            var functionInstructions = 0;
            var beforeFirstLoop = 0;
            var insideFunction = false;
            for (var offset = 20; offset < payload.Length;)
            {
                var instruction = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(payload.AsSpan(offset));
                var wordCount = (int)(instruction >> 16);
                if (wordCount == 0 || wordCount > (payload.Length - offset) / 4) break;
                instructions++;
                switch ((SharpEmu.ShaderCompiler.Vulkan.SpirvOp)(instruction & 0xffff))
                {
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.Function: insideFunction = true; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.FunctionEnd: insideFunction = false; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.ControlBarrier: barriers++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.LoopMerge:
                        if (loops == 0) beforeFirstLoop = functionInstructions;
                        loops++;
                        break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.GroupNonUniformElect: elections++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.AtomicOr: atomicOrs++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.Load: loads++; break;
                    case SharpEmu.ShaderCompiler.Vulkan.SpirvOp.Store: stores++; break;
                }
                if (insideFunction) functionInstructions++;
                offset += wordCount * 4;
            }
            Console.Error.WriteLine($"[PERF][MESH_COMPILE] hash=0x{source.Hash:X16} bytes={payload.Length} " +
                $"static_barriers={barriers} static_loops={loops} static_elections={elections} static_atomic_or={atomicOrs} " +
                $"static_instructions={instructions} static_loads={loads} static_stores={stores} " +
                $"static_before_first_loop={beforeFirstLoop} " +
                $"bound_format_specialization=enabled buffer_formats={string.Join(',', resources.Info.Buffers.Select(buffer => buffer.DescriptorFormat))}");
        }
        if (resources.Info.UsesDeviceAddresses)
        {
            ShaderCacheCounters.CountDeviceAddressProgram();
            if (VideoOut.BufferUploadProfile.Enabled || SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled)
            {
                var accesses = plan.Memory.Entries.Where(memory => !memory.PlanningOnly &&
                    memory.Kind is MemoryResourceKind.ScalarAddress or MemoryResourceKind.Flat or MemoryResourceKind.Global).ToArray();
                var instructions = accesses.Select(memory => $"0x{memory.Pc:X}:{memory.Kind}:{memory.Access}").Distinct().ToArray();
                Console.Error.WriteLine($"[PERF][DEVICE_ADDRESS_PROGRAM] stage={source.Stage} hash=0x{source.Hash:X16} " +
                    $"address=0x{source.Address:X16} accesses={accesses.Length} " +
                    $"bounded_ranges={plan.DeviceAddressRanges.Count(range => range.Bounded && range.Plannable)} " +
                    $"unbounded_ranges={plan.DeviceAddressRanges.Count(range => !range.Bounded || !range.Plannable)} " +
                    $"instructions={string.Join(',', instructions.Take(32))} omitted={Math.Max(0, instructions.Length - 32)}");
            }
        }

        if (!layout.UsesPushData)
        {
            ShaderCacheCounters.CountShaderDataFallback();
        }

        _compiler.CountShaderCompilation();
        ShaderPermutationDump.WriteModule(permutationDump, compiled.Payload, compiled.PayloadFileExtension);
        CompiledShaderDump.Write(source.Label, source.Address, source.Hash, compiled, program);
        var id = ++_nextProgramId;
        var module = _host.CreateShaderModule(compiled, source.Stage, source.Hash, id);
        var info = CreateProgramInfo(source.Stage, source.Hash, source.UserDataBase, (uint)source.UserData.Length,
            entry, resources, layout, request, compiled.InputLocationMask);
        if (_host.ShaderCache is { } shaderCache &&
            source.Stage != ShaderStage.Mesh &&
            source.ContinuationAddressOffset == 0 &&
            !(_programs.TryGetValue(key, out var owner) && owner.HasShaderCalls) &&
            _codeCaptures.TryGetValue((source.Hash, source.CodeSize, source.ContinuationAddressOffset), out var capture))
        {
            var record = new StageRecord
            {
                Stage = source.Stage,
                Hash = source.Hash,
                CodeSize = source.CodeSize,
                Address = capture.Address,
                UserDataBase = source.UserDataBase,
                UserDataCount = (uint)source.UserData.Length,
                PushDataCursor = pushDataCursor,
                Specialization = specialization.Clone(),
                Compute = source.Stage == ShaderStage.Compute ? options.ComputeInfo : null,
                ComputeSystemRegisters = source.Stage == ShaderStage.Compute ? options.ComputeSystemRegisters : null,
                Vertex = vertexInputs,
                Pixel = pixelInputs,
            };
            if (source.Stage == ShaderStage.Compute)
            {
                shaderCache.RecordCompute(record, capture);
            }
            else
            {
                lock (_stageRecords)
                {
                    _stageRecords[id] = (record, capture);
                }
            }
        }

        return new ProgramPermutation
        {
            Specialization = specialization,
            Program = info,
            Handle = new ShaderProgram(id, module),
            Compiled = compiled,
        };
    }

    private static VertexStageInputs VertexInputsOf(StageCompileOptions options, ProgramSourceEntry entry)
    {
        var info = options.VertexInfo!;
        return new VertexStageInputs(
            entry.FetchEmbedded,
            entry.FetchAttributeRegister,
            entry.FetchBufferRegister,
            entry.VertexInputs,
            info.ScratchDwords,
            options.RequiredVertexOutputCount,
            info.PositionExportControl,
            new ShaderClipSpaceTransform(
                info.ClipSpace.Enabled,
                info.ClipSpace.ScaleX,
                info.ClipSpace.ScaleY,
                info.ClipSpace.OffsetX,
                info.ClipSpace.OffsetY,
                info.ClipSpace.HalfExtentX,
                info.ClipSpace.HalfExtentY),
            info.IeeeMode);
    }

    private static PixelStageInputs PixelInputsOf(StageCompileOptions options)
    {
        var info = options.PixelInfo!;
        var interpolators = new uint[info.InputCount];
        Array.Copy(info.InterpolatorSettings, interpolators, interpolators.Length);
        return new PixelStageInputs(
            info.ScratchDwords,
            options.PixelOutputs.ToArray(),
            options.PixelInputEnable,
            info.CustomInterpolationMask,
            options.PixelInputAddress,
            interpolators,
            info.IeeeMode,
            info.DepthExportEnable);
    }

    private static ShaderCompileRequest BuildMeshRequest(
        StageCompileOptions options,
        ProgramSourceEntry entry,
        SpecializedResourceInfo resources,
        BindingLayout layout,
        ShaderCompileHostFlags flags)
    {
        var info = options.MeshInfo!;
        return new ShaderCompileRequest(entry.Plan, resources, layout)
        {
            WaveSize = info.Geometry.WaveSize,
            TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
            IeeeMode = info.Geometry.IeeeMode,
            ScratchDwords = info.Geometry.ScratchDwords,
            EnableGraphicsSubgroupOperations = flags.GraphicsSubgroups,
            RequiredVertexOutputCount = options.RequiredVertexOutputCount,
            PositionExportControl = info.Geometry.PositionExportControl,
            MeshOutputLocationMask = options.MeshOutputLocationMask,
            ClipSpace = new ShaderClipSpaceTransform(
                info.Geometry.ClipSpace.Enabled,
                info.Geometry.ClipSpace.ScaleX,
                info.Geometry.ClipSpace.ScaleY,
                info.Geometry.ClipSpace.OffsetX,
                info.Geometry.ClipSpace.OffsetY,
                info.Geometry.ClipSpace.HalfExtentX,
                info.Geometry.ClipSpace.HalfExtentY),
            LocalSizeX = info.Geometry.ThreadsPerGroup,
            Mesh = new MeshShaderConfiguration(info.Geometry.OutputVertexCapacity, info.Geometry.OutputPrimitiveCapacity,
                info.Geometry.ProvokingVertex, info.Geometry.InputPrimitiveCountPerWorkgroup, info.Geometry.InputVertexCountPerWorkgroup,
                info.Geometry.LocalDataShareDwords, info.Geometry.InputTriangleStrip, info.Execution.DeviceSubgroupLaneCount,
                info.Geometry.InputPointList, info.Geometry.InputLineList),
        };
    }

    private static ShaderCompileRequest BuildRequest(
        ShaderStage stage,
        ShaderResourcePlan plan,
        SpecializedResourceInfo resources,
        BindingLayout layout,
        VertexStageInputs? vertex,
        PixelStageInputs? pixel,
        ComputeInputInfo? compute,
        Gen5ComputeSystemRegisters? systemRegisters,
        ShaderCompileHostFlags flags,
        Func<ComputeInputInfo, bool> computeRunsOn32LaneSubgroups)
    {
        switch (stage)
        {
            case ShaderStage.Vertex:
            {
                var inputs = vertex!;
                return new ShaderCompileRequest(plan, resources, layout)
                {
                    WaveSize = 32,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    IeeeMode = inputs.IeeeMode,
                    ScratchDwords = inputs.ScratchDwords,
                    EnableGraphicsSubgroupOperations = flags.GraphicsSubgroups,
                    SupportsSharedInt64Atomics = flags.SharedInt64Atomics,
                    ShaderSignedZeroInfNanPreserveFloat32Supported = flags.SignedZeroInfNanPreserveFloat32,
                    RequiredVertexOutputCount = inputs.RequiredVertexOutputCount,
                    VertexInputs = inputs.Inputs,
                    PositionExportControl = inputs.PositionExportControl,
                    SupportsClipDistance = flags.ClipDistance,
                    ClipSpace = inputs.ClipSpace,
                };
            }

            case ShaderStage.Pixel:
            {
                var inputs = pixel!;
                return new ShaderCompileRequest(plan, resources, layout)
                {
                    WaveSize = 32,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    IeeeMode = inputs.IeeeMode,
                    ScratchDwords = inputs.ScratchDwords,
                    EnableGraphicsSubgroupOperations = flags.GraphicsSubgroups,
                    SupportsSharedInt64Atomics = flags.SharedInt64Atomics,
                    ShaderSignedZeroInfNanPreserveFloat32Supported = flags.SignedZeroInfNanPreserveFloat32,
                    PixelOutputs = inputs.Outputs,
                    PixelDepthExportEnable = inputs.DepthExportEnable,
                    PixelInputEnable = inputs.InputEnable,
                    PixelCustomInterpolationMask = inputs.CustomInterpolationMask,
                    SupportsPerVertexPixelInputs = flags.PerVertexPixelInputs,
                    PixelInputAddress = inputs.InputAddress,
                    PixelInputCntl = inputs.InputCntl,
                    MipStatisticsSubgroupReduction = flags.MipStatisticsSubgroupReduction,
                };
            }

            default:
                return BuildComputeRequest(plan, resources, layout, compute!, systemRegisters,
                    flags.SharedInt64Atomics, flags.ExecGuardElision, flags.SignedZeroInfNanPreserveFloat32, flags.ComputeWave64SubgroupNative,
                    computeRunsOn32LaneSubgroups(compute!));
        }
    }

    private static BindingLayout AllocateLayout(Gen5ShaderProgram program, ShaderResourcePlan plan, SpecializedResourceInfo resources,
        uint userDataBase, uint userDataCount, uint pushDataCursor, bool usesDispatchThreadLimits, bool usesMeshDrawParameters = false,
        bool portableBuffers = false) =>
        BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, userDataBase, userDataCount, plan.Graph.ExcludedUserDataRegisters),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            pushDataCursor,
            usesDispatchThreadLimits: usesDispatchThreadLimits,
            usesMeshDrawParameters: usesMeshDrawParameters,
            portableBuffers: portableBuffers);

    private static ShaderCompileRequest BuildComputeRequest(ShaderResourcePlan plan, SpecializedResourceInfo resources, BindingLayout layout,
        ComputeInputInfo info, Gen5ComputeSystemRegisters? systemRegisters, bool sharedInt64Atomics, bool execGuardElision,
        bool signedZeroInfNanPreserve, bool hostWave64Supported = false, bool hostComputeSubgroup32 = false) =>
        new(plan, resources, layout)
        {
            WaveSize = info.WaveSize,
            HostWave64Supported = info.WaveSize == 64 && hostWave64Supported,
            HostComputeSubgroup32 = info.WaveSize == 64 && !hostWave64Supported && hostComputeSubgroup32,
            EnableExecGuardElision = info.WaveSize != 64 || execGuardElision,
            IeeeMode = info.IeeeMode,
            TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
            ScratchDwords = info.ScratchDwords,
            SupportsSharedInt64Atomics = sharedInt64Atomics,
            ShaderSignedZeroInfNanPreserveFloat32Supported = signedZeroInfNanPreserve,
            ComputeSystemRegisters = systemRegisters,
            LocalDataShareDwords = info.LocalDataShareDwords,
            LocalSizeX = Math.Max(info.ThreadsX, 1),
            LocalSizeY = Math.Max(info.ThreadsY, 1),
            LocalSizeZ = Math.Max(info.ThreadsZ, 1),
        };

    internal bool TryGetStageRecord(ulong programId, out StageRecord record, out ShaderCodeCapture code)
    {
        lock (_stageRecords)
        {
            if (_stageRecords.TryGetValue(programId, out var stored))
            {
                (record, code) = stored;
                return true;
            }
        }

        record = null!;
        code = null!;
        return false;
    }

    internal static bool TryReplay(
        StageRecord record,
        ShaderCodeCapture code,
        IGuestGpuBackend compiler,
        ShaderCompileHostFlags flags,
        Func<ComputeInputInfo, bool> computeRunsOn32LaneSubgroups,
        out IGuestCompiledShader? compiled,
        out ShaderProgramInfo? info,
        out string error)
    {
        compiled = null;
        info = null;
        try
        {
            if (!Gen5ShaderTranslator.TryDecodeProgram(code.CreateContext(), code.Address, out var program, out error))
            {
                return false;
            }

            var entry = BuildReplayEntry(record, program);
            var resources = ResourceMaterializer.ApplyTo(entry.Plan, record.Specialization ?? ResourceSpecialization.Predicted(entry.Plan.Info));
            var layout = AllocateLayout(entry.Program, entry.Plan, resources, record.UserDataBase, record.UserDataCount, record.PushDataCursor,
                record.Stage == ShaderStage.Compute && record.Compute!.DispatchThreadDimensions, portableBuffers: flags.PortableBuffers);
            var request = BuildRequest(record.Stage, entry.Plan, resources, layout, record.Vertex, record.Pixel, record.Compute,
                record.ComputeSystemRegisters, flags, computeRunsOn32LaneSubgroups);
            if (!compiler.TryCompileProgram(request, out compiled, out error) || compiled is null)
            {
                return false;
            }

            info = CreateProgramInfo(record.Stage, record.Hash, record.UserDataBase, record.UserDataCount, entry, resources, layout, request,
                compiled.InputLocationMask);
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    internal static ResourceSpecialization? EffectiveSpecialization(StageRecord record, ShaderCodeCapture code)
    {
        if (record.Specialization is { } specialization)
        {
            return specialization;
        }

        try
        {
            if (!Gen5ShaderTranslator.TryDecodeProgram(code.CreateContext(), code.Address, out var program, out _))
            {
                return null;
            }

            return ResourceSpecialization.Predicted(BuildReplayEntry(record, program).Plan.Info);
        }
        catch (Exception exception) when (exception is ResourcePlanException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private static ProgramSourceEntry BuildReplayEntry(StageRecord record, Gen5ShaderProgram program)
    {
        var vertex = record.Vertex;
        return BuildEntry(
            program,
            record.Stage,
            record.Hash,
            record.UserDataBase,
            record.UserDataCount,
            vertex is { FetchEmbedded: true } ? (vertex.FetchAttributeRegister, vertex.FetchBufferRegister) : null,
            _ => vertex!.Inputs.ToArray(),
            record.Stage == ShaderStage.Compute ? record.Compute?.WaveSize ?? 64u : 32u,
            null);
    }

    // A fill kernel computes its thread index, moves one constant into a VGPR and stores it once.
    internal static uint? FindConstantStoreValue(Gen5ShaderProgram program)
    {
        var constants = new Dictionary<uint, uint>();
        uint? stored = null;
        foreach (var instruction in program.Instructions)
        {
            switch (instruction.Opcode)
            {
                case "SEndpgm" or "SWaitcnt" or "SNop" or "SInstPrefetch":
                    continue;
                case "VLshlAddU32" or "VMadU32U24" or "VAddU32" or "VAddI32" or "VAddNcU32" or "VLshlrevB32":
                    // Index arithmetic; a register it writes is no longer a known constant.
                    foreach (var destination in instruction.Destinations)
                    {
                        if (destination.Kind == Gen5OperandKind.VectorRegister) constants.Remove(destination.Value);
                    }

                    continue;
                case "VMovB32":
                {
                    if (instruction.Destinations.Count != 1 || instruction.Destinations[0].Kind != Gen5OperandKind.VectorRegister ||
                        instruction.Sources.Count != 1 || !TryDecodeIntegerConstant(instruction.Sources[0], out var value))
                    {
                        return null;
                    }

                    constants[instruction.Destinations[0].Value] = value;
                    continue;
                }

                case "BufferStoreFormatX" or "BufferStoreDword":
                {
                    if (stored.HasValue || instruction.Control is not Gen5BufferMemoryControl control ||
                        !constants.TryGetValue(control.VectorData, out var value))
                    {
                        return null;
                    }

                    stored = value;
                    continue;
                }

                default:
                    return null;
            }
        }

        return stored;
    }

    private static bool TryDecodeIntegerConstant(Gen5Operand operand, out uint value)
    {
        value = 0;
        if (operand.Kind == Gen5OperandKind.LiteralConstant)
        {
            value = operand.Value;
            return true;
        }

        if (operand.Kind != Gen5OperandKind.EncodedConstant)
        {
            return false;
        }

        // Inline integers: 128..192 are 0..64, 193..208 are -1..-16.
        if (operand.Value is >= 128 and <= 192)
        {
            value = operand.Value - 128;
            return true;
        }

        if (operand.Value is >= 193 and <= 208)
        {
            value = unchecked((uint)(192 - (int)operand.Value));
            return true;
        }

        return false;
    }

    private static ShaderProgramInfo CreateProgramInfo(
        ShaderStage stage,
        ulong hash,
        uint userDataBase,
        uint userDataCount,
        ProgramSourceEntry entry,
        SpecializedResourceInfo resources,
        BindingLayout layout,
        ShaderCompileRequest request, uint? inputLocationMask)
    {
        var info = resources.Info;
        var buffers = new BufferResourceInfo[info.Buffers.Count];
        for (var index = 0; index < buffers.Length; index++)
        {
            var buffer = info.Buffers[index];
            buffers[index] = new BufferResourceInfo(buffer.Read, buffer.Written, buffer.Atomic, buffer.Formatted, buffer.Scalar, buffer.MaxByteExtent, buffer.PackedStride);
        }

        var images = new ImageResourceInfo[info.Images.Count];
        for (var index = 0; index < images.Length; index++)
        {
            var image = info.Images[index];
            var resourceClass = image.ResourceClass switch
            {
                ShaderCompiler.Resources.ImageResourceClass.Sampled => ImageResourceClass.Sampled,
                ShaderCompiler.Resources.ImageResourceClass.Storage => ImageResourceClass.Storage,
                _ => ImageResourceClass.None,
            };
            images[index] = new ImageResourceInfo(resourceClass, image.Written);
        }

        var fetchComponents = new byte[VertexInputInfo.MaxBuffers];
        foreach (var input in entry.VertexInputs)
        {
            if (input.Location < (uint)fetchComponents.Length)
            {
                fetchComponents[input.Location] = (byte)input.ComponentCount;
            }
        }

        return new ShaderProgramInfo
        {
            Stage = stage switch
            {
                ShaderStage.Vertex => ShaderStageKind.Vertex,
                ShaderStage.Pixel => ShaderStageKind.Pixel,
                ShaderStage.Mesh => ShaderStageKind.Mesh,
                _ => ShaderStageKind.Compute,
            },
            Hash = hash,
            UserDataBase = userDataBase,
            UserDataCount = userDataCount,
            ParameterExportMask = entry.Program.ParameterExportMask,
            InputLocationMask = inputLocationMask,
            PixelColorExportMasks = entry.Program.PixelColorExportMasks,
            VertexOffsetScalarRegister = entry.EmbeddedFetch?.VertexOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            InstanceOffsetScalarRegister = entry.EmbeddedFetch?.InstanceOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            UsesDeviceAddresses = info.UsesDeviceAddresses,
            ConstantStoreValue = FindConstantStoreValue(entry.Program),
            HasBitwiseExclusiveOr = entry.HasBitwiseExclusiveOr,
            ConstantFill = entry.ConstantFill,
            BoundedFill = entry.BoundedFill,
            BoundedCopy = entry.BoundedCopy,
            Buffers = buffers,
            Images = images,
            SamplerCount = info.Samplers.Count,
            WaveSize = request.WaveSize,
            ScratchDwords = request.ScratchDwords,
            VertexFetchComponents = fetchComponents,
            Resources = resources,
            Bindings = layout,
        };
    }
}

// Writes each compiled module and its decoded listing when the dump switch is on.
internal static class CompiledShaderDump
{
    internal static bool ShouldWrite(ulong shaderAddress, ulong? shaderHash)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV"), "1", StringComparison.Ordinal))
        {
            return false;
        }

        var addressFilter = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV_ADDRESS");
        if (!string.IsNullOrWhiteSpace(addressFilter))
        {
            var span = addressFilter.AsSpan();
            if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                span = span[2..];
            }

            if (!ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var filteredAddress) ||
                shaderAddress != filteredAddress)
            {
                return false;
            }
        }

        var hashFilter = Environment.GetEnvironmentVariable("SHARPEMU_DUMP_SPIRV_HASH");
        if (!string.IsNullOrWhiteSpace(hashFilter))
        {
            foreach (var filter in hashFilter.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var span = filter.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var filteredHash) &&
                    shaderHash == filteredHash)
                {
                    return true;
                }
            }

            return false;
        }

        return true;
    }

    internal static string GetBasePath(string stage, ulong shaderAddress, ulong hash)
    {
        var directory = Environment.GetEnvironmentVariable("SHARPEMU_SHADER_SPIRV_DUMP_DIR");
        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.Combine(AppContext.BaseDirectory, "shader-dumps");
        }

        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{shaderAddress:X16}-{hash:X16}.{stage}");
    }

    public static void Write(string stage, ulong shaderAddress, ulong hash, IGuestCompiledShader shader, Gen5ShaderProgram program)
    {
        if (shader.Payload.Length == 0 || !ShouldWrite(shaderAddress, hash)) return;
        var basePath = GetBasePath(stage, shaderAddress, hash);
        File.WriteAllBytes($"{basePath}.{shader.PayloadFileExtension}", shader.Payload);
        var lines = new List<string>(program.Instructions.Count + 2)
        {
            $"address=0x{program.Address:X16}",
            "pc words opcode destinations <- sources control",
        };
        foreach (var instruction in program.Instructions)
        {
            lines.Add(
                $"0x{instruction.Pc:X4} {string.Join('_', instruction.Words.Select(static word => $"{word:X8}"))} {instruction.Opcode} " +
                $"{string.Join(',', instruction.Destinations)} <- {string.Join(',', instruction.Sources)} {instruction.Control}");
        }

        File.WriteAllLines($"{basePath}.ir.txt", lines);
    }
}
