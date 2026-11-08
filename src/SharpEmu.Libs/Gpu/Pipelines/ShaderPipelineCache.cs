// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The pipeline provider behind the executor: programs by identity, pipelines by their full static state.
internal sealed partial class ShaderPipelineCache : IShaderPipelineProvider
{
    internal bool SupportsParallelResolution => _registry.SupportsParallelResolution;
    internal long FrontendImageVersion => _registry.FrontendImageVersion;

    private const uint VertexUserDataBase = 8;
    private const uint MaxPixelInputs = 32;
    private const uint MaxViewportDimension = 16384;

    private readonly CpuContext _context;
    private readonly IShaderPipelineHost _host;
    private readonly ShaderHeaderRegistry _registry;
    private readonly ShaderProgramCache _programs;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<GraphicsPipelineKey, PipelineHandle> _graphicsPipelines = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<ComputePipelineKey, PipelineHandle> _computePipelines = new();
    private readonly Dictionary<Gen5ShaderProgram, bool> _rayTracingComputePrograms = new();
    private readonly object _pipelineGate = new();
    private readonly bool _strictShaders = Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") != "0";
    private readonly bool _skipRayTracing = Environment.GetEnvironmentVariable("SHARPEMU_SKIP_RT") == "1";
    private readonly HashSet<(ShaderStage Stage, ulong Hash, uint CodeSize)> _reportedShaderSkips = [];
    private readonly HashSet<(uint Stages, uint PrimitiveType, uint MaxOutput, uint MaxVerticesOut)> _reportedMeshSkips = [];
    [ThreadStatic]
    private static uint[]? _vertexTableScratch;

    public ShaderPipelineCache(CpuContext context, IShaderPipelineHost host, IGuestGpuBackend compiler, ShaderHeaderRegistry registry)
    {
        _context = context;
        _host = host;
        _registry = registry;
        _programs = new ShaderProgramCache(context, compiler, host) { DeferLinking = true };
        _programs.TryComputePipeline = (program, stage, input) =>
        {
            input.Stage = stage;
            return TryCreateComputePipeline(input, program, out _);
        };
    }

    public ShaderProgramCache Programs => _programs;

    public bool MeshShadersSupported => _host.MeshShadersSupported;

    public int GraphicsPipelineCount => _graphicsPipelines.Count;

    public int ComputePipelineCount => _computePipelines.Count;

    // The user data of one stage: the count from its resource register, else the registers the guest wrote.
    private static ReadOnlySpan<uint> UserData(UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, ulong shaderAddress, string label)
    {
        var count = declaredCount;
        if (count == 0 && probeWrittenRegisters)
        {
            count = registers.Count;
        }

        if (count > UserScalarRegisters.Capacity)
        {
            throw SubmissionScheduler.Fatal($"The shader declares more user registers than the bank holds: label={label} shader=0x{shaderAddress:X16} count={count}.");
        }

        return registers.ValueSpan((int)count);
    }

    private uint[] KeptUserData(KeptUserData kept, ulong userDataStamp, ulong programsStamp, UserScalarRegisters registers, uint declaredCount,
        ulong shaderAddress, string label)
    {
        if (_registry.HasFrontend)
            return UserData(registers, declaredCount, true, shaderAddress, label).ToArray();
        if (kept.Words is { } words && kept.UserDataStamp == userDataStamp && kept.ProgramsStamp == programsStamp)
        {
            return words;
        }

        var current = UserData(registers, declaredCount, probeWrittenRegisters: true, shaderAddress, label);
        if (kept.Words is null || !current.SequenceEqual(kept.Words))
        {
            kept.Words = current.ToArray();
        }

        kept.UserDataStamp = userDataStamp;
        kept.ProgramsStamp = programsStamp;
        return kept.Words;
    }

