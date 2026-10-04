// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using System.Runtime.InteropServices;
using ImageResourceClass = SharpEmu.Libs.Gpu.Rendering.ImageResourceClass;
using ResourceSnapshot = SharpEmu.ShaderCompiler.Resources.ResourceSnapshot;

namespace SharpEmu.Libs.Gpu.Pipelines;

// One program as a draw names it: the registered code, its identity and the user data of the draw.
public sealed record ShaderSource(RegisteredShader Registered, ulong Hash, uint[] UserData, uint UserDataBase, ShaderStage Stage)
{
    public ulong Address => Registered.CodeAddress;

    public uint CodeSize => Registered.TotalCodeSizeBytes;

    // SPIR-V/MSL adds this offset to the per-draw entry base for S_GETPC in a
    // fused continuation. Identical words at a different relative layout are
    // therefore a different compiled program, while relocated equal layouts
    // remain shareable.
    public ulong FusedLayout => Registered.IsFused
        ? unchecked(Registered.ContinuationAddress - Registered.CodeAddress)
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
    public PixelInputInfo? PixelInfo { get; init; }
    public IReadOnlyList<Gen5PixelOutputBinding> PixelOutputs { get; init; } = [];
    public uint PixelInputEnable { get; init; }
    public uint PixelInputAddress { get; init; }
    public ComputeInputInfo? ComputeInfo { get; init; }
    public Gen5ComputeSystemRegisters? ComputeSystemRegisters { get; init; }
}

// The key of a program entry: what the emitter reads besides the resource specialization.
public sealed class ProgramKey : IEquatable<ProgramKey>
{
    public ProgramKey(
        ShaderStage stage,
        ulong hash,
        uint userDataCount,
        uint codeSize,
        ulong fusedLayout,
        uint[] staticState)
        : this(stage, hash, userDataCount, codeSize, fusedLayout, staticState.ToArray(), staticState.Length)
    {
    }

    private ProgramKey(
        ShaderStage stage,
        ulong hash,
        uint userDataCount,
        uint codeSize,
        ulong fusedLayout,
        uint[] staticState,
        int staticStateLength)
    {
        Stage = stage;
        Hash = hash;
        UserDataCount = userDataCount;
        CodeSize = codeSize;
        FusedLayout = fusedLayout;
        _staticState = staticState;
        _staticStateLength = staticStateLength;
    }

    private uint[] _staticState;
    private int _staticStateLength;

    public ShaderStage Stage { get; private set; }
    public ulong Hash { get; private set; }
    public uint UserDataCount { get; private set; }
    public uint CodeSize { get; private set; }
    public ulong FusedLayout { get; private set; }
    // Diagnostic serialization is off the hot path. Return only logical words so a reusable
    // lookup key never exposes stale capacity beyond its current state.
    public uint[] StaticState => StaticStateSpan.ToArray();

    private ReadOnlySpan<uint> StaticStateSpan => _staticState.AsSpan(0, _staticStateLength);

    internal static ProgramKey CreateLookup(int staticStateCapacity) =>
        new(default, 0, 0, 0, 0, new uint[staticStateCapacity], 0);

    internal void ResetLookup(
        ShaderStage stage,
        ulong hash,
        uint userDataCount,
        uint codeSize,
        ulong fusedLayout,
        ReadOnlySpan<uint> staticState)
    {
        if (_staticState.Length < staticState.Length)
        {
            Array.Resize(ref _staticState, staticState.Length);
        }

        staticState.CopyTo(_staticState);
        _staticStateLength = staticState.Length;
        Stage = stage;
        Hash = hash;
        UserDataCount = userDataCount;
        CodeSize = codeSize;
        FusedLayout = fusedLayout;
    }

    // A reusable lookup key must never be inserted into the dictionary: its next ResetLookup
    // would invalidate the bucket. Freeze only a miss, copying exactly the logical state.
    internal ProgramKey CloneImmutable() =>
        new(Stage, Hash, UserDataCount, CodeSize, FusedLayout, StaticStateSpan.ToArray(), _staticStateLength);

