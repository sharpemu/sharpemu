// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.ShaderCache;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Gpu.Pipelines;

// The pipeline provider behind the executor: programs by identity, pipelines by their full static state.
internal sealed partial class ShaderPipelineCache : IShaderPipelineProvider
{
    private const uint VertexUserDataBase = 8;
    private const uint MaxPixelInputs = 32;
    private const uint MaxViewportDimension = 16384;

    private readonly CpuContext _context;
    private readonly IShaderPipelineHost _host;
    private readonly ShaderHeaderRegistry _registry;
    private readonly ShaderProgramCache _programs;
    private readonly Dictionary<GraphicsPipelineKey, PipelineHandle> _graphicsPipelines = new();
    private readonly Dictionary<ComputePipelineKey, PipelineHandle> _computePipelines = new();
    private readonly Dictionary<Gen5ShaderProgram, bool> _rayTracingComputePrograms = new();
    private readonly object _gate = new();
    // The pipeline maps: a lookup takes this lock or _gate, an addition both; a cached draw so does
    // not wait for the program preparation a resolving thread holds _gate for.
    private readonly object _pipelineGate = new();
    private readonly bool _strictShaders = Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") != "0";
    private readonly bool _skipRayTracing = Environment.GetEnvironmentVariable("SHARPEMU_SKIP_RT") == "1";
    private readonly HashSet<(ShaderStage Stage, ulong Hash, uint CodeSize)> _reportedShaderSkips = [];
    private readonly HashSet<(uint Stages, uint PrimitiveType, uint MaxOutput, uint MaxVerticesOut)> _reportedMeshSkips = [];

    public ShaderPipelineCache(CpuContext context, IShaderPipelineHost host, IGuestGpuBackend compiler, ShaderHeaderRegistry registry)
    {
        _context = context;
        _host = host;
        _registry = registry;
        _programs = new ShaderProgramCache(context, compiler, host);
    }

    public ShaderProgramCache Programs => _programs;

    public bool MeshShadersSupported => _host.MeshShadersSupported;

    public int GraphicsPipelineCount => _graphicsPipelines.Count;

    public int ComputePipelineCount => _computePipelines.Count;

    // The user data of one stage: the count from its resource register, else the registers the guest wrote.
    private static uint[] UserData(UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, ulong shaderAddress, string label,
        bool includeGeometryPointer = false, ulong geometryPointer = 0)
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