    private ShaderSource PrepareSource(ulong codeAddress, ShaderStage stage, string label, uint[] userData, uint userDataBase, out ShaderHeaderEntry header,
        ulong geometryPointer = 0)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramSourceRead);
        header = _registry.RequireEntry(codeAddress, label, userData);
        return SourceOf(header, IdentityOf(header, codeAddress, label), stage, userData, userDataBase, geometryPointer);
    }

    private static ShaderSource SourceOf(ShaderHeaderEntry header, ulong hash, ShaderStage stage, uint[] userData, uint userDataBase, ulong geometryPointer)
    {
        if (header.Shader.Frontend is { } frontend && userDataBase == VertexUserDataBase)
            userDataBase = frontend.VertexUserDataBase;
        if (stage == ShaderStage.Mesh && header.Shader.IsFused)
        {
            var withPointer = new uint[VertexUserDataBase + userData.Length];
            userData.CopyTo(withPointer.AsSpan((int)VertexUserDataBase));
            withPointer[0] = (uint)geometryPointer;
            withPointer[1] = (uint)(geometryPointer >> 32);
            return new ShaderSource(header.Shader, hash, withPointer, 0, stage);
        }

        return new ShaderSource(header.Shader, hash, userData, userDataBase, stage);
    }

    // The header a pair found last stays its header while no shader was registered since.
    private ShaderSource PrepareSource(ulong codeAddress, ShaderStage stage, string label, uint[] userData, uint userDataBase, ref ShaderHeaderEntry? cached,
        out ShaderHeaderEntry header, ulong geometryPointer = 0)
    {
        if (_registry.HasFrontend || cached is not { } known || known.Registrations != ShaderHeaderRegistry.Registrations)
        {
            var source = PrepareSource(codeAddress, stage, label, userData, userDataBase, out header, geometryPointer);
            cached = header;
            return source;
        }

        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramSourceRead);
        header = known;
        return SourceOf(header, IdentityOf(header, codeAddress, label), stage, userData, userDataBase, geometryPointer);
    }

    private const int IdentityRecheckPeriod = 64;
    private static readonly ThreadCounter _identityMemoHits = new();

    internal ulong IdentityOf(ShaderHeaderEntry header, ulong codeAddress, string label)
    {
        if (header.Shader.Frontend is { } frontend)
            return ShaderIdentity.ComputeContent(_context.Memory, header.CodeRanges, label) ^ frontend.CacheDomain;
        var uses = header.IdentityUses;
        if (uses > 0 && uses % IdentityRecheckPeriod != 0)
        {
            header.IdentityUses = uses + 1;
            _identityMemoHits.Increment();
            return header.IdentityHash;
        }

        var hash = ShaderIdentity.Compute(_context.Memory, codeAddress, header.CodeRanges, label);
        header.IdentityHash = hash;
        header.IdentityUses = uses + 1;
        return hash;
    }

    public static string TakeFastPathReport() =>
        FormattableString.Invariant($"[PERF][FAST_PATHS] zero_group_skips={Interlocked.Exchange(ref Rendering.RenderExecutor.ZeroGroupSkips, 0)} shader_hash_memo={_identityMemoHits.Take()} ") +
        FormattableString.Invariant($"stream_cleaned={Interlocked.Exchange(ref Buffers.GuestBufferCache.StreamCleaned, 0)} stream_uses={Interlocked.Exchange(ref Buffers.GuestBufferCache.StreamUses, 0)}");

    private uint[]? InputSemantics(ShaderHeaderEntry header)
    {
        if (!header.InputSemanticsRead)
        {
            header.InputSemanticsRead = true;
            header.InputSemantics = ReadInputSemantics(header.Shader);
        }

        return header.InputSemantics;
    }

    private uint[]? ReadInputSemantics(RegisteredShader shader)
    {
        var words = new uint[Math.Min(shader.InputSemanticsCount, (uint)PixelInputInfo.InterpolatorCount)];
        return words.Length == 0 || _context.Memory.TryRead(shader.InputSemanticsAddress, MemoryMarshal.AsBytes(words.AsSpan())) ? words : null;
    }

    public GraphicsPrograms GetGraphicsPrograms(
        RegisterBanks banks,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound) =>
        GetGraphicsProgramsCore(banks, targetExportMapping, pixelActive, depthBound, -1);

    public GraphicsPrograms GetGraphicsPrograms(
        RegisterBanks banks,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        int startInstanceUserData) =>
        GetGraphicsProgramsCore(banks, targetExportMapping, pixelActive, depthBound, startInstanceUserData);

    public GraphicsPrograms GetMeshGraphicsPrograms(
        RegisterBanks banks,
        UserConfigRegisters userConfig,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound)
    {
        var vertex = banks.Shader.Vertex;
        var context = banks.Context;
        var shaderInterface = context.ShaderInterface;
        var mesh = PrepareMeshInput(vertex, shaderInterface, context, userConfig);
        if (MeshDrawTrace.Active)
        {
            var group = userConfig.GeometryEngineControl;
            MeshDrawTrace.Write("plan", $"accepted={mesh is not null} primitive={userConfig.PrimitiveType} outputPrimitive={shaderInterface.GeometryOutputPrimitiveType} maxOutput={shaderInterface.MaxOutputPerSubgroup} maxVerticesOut={shaderInterface.GeometryMaxVerticesOut} group={group.PrimitiveGroupSize}/{group.VertexGroupSize} lds={vertex.GeometryResource2.LocalDataShareSize * 128} configuration={mesh}");
        }
        if (mesh is null)
        {
            var key = (context.ShaderStages, userConfig.PrimitiveType,
                shaderInterface.MaxOutputPerSubgroup, shaderInterface.GeometryMaxVerticesOut);
            lock (_reportedMeshSkips)
            {
                if (_reportedMeshSkips.Add(key))
                {
                    var group = userConfig.GeometryEngineControl;
                    Console.Error.WriteLine($"[GPU][WARN][DRAW_SKIPPED] The mesh draw has an unsupported configuration: " +
                        $"shader=0x{vertex.ExportAddress:X16} primitive={userConfig.PrimitiveType} " +
                        $"outputPrimitive={shaderInterface.GeometryOutputPrimitiveType} " +
                        $"maxOutput={shaderInterface.MaxOutputPerSubgroup} " +
                        $"maxVerticesOut={shaderInterface.GeometryMaxVerticesOut} " +
                        $"group={group.PrimitiveGroupSize}/{group.VertexGroupSize} " +
                        $"ldsDwords={(uint)vertex.GeometryResource2.LocalDataShareSize * 128}.");
                }
            }
            return new GraphicsPrograms { Available = false };
        }

        return ResolveGraphicsPrograms(banks, targetExportMapping, pixelActive, depthBound, -1,
            banks.Stamp(RegisterStamp.ShaderPrograms), banks.Stamp(RegisterStamp.VertexUserData), banks.Stamp(RegisterStamp.PixelUserData),
            banks.Stamp(RegisterStamp.ColorTargets, RegisterStamp.ShaderInterface), null, mesh);
    }

    [ThreadStatic]
    private static KeptUserData? _vertexUserData;
    [ThreadStatic]
    private static KeptVertexInput? _vertexInput;
    [ThreadStatic]
    private static KeptGraphicsPrograms? _keptGraphics;
    [ThreadStatic]
    private static Dictionary<(ulong Vertex, ulong Pixel), GraphicsPair>? _pairs;
    private const int MaxPairs = 4096;
    private static readonly ThreadCounter _pairEntryHits = new();
    private static readonly ThreadCounter _pairEntryMisses = new();

    public static string TakePairReport() => FormattableString.Invariant(
        $"pair_entry_hits={_pairEntryHits.Take()} pair_entry_misses={_pairEntryMisses.Take()}");

    private static GraphicsPair PairOf(ulong vertex, ulong pixel)
    {
        var pairs = _pairs ??= new Dictionary<(ulong Vertex, ulong Pixel), GraphicsPair>();
        if (pairs.TryGetValue((vertex, pixel), out var pair))
        {
            return pair;
        }

        if (pairs.Count >= MaxPairs)
        {
            pairs.Clear();
        }

        pair = new GraphicsPair();
        pairs.Add((vertex, pixel), pair);
        return pair;
    }
    [ThreadStatic]
    private static Dictionary<ulong, List<KeptComputeProgram>>? _keptComputes;
    private const int KeptComputesPerShader = 4;

    private static readonly ThreadCounter _graphicsKept = new();
    private static readonly ThreadCounter _graphicsResolved = new();
    private static readonly ThreadCounter _computeKept = new();
    private static readonly ThreadCounter _computeRebound = new();
    private static readonly ThreadCounter _computeResolved = new();

    public static string TakeKeptProgramsReport() =>
        FormattableString.Invariant($"[PERF][KEPT_PROGRAMS] graphics_kept={_graphicsKept.Take()} graphics_resolved={_graphicsResolved.Take()} ") +
        FormattableString.Invariant($"compute_kept={_computeKept.Take()} compute_rebound={_computeRebound.Take()} compute_resolved={_computeResolved.Take()}");


    private bool TryKeepGraphicsPrograms(RegisterBanks banks, KeptGraphicsPrograms kept, ulong programsStamp, ulong vertexUserDataStamp, ulong pixelUserDataStamp, ulong interfaceStamp,
        ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive, bool depthBound, int startInstanceUserData, out GraphicsPrograms programs)
    {
        programs = null!;
        if (_registry.HasFrontend)
            return false;
        if (kept.Programs is not { } previous || kept.ProgramsStamp != programsStamp || kept.VertexUserDataStamp != vertexUserDataStamp ||
            kept.PixelUserDataStamp != pixelUserDataStamp || kept.InterfaceStamp != interfaceStamp || kept.PixelActive != pixelActive ||
            kept.DepthBound != depthBound || kept.StartInstanceUserData != startInstanceUserData ||
            kept.Registrations != ShaderHeaderRegistry.Registrations || kept.ExportMappingCount != targetExportMapping.Length ||
            !targetExportMapping.SequenceEqual(kept.ExportMapping.AsSpan(0, kept.ExportMappingCount)))
        {
            return false;
        }

        if ((kept.PipelineStateStamp != 0 && kept.PipelineStateStamp != banks.Stamp(RegisterStamp.PipelineState)) ||
            (pixelActive && kept.OutputBlend != PixelOutputBlend(banks.Context)) ||
            (previous.VertexInput.ClipSpace.Enabled && !SameClipViewport(previous.VertexInput.ClipSpace, in banks.Context.ScreenViewport.Primary)))
        {
            return false;
        }

        var keptInput = _vertexInput;
        if (keptInput?.Header?.VertexTables is { } tables && _vertexUserData?.Words is { } words && !SameVertexTables(keptInput, tables, words))
        {
            return false;
        }

        if (!_programs.Revalidate(previous.VertexInput.Stage) || (pixelActive && !_programs.Revalidate(previous.PixelInput.Stage)))
        {
            return false;
        }

        programs = previous;
        return true;
    }

    private static bool SameClipViewport(in ClipSpaceTransform clipSpace, in ViewportRegisters viewport) =>
        BitConverter.SingleToInt32Bits(clipSpace.ScaleX) == BitConverter.SingleToInt32Bits(viewport.XScale) &&
        BitConverter.SingleToInt32Bits(clipSpace.ScaleY) == BitConverter.SingleToInt32Bits(viewport.YScale) &&
        BitConverter.SingleToInt32Bits(clipSpace.OffsetX) == BitConverter.SingleToInt32Bits(viewport.XOffset) &&
        BitConverter.SingleToInt32Bits(clipSpace.OffsetY) == BitConverter.SingleToInt32Bits(viewport.YOffset);

    private static void KeepGraphicsPrograms(KeptGraphicsPrograms kept, GraphicsPrograms programs, ulong programsStamp, ulong vertexUserDataStamp,
        ulong pixelUserDataStamp, ulong interfaceStamp, ulong pipelineStateStamp, uint outputBlend, ReadOnlySpan<ColorComponentMap> targetExportMapping, bool pixelActive,
        bool depthBound, int startInstanceUserData, long registrations)
    {
        kept.Programs = programs.Available ? programs : null;
        kept.StartInstanceUserData = startInstanceUserData;
        kept.ProgramsStamp = programsStamp;
        kept.VertexUserDataStamp = vertexUserDataStamp;
        kept.PixelUserDataStamp = pixelUserDataStamp;
        kept.InterfaceStamp = interfaceStamp;
        kept.PipelineStateStamp = pipelineStateStamp;
        kept.OutputBlend = outputBlend;
        kept.PixelActive = pixelActive;
        kept.DepthBound = depthBound;
        kept.Registrations = registrations;
        kept.ExportMappingCount = targetExportMapping.Length;
        targetExportMapping.CopyTo(kept.ExportMapping);
    }

    private GraphicsPrograms GetGraphicsProgramsCore(
        RegisterBanks banks,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        int startInstanceUserData)
    {
        var programsStamp = banks.Stamp(RegisterStamp.ShaderPrograms);
        var vertexUserDataStamp = banks.Stamp(RegisterStamp.VertexUserData);
        var pixelUserDataStamp = banks.Stamp(RegisterStamp.PixelUserData);
        var interfaceStamp = banks.Stamp(RegisterStamp.ColorTargets, RegisterStamp.ShaderInterface);
        var keptGraphics = _keptGraphics ??= new KeptGraphicsPrograms();
        if (TryKeepGraphicsPrograms(banks, keptGraphics, programsStamp, vertexUserDataStamp, pixelUserDataStamp, interfaceStamp, targetExportMapping, pixelActive, depthBound, startInstanceUserData, out var keptPrograms))
        {
            _graphicsKept.Increment();
            return keptPrograms;
        }

        return ResolveGraphicsPrograms(banks, targetExportMapping, pixelActive, depthBound, startInstanceUserData,
            programsStamp, vertexUserDataStamp, pixelUserDataStamp, interfaceStamp, keptGraphics, null);
    }

    private GraphicsPrograms ResolveGraphicsPrograms(
        RegisterBanks banks,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        int startInstanceUserData,
        ulong programsStamp,
        ulong vertexUserDataStamp,
        ulong pixelUserDataStamp,
        ulong interfaceStamp,
        KeptGraphicsPrograms? keptGraphics,
        MeshDrawConfiguration? mesh)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var vertex = banks.Shader.Vertex;
        var pixel = banks.Shader.Pixel;
        var context = banks.Context;
        var shaderInterface = context.ShaderInterface;
        _graphicsResolved.Increment();
        if (keptGraphics is not null)
        {
            keptGraphics.Programs = null;
        }

        var tessellated = mesh is null && IsMergedTessellationMask(context.ShaderStages);
        Gen5TessellationInfo? tessellation = null;
        if (tessellated)
        {
            if (!Gen5TessellationInfo.TryDecode(shaderInterface.TessellationFactorParameter, out var configuration, out var tessellationError))
                throw SubmissionScheduler.Fatal(tessellationError);
            tessellation = configuration;
        }
        var vertexLabel = tessellated ? "domain" : mesh is null ? "vertex" : "mesh";
        var pair = PairOf(vertex.ExportAddress, pixelActive ? pixel.Address : 0);
        var vertexUserData = KeptUserData(_vertexUserData = pair.VertexUserData, vertexUserDataStamp, programsStamp,
            vertex.GeometryUserScalars, vertex.GeometryResource2.UserScalarCount, vertex.ExportAddress, vertexLabel);
        var vertexSource = PrepareSource(vertex.ExportAddress, tessellated ? ShaderStage.TessellationEvaluation : mesh is null ? ShaderStage.Vertex : ShaderStage.Mesh, vertexLabel, vertexUserData,
            VertexUserDataBase, ref pair.VertexHeader, out var vertexHeader, vertex.GeometryUserDataAddress);
        var vertexInfo = tessellated ? new VertexInputInfo { PositionExportControl = shaderInterface.VertexOutputControl }
            : mesh is null
            ? PrepareVertexInput(vertexSource, vertexHeader, shaderInterface, context, _vertexInput = pair.VertexInput, startInstanceUserData)
            : new VertexInputInfo();
        vertexInfo.IeeeMode = vertex.GeometryResource1.IeeeMode;
        ShaderSource? pixelSource = null;
        PixelInputInfo? pixelInfo = null;
        Gen5PixelOutputBinding[] pixelOutputs = [];
        var dropUnwritten = false;
        var attributeCount = 0u;
        if (pixelActive)
        {
            var pixelUserData = KeptUserData(pair.PixelUserData, pixelUserDataStamp, programsStamp,
                pixel.UserScalars, pixel.Resource2.UserScalarCount, pixel.Address, "pixel");
            pixelSource = PrepareSource(pixel.Address, ShaderStage.Pixel, "pixel", pixelUserData, userDataBase: 0, ref pair.PixelHeader, out var pixelHeader);
            if (RenderTrace.Enabled)
                RenderTrace.Write($"PixelDrawState shader=0x{pixelSource.Address:X16} hash=0x{pixelSource.Hash:X16} " +
                    $"user_data=[{string.Join(",", pixelSource.UserData.Select(word => $"{word:X8}"))}]");
            using var pixelProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PixelInputResolution);
            pixelInfo = PreparePixelInput(banks, pixelSource, pixelHeader, targetExportMapping, depthBound, pair.PixelInput,
                out pixelOutputs, out attributeCount, out dropUnwritten);
            pixelInfo.IeeeMode = pixel.Resource1.IeeeMode;
        }

        ShaderProgram vertexProgram;
        ShaderProgram pixelProgramHandle = default;
        ShaderStageResources vertexStage;
        ShaderStageResources pixelStage = default;
        profileScope.SwitchPhase(RenderPhaseProfile.Phase.ProgramPreparation);
        var pushDataCursor = 0u;
        if (pixelSource is not null)
        {
            var pixelOptions = new StageCompileOptions
            {
                PixelInfo = pixelInfo,
                PixelOutputs = pixelOutputs,
                PixelInputEnable = shaderInterface.PixelInputEnable,
                PixelInputAddress = shaderInterface.PixelInputAddress,
            };
            if (!TryPreparePairProgram(pixelSource, pixelOptions, pair.Pixel, pair.PixelInput.Info, 0, ref pushDataCursor, out pixelProgramHandle, out pixelStage))
                return new GraphicsPrograms { Available = false };
        }

        if (!TryPreparePairProgram(
            vertexSource,
            mesh is null
                ? new StageCompileOptions { VertexInfo = vertexInfo, RequiredVertexOutputCount = (int)attributeCount, Tessellation = tessellation }
                : new StageCompileOptions
                {
                    MeshInfo = mesh, RequiredVertexOutputCount = (int)attributeCount,
                    MeshOutputLocationMask = pixelSource is null ? 0u : pixelStage.Program?.InputLocationMask,
                },
            pair.Vertex,
            mesh is null ? vertexInfo : null,
            (int)attributeCount,
            ref pushDataCursor,
            out vertexProgram,
            out vertexStage))
            return new GraphicsPrograms { Available = false };

        vertexInfo.Stage = vertexStage;
        if (pixelInfo is not null)
        {
            pixelInfo.Stage = pixelStage;
        }

        TessellationDrawPrograms? tessellationPrograms = null;
        if (tessellated)
        {
            lock (_pipelineGate)
            {
                tessellationPrograms = PrepareTessellationHull(vertex, shaderInterface, tessellation!.Value);
                var bridge = _programs.GetTessellationBridge(vertexStage.Program!.Bindings!, tessellation.Value.Domain);
                vertexInfo = new VertexInputInfo
                {
                    PositionExportControl = vertexInfo.PositionExportControl,
                    IeeeMode = vertexInfo.IeeeMode,
                    Stage = vertexStage,
                    Tessellation = new(bridge.Control, vertexProgram, tessellationPrograms.HullConfiguration.InputControlPoints),
                };
                vertexProgram = bridge.Vertex;
            }
        }

        SolidColorClear? solidClear = null;
        var disableBlending = false;
        var blendDependent = false;
        if (mesh is null && !tessellated && pixelInfo is not null && vertexInfo.Attributes.Length == 0 && HasNoImagesOrBuffers(vertexStage) && HasNoImagesOrBuffers(pixelStage))
        {
            var vertexProgramWords = _programs.Decode(vertexSource);
            var pixelProgramWords = _programs.Decode(pixelSource!);
            if (IsProceduralFullscreenClearPair(vertexProgramWords, vertexInfo, vertexStage, pixelProgramWords, pixelStage))
            {
                var color = DecodeSolidClearColor(pixelStage.Resources.UserData);
                solidClear = new SolidColorClear(color.Red, color.Green, color.Blue, color.Alpha);
            }
            else
            {
                blendDependent = true;
                disableBlending = IsTransparentPremultipliedFill(context, pixelOutputs, vertexInfo, vertexStage, pixelStage);
            }
        }

        var programs = new GraphicsPrograms(vertexInfo, pixelInfo ?? new PixelInputInfo())
        {
            Vertex = vertexProgram,
            Tessellation = tessellationPrograms,
            Pixel = pixelProgramHandle,
            MeshInput = mesh,
            SolidClear = solidClear,
            DisableBlending = disableBlending,
            DropUnwrittenColorTargets = dropUnwritten,
            PositionStream = mesh is null ? FindPositionStream(vertexInfo) : null,
        };
        if (keptGraphics is not null)
        {
            KeepGraphicsPrograms(keptGraphics, programs, programsStamp, vertexUserDataStamp, pixelUserDataStamp, interfaceStamp,
                blendDependent ? banks.Stamp(RegisterStamp.PipelineState) : 0, pixelActive ? PixelOutputBlend(context) : 0, targetExportMapping, pixelActive,
                depthBound, startInstanceUserData, ShaderHeaderRegistry.Registrations);
        }

        return programs;
    }

    private MeshDrawConfiguration? PrepareMeshInput(VertexStageRegisters vertex,
        ShaderInterfaceRegisters shaderInterface, ContextRegisters context,
        UserConfigRegisters userConfig)
    {
        var planned = MeshDrawPlanner.Create(vertex, shaderInterface, context, userConfig,
            _host.MeshLimits, _host.MeshSubgroupSize);
        if (planned is not { } configuration) return null;
        var registered = _registry.Require(vertex.ExportAddress, "mesh");
        return new MeshDrawConfiguration
        {
            Execution = configuration.Execution,
            Geometry = configuration.Geometry with
            {
                IeeeMode = vertex.GeometryResource1.IeeeMode,
                ScratchDwords = Math.Max(registered.ScratchDwords, registered.ContinuationScratchDwords),
                ClipSpace = ResolveClipSpace(context),
            },
        };
    }

    private ClipSpaceTransform ResolveClipSpace(ContextRegisters context)
    {
        if (!context.Clip.ClipDisable) return default;
        ref readonly var viewport = ref context.ScreenViewport.Primary;
        var limits = _host.Limits;
        return new ClipSpaceTransform(true, viewport.XScale, viewport.YScale,
            viewport.XOffset, viewport.YOffset,
            Math.Min(limits.MaxViewportWidth, MaxViewportDimension) * 0.5f,
            Math.Min(limits.MaxViewportHeight, MaxViewportDimension) * 0.5f);
    }

    private static bool HasNoImagesOrBuffers(in ShaderStageResources stage) =>
        stage.Program?.Resources?.Info is { } info && info.Images.Count == 0 && info.Buffers.Count == 0;

    // The program entry a pair's stage used stays its entry while the stage's static inputs are the
    // same memoized object: the entry's key is built from them alone.
    private bool TryPreparePairProgram(ShaderSource source, StageCompileOptions options, GraphicsPairStage kept, object? staticInputs, int requiredOutputs,
        ref uint pushDataCursor, out ShaderProgram program, out ShaderStageResources stage)
    {
        var startInstance = options.VertexInfo?.StartInstanceUserData ?? -1;
        var ieeeMode = options.VertexInfo?.IeeeMode ?? options.PixelInfo?.IeeeMode ?? false;
        if (kept.Entry is { } entry && staticInputs is not null && ReferenceEquals(kept.StaticInputs, staticInputs) && kept.Hash == source.Hash &&
            kept.UserDataCount == source.UserData.Length && kept.RequiredOutputs == requiredOutputs && kept.StartInstance == startInstance &&
            kept.IeeeMode == ieeeMode && kept.Registrations == ShaderHeaderRegistry.Registrations)
        {
            _pairEntryHits.Increment();
            if (_programs.TryGetProgramForEntry(entry, source, options, _strictShaders, ref pushDataCursor, out program, out stage, out var rejection))
            {
                return true;
            }

            ReportShaderSkip(source, rejection);
            return false;
        }

        _pairEntryMisses.Increment();
        if (!TryPrepareProgram(source, options, ref pushDataCursor, out program, out stage))
        {
            kept.Entry = null;
            return false;
        }

        kept.Entry = ShaderProgramCache.LastEntry;
        kept.StaticInputs = staticInputs;
        kept.Hash = source.Hash;
        kept.UserDataCount = source.UserData.Length;
        kept.RequiredOutputs = requiredOutputs;
        kept.StartInstance = startInstance;
        kept.IeeeMode = ieeeMode;
        kept.Registrations = ShaderHeaderRegistry.Registrations;
        return true;
    }

    // The vertex tables of the draw and the clip-space transform when clipping is off.
    private PixelInputInfo PreparePixelInput(RegisterBanks banks, ShaderSource source, ShaderHeaderEntry header,
        ReadOnlySpan<ColorComponentMap> targetExportMapping, bool depthBound, KeptPixelInput kept, out Gen5PixelOutputBinding[] outputs,
        out uint attributeCount, out bool dropUnwritten)
    {
        // Headerless frontends reread each draw's shader; that wrapper is not pixel input state.
        object headerKey = header.Shader is { HeaderAddress: 0, Frontend: not null, InputSemanticsCount: 0 } ? _registry : header;
        var key = new PixelInputKey(banks.Stamp(RegisterStamp.ColorTargets, RegisterStamp.ShaderInterface), PixelOutputBlend(banks.Context), headerKey,
            header.Registrations, header.Shader.ScratchDwords, source.Hash,
            Pack(targetExportMapping), targetExportMapping.Length, depthBound);
        if (kept.Info is { } info && kept.Key == key)
        {
            outputs = kept.Outputs;
            attributeCount = kept.AttributeCount;
            dropUnwritten = kept.DropUnwritten;
            return info.WithoutStage();
        }

        var resolved = ResolvePixelInput(banks.Context, source, header, targetExportMapping, depthBound, out outputs, out attributeCount,
            out dropUnwritten);
        kept.Key = key;
        kept.Info = resolved;
        kept.Outputs = outputs;
        kept.AttributeCount = attributeCount;
        kept.DropUnwritten = dropUnwritten;
        return resolved;
    }

    private PixelInputInfo ResolvePixelInput(ContextRegisters context, ShaderSource source, ShaderHeaderEntry header,
        ReadOnlySpan<ColorComponentMap> targetExportMapping, bool depthBound, out Gen5PixelOutputBinding[] outputs, out uint attributeCount,
        out bool dropUnwritten)
    {
        var shaderInterface = context.ShaderInterface;
        var pixelProgram = _programs.Decode(source);
        var attributes = GetPixelAttributes(pixelProgram);
        attributeCount = attributes.Length == 0 ? 0 : attributes[^1] + 1;
        var inputCount = shaderInterface.PixelInputControl & 0x3Fu;
        if (inputCount == 0)
        {
            inputCount = Math.Min(attributeCount, MaxPixelInputs);
        }

        if (inputCount > MaxPixelInputs)
        {
            throw SubmissionScheduler.Fatal($"The pixel program declares too many interpolators: shader=0x{source.Address:X16} count={inputCount}.");
        }

        dropUnwritten = depthBound ||
            LeavesSingleDualSourceTarget(context, targetExportMapping, pixelProgram.PixelColorExportMasks) ||
            UnwrittenTargetBoundsRenderArea(context, targetExportMapping, pixelProgram.PixelColorExportMasks);
        Span<byte> outputModes = stackalloc byte[PixelInputInfo.TargetCount];
        Span<ColorComponentMap> outputMappings = stackalloc ColorComponentMap[PixelInputInfo.TargetCount];
        outputs = ResolveBoundTargets(context, targetExportMapping, dropUnwritten ? pixelProgram.PixelColorExportMasks : null, outputModes, outputMappings);
        var info = PixelStageInputResolver.Resolve(_context, source.Registered, shaderInterface, outputModes, outputMappings, inputCount,
            InputSemantics(header));
        // SPI_PS_INPUT_CNTL can map an input to any parameter export, beyond the input count;
        // the vertex program must declare every location the pixel program reads.
        attributeCount = Math.Max(attributeCount, ReadVertexOutputCount(attributes, info));
        return info;
    }

    private static ulong Pack(ReadOnlySpan<ColorComponentMap> mappings)
    {
        var packed = 0ul;
        for (var index = 0; index < mappings.Length && index < sizeof(ulong); index++)
        {
            packed |= (ulong)mappings[index].Packed << (index * 8);
        }

        return packed;
    }

    private VertexInputInfo PrepareVertexInput(ShaderSource source, ShaderHeaderEntry header, ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context, KeptVertexInput kept, int startInstanceUserData)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VertexInputResolution);
        var clipSpace = ResolveClipSpace(context);
        var tables = header.VertexTables ??= VertexInputResolver.RequireTables(_context, header.Shader);
        var control = shaderInterface.VertexOutputControl;
        if (ReferenceEquals(kept.Header, header) && kept.Info is { } info && kept.PositionExportControl == control &&
            SameBits(kept.ClipSpace, clipSpace) && SameVertexTables(kept, tables, source.UserData))
        {
            var copy = info.WithoutStage();
            copy.StartInstanceUserData = startInstanceUserData;
            return copy;
        }

        var resolved = VertexInputResolver.ResolveVertexInputs(_context, header.Shader, tables, source.UserData, control, clipSpace, out var words);
        resolved.StartInstanceUserData = startInstanceUserData;
        kept.Header = header;
        kept.Words = words;
        kept.PositionExportControl = control;
        kept.ClipSpace = clipSpace;
        kept.Info = resolved;
        return resolved;
    }

    // The interpolated attributes a pixel program reads, ascending. A program never changes, and this
    // was rebuilt with LINQ on every draw.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Gen5ShaderProgram, uint[]> InterpolatedAttributeCache = new();

    internal uint[] GetPixelAttributes(Gen5ShaderProgram program) =>
        InterpolatedAttributeCache.GetValue(program, static program => program.Instructions
            .Select(static instruction => instruction.Control)
            .OfType<Gen5InterpolationControl>()
            .Select(static control => control.Attribute)
            .Distinct()
            .Order()
            .ToArray());

    private bool SameVertexTables(KeptVertexInput kept, VertexTableMetadata tables, uint[] userData)
    {
        if (kept.Words is not { } words)
        {
            return true;
        }

        var attributeTable = VertexInputResolver.ReadTablePointer(userData, tables.VertexAttributeRegister);
        var bufferTable = VertexInputResolver.ReadTablePointer(userData, tables.VertexBufferRegister);
        return attributeTable != 0 && bufferTable != 0 &&
            VertexInputResolver.SameTableWords(_context, words, attributeTable, bufferTable, _vertexTableScratch ??= new uint[VertexTableWords.MaxWords]);
    }

    private static bool SameBits(in ClipSpaceTransform left, in ClipSpaceTransform right) =>
        left.Enabled == right.Enabled &&
        BitConverter.SingleToUInt32Bits(left.ScaleX) == BitConverter.SingleToUInt32Bits(right.ScaleX) &&
        BitConverter.SingleToUInt32Bits(left.ScaleY) == BitConverter.SingleToUInt32Bits(right.ScaleY) &&
        BitConverter.SingleToUInt32Bits(left.OffsetX) == BitConverter.SingleToUInt32Bits(right.OffsetX) &&
        BitConverter.SingleToUInt32Bits(left.OffsetY) == BitConverter.SingleToUInt32Bits(right.OffsetY) &&
        BitConverter.SingleToUInt32Bits(left.HalfExtentX) == BitConverter.SingleToUInt32Bits(right.HalfExtentX) &&
        BitConverter.SingleToUInt32Bits(left.HalfExtentY) == BitConverter.SingleToUInt32Bits(right.HalfExtentY);

    // One past the highest parameter location the pixel program reads, resolved as its translator does.
    internal static uint ReadVertexOutputCount(ReadOnlySpan<uint> attributes, PixelInputInfo info)
    {
        if (attributes.Length == 0)
        {
            return 0;
        }

        Span<uint> controls = stackalloc uint[32];
        for (var index = 0u; index < (uint)controls.Length; index++)
        {
            controls[(int)index] = index < info.InputCount && index < (uint)info.InterpolatorSettings.Length
                ? info.InterpolatorSettings[index]
                : (uint)index;
        }

        // Attributes are distinct and below 32, so a program never has more than 32 of them.
        Span<uint> locations = attributes.Length <= 32 ? stackalloc uint[attributes.Length] : new uint[attributes.Length];
        Gen5PixelInputMapping.ResolveLocations(controls, attributes, locations);
        var highest = 0u;
        foreach (var location in locations)
        {
            highest = Math.Max(highest, location);
        }

        return highest + 1;
    }


    // The bound colour slots in order; each output mode names the kind the pixel program exports.
    // With a depth target bound, a slot the draw never writes is dropped here and from the
    // attachments alike (RenderExecutor.DropUnwrittenColorTargets): unwrittenExportMasks is the
    // pixel program's export mask then, null otherwise.
    private Gen5PixelOutputBinding[] ResolveBoundTargets(ContextRegisters context, ReadOnlySpan<ColorComponentMap> targetExportMapping,
        uint? unwrittenExportMasks, Span<byte> outputModes, Span<ColorComponentMap> outputMappings)
    {
        Span<Gen5PixelOutputBinding> outputs = stackalloc Gen5PixelOutputBinding[ContextRegisters.ColorTargetCount];
        var outputCount = 0;
        var location = 0u;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (slot != 0 && (context.RenderTargetMaskForSlot(slot) == 0 || context.ColorTargets[slot].BaseAddress == 0))
            {
                continue;
            }

            if (ColorTargetResolver.Resolve(context, slot, 0, ignoreTargetMask: false, out _) is null)
            {
                continue;
            }

            if (unwrittenExportMasks is { } exportMasks &&
                RenderExecutor.IsUnwrittenColorTarget(context, slot, targetExportMapping[(int)slot], exportMasks))
            {
                continue;
            }

            var words = context.ColorTargets[slot];
            if (!_host.TryResolveColorOutput((uint)words.Layout, (uint)words.NumberType, (uint)words.Order, out var kind, out var mapping))
            {
                throw SubmissionScheduler.Fatal($"The color target format has no pixel output kind: slot={slot} layout={(uint)words.Layout} numberType={(uint)words.NumberType} order={(uint)words.Order}.");
            }

            // A bound slot that no export reaches keeps its contents: its color write mask is zero.
            var exportTarget = PixelExportRouting.ExportForSlot(context.ShaderInterface, slot);
            // The high nibble keys the compiled program by the export that feeds the slot.
            outputModes[(int)slot] = (byte)(((uint)kind + 1) | (uint)((exportTarget + 1) << 4));
            outputMappings[(int)slot] = new ColorComponentMap(mapping.Packed);
            // No EXP target reaches 8 and above, so an unfed slot is declared but never written.
            outputs[outputCount++] = new Gen5PixelOutputBinding(slot, location++, kind, mapping)
            {
                ExportTarget = exportTarget >= 0 ? (uint)exportTarget : ContextRegisters.ColorTargetCount + slot,
                ExportFormat = ExportFormatOf(context.ShaderInterface, exportTarget),
            };
        }

        if (context.BlendControls[0].UsesSecondarySource && (context.ColorTargets[0].Info & (1u << 16)) == 0 &&
            outputCount != 0 && outputs[0].GuestSlot == 0)
        {
            if (outputCount != 1 || outputs[0].Kind != Gen5PixelOutputKind.Float)
            {
                throw SubmissionScheduler.Fatal("Dual-source blending requires one floating-point color output.");
            }
            if (NeedsDualSourceAlpha(context, 0, outputMappings[0]))
            {
                throw SubmissionScheduler.Fatal("Dual-source blending cannot also export an unmapped source alpha.");
            }

            // EXP1 supplies the second source for target zero, not a second attachment.
            outputs[0] = outputs[0] with { ExportTarget = 0, ExportFormat = ExportFormatOf(context.ShaderInterface, 0) };
            outputs[outputCount++] = outputs[0] with { GuestSlot = 1, ExportTarget = 1, Index = 1, ExportFormat = ExportFormatOf(context.ShaderInterface, 1) };
            outputModes[0] = (byte)(((uint)outputs[0].Kind + 1) | 0x10u);
            outputModes[1] = DualSourceOutputMode;
        }

        // Dual-source blending is limited to a single attachment.
        if (outputCount == 1 &&
            _host.SupportsDualSourceBlend &&
            outputs[0].Kind == Gen5PixelOutputKind.Float &&
            NeedsDualSourceAlpha(context, outputs[0].GuestSlot, outputMappings[(int)outputs[0].GuestSlot]))
        {
            outputs[0] = outputs[0] with { DualSourceAlpha = true };
            outputModes[(int)outputs[0].GuestSlot] |= DualSourceAlphaOutputMode;
        }

        return outputs[..outputCount].ToArray();
    }

    private const byte DualSourceOutputMode = 0xFF;

    // SPI_SHADER_COL_FORMAT of an EXP target; a slot no export feeds keeps the default.
    private static Gen5PixelExportFormat ExportFormatOf(ShaderInterfaceRegisters shaderInterface, int exportTarget) =>
        exportTarget >= 0 && exportTarget < shaderInterface.TargetOutputModes.Length
            ? (Gen5PixelExportFormat)shaderInterface.TargetOutputModes[exportTarget]
            : Gen5PixelExportFormat.Float16;

    // BLEND_SRC1_COLOR, BLEND_ONE_MINUS_SRC1_COLOR, BLEND_SRC1_ALPHA, BLEND_ONE_MINUS_SRC1_ALPHA.
    internal static bool UsesSecondBlendSource(in BlendRegisters blend)
    {
        return blend.UsesSecondarySource;
    }

    // Marks a pixel output mode whose program also writes its alpha to the dual-source output.
    internal const byte DualSourceAlphaOutputMode = 0x08;

    // The hardware blends in shader component order before the component swap lays the result out in memory,
    // but the host image keeps the memory layout and the program writes the swapped vector.
    // When that swap moves the shader's alpha out of host alpha (ABGR and ARGBorders),
    // SRC_ALPHA would read the wrong channel; the program exports the real alpha as the second source and
    // the blend uses SRC1_ALPHA instead.
    private static bool NeedsDualSourceAlpha(ContextRegisters context, uint slot, ColorComponentMap mapping) =>
        mapping.Map(3) != 3 && BlendsWithSourceAlpha(context, slot);

    // The mapping-independent half of NeedsDualSourceAlpha:
    // an enabled, not bypassed blend that reads the source alpha.
    private static bool BlendsWithSourceAlpha(ContextRegisters context, uint slot)
    {
        var blend = context.BlendControls[slot];
        if (!blend.Enable || ((context.ColorTargets[slot].Info >> 16) & 0x1) != 0)
        {
            return false;
        }

        var separate = blend.SeparateAlpha;
        var alphaSource = separate ? blend.AlphaSourceFactor : blend.ColorSourceFactor;
        var alphaDestination = separate ? blend.AlphaDestinationFactor : blend.ColorDestinationFactor;
        return UsesSourceAlpha(blend.ColorSourceFactor) || UsesSourceAlpha(blend.ColorDestinationFactor) ||
            UsesSourceAlpha(alphaSource) || UsesSourceAlpha(alphaDestination);
    }

    private static bool UsesSourceAlpha(byte factor) => factor is 4 or 5;

    private static uint PixelOutputBlend(ContextRegisters context)
    {
        var bits = context.BlendControls[0].UsesSecondarySource ? 1u : 0u;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (BlendsWithSourceAlpha(context, slot))
            {
                bits |= 2u << (int)slot;
            }
        }

        return bits;
    }

    // Dual-source blending allows a single attachment,
    // but post passes often keep another target bound that the program never writes.
    // Dropping such targets (as depth passes already do) is invisible to the guest and
    // lets the remaining target blend correctly. The executor drops the same targets through
    // GraphicsPrograms.DropUnwrittenColorTargets.
    private bool LeavesSingleDualSourceTarget(
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> mappingBySlot,
        uint pixelColorExportMasks)
    {
        // Most draws blend no target with source alpha; skip resolving their targets.
        if (!_host.SupportsDualSourceBlend || !AnyTargetBlendsWithSourceAlpha(context))
        {
            return false;
        }

        Span<uint> boundSlots = stackalloc uint[(int)ContextRegisters.ColorTargetCount];
        boundSlots = boundSlots[..BoundColorSlots(context, boundSlots)];
        if (boundSlots.Length < 2)
        {
            return false;
        }

        var written = 0;
        var writtenSlot = 0u;
        foreach (var slot in boundSlots)
        {
            if (!RenderExecutor.IsUnwrittenColorTarget(context, slot, mappingBySlot[(int)slot], pixelColorExportMasks))
            {
                written++;
                writtenSlot = slot;
            }
        }

        return written == 1 && NeedsDualSourceAlpha(context, writtenSlot, mappingBySlot[(int)writtenSlot]);
    }

    private static bool AnyTargetBlendsWithSourceAlpha(ContextRegisters context)
    {
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (BlendsWithSourceAlpha(context, slot))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UnwrittenTargetBoundsRenderArea(
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> mappingBySlot,
        uint pixelColorExportMasks)
    {
        Span<uint> boundSlots = stackalloc uint[(int)ContextRegisters.ColorTargetCount];
        boundSlots = boundSlots[..BoundColorSlots(context, boundSlots)];
        if (boundSlots.Length < 2)
        {
            return false;
        }

        uint writtenWidth = 0, writtenHeight = 0;
        uint unwrittenWidth = uint.MaxValue, unwrittenHeight = uint.MaxValue;
        foreach (var slot in boundSlots)
        {
            if (ColorTargetResolver.Resolve(context, slot, 0, ignoreTargetMask: false, out _) is not { } resolution)
            {
                continue;
            }

            var extent = resolution.Extent;
            if (RenderExecutor.IsUnwrittenColorTarget(context, slot, mappingBySlot[(int)slot], pixelColorExportMasks))
            {
                unwrittenWidth = Math.Min(unwrittenWidth, extent.Width);
                unwrittenHeight = Math.Min(unwrittenHeight, extent.Height);
            }
            else
            {
                writtenWidth = Math.Max(writtenWidth, extent.Width);
                writtenHeight = Math.Max(writtenHeight, extent.Height);
            }
        }

        return writtenWidth > unwrittenWidth || writtenHeight > unwrittenHeight;
    }

    // The slots ResolveBoundTargets considers bound, before any are dropped as unwritten.
    private static int BoundColorSlots(ContextRegisters context, Span<uint> slots)
    {
        var count = 0;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if (slot != 0 && (context.RenderTargetMaskForSlot(slot) == 0 || context.ColorTargets[slot].BaseAddress == 0))
            {
                continue;
            }

            if (ColorTargetResolver.Resolve(context, slot, 0, ignoreTargetMask: false, out _) is null)
            {
                continue;
            }

            slots[count++] = slot;
        }

        return count;
    }

    // SRC_ALPHA / ONE_MINUS_SRC_ALPHA read the dual-source alpha instead.
    private static byte ToDualSourceFactor(byte factor) => factor switch
    {
        4 => 17,
        5 => 18,
        _ => factor,
    };

    private static VertexPositionStream? FindPositionStream(VertexInputInfo info)
    {
        foreach (var attribute in info.Attributes)
        {
            if (Gfx10UnifiedFormat.TryDecode(attribute.Descriptor.Format, out var dataFormat, out var numberFormat) &&
                dataFormat == PositionDataFormat && numberFormat == PositionNumberFormat && attribute.BufferIndex >= 0)
            {
                var buffer = info.Buffers[attribute.BufferIndex];
                return new VertexPositionStream(buffer.Address, buffer.Stride, attribute.OffsetBytes);
            }
        }

        return null;
    }

    public ComputeProgram GetComputeProgram(RegisterBanks banks, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ) =>
        GetComputeProgram(banks, dispatchInitiator, dimensionX, dimensionY, dimensionZ, indirect: false);

    public ComputeProgram GetComputeProgram(RegisterBanks banks, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ, bool indirect)
    {
        var compute = banks.Shader.Compute;
        var registrations = ShaderHeaderRegistry.Registrations;
        var all = _keptComputes ??= new Dictionary<ulong, List<KeptComputeProgram>>();
        if (!all.TryGetValue(compute.Address, out var keptList))
        {
            keptList = new List<KeptComputeProgram>(KeptComputesPerShader);
            all.Add(compute.Address, keptList);
        }

        KeptComputeProgram? kept = null;
        foreach (var candidate in keptList)
        {
            if (candidate.Registrations == registrations && candidate.DispatchInitiator == dispatchInitiator && SameComputeShape(candidate.Registers, compute))
            {
                kept = candidate;
                break;
            }
        }

        if (kept is not null && !_registry.HasFrontend)
        {
            if (kept.Program is { } previous && kept.Registers is { } previousRegisters && kept.DimensionX == dimensionX && kept.DimensionY == dimensionY && kept.DimensionZ == dimensionZ &&
                SameUserScalars(previousRegisters.UserScalars, compute.UserScalars, compute.UserScalarCount) && _programs.Revalidate(previous.Input.Stage))
            {
                _computeKept.Increment();
                return WithPrediction(previous, kept.Kernel, kept.Source, kept.SystemRegisters, compute, dimensionX, dimensionY, dimensionZ, indirect);
            }

            if (kept.Entry is { } entry && ++kept.Rebinds % IdentityRecheckPeriod != 0)
            {
                _computeRebound.Increment();
                var rebound = RebindComputeProgram(kept, entry, compute, dispatchInitiator, dimensionX, dimensionY, dimensionZ);
                KeepComputeProgram(kept, rebound, compute, dimensionX, dimensionY, dimensionZ);
                return WithPrediction(rebound, kept.Kernel, kept.Source, kept.SystemRegisters, compute, dimensionX, dimensionY, dimensionZ, indirect);
            }
        }

        _computeResolved.Increment();
        var program = ResolveComputeProgram(compute, dispatchInitiator, dimensionX, dimensionY, dimensionZ, out var source, out var systemRegisters, out var decoded);
        if (program.ImageCopy is not null)
            return program;
        if (kept is null)
        {
            if (keptList.Count == KeptComputesPerShader)
            {
                keptList.RemoveAt(0);
            }

            kept = new KeptComputeProgram { Registrations = registrations, DispatchInitiator = dispatchInitiator };
            keptList.Add(kept);
        }

        kept.Source = source;
        kept.SystemRegisters = systemRegisters;
        kept.Kernel = decoded is not null && program.Available && !program.Consumed ? KernelFor(decoded) : null;
        kept.Entry = program.Available && !program.Consumed && source is not null ? ShaderProgramCache.LastEntry : null;
        KeepComputeProgram(kept, program, compute, dimensionX, dimensionY, dimensionZ);
        return WithPrediction(program, kept.Kernel, source, systemRegisters, compute, dimensionX, dimensionY, dimensionZ, indirect);
    }

    private static void KeepComputeProgram(KeptComputeProgram kept, ComputeProgram program, ComputeStageRegisters compute, uint dimensionX, uint dimensionY, uint dimensionZ)
    {
        kept.Program = program.Available && !program.Consumed ? program : null;
        if (kept.Registers is { } registers && SameComputeShape(registers, compute))
        {
            registers.UserScalars.CopyFrom(compute.UserScalars, compute.UserScalarCount);
        }
        else
        {
            kept.Registers = compute.Copy();
        }
        kept.DimensionX = dimensionX;
        kept.DimensionY = dimensionY;
        kept.DimensionZ = dimensionZ;
    }

    private ComputeProgram RebindComputeProgram(KeptComputeProgram kept, ProgramSourceEntry entry, ComputeStageRegisters compute, uint dispatchInitiator,
        uint dimensionX, uint dimensionY, uint dimensionZ)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var userData = UserData(compute.UserScalars, compute.UserScalarCount, probeWrittenRegisters: false, compute.Address, "compute").ToArray();
        var source = kept.Source! with { UserData = userData };
        kept.Source = source;
        var input = ComputeStageInputResolver.Resolve(compute, source.Registered, dispatchInitiator, !_host.ComputeWave64Supported, dimensionX, dimensionY, dimensionZ);
        var pushDataCursor = 0u;
        var options = new StageCompileOptions { ComputeInfo = input, ComputeSystemRegisters = kept.SystemRegisters };
        if (!_programs.TryGetProgramForEntry(entry, source, options, _strictShaders, ref pushDataCursor, out var handle, out var stage, out var rejection))
        {
            ReportShaderSkip(source, rejection);
            return new ComputeProgram { Available = false };
        }

        input.Stage = stage;
        return new ComputeProgram { Program = handle, Input = input };
    }

    private static bool SameUserScalars(UserScalarRegisters kept, UserScalarRegisters current, int count) =>
        kept.ValueSpan(count).SequenceEqual(current.ValueSpan(count)) &&
        kept.KindSpan(count).SequenceEqual(current.KindSpan(count));

    private static bool SameComputeShape(ComputeStageRegisters? kept, ComputeStageRegisters current) =>
        kept is not null &&
        kept.Address == current.Address && kept.ProgramChecksum == current.ProgramChecksum &&
        kept.ThreadsX == current.ThreadsX && kept.ThreadsY == current.ThreadsY && kept.ThreadsZ == current.ThreadsZ &&
        kept.VectorRegisterCount == current.VectorRegisterCount && kept.Priority == current.Priority && kept.FloatMode == current.FloatMode &&
        kept.Dx10Clamp == current.Dx10Clamp && kept.DebugMode == current.DebugMode && kept.IeeeMode == current.IeeeMode &&
        kept.RequireForwardProgress == current.RequireForwardProgress && kept.HalfPrecisionOverflow == current.HalfPrecisionOverflow &&
        kept.ThreadgroupConfiguration == current.ThreadgroupConfiguration && kept.WaveSize == current.WaveSize && kept.ScratchEnable == current.ScratchEnable &&
        kept.UserScalarCount == current.UserScalarCount && kept.ThreadGroupIdXEnable == current.ThreadGroupIdXEnable &&
        kept.ThreadGroupIdYEnable == current.ThreadGroupIdYEnable && kept.ThreadGroupIdZEnable == current.ThreadGroupIdZEnable &&
        kept.ThreadGroupSizeEnable == current.ThreadGroupSizeEnable && kept.ThreadIdComponentCount == current.ThreadIdComponentCount &&
        kept.LocalDataShareSize == current.LocalDataShareSize && kept.SharedVectorRegisters == current.SharedVectorRegisters;

    public ComputeProgram GetComputeProgram(
        ComputeStageRegisters compute,
        ShaderInterfaceRegisters shaderInterface,
        uint dispatchInitiator,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ)
    {
        var program = ResolveComputeProgram(compute, dispatchInitiator, dimensionX, dimensionY, dimensionZ, out var source, out var systemRegisters, out var decoded);
        var kernel = decoded is not null && program.Available && !program.Consumed ? KernelFor(decoded) : null;
        return WithPrediction(program, kernel, source, systemRegisters, compute, dimensionX, dimensionY, dimensionZ, indirect: false);
    }

    private ComputeProgram ResolveComputeProgram(ComputeStageRegisters compute, uint dispatchInitiator, uint dimensionX, uint dimensionY, uint dimensionZ,
        out ShaderSource? resolvedSource, out Gen5ComputeSystemRegisters resolvedSystemRegisters, out Gen5ShaderProgram? decoded)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var userData = UserData(compute.UserScalars, compute.UserScalarCount, probeWrittenRegisters: false, compute.Address, "compute").ToArray();
        var source = PrepareSource(compute.Address, ShaderStage.Compute, "compute", userData, userDataBase: 0, out _);
        var program = _programs.Decode(source);
        var systemRegisters = DecodeComputeSystemRegisters(compute);
        resolvedSource = source;
        resolvedSystemRegisters = systemRegisters;
        decoded = program;
        if (_skipRayTracing)
        {
            lock (_rayTracingComputePrograms)
            {
                if (!_rayTracingComputePrograms.TryGetValue(program, out var usesRayTracing))
                {
                    usesRayTracing = program.Instructions.Any(instruction =>
                        instruction.Opcode is "ImageBvhIntersectRay" or "ImageBvh64IntersectRay");
                    _rayTracingComputePrograms.Add(program, usesRayTracing);
                    if (usesRayTracing)
                        Console.Error.WriteLine($"[GPU][WARN][RAY_TRACING_SKIPPED] Compute dispatch skipped: hash=0x{source.Hash:X16}. " +
                            "The shader contains BVH ray-intersection instructions. Ray-traced effects can be missing or incorrect. " +
                            "Set SHARPEMU_SKIP_RT=0 before launch to disable this skip.");
                }
                if (usesRayTracing) return new ComputeProgram { Available = false };
            }
        }
        var input = ComputeStageInputResolver.Resolve(compute, source.Registered, dispatchInitiator, !_host.ComputeWave64Supported, dimensionX, dimensionY, dimensionZ);
        if (source.Registered.Frontend is { } frontend &&
            frontend.TryResolveImageCopy(_context, source.Registered, source.UserData, input, systemRegisters, dimensionX, dimensionY, dimensionZ,
                out var copySource, out var copyDestination))
        {
            return new ComputeProgram
            {
                Input = input,
                ImageCopy = new(copySource, copyDestination, () => CompileComputeProgram(source, input, systemRegisters)),
            };
        }
        return CompileComputeProgram(source, input, systemRegisters);
    }

    private ComputeProgram CompileComputeProgram(ShaderSource source, ComputeInputInfo input, Gen5ComputeSystemRegisters systemRegisters)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var pushDataCursor = 0u;
        if (!TryPrepareProgram(
            source,
            new StageCompileOptions { ComputeInfo = input, ComputeSystemRegisters = systemRegisters },
            ref pushDataCursor,
            out var handle,
            out var stage))
        {
            return new ComputeProgram { Available = false };
        }

        input.Stage = stage;
        return new ComputeProgram { Program = handle, Input = input };
    }

    private bool TryPrepareProgram(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor,
        out ShaderProgram program, out ShaderStageResources stage)
    {
        if (_programs.TryGetProgram(source, options, _strictShaders, ref pushDataCursor, out program, out stage, out var rejection))
            return true;

        ReportShaderSkip(source, rejection);
        return false;
    }

    private void ReportShaderSkip(ShaderSource source, string rejection)
    {
        if (rejection.Length == 0)
        {
            return;
        }

        bool reported;
        lock (_reportedShaderSkips)
        {
            reported = _reportedShaderSkips.Add((source.Stage, source.Hash, source.CodeSize));
        }

        if (reported)
        {
            var tag = source.Stage == ShaderStage.Compute ? "COMPUTE_SKIPPED" : "DRAW_SKIPPED";
            Console.Error.WriteLine($"[GPU][WARN][{tag}] {rejection} " +
                "The operation was not executed. Images and FPS can be incorrect. Set SHARPEMU_STRICT_COMPUTE=1 to stop on this failure.");
        }
    }

    // The system registers that follow the user data, in the order the resource register enables them.
    public static Gen5ComputeSystemRegisters DecodeComputeSystemRegisters(ComputeStageRegisters compute)
    {
        var nextRegister = (uint)compute.UserScalarCount;
        uint? workGroupX = null;
        uint? workGroupY = null;
        uint? workGroupZ = null;
        uint? threadGroupSize = null;
        if (compute.ThreadGroupIdXEnable)
        {
            workGroupX = nextRegister++;
        }

        if (compute.ThreadGroupIdYEnable)
        {
            workGroupY = nextRegister++;
        }

        if (compute.ThreadGroupIdZEnable)
        {
            workGroupZ = nextRegister++;
        }

        if (compute.ThreadGroupSizeEnable)
        {
            threadGroupSize = nextRegister;
        }

        return new Gen5ComputeSystemRegisters(workGroupX, workGroupY, workGroupZ, threadGroupSize);
    }

    public PipelineHandle CreateGraphicsPipeline(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        PrimitiveTopology topology,
        bool primitiveRestartEnabled,
        bool disableBlending,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineCreation);
        // The lookup fills objects this thread reuses for every draw; only a new pipeline gets its own.
        var lookup = _graphicsLookup ??= new GraphicsPipelineKey
        {
            Rendering = new PipelineRenderingState(),
            VertexInput = new PipelineVertexInputState(),
            StaticParameters = new PipelineStaticParameters(),
        };
        lookup.Rendering.Reset();
        lookup.VertexInput.Reset();
        lookup.StaticParameters.Reset();
        FillGraphicsState(colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
            vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts, lookup.Rendering, lookup.StaticParameters, lookup.VertexInput);
        lookup.VertexProgramId = vertexProgram.Id;
        lookup.PixelProgramId = pixelInput is not null ? pixelProgram.Id : 0;
        lookup.Tessellation = vertexInput.Tessellation;
        if (_graphicsPipelines.TryGetValue(lookup, out var known))
        {
            return known;
        }

        var description = BuildGraphicsDescription(
            colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
            vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts);
        var key = KeyOf(description);
        profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineSharedGateWait);
        lock (_pipelineGate)
        {
            profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineCreation);
            if (_graphicsPipelines.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"PipelineCache create graphics vertex=0x{vertexProgram.Id:X16} pixel=0x{key.PixelProgramId:X16} colors={key.Rendering.ColorCount} " +
                    $"depth={description.StaticParameters.WithDepth} samples={description.StaticParameters.Samples}");

            }

            var created = _host.CreateGraphicsPipeline(description);
            lock (_pipelineGate)
            {
                _graphicsPipelines.TryAdd(key, created);
            }

            if (_host.ShaderPrewarm is { } prewarm && description.Tessellation is null &&
                description.VertexStage.Stage == ShaderStageKind.Vertex)
            {
                prewarm.RecordGraphics(description);
            }

            ShaderCacheCounters.CountGraphicsPipeline();
            return created;
        }
    }

    [ThreadStatic]
    private static GraphicsPipelineKey? _graphicsLookup;

    // The full static state of one graphics pipeline from the draw's targets, registers and programs.
    internal static GraphicsPipelineDescription BuildGraphicsDescription(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        PrimitiveTopology topology,
        bool primitiveRestartEnabled,
        bool disableBlending,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram,
        SampleCountFlags noAttachmentSampleCounts)
    {
        var renderingState = new PipelineRenderingState();
        var parameters = new PipelineStaticParameters();
        var vertexState = new PipelineVertexInputState();
        var (vertexStage, pixelStage) = FillGraphicsState(colors, in depth, vertexInput, pixelInput, context, in rendering, topology,
            primitiveRestartEnabled, disableBlending, vertexProgram, pixelProgram, noAttachmentSampleCounts, renderingState, parameters, vertexState);
        return new GraphicsPipelineDescription
        {
            Tessellation = vertexInput.Tessellation,
            Rendering = renderingState,
            VertexInput = vertexState,
            VertexInfo = vertexInput,
            VertexProgram = vertexProgram,
            VertexStage = vertexStage,
            PixelInfo = pixelInput,
            PixelProgram = pixelProgram,
            PixelStage = pixelStage,
            StaticParameters = parameters,
        };
    }

    // Writes the pipeline state into objects in their initial state; returns the stage programs.
    private static (ShaderProgramInfo VertexStage, ShaderProgramInfo? PixelStage) FillGraphicsState(
        ReadOnlySpan<ColorTargetState> colors,
        in DepthAttachmentState depth,
        VertexInputInfo vertexInput,
        PixelInputInfo? pixelInput,
        ContextRegisters context,
        in RenderingState rendering,
        PrimitiveTopology topology,
        bool primitiveRestartEnabled,
        bool disableBlending,
        ShaderProgram vertexProgram,
        ShaderProgram pixelProgram,
        SampleCountFlags noAttachmentSampleCounts,
        PipelineRenderingState renderingState,
        PipelineStaticParameters parameters,
        PipelineVertexInputState vertexState)
    {
        if (colors.Length > PipelineStaticParameters.ColorAttachmentCount)
        {
            throw SubmissionScheduler.Fatal($"The draw binds more color attachments than a pipeline holds: count={colors.Length}.");
        }

        if (!vertexProgram.IsValid)
        {
            throw SubmissionScheduler.Fatal("The draw has no vertex program.");
        }

        var pixelActive = pixelInput is not null;
        if (pixelActive && !pixelProgram.IsValid)
        {
            throw SubmissionScheduler.Fatal("The draw has an active pixel stage without a pixel program.");
        }

        var vertexStage = vertexInput.Stage.Program ?? throw SubmissionScheduler.Fatal("The vertex stage has no program.");
        var pixelStage = pixelActive ? pixelInput!.Stage.Program ?? throw SubmissionScheduler.Fatal("The pixel stage has no program.") : null;
        var colorCount = (uint)colors.Length;
        parameters.ColorCount = colorCount;
        renderingState.ColorCount = colorCount;
        for (var index = 0; index < colors.Length; index++)
        {
            ref readonly var color = ref colors[index];
            var format = rendering.ColorAttachments[index].Format;
            if (!color.Image.IsValid || format == Format.Undefined)
            {
                throw SubmissionScheduler.Fatal($"A color attachment has no image or format: slot={color.Slot} format={format}.");
            }

            renderingState.ColorFormats[index] = format;
            // A target the pixel program never exports keeps its contents, as on hardware; the
            // host output would otherwise write an undefined value (e.g. depth-only passes that
            // leave a color target bound and export only to the null target).
            var exportTarget = color.Slot == 0 && context.BlendControls[0].UsesSecondarySource &&
                (context.ColorTargets[0].Info & (1u << 16)) == 0
                ? 0 : PixelExportRouting.ExportForSlot(context.ShaderInterface, color.Slot);
            var exported = pixelStage is not null &&
                (exportTarget >= 0 && ((pixelStage.PixelColorExportMasks >> (exportTarget * 4)) & 0xFu) != 0);
            var colorMask = exported ? color.Resolution.ExportMapping.ApplyMask(context.RenderTargetMaskForSlot(color.Slot)) : 0;
            parameters.SetColorMask(index, colorMask);
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"PipelineCache output slot={color.Slot} export={exportTarget} " +
                    $"guestMask=0x{context.RenderTargetMaskForSlot(color.Slot):X} " +
                    $"exported={(exported ? 1 : 0)} shaderMask=0x{pixelStage?.PixelColorExportMasks ?? 0:X8} " +
                    $"mapping=0x{color.Resolution.ExportMapping.Packed:X2} hostMask=0x{colorMask:X} format={(int)format}");
            }
        }

        var withDepth = depth.HasTarget;
        if (withDepth)
        {
            renderingState.DepthFormat = rendering.DepthFormat;
            renderingState.StencilFormat = rendering.StencilFormat;
        }

        var samples = rendering.Samples;
        if (colorCount == 0 && !withDepth)
        {
            samples = 1u << context.AntialiasingConfig.SampleCountLog2;
            var flag = ImageDescription.VulkanSampleCount(samples);
            if (flag == 0 || (noAttachmentSampleCounts & flag) == 0)
            {
                throw SubmissionScheduler.Fatal($"The device cannot rasterize without attachments at the draw's sample count: samples={samples}.");
            }
        }

        if (samples == 0 || ImageDescription.VulkanSampleCount(samples) == 0)
        {
            throw SubmissionScheduler.Fatal($"The draw's sample count is invalid: samples={samples}.");
        }

        var depthState = withDepth ? depth.Target.State : default;
        // A patch list without tessellation stages carries legacy rectangles, which are never
        // culled. Tessellated patches rasterize as ordinary triangles and keep the guest's culling.
        var rectangleList = topology == PrimitiveTopology.PatchList && vertexInput.Tessellation is null;
        var mode = context.RasterMode;
        parameters.NegativeOneToOne = !context.Clip.DirectXClipSpace;
        parameters.DepthClipEnable = context.Clip.IsZClipEnabled;
        parameters.Topology = topology;
        parameters.PrimitiveRestartEnable = primitiveRestartEnabled;
        parameters.Samples = samples;
        parameters.SampleShadingEnable = pixelActive && samples > 1 && pixelInput!.SampleShading;
        parameters.WithDepth = withDepth;
        parameters.DepthBoundsTestEnable = depthState.DepthBoundsTestEnabled;
        parameters.StencilTestEnable = depthState.StencilTestEnabled;
        parameters.StencilFront = withDepth ? depthState.FrontOperations : StencilOperations.Default;
        parameters.StencilBack = withDepth ? depthState.BackOperations : StencilOperations.Default;
        parameters.CullBack = !rectangleList && mode.CullBack;
        parameters.CullFront = !rectangleList && mode.CullFront;
        parameters.FrontFaceClockwise = mode.FrontFaceClockwise;
        for (var index = 0; index < colors.Length; index++)
        {
            var slot = colors[index].Slot;
            var blend = context.BlendControls[slot];
            var dualSource = colors.Length == 1 && pixelActive &&
                (pixelInput!.TargetOutputModes[slot] & DualSourceAlphaOutputMode) != 0;
            byte Factor(byte factor) => dualSource ? ToDualSourceFactor(factor) : factor;
            parameters.SetColorSourceBlend(index, Factor(blend.ColorSourceFactor));
            parameters.SetColorBlendFunction(index, (byte)blend.ColorFunction);
            parameters.SetColorDestinationBlend(index, Factor(blend.ColorDestinationFactor));
            parameters.SetAlphaSourceBlend(index, Factor(blend.AlphaSourceFactor));
            parameters.SetAlphaBlendFunction(index, (byte)blend.AlphaFunction);
            parameters.SetAlphaDestinationBlend(index, Factor(blend.AlphaDestinationFactor));
            parameters.SetSeparateAlphaBlend(index, blend.SeparateAlpha);
            parameters.SetBlendEnable(index, blend.Enable && !disableBlending);
            parameters.SetBlendBypass(index, ((context.ColorTargets[slot].Info >> 16) & 0x1) != 0);
        }

        FillVertexInputState(vertexInput, vertexState);
        return (vertexStage, pixelStage);
    }

    internal static GraphicsPipelineKey KeyOf(GraphicsPipelineDescription description) => new()
    {
        Rendering = description.Rendering,
        VertexProgramId = description.VertexProgram.Id,
        PixelProgramId = description.PixelInfo is not null ? description.PixelProgram.Id : 0,
        Tessellation = description.Tessellation,
        VertexInput = description.VertexInput,
        StaticParameters = description.StaticParameters,
    };

    private static void FillVertexInputState(VertexInputInfo info, PipelineVertexInputState state)
    {
        if (info.Buffers.Length > VertexInputInfo.MaxBuffers || info.Attributes.Length > VertexInputInfo.MaxBuffers)
        {
            throw SubmissionScheduler.Fatal($"The vertex input is too large: buffers={info.Buffers.Length} attributes={info.Attributes.Length}.");
        }

        state.BindingCount = (byte)info.Buffers.Length;
        state.AttributeCount = (byte)info.Attributes.Length;
        for (var binding = 0; binding < info.Buffers.Length; binding++)
        {
            var buffer = info.Buffers[binding];
            state.Bindings[binding] = new PipelineVertexBinding(buffer.Stride, buffer.PerInstance);
        }

        for (var index = 0; index < info.Attributes.Length; index++)
        {
            var attribute = info.Attributes[index];
            if (attribute.BufferIndex < 0 || attribute.BufferIndex >= info.Buffers.Length)
            {
                throw SubmissionScheduler.Fatal($"The vertex attribute names a buffer outside the input: attribute={index} buffer={attribute.BufferIndex} buffers={info.Buffers.Length}.");
            }

            state.Attributes[index] = new PipelineVertexAttribute(attribute.OffsetBytes, (byte)attribute.BufferIndex);
        }
    }

    public PipelineHandle CreateComputePipeline(ComputeInputInfo input, ShaderProgram program)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineCreation);
        if (!program.IsValid)
        {
            throw SubmissionScheduler.Fatal("The dispatch has no compute program.");
        }

        var stage = input.Stage.Program ?? throw SubmissionScheduler.Fatal("The compute stage has no program.");
        var key = new ComputePipelineKey(program.Id);
        if (_computePipelines.TryGetValue(key, out var known))
        {
            return known;
        }

        profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineSharedGateWait);
        lock (_pipelineGate)
        {
            profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineCreation);
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"PipelineCache create compute program=0x{program.Id:X16} hash=0x{stage.Hash:X16}");
            }

            var created = _host.CreateComputePipeline(new ComputePipelineDescription { Input = input, Program = program, Stage = stage });
            lock (_pipelineGate)
            {
                _computePipelines.TryAdd(key, created);
            }

            ShaderCacheCounters.CountComputePipeline();
            return created;
        }
    }

    public bool TryCreateComputePipeline(ComputeInputInfo input, ShaderProgram program, out PipelineHandle handle)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PipelineCreation);
        handle = default;
        if (!program.IsValid)
        {
            throw SubmissionScheduler.Fatal("The dispatch has no compute program.");
        }

        var stage = input.Stage.Program ?? throw SubmissionScheduler.Fatal("The compute stage has no program.");
        var key = new ComputePipelineKey(program.Id);
        if (_computePipelines.TryGetValue(key, out var known))
        {
            handle = known;
            return true;
        }

        profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineSharedGateWait);
        lock (_pipelineGate)
        {
            profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineCreation);
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                handle = cached;
                return true;
            }

            if (!_host.TryCreateComputePipeline(
                    new ComputePipelineDescription { Input = input, Program = program, Stage = stage },
                    out var created))
            {
                return false;
            }

            lock (_pipelineGate)
            {
                _computePipelines.TryAdd(key, created);
            }

            ShaderCacheCounters.CountComputePipeline();
            handle = created;
            return true;
        }
    }

    public Task? GetComputePipelineCompletion(ShaderProgram program)
    {
        lock (_pipelineGate)
        {
            return _host.GetComputePipelineCompletion(program);
        }
    }
}

