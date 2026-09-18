// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Rendering;
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

    public string Label => Stage switch
    {
        ShaderStage.Vertex => "vertex",
        ShaderStage.Pixel => "pixel",
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
    public ProgramKey(ShaderStage stage, ulong hash, uint userDataCount, uint codeSize, uint[] staticState)
    {
        Stage = stage;
        Hash = hash;
        UserDataCount = userDataCount;
        CodeSize = codeSize;
        StaticState = staticState;
    }

    public ShaderStage Stage { get; }
    public ulong Hash { get; }
    public uint UserDataCount { get; }
    public uint CodeSize { get; }
    public uint[] StaticState { get; }

    public bool Equals(ProgramKey? other) =>
        other is not null && Stage == other.Stage && Hash == other.Hash && UserDataCount == other.UserDataCount &&
        CodeSize == other.CodeSize && StaticState.AsSpan().SequenceEqual(other.StaticState);

    public override bool Equals(object? obj) => Equals(obj as ProgramKey);

    // Same-shape variants share a bucket; equality does the one exact comparison of the state words.
    public override int GetHashCode() => HashCode.Combine(Stage, Hash, UserDataCount, CodeSize, StaticState.Length);
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
    private readonly Dictionary<(ulong Hash, uint CodeSize), Gen5ShaderProgram> _decoded = new();
    private readonly List<uint> _staticState = new(StageStaticKey.MaxWords);
    private ulong _nextProgramId;

    public ShaderProgramCache(CpuContext context, IGuestGpuBackend compiler, IShaderPipelineHost host)
    {
        _context = context;
        _compiler = compiler;
        _host = host;
    }

    public int ProgramCount => _programs.Count;

    public IEnumerable<ProgramSourceEntry> Entries => _programs.Values;

    // The decoded instructions of a program, shared by every static variant of the same code.
    public Gen5ShaderProgram Decode(ShaderSource source)
    {
        var key = (source.Hash, source.CodeSize);
        if (_decoded.TryGetValue(key, out var program))
        {
            return program;
        }

        if (!Gen5ShaderTranslator.TryDecodeProgram(_context, source.Address, out program, out var error))
        {
            throw SubmissionScheduler.Fatal($"The shader program cannot be decoded: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16} error={error}.");
        }

        _decoded.Add(key, program);
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
            key = new ProgramKey(source.Stage, source.Hash, (uint)source.UserData.Length, source.CodeSize, _staticState.ToArray());
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
            ReadMemory = _host.TryReadGuestWord,
            ReadCleanMemory = _host.TryReadCleanGuestWord,
            ComputeState = source.Stage == ShaderStage.Compute && options.ComputeInfo is { } computeState
                ? new ComputeSelectorState(computeState.WaveSize, Math.Max(computeState.ThreadsX, 1),
                    Math.Max(computeState.ThreadsY, 1), Math.Max(computeState.ThreadsZ, 1), computeState.DispatchThreadDimensions,
                    computeState.LocalDataShareDwords, computeState.ThreadIdCount)
                : null,
        };
        if (entry is null)
        {
            entry = CreateEntry(source, options);
            _programs.Add(key, entry);
            ShaderCacheCounters.CountProgram();
        }

        var snapshot = new ResourceSnapshot();
        var specialization = new ResourceSpecialization();
        var captureIndirectImageFailure = ShaderPermutationDump.CreateFailureCapture(source);
        using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ResourceMaterialization))
        {
            if (!ResourceMaterializer.Materialize(entry.Plan, inputs, ref snapshot, ref specialization, out var materializationFailure, captureIndirectImageFailure))
            {
                var message = $"The shader resources could not be materialized: stage={source.Label} hash=0x{source.Hash:X16} shader=0x{source.Address:X16}.";
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

        var permutation = CompilePermutation(source, options, entry, specialization, pushDataCursor, key, sourceWasCached);
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
                break;
            default:
                StageStaticKey.Build(options.ComputeInfo ?? throw new ArgumentException("The compute lookup has no compute input info."), _staticState);
                break;
        }
    }

    private ProgramSourceEntry CreateEntry(ShaderSource source, StageCompileOptions options)
    {
        var program = Decode(source);
        var dumpPlanning = CompiledShaderDump.ShouldWrite(source.Address);
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
                waveSize: 32);
            vertexInputs = BuildVertexInputs(fetch, vertexInfo, source);
            program = fetch.RemoveReplacedTableLoads(program);
        }

        ShaderResourcePlan plan;
        try
        {
            plan = ShaderResourcePlan.Extract(program, source.Stage, source.Hash, source.UserDataBase, (uint)source.UserData.Length,
                fetch?.Loads.Select(load => load.Pc).ToHashSet(),
                beforeResourceTracking: dumpPlanning ? resourcePlan => ShaderPlanningDump.WriteGraph(source, resourcePlan) : null);
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
            EmbeddedFetch = fetch,
            VertexInputs = vertexInputs,
        };
    }

    // One fixed-function input per fetched attribute; later loads of the same attribute alias the first.
    private static ShaderVertexInput[] BuildVertexInputs(EmbeddedVertexFetchPlan fetch, VertexInputInfo info, ShaderSource source)
    {
        var inputs = new List<ShaderVertexInput>();
        var aliases = new Dictionary<int, List<uint>>();
        foreach (var load in fetch.Loads)
        {
            if (aliases.TryGetValue(load.AttributeId, out var aliasPcs))
            {
                aliasPcs.Add(load.Pc);
                continue;
            }

            var location = -1;
            for (var index = 0; index < info.Attributes.Length; index++)
            {
                if (info.Attributes[index].AttributeId == load.AttributeId)
                {
                    location = index;
                    break;
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

            aliasPcs = [];
            aliases.Add(load.AttributeId, aliasPcs);
            inputs.Add(new ShaderVertexInput(load.Pc, (uint)location, load.Components, numberFormat, attribute.FetchIndex != 0, aliasPcs));
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
            layout = BindingLayout.Allocate(
                resources.Info,
                BindingLayout.CollectUserDataRegisters(program, source.UserDataBase, (uint)source.UserData.Length),
                BindingLayout.UsesGlobalDataShare(program),
                ShaderCompileRequest.RequiresFlattenedTable(plan, resources),
                BindingLayout.ReadsShaderBase(program),
                pushDataCursor,
                usesDispatchThreadLimits: source.Stage == ShaderStage.Compute && options.ComputeInfo!.DispatchThreadDimensions);
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
        switch (source.Stage)
        {
            case ShaderStage.Vertex:
            {
                var info = options.VertexInfo!;
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = 32,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    RequiredVertexOutputCount = options.RequiredVertexOutputCount,
                    VertexInputs = entry.VertexInputs,
                };
            }

            case ShaderStage.Pixel:
            {
                var info = options.PixelInfo!;
                var interpolators = new uint[info.InputCount];
                Array.Copy(info.InterpolatorSettings, interpolators, interpolators.Length);
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = 32,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    ScratchDwords = info.ScratchDwords,
                    EnableGraphicsSubgroupOperations = enableGraphicsSubgroups,
                    PixelOutputs = options.PixelOutputs,
                    PixelInputEnable = options.PixelInputEnable,
                    PixelInputAddress = options.PixelInputAddress,
                    PixelInputCntl = interpolators,
                };
            }

            default:
            {
                var info = options.ComputeInfo!;
                return new ShaderCompileRequest(entry.Plan, resources, layout)
                {
                    WaveSize = info.WaveSize,
                    TraceDeviceAddressFaults = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                    ScratchDwords = info.ScratchDwords,
                    ComputeSystemRegisters = options.ComputeSystemRegisters,
                    LocalSizeX = Math.Max(info.ThreadsX, 1),
                    LocalSizeY = Math.Max(info.ThreadsY, 1),
                    LocalSizeZ = Math.Max(info.ThreadsZ, 1),
                };
            }
        }
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
                fetchComponents[input.Location] = (byte)input.ComponentCount;
            }
        }

        return new ShaderProgramInfo
        {
            Stage = source.Stage switch
            {
                ShaderStage.Vertex => ShaderStageKind.Vertex,
                ShaderStage.Pixel => ShaderStageKind.Pixel,
                _ => ShaderStageKind.Compute,
            },
            Hash = source.Hash,
            UserDataBase = source.UserDataBase,
            UserDataCount = (uint)source.UserData.Length,
            ParameterExportMask = entry.Program.ParameterExportMask,
            VertexOffsetScalarRegister = entry.EmbeddedFetch?.VertexOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            InstanceOffsetScalarRegister = entry.EmbeddedFetch?.InstanceOffsetScalarRegister ?? ShaderProgramInfo.NoScalarRegister,
            UsesDeviceAddresses = info.UsesDeviceAddresses,
            HasBitwiseExclusiveOr = entry.HasBitwiseExclusiveOr,
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
    internal static bool ShouldWrite(ulong shaderAddress)
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
        if (shader.Payload.Length == 0 || !ShouldWrite(shaderAddress)) return;
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