    public bool Equals(ProgramKey? other) =>
        other is not null && Stage == other.Stage && Hash == other.Hash && UserDataCount == other.UserDataCount &&
        CodeSize == other.CodeSize && FusedLayout == other.FusedLayout &&
        _staticStateLength == other._staticStateLength &&
        StaticStateSpan.SequenceEqual(other.StaticStateSpan);

    public override bool Equals(object? obj) => Equals(obj as ProgramKey);

    // Same-shape variants share a bucket; equality does the one exact comparison of the state words.
    public override int GetHashCode() =>
        HashCode.Combine(Stage, Hash, UserDataCount, CodeSize, FusedLayout, _staticStateLength);
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
    public ConstantFill? ConstantFill { get; init; }
    public BoundedFill? BoundedFill { get; init; }
    public BoundedCopy? BoundedCopy { get; init; }
    public EmbeddedVertexFetchPlan? EmbeddedFetch { get; init; }
    public ShaderVertexInput[] VertexInputs { get; init; } = [];
    public List<ProgramPermutation> Permutations { get; } = new(8);
}

// Programs keyed by identity and static state; each draw materializes its resources and reuses
// the permutation whose specialization and push-data start match, else compiles one more.
internal sealed class ShaderProgramCache
{
    private const uint MaxInstructionScan = 16384;