internal sealed class KeptGraphicsPrograms
{
    public GraphicsPrograms? Programs;
    public ulong ProgramsStamp;
    public ulong VertexUserDataStamp;
    public ulong PixelUserDataStamp;
    public ulong InterfaceStamp;
    public ulong PipelineStateStamp;
    public uint OutputBlend;
    public long Registrations;
    public bool PixelActive;
    public bool DepthBound;
    public int StartInstanceUserData;
    public int ExportMappingCount;
    public readonly ColorComponentMap[] ExportMapping = new ColorComponentMap[RenderingState.ColorAttachmentCapacity];
}

internal sealed class KeptComputeProgram
{
    public ComputeProgram? Program;
    public ComputeStageRegisters? Registers;
    public ShaderSource? Source;
    public Gen5ComputeSystemRegisters SystemRegisters;
    public ProgramSourceEntry? Entry;
    public ShaderPipelineCache.KernelDemand? Kernel;
    public long Rebinds;
    public long Registrations;
    public uint DispatchInitiator;
    public uint DimensionX;
    public uint DimensionY;
    public uint DimensionZ;
}

internal sealed class GraphicsPairStage
{
    public ProgramSourceEntry? Entry;
    public object? StaticInputs;
    public ulong Hash;
    public int UserDataCount;
    public int RequiredOutputs;
    public int StartInstance;
    public bool IeeeMode;
    public long Registrations;
}