        var prefix = includeGeometryPointer ? (int)VertexUserDataBase : 0;
        var values = new uint[prefix + (int)count];
        registers.Values.AsSpan(0, (int)count).CopyTo(values.AsSpan(prefix));
        if (includeGeometryPointer)
        {
            values[0] = (uint)geometryPointer;
            values[1] = (uint)(geometryPointer >> 32);
        }
        return values;
    }

    private ShaderSource PrepareSource(ulong codeAddress, ShaderStage stage, string label, UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, uint userDataBase,
        ulong geometryPointer = 0)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramSourceRead);
        var registered = _registry.Require(codeAddress, label);
        var hash = ShaderIdentity.Compute(_context.Memory, codeAddress, registered.CodeRanges, label);
        var includeGeometryPointer = stage == ShaderStage.Mesh && registered.IsFused;
        var userData = UserData(registers, declaredCount, probeWrittenRegisters, codeAddress, label, includeGeometryPointer, geometryPointer);
        return new ShaderSource(registered, hash, userData, includeGeometryPointer ? 0 : userDataBase, stage);
    }

    // Program resolution runs on the render thread and on a thread resolving draws ahead of it;
    // the program, decode and materialization caches it updates are used under this lock only.
    // A pipeline lookup takes _pipelineGate instead, so it does not wait for a resolution.
    private readonly object _resolveGate = new();

    public GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound)
        => GetGraphicsProgramsCore(vertex, pixel, shaderInterface, context,
            targetExportMapping, pixelActive, depthBound, null);

    public GraphicsPrograms GetMeshGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        UserConfigRegisters userConfig,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound)
    {
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
            lock (_gate)
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
        return GetGraphicsProgramsCore(vertex, pixel, shaderInterface, context,
            targetExportMapping, pixelActive, depthBound, mesh);
    }

    private GraphicsPrograms GetGraphicsProgramsCore(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        MeshDrawConfiguration? mesh)
    {
        using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.GraphicsResolveLockWait);
        lock (_resolveGate)
        {
            using var workScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
            return ResolveGraphicsProgramsCore(vertex, pixel, shaderInterface, context, targetExportMapping, pixelActive, depthBound, mesh);
        }
    }

    private GraphicsPrograms ResolveGraphicsProgramsCore(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound,
        MeshDrawConfiguration? mesh)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var vertexSource = PrepareSource(
            vertex.ExportAddress, mesh is null ? ShaderStage.Vertex : ShaderStage.Mesh,
            mesh is null ? "vertex" : "mesh", vertex.GeometryUserScalars, vertex.GeometryResource2.UserScalarCount,
            probeWrittenRegisters: true, VertexUserDataBase, vertex.GeometryUserDataAddress);
        var vertexInfo = mesh is null ? PrepareVertexInput(vertexSource, shaderInterface, context) : new VertexInputInfo();
        vertexInfo.IeeeMode = vertex.GeometryResource1.IeeeMode;
        ShaderSource? pixelSource = null;
        PixelInputInfo? pixelInfo = null;
        Gen5PixelOutputBinding[] pixelOutputs = [];
        var dropUnwritten = false;
        var attributeCount = 0u;
        if (pixelActive)
        {
            pixelSource = PrepareSource(
                pixel.Address, ShaderStage.Pixel, "pixel", pixel.UserScalars, pixel.Resource2.UserScalarCount,
                probeWrittenRegisters: true, userDataBase: 0);
            if (RenderTrace.Enabled)
                RenderTrace.Write($"PixelDrawState shader=0x{pixelSource.Address:X16} hash=0x{pixelSource.Hash:X16} " +
                    $"user_data=[{string.Join(",", pixelSource.UserData.Select(word => $"{word:X8}"))}]");
            using var pixelProfile = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.PixelInputResolution);
            var pixelProgram = _programs.Decode(pixelSource);
            var attributes = GetPixelAttributes(pixelProgram);
            attributeCount = attributes.Length == 0 ? 0 : attributes[^1] + 1;
            var inputCount = shaderInterface.PixelInputControl & 0x3Fu;
            if (inputCount == 0)
            {
                inputCount = Math.Min(attributeCount, MaxPixelInputs);
            }

            if (inputCount > MaxPixelInputs)
            {
                throw SubmissionScheduler.Fatal($"The pixel program declares too many interpolators: shader=0x{pixel.Address:X16} count={inputCount}.");
            }

            dropUnwritten = depthBound ||
                LeavesSingleDualSourceTarget(context, targetExportMapping, pixelProgram.PixelColorExportMasks) ||
                UnwrittenTargetBoundsRenderArea(context, targetExportMapping, pixelProgram.PixelColorExportMasks);
            pixelOutputs = ResolveBoundTargets(context, targetExportMapping, dropUnwritten ? pixelProgram.PixelColorExportMasks : null,
                out var outputModes, out var outputMappings);
            pixelInfo = PixelStageInputResolver.Resolve(_context, pixelSource.Registered, shaderInterface, outputModes, outputMappings, inputCount);
            // SPI_PS_INPUT_CNTL can map an input to any parameter export, beyond the input count;
            // the vertex program must declare every location the pixel program reads.
            attributeCount = Math.Max(attributeCount, ReadVertexOutputCount(attributes, pixelInfo));
            pixelInfo.IeeeMode = pixel.Resource1.IeeeMode;
        }

        ShaderProgram vertexProgram;
        ShaderProgram pixelProgramHandle = default;
        ShaderStageResources vertexStage;
        ShaderStageResources pixelStage = default;
        profileScope.SwitchPhase(RenderPhaseProfile.Phase.ProgramSharedGateWait);
        lock (_gate)
        {
            profileScope.SwitchPhase(RenderPhaseProfile.Phase.ProgramPreparation);
            var pushDataCursor = 0u;
            if (pixelSource is not null)
            {
                if (!TryPrepareProgram(
                    pixelSource,
                    new StageCompileOptions
                    {
                        PixelInfo = pixelInfo,
                        PixelOutputs = pixelOutputs,
                        PixelInputEnable = shaderInterface.PixelInputEnable,
                        PixelInputAddress = shaderInterface.PixelInputAddress,
                    },
                    ref pushDataCursor,
                    out pixelProgramHandle,
                    out pixelStage))
                    return new GraphicsPrograms { Available = false };
            }

            if (!TryPrepareProgram(
                vertexSource,
                mesh is null
                    ? new StageCompileOptions { VertexInfo = vertexInfo, RequiredVertexOutputCount = (int)attributeCount }
                    : new StageCompileOptions
                    {
                        MeshInfo = mesh, RequiredVertexOutputCount = (int)attributeCount,
                        MeshOutputLocationMask = pixelSource is null ? 0u : pixelStage.Program?.InputLocationMask,
                    },
                ref pushDataCursor,
                out vertexProgram,
                out vertexStage))
                return new GraphicsPrograms { Available = false };
        }

        vertexInfo.Stage = vertexStage;
        if (pixelInfo is not null)
        {
            pixelInfo.Stage = pixelStage;
        }

        SolidColorClear? solidClear = null;
        var disableBlending = false;
        if (mesh is null && pixelInfo is not null)
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
                disableBlending = IsTransparentPremultipliedFill(context, pixelOutputs, vertexInfo, vertexStage, pixelStage);
            }
        }

        return new GraphicsPrograms
        {
            Vertex = vertexProgram,
            Pixel = pixelProgramHandle,
            VertexInput = vertexInfo,
            MeshInput = mesh,
            PixelInput = pixelInfo ?? new PixelInputInfo(),
            SolidClear = solidClear,
            DisableBlending = disableBlending,
            DropUnwrittenColorTargets = dropUnwritten,
            PositionStream = mesh is null ? FindPositionStream(vertexInfo) : null,
        };
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
        ref readonly var viewport = ref context.ScreenViewport.Viewports[0];
        var limits = _host.Limits;
        return new ClipSpaceTransform(true, viewport.XScale, viewport.YScale,
            viewport.XOffset, viewport.YOffset,
            Math.Min(limits.MaxViewportWidth, MaxViewportDimension) * 0.5f,
            Math.Min(limits.MaxViewportHeight, MaxViewportDimension) * 0.5f);
    }

    // The vertex tables of the draw and the clip-space transform when clipping is off.
    private VertexInputInfo PrepareVertexInput(ShaderSource source, ShaderInterfaceRegisters shaderInterface, ContextRegisters context)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VertexInputResolution);
        return VertexInputResolver.ResolveVertexInputs(_context, source.Registered, source.UserData,
            shaderInterface.VertexOutputControl, ResolveClipSpace(context));
    }

    // The interpolated attributes a pixel program reads, ascending. A program never changes, and this
    // was rebuilt with LINQ on every draw.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Gen5ShaderProgram, uint[]> InterpolatedAttributeCache = new();

    internal uint[] GetPixelAttributes(Gen5ShaderProgram program) => PixelAttributes(program);

    internal static uint[] PixelAttributes(Gen5ShaderProgram program) =>
        InterpolatedAttributeCache.GetValue(program, static program => program.Instructions
            .Select(static instruction => instruction.Control)
            .OfType<Gen5InterpolationControl>()
            .Select(static control => control.Attribute)
            .Distinct()
            .Order()
            .ToArray());

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
        uint? unwrittenExportMasks, out byte[] outputModes, out ColorComponentMap[] outputMappings)
    {
        outputModes = new byte[PixelInputInfo.TargetCount];
        outputMappings = new ColorComponentMap[PixelInputInfo.TargetCount];
        var outputs = new List<Gen5PixelOutputBinding>(ContextRegisters.ColorTargetCount);
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
            outputModes[slot] = (byte)(((uint)kind + 1) | (uint)((exportTarget + 1) << 4));
            outputMappings[slot] = new ColorComponentMap(mapping.Packed);
            // No EXP target reaches 8 and above, so an unfed slot is declared but never written.
            outputs.Add(new Gen5PixelOutputBinding(slot, location++, kind, mapping)
            {
                ExportTarget = exportTarget >= 0 ? (uint)exportTarget : ContextRegisters.ColorTargetCount + slot,
            });
        }

        if (context.BlendControls[0].UsesSecondarySource && (context.ColorTargets[0].Info & (1u << 16)) == 0 &&
            outputs.Any(output => output.GuestSlot == 0))
        {
            if (outputs.Count != 1 || outputs[0].Kind != Gen5PixelOutputKind.Float)
            {
                throw SubmissionScheduler.Fatal("Dual-source blending requires one floating-point color output.");
            }
            if (NeedsDualSourceAlpha(context, 0, outputMappings[0]))
            {
                throw SubmissionScheduler.Fatal("Dual-source blending cannot also export an unmapped source alpha.");
            }

            // EXP1 supplies the second source for target zero, not a second attachment.
            outputs[0] = outputs[0] with { ExportTarget = 0 };
            outputs.Add(outputs[0] with { GuestSlot = 1, ExportTarget = 1, Index = 1 });
            outputModes[0] = (byte)(((uint)outputs[0].Kind + 1) | 0x10u);
            outputModes[1] = DualSourceOutputMode;
        }

        // Dual-source blending is limited to a single attachment.
        if (outputs.Count == 1 &&
            _host.SupportsDualSourceBlend &&
            outputs[0].Kind == Gen5PixelOutputKind.Float &&
            NeedsDualSourceAlpha(context, outputs[0].GuestSlot, outputMappings[outputs[0].GuestSlot]))
        {
            outputs[0] = outputs[0] with { DualSourceAlpha = true };
            outputModes[outputs[0].GuestSlot] |= DualSourceAlphaOutputMode;
        }

        return outputs.ToArray();
    }

    private const byte DualSourceOutputMode = 0xFF;

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

    public ComputeProgram GetComputeProgram(
        ComputeStageRegisters compute,
        ShaderInterfaceRegisters shaderInterface,
        uint dispatchInitiator,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ)
    {
        using var waitScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ComputeResolveLockWait);
        lock (_resolveGate)
        {
            using var workScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
            return GetComputeProgramCore(compute, shaderInterface, dispatchInitiator, dimensionX, dimensionY, dimensionZ);
        }
    }

    private ComputeProgram GetComputeProgramCore(
        ComputeStageRegisters compute,
        ShaderInterfaceRegisters shaderInterface,
        uint dispatchInitiator,
        uint dimensionX,
        uint dimensionY,
        uint dimensionZ)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var source = PrepareSource(compute.Address, ShaderStage.Compute, "compute", compute.UserScalars, compute.UserScalarCount, probeWrittenRegisters: false, userDataBase: 0);
        var program = _programs.Decode(source);
        if (_skipRayTracing)
        {
            profileScope.SwitchPhase(RenderPhaseProfile.Phase.ProgramSharedGateWait);
            lock (_gate)
            {
                profileScope.SwitchPhase(RenderPhaseProfile.Phase.ProgramPreparation);
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
        var systemRegisters = DecodeComputeSystemRegisters(compute);
        if (TrySubmitMaskedDwordCopyKernel(program, source, systemRegisters, input, out var description))
        {
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write($"Consumed a masked dword copy dispatch: shader=0x{compute.Address:X16} hash=0x{source.Hash:X16} {description}");
            }

            return new ComputeProgram { Consumed = true };
        }

        ShaderProgram handle;
        ShaderStageResources stage;
        profileScope.SwitchPhase(RenderPhaseProfile.Phase.ProgramSharedGateWait);
        lock (_gate)
        {
            profileScope.SwitchPhase(RenderPhaseProfile.Phase.ProgramPreparation);
            var pushDataCursor = 0u;
            if (!TryPrepareProgram(
                source,
                new StageCompileOptions { ComputeInfo = input, ComputeSystemRegisters = systemRegisters },
                ref pushDataCursor,
                out handle,
                out stage))
            {
                return new ComputeProgram { Available = false };
            }
        }

        input.Stage = stage;
        return new ComputeProgram { Program = handle, Input = input };
    }

    private bool TryPrepareProgram(ShaderSource source, StageCompileOptions options, ref uint pushDataCursor,
        out ShaderProgram program, out ShaderStageResources stage)
    {
        if (_programs.TryGetProgram(source, options, _strictShaders, ref pushDataCursor, out program, out stage, out var rejection))
            return true;

        if (_reportedShaderSkips.Add((source.Stage, source.Hash, source.CodeSize)))
        {
            var tag = source.Stage == ShaderStage.Compute ? "COMPUTE_SKIPPED" : "DRAW_SKIPPED";
            Console.Error.WriteLine($"[GPU][WARN][{tag}] {rejection} " +
                "The operation was not executed. Images and FPS can be incorrect. Set SHARPEMU_STRICT_COMPUTE=1 to stop on this failure.");
        }
        return false;
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
        lock (_pipelineGate)
        {
            if (_graphicsPipelines.TryGetValue(lookup, out var cached))
            {
                return cached;
            }
        }

        var description = BuildGraphicsDescription(
            colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
            vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts);
        var key = KeyOf(description);
        profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineSharedGateWait);
        lock (_gate)
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
                _graphicsPipelines.Add(key, created);
            }

            ShaderCacheCounters.CountGraphicsPipeline();
            RecordGraphicsPipeline(description);
            return created;
        }
    }

    private void RecordGraphicsPipeline(GraphicsPipelineDescription description)
    {
        if (_host.ShaderCache is not { } cache ||
            !_programs.TryGetStageRecord(description.VertexProgram.Id, out var vertex, out var vertexCode))
        {
            return;
        }

        StageRecord? pixel = null;
        ShaderCodeCapture? pixelCode = null;
        if (description.PixelStage is not null &&
            !_programs.TryGetStageRecord(description.PixelProgram.Id, out pixel, out pixelCode))
        {
            return;
        }

        var attributes = new VertexAttributeFormat[description.VertexInput.AttributeCount];
        for (var index = 0; index < attributes.Length; index++)
        {
            var attribute = description.VertexInfo.Attributes[index];
            attributes[index] = new VertexAttributeFormat(attribute.Descriptor, attribute.RegisterCount);
        }

        cache.RecordGraphics(
            new GraphicsPipelineRecord
            {
                Vertex = vertex,
                Pixel = pixel,
                Rendering = description.Rendering,
                VertexInput = description.VertexInput,
                StaticParameters = description.StaticParameters,
                Attributes = attributes,
            },
            vertexCode,
            pixelCode);
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
        var rectangleList = topology == PrimitiveTopology.PatchList;
        var mode = context.RasterMode;
        parameters.NegativeOneToOne = !context.Clip.DirectXClipSpace;
        parameters.DepthClipEnable = context.Clip.IsZClipEnabled;
        parameters.Topology = topology;
        parameters.PrimitiveRestartEnable = primitiveRestartEnabled;
        parameters.Samples = samples;
        parameters.SampleShadingEnable = pixelActive && samples > 1 && pixelInput!.SampleShading;
        parameters.WithDepth = withDepth;
        parameters.DepthBoundsTestEnable = depthState.DepthBoundsTestEnabled;
        parameters.DepthMinBounds = depthState.DepthMinBounds;
        parameters.DepthMaxBounds = depthState.DepthMaxBounds;
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
        lock (_pipelineGate)
        {
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineSharedGateWait);
        lock (_gate)
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
                _computePipelines.Add(key, created);
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
        lock (_pipelineGate)
        {
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                handle = cached;
                return true;
            }
        }

        profileScope.SwitchPhase(RenderPhaseProfile.Phase.PipelineSharedGateWait);
        lock (_gate)
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
                _computePipelines.Add(key, created);
            }

            ShaderCacheCounters.CountComputePipeline();
            handle = created;
            return true;
        }
    }

    public Task? GetComputePipelineCompletion(ShaderProgram program)
    {
        lock (_gate)
        {
            return _host.GetComputePipelineCompletion(program);
        }
    }
}