    private readonly CpuContext _context;
    private readonly IGuestGpuBackend _compiler;
    private readonly IShaderPipelineHost _host;
    private readonly Dictionary<ProgramKey, ProgramSourceEntry> _programs = new();
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong FusedLayout), Gen5ShaderProgram> _decoded = new();
    private readonly Dictionary<(ulong Hash, uint CodeSize, ulong FusedLayout), ShaderCodeCapture> _codeCaptures = new();
    private readonly List<uint> _staticState = new(StageStaticKey.MaxWords);
    private readonly ProgramKey _lookupKey = ProgramKey.CreateLookup(StageStaticKey.MaxWords);
    // Pixel resources must remain intact while the vertex stage is materialized, so each
    // stage owns distinct transient specialization storage. Cache misses clone before the
    // specialization escapes into ProgramPermutation.
    private readonly ResourceSpecializationScratch _vertexSpecializationScratch = new();
    private readonly ResourceSpecializationScratch _pixelSpecializationScratch = new();
    private readonly ResourceSpecializationScratch _computeSpecializationScratch = new();
    private readonly ResourceSpecializationScratch _meshSpecializationScratch = new();

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

    public IEnumerable<ProgramSourceEntry> Entries => _programs.Values;

    // The decoded instructions of a program, shared by every static variant of the same code.
    public Gen5ShaderProgram Decode(ShaderSource source)
    {
        var key = (source.Hash, source.CodeSize, source.FusedLayout);
        if (_decoded.TryGetValue(key, out var program))
        {
            return program;
        }

        var recording = _host.ShaderPrewarm is not null ? new RecordingCpuMemory(_context.Memory) : null;
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
        ProgramKey key;
        ProgramSourceEntry? entry;
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramCacheLookup))
        {
            BuildStaticState(source.Stage, options);
            _lookupKey.ResetLookup(
                source.Stage,
                source.Hash,
                (uint)source.UserData.Length,
                source.CodeSize,
                source.FusedLayout,
                CollectionsMarshal.AsSpan(_staticState));
            key = _lookupKey;
            _programs.TryGetValue(key, out entry);
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
            ReadResidentMemory = _prefetchResidentGuestBytes,
            ComputeState = source.Stage == ShaderStage.Compute && options.ComputeInfo is { } computeState
                ? new ComputeSelectorState(computeState.WaveSize, Math.Max(computeState.ThreadsX, 1),
                    Math.Max(computeState.ThreadsY, 1), Math.Max(computeState.ThreadsZ, 1), computeState.DispatchThreadDimensions,
                    computeState.LocalDataShareDwords, computeState.ThreadIdCount)
                : null,
        };
        if (entry is null)
        {
            entry = CreateEntry(source, options);
            key = key.CloneImmutable();
            _programs.Add(key, entry);
            ShaderCacheCounters.CountProgram();
        }

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
            var scratch = SpecializationScratch(source.Stage);
            var materialized = _materializations is not null && captureIndirectImageFailure is null
                ? _materializations.Materialize(
                    entry.Plan,
                    inputs,
                    _readResidentGuestBytes,
                    scratch,
                    ref snapshot,
                    ref specialization,
                    out var materializationFailure,
                    out var materializationFailureDetail)
                : ResourceMaterializer.Materialize(
                    entry.Plan,
                    inputs,
                    scratch,
                    ref snapshot,
                    ref specialization,
                    out materializationFailure,
                    out materializationFailureDetail,
                    captureIndirectImageFailure);
            if (!materialized)
            {
                var detail = string.IsNullOrWhiteSpace(materializationFailureDetail)
                    ? string.Empty
                    : $" detail=[{materializationFailureDetail}]";
                var message = $"The shader resources could not be materialized: stage={source.Label} " +
                    $"hash=0x{source.Hash:X16} shader=0x{source.Address:X16} " +
                    $"reason={materializationFailure}{detail}.";
                if (materializationFailure is ResourceMaterializationFailure.IncompatibleImageCandidates or ResourceMaterializationFailure.ImageCapacityExceeded)
                    throw new ShaderProgramRejectedException(message);
                throw SubmissionScheduler.Fatal(message);
            }
        }

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

        // The materializer's specialization is stage-local scratch. Only a cache miss makes
        // it persistent, so clone exactly that escaping value and keep hit probes allocation-free.
        var permutation = CompilePermutation(
            source,
            options,
            entry,
            specialization.Clone(),
            pushDataCursor,
            key,
            sourceWasCached);
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

    private ResourceSpecializationScratch SpecializationScratch(ShaderStage stage) => stage switch
    {
        ShaderStage.Vertex => _vertexSpecializationScratch,
        ShaderStage.Pixel => _pixelSpecializationScratch,
        ShaderStage.Compute => _computeSpecializationScratch,
        ShaderStage.Mesh => _meshSpecializationScratch,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "The shader stage has no resource scratch storage."),
    };

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
            case ShaderStage.Mesh:
                StageStaticKey.Build(options.VertexInfo ?? throw new ArgumentException("The vertex lookup has no vertex input info."), options.RequiredVertexOutputCount, _staticState);
                break;
            case ShaderStage.Pixel:
                StageStaticKey.Build(
                    options.PixelInfo ?? throw new ArgumentException("The pixel lookup has no pixel input info."),
                    options.PixelOutputs,
                    _staticState);
                break;
            default:
                StageStaticKey.Build(options.ComputeInfo ?? throw new ArgumentException("The compute lookup has no compute input info."), _staticState);
                break;
        }
    }

    private ProgramSourceEntry CreateEntry(ShaderSource source, StageCompileOptions options)
    {
        var program = Decode(source);
        var dumpPlanning = CompiledShaderDump.ShouldWrite(source.Address, source.Hash);
        if (dumpPlanning) ShaderPlanningDump.WriteInput(source, program);
        EmbeddedVertexFetchPlan? fetch = null;
        ShaderVertexInput[] vertexInputs = [];
        if (source.Stage == ShaderStage.Vertex && options.VertexInfo is { FetchEmbedded: true } vertexInfo)
        {
            fetch = EmbeddedVertexFetchDetector.Detect(
                program,
                (int)source.UserDataBase + vertexInfo.FetchAttributeRegister,
                (int)source.UserDataBase + vertexInfo.FetchBufferRegister,
                source.UserDataBase,
                (uint)source.UserData.Length,
                waveSize: vertexInfo.WaveSize);
            vertexInputs = BuildVertexInputs(fetch, vertexInfo, source);
            program = fetch.RemoveReplacedTableLoads(program);
        }

        var waveSize = source.Stage switch
        {
            ShaderStage.Vertex => options.VertexInfo?.WaveSize ??
                throw new ArgumentException("The vertex lookup has no vertex input info."),
            ShaderStage.Pixel => options.PixelInfo?.WaveSize ??
                throw new ArgumentException("The pixel lookup has no pixel input info."),
            ShaderStage.Mesh => options.VertexInfo?.Mesh.WaveSize ??
                throw new ArgumentException("The mesh lookup has no vertex input info."),
            _ => options.ComputeInfo?.WaveSize ??
                throw new ArgumentException("The compute lookup has no compute input info."),
        };

        ShaderResourcePlan plan;
        try
        {
            plan = ShaderResourcePlan.Extract(program, source.Stage, source.Hash, source.UserDataBase, (uint)source.UserData.Length,
                fetch?.Loads.Select(load => load.Pc).ToHashSet(),
                beforeResourceTracking: dumpPlanning ? resourcePlan => ShaderPlanningDump.WriteGraph(source, resourcePlan) : null,
                waveSize: waveSize);
        }
        catch (ResourcePlanException exception)
        {
            if (dumpPlanning) ShaderPlanningDump.WriteFailure(source, exception.Message);
            throw new ShaderProgramRejectedException($"The shader resource plan is invalid: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={exception.Message}.");
        }

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
            ConstantFill = source.Stage == ShaderStage.Compute ? ConstantFillDetector.Detect(program) : null,
            BoundedFill = source.Stage == ShaderStage.Compute ? BoundedFillDetector.Detect(program) : null,
            BoundedCopy = source.Stage == ShaderStage.Compute ? BoundedFillDetector.DetectCopy(program) : null,
            EmbeddedFetch = fetch,
            VertexInputs = vertexInputs,
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
            var descriptor = attribute.Descriptor;
            var numberFormat = 0u;
            var formatComponents = 0u;
            if (Gfx10UnifiedFormat.TryDecode(descriptor.Format, out _, out var decodedNumberFormat))
            {
                numberFormat = decodedNumberFormat;
            }

            var requiredComponents = 0u;
            var destinationSelect = descriptor.DestinationSelectXYZW;
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

            if (VertexAttributeFormats.TryResolve(in descriptor, 4, out _, out var resolvedComponents))
            {
                formatComponents = resolvedComponents;
            }

            if (inputByLocation.TryGetValue(location, out var existingIndex))
            {
                var existing = inputs[existingIndex];
                ((List<uint>)existing.AliasPcs).Add(load.Pc);
                inputs[existingIndex] = existing with
                {
                    FetchComponentCount = Math.Max(existing.FetchComponentCount, load.Components),
                    ComponentCount = Math.Max(existing.ComponentCount, requiredComponents),
                    FormatComponentCount = Math.Max(existing.FormatComponentCount, formatComponents),
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
                aliasPcs,
                formatComponents));
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
        bool sourceWasCached)
    {
        var program = entry.Program;
        var plan = entry.Plan;
        SpecializedResourceInfo resources;
        BindingLayout layout;
        try
        {
            resources = ResourceMaterializer.ApplyTo(plan, specialization);
            if (source.Stage == ShaderStage.Mesh)
            {
                // The mesh entry ABI performs the original guest-index fetch through BDA,
                // even when the decoded guest instructions have no other address accesses.
                resources.Info.UsesDeviceAddresses = true;
            }
            layout = AllocateLayout(program, plan, resources, source.UserDataBase, (uint)source.UserData.Length, pushDataCursor,
                source.Stage == ShaderStage.Compute && options.ComputeInfo!.DispatchThreadDimensions);
        }
        catch (ResourcePlanException exception)
        {
            throw SubmissionScheduler.Fatal($"The shader binding layout is invalid: stage={source.Label} hash=0x{source.Hash:X16} error={exception.Message}.");
        }

        var request = BuildRequest(source, options, entry, resources, layout);
        var permutationDump = ShaderPermutationDump.WriteInputs(
            source, key, sourceWasCached, _programs.Keys, entry.Permutations,
            specialization, request, pushDataCursor, _nextProgramId + 1);
        if (!_compiler.TryCompileProgram(request, out var compiled, out var error) || compiled is null)
        {
            throw new ShaderProgramRejectedException($"The shader program cannot be compiled: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.");
        }

        ShaderCacheCounters.CountCompile();
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
        var info = CreateProgramInfo(source, entry, resources, layout, request);
        if (source.Stage == ShaderStage.Compute &&
            _host.ShaderPrewarm is { } prewarm &&
            _codeCaptures.TryGetValue((source.Hash, source.CodeSize, source.FusedLayout), out var capture))
        {
            prewarm.RecordCompute(capture, new ComputePrewarmRecord
            {
                Hash = source.Hash,
                CodeSize = source.CodeSize,
                Address = capture.Address,
                UserDataBase = source.UserDataBase,
                UserDataCount = (uint)source.UserData.Length,
                PushDataCursor = pushDataCursor,
                Info = options.ComputeInfo!,
                SystemRegisters = options.ComputeSystemRegisters,
                Specialization = specialization.Clone(),
            });
        }

        return new ProgramPermutation
        {
            Specialization = specialization,
            Program = info,
            Handle = new ShaderProgram(id, module),
            Compiled = compiled,
        };
    }

    private ShaderCompileRequest BuildRequest(
        ShaderSource source,
        StageCompileOptions options,
        ProgramSourceEntry entry,
        SpecializedResourceInfo resources,
        BindingLayout layout)
    {
        var enableGraphicsSubgroups = _host.GraphicsSubgroupOperationsEnabled;
        var bufferInt64AtomicsSupported = _host.BufferInt64AtomicsSupported;
        var shaderFloat64Supported = _host.ShaderFloat64Supported;
        var shaderSignedZeroInfNanPreserveFloat32Supported =
            _host.ShaderSignedZeroInfNanPreserveFloat32Supported;
        var shaderDeviceClockSupported = _host.ShaderDeviceClockSupported;
        var shaderDeviceClockShift = _host.ShaderDeviceClockShift;
        var sharedInt64Atomics = _host.SharedInt64AtomicsEnabled;
        switch (source.Stage)
        {
            case ShaderStage.Vertex:
            {
                var info = options.VertexInfo!;
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = info.WaveSize,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    BufferInt64AtomicsSupported = bufferInt64AtomicsSupported,
                    ShaderFloat64Supported = shaderFloat64Supported,
                    ShaderSignedZeroInfNanPreserveFloat32Supported =
                        shaderSignedZeroInfNanPreserveFloat32Supported,
                    ShaderDeviceClockSupported = shaderDeviceClockSupported,
                    ShaderDeviceClockShift = shaderDeviceClockShift,
                    SupportsSharedInt64Atomics = sharedInt64Atomics,
                    RequiredVertexOutputCount = options.RequiredVertexOutputCount,
                    VertexInputs = entry.VertexInputs,
                    PositionExportControl = info.PositionExportControl,
                    ClipSpace = new ShaderClipSpaceTransform(
                        info.ClipSpace.Enabled,
                        info.ClipSpace.ScaleX,
                        info.ClipSpace.ScaleY,
                        info.ClipSpace.OffsetX,
                        info.ClipSpace.OffsetY,
                        info.ClipSpace.HalfExtentX,
                        info.ClipSpace.HalfExtentY),
                };
            }

            case ShaderStage.Mesh:
            {
                var info = options.VertexInfo!;
                var mesh = info.Mesh;
                if (!mesh.IsActive)
                {
                    throw SubmissionScheduler.Fatal(
                        $"The mesh compile request has no workgroup shape: shader=0x{source.Address:X16}.");
                }

                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = mesh.WaveSize,
                    HostSubgroupSize = mesh.HostSubgroupSize,
                    TraceMeshOutputs = MeshOutputProbe.Matches(source.Hash),
                    ScratchDwords = mesh.ScratchDwords,
                    LocalDataShareDwords = mesh.LocalDataShareDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    BufferInt64AtomicsSupported = bufferInt64AtomicsSupported,
                    ShaderFloat64Supported = shaderFloat64Supported,
                    ShaderSignedZeroInfNanPreserveFloat32Supported =
                        shaderSignedZeroInfNanPreserveFloat32Supported,
                    ShaderDeviceClockSupported = shaderDeviceClockSupported,
                    ShaderDeviceClockShift = shaderDeviceClockShift,
                    RequiredVertexOutputCount = options.RequiredVertexOutputCount,
                    PositionExportControl = info.PositionExportControl,
                    ClipSpace = new ShaderClipSpaceTransform(
                        info.ClipSpace.Enabled,
                        info.ClipSpace.ScaleX,
                        info.ClipSpace.ScaleY,
                        info.ClipSpace.OffsetX,
                        info.ClipSpace.OffsetY,
                        info.ClipSpace.HalfExtentX,
                        info.ClipSpace.HalfExtentY),
                    LocalSizeX = mesh.ThreadsX,
                    LocalSizeY = mesh.ThreadsY,
                    LocalSizeZ = mesh.ThreadsZ,
                    Mesh = new ShaderMeshInfo(
                        (uint)mesh.InputPrimitive,
                        mesh.PrimitivesPerGroup,
                        mesh.VerticesPerGroup,
                        mesh.MaxVertices,
                        mesh.MaxPrimitives,
                        mesh.OutputPrimitive,
                        mesh.ProvokingVertex),
                };
            }

            case ShaderStage.Pixel:
            {
                var info = options.PixelInfo!;
                var interpolators = new uint[info.InputCount];
                Array.Copy(info.InterpolatorSettings, interpolators, interpolators.Length);
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = info.WaveSize,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    BufferInt64AtomicsSupported = bufferInt64AtomicsSupported,
                    ShaderFloat64Supported = shaderFloat64Supported,
                    ShaderSignedZeroInfNanPreserveFloat32Supported =
                        shaderSignedZeroInfNanPreserveFloat32Supported,
                    ShaderDeviceClockSupported = shaderDeviceClockSupported,
                    ShaderDeviceClockShift = shaderDeviceClockShift,
                    SupportsSharedInt64Atomics = sharedInt64Atomics,
                    PixelOutputs = options.PixelOutputs,
                    PixelInputEnable = options.PixelInputEnable,
                    PixelCustomInterpolationMask = info.CustomInterpolationMask,
                    SupportsPerVertexPixelInputs = _host.PerVertexPixelInputsSupported,
                    PixelInputAddress = options.PixelInputAddress,
                    PixelInputCntl = interpolators,
                    PixelEarlyDepth = info.EarlyDepth,
                };
            }

            default:
                return BuildComputeRequest(entry.Plan, resources, layout, options.ComputeInfo!, options.ComputeSystemRegisters,
                    sharedInt64Atomics, _host.ExecGuardElisionEnabled,
                    bufferInt64AtomicsSupported,
                    shaderFloat64Supported,
                    shaderSignedZeroInfNanPreserveFloat32Supported,
                    shaderDeviceClockSupported,
                    shaderDeviceClockShift);
        }
    }

    private static BindingLayout AllocateLayout(Gen5ShaderProgram program, ShaderResourcePlan plan, SpecializedResourceInfo resources,
        uint userDataBase, uint userDataCount, uint pushDataCursor, bool usesDispatchThreadLimits) =>
        BindingLayout.Allocate(
            resources.Info,
            BindingLayout.CollectUserDataRegisters(program, userDataBase, userDataCount, plan.Graph.WaveSize),
            BindingLayout.UsesGlobalDataShare(program),
            ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
            BindingLayout.ReadsShaderBase(program),
            pushDataCursor,
            usesDispatchThreadLimits: usesDispatchThreadLimits);

    private static ShaderCompileRequest BuildComputeRequest(ShaderResourcePlan plan, SpecializedResourceInfo resources, BindingLayout layout,
        ComputeInputInfo info, Gen5ComputeSystemRegisters? systemRegisters, bool sharedInt64Atomics, bool execGuardElision,
        bool bufferInt64AtomicsSupported = false,
        bool shaderFloat64Supported = false,
        bool shaderSignedZeroInfNanPreserveFloat32Supported = false,
        bool shaderDeviceClockSupported = false,
        uint shaderDeviceClockShift = 0,
        uint? hostSubgroupSize = null) =>
        new(plan, resources, layout)
        {
            WaveSize = info.WaveSize,
            EnableExecGuardElision = info.WaveSize != 64 || execGuardElision,
            TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
            HostSubgroupSize = hostSubgroupSize ?? info.HostSubgroupSize,
            ScratchDwords = info.ScratchDwords,
            BufferInt64AtomicsSupported = bufferInt64AtomicsSupported,
            ShaderFloat64Supported = shaderFloat64Supported,
            ShaderSignedZeroInfNanPreserveFloat32Supported = shaderSignedZeroInfNanPreserveFloat32Supported,
            ShaderDeviceClockSupported = shaderDeviceClockSupported,
            ShaderDeviceClockShift = shaderDeviceClockShift,
            SupportsSharedInt64Atomics = sharedInt64Atomics,
            ComputeSystemRegisters = systemRegisters,
            LocalDataShareDwords = info.LocalDataShareDwords,
            LocalSizeX = Math.Max(info.ThreadsX, 1),
            LocalSizeY = Math.Max(info.ThreadsY, 1),
            LocalSizeZ = Math.Max(info.ThreadsZ, 1),
        };

    internal static bool TryCompilePrewarm(ComputePrewarmRecord record, ShaderCodeCapture code, IGuestGpuBackend compiler,
        IShaderPipelineHost host, out IGuestCompiledShader? compiled, out BindingLayout? layout, out string error)
    {
        compiled = null;
        layout = null;
        try
        {
            if (!Gen5ShaderTranslator.TryDecodeProgram(code.CreateContext(), code.Address, out var program, out error))
            {
                return false;
            }

            var plan = ShaderResourcePlan.Extract(program, ShaderStage.Compute, record.Hash, record.UserDataBase, record.UserDataCount,
                waveSize: record.Info.WaveSize);
            var resources = ResourceMaterializer.ApplyTo(plan, record.Specialization);
            layout = AllocateLayout(program, plan, resources, record.UserDataBase, record.UserDataCount, record.PushDataCursor,
                record.Info.DispatchThreadDimensions);
            var request = BuildComputeRequest(plan, resources, layout, record.Info, record.SystemRegisters,
                host.SharedInt64AtomicsEnabled,
                host.ExecGuardElisionEnabled,
                host.BufferInt64AtomicsSupported,
                host.ShaderFloat64Supported,
                host.ShaderSignedZeroInfNanPreserveFloat32Supported,
                host.ShaderDeviceClockSupported,
                host.ShaderDeviceClockShift,
                record.HasHostComputeInfo ? record.Info.HostSubgroupSize : host.ComputeSubgroupSize);
            return compiler.TryCompileProgram(request, out compiled, out error) && compiled is not null;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
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
        ShaderSource source,
        ProgramSourceEntry entry,
        SpecializedResourceInfo resources,
        BindingLayout layout,
        ShaderCompileRequest request)
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
                fetchComponents[input.Location] = (byte)input.InputComponentCount;
            }
        }

        return new ShaderProgramInfo
        {
            Stage = source.Stage switch
            {
                ShaderStage.Vertex => ShaderStageKind.Vertex,
                ShaderStage.Pixel => ShaderStageKind.Pixel,
                ShaderStage.Mesh => ShaderStageKind.Mesh,
                _ => ShaderStageKind.Compute,
            },
            Hash = source.Hash,
            UserDataBase = source.UserDataBase,
            UserDataCount = (uint)source.UserData.Length,
            ParameterExportMask = entry.Program.ParameterExportMask,
            PixelParameterInputs = source.Stage == ShaderStage.Pixel
                ? entry.Program.Instructions
                    .Select(instruction => instruction.Control)
                    .OfType<Gen5InterpolationControl>()
                    .Select(control => control.Attribute)
                    .Distinct()
                    .Order()
                    .ToArray()
                : [],
            WritesViewportIndex = source.Stage is ShaderStage.Vertex or ShaderStage.Mesh &&
                entry.Program.WritesViewportIndex(request.PositionExportControl),
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
    internal static bool ShouldWrite(ulong shaderAddress, ulong shaderHash)
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
            var matches = false;
            foreach (var filter in hashFilter.Split(new[] { ',', ';' }, StringSplitOptions.TrimEntries))
            {
                var span = filter.AsSpan();
                if (span.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                {
                    span = span[2..];
                }

                if (!ulong.TryParse(span, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var filteredHash))
                {
                    return false;
                }

                matches |= shaderHash == filteredHash;
            }

            return matches;
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