// One vertex/pixel program pair's memos, so pairs that alternate keep theirs.
internal sealed class GraphicsPair
{
    public readonly KeptUserData VertexUserData = new();
    public readonly KeptUserData PixelUserData = new();
    public readonly KeptVertexInput VertexInput = new();
    public readonly KeptPixelInput PixelInput = new();
    public readonly GraphicsPairStage Vertex = new();
    public readonly GraphicsPairStage Pixel = new();
    public ShaderHeaderEntry? VertexHeader;
    public ShaderHeaderEntry? PixelHeader;
}

internal sealed class KeptUserData
{
    public ulong UserDataStamp;
    public ulong ProgramsStamp;
    public uint[]? Words;
}

internal sealed class KeptVertexInput
{
    public ShaderHeaderEntry? Header;
    public VertexTableWords? Words;
    public uint PositionExportControl;
    public ClipSpaceTransform ClipSpace;
    public VertexInputInfo? Info;
}

internal sealed class KeptPixelInput
{
    public PixelInputKey Key;
    public PixelInputInfo? Info;
    public Gen5PixelOutputBinding[] Outputs = [];
    public uint AttributeCount;
    public bool DropUnwritten;
}

internal readonly record struct PixelInputKey(
    ulong InterfaceStamp,
    uint OutputBlend,
    object Header,
    long Registrations,
    uint ScratchDwords,
    ulong Hash,
    ulong ExportMappings,
    int ExportMappingCount,
    bool DepthBound);
