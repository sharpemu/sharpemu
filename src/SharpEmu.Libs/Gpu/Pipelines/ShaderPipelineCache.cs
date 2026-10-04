// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands.Registers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Logging;
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
    private static readonly SharpEmuLogger GeometryLog = SharpEmuLog.For("GPU.Geometry");
    private static readonly bool LogGeometryUserData =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_GEOMETRY"),
            "1",
            StringComparison.Ordinal) ||
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_LOG_LEGACY_FUSED_GEOMETRY"),
            "1",
            StringComparison.Ordinal);

    private readonly CpuContext _context;
    private readonly IShaderPipelineHost _host;
    private readonly ShaderHeaderRegistry _registry;
    private readonly ShaderProgramCache _programs;
    private readonly Dictionary<GraphicsPipelineKey, PipelineHandle> _graphicsPipelines = new();
    private readonly Dictionary<ComputePipelineKey, PipelineHandle> _computePipelines = new();
    // This mutable key is only a dictionary probe under _gate. It is never inserted;
    // cache misses clone it so keys already owned by the dictionary remain immutable.
    private readonly GraphicsPipelineKey _graphicsPipelineLookup = new()
    {
        Rendering = new PipelineRenderingState(),
        VertexInput = new PipelineVertexInputState(),
        StaticParameters = new PipelineStaticParameters(),
    };
    private readonly object _gate = new();
    private readonly HashSet<(ulong Shader, ulong RegisterAddress, ulong HeaderAddress)> _reportedFusedVertexUserData = [];
    private readonly bool _strictShaders = Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") != "0";
    private readonly HashSet<(ShaderStage Stage, ulong Hash, uint CodeSize)> _reportedShaderSkips = [];
    // ShaderSource owns these arrays only until resource materialization snapshots them.
    // Keep one exact-length buffer per stage so pixel and vertex sources never alias while
    // a graphics draw is being assembled.
    private uint[] _vertexUserData = [];
    private uint[] _pixelUserData = [];
    private uint[] _computeUserData = [];

    public ShaderPipelineCache(CpuContext context, IShaderPipelineHost host, IGuestGpuBackend compiler, ShaderHeaderRegistry registry)
    {
        _context = context;
        _host = host;
        _registry = registry;
        _programs = new ShaderProgramCache(context, compiler, host);
    }

    public ShaderProgramCache Programs => _programs;

    public int GraphicsPipelineCount => _graphicsPipelines.Count;

    public int ComputePipelineCount => _computePipelines.Count;

    public bool IsFusedGraphicsProgram(ulong codeAddress) => _registry.IsFused(codeAddress);

    public bool TryGetFusedGraphicsProgram(ulong codeAddress, out ulong continuationAddress) =>
        _registry.TryGetFusedContinuation(codeAddress, out continuationAddress);

    public bool SupportsFusedGeometry => _host.MeshShadersSupported;

    // The user data of one stage: the count from its resource register, else the registers the guest wrote.
    private static uint UserDataCount(
        UserScalarRegisters registers,
        uint declaredCount,
        bool probeWrittenRegisters,
        ulong shaderAddress,
        string label)
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

        return count;
    }

    private static uint[] UserData(
        UserScalarRegisters registers,
        uint declaredCount,
        bool probeWrittenRegisters,
        ulong shaderAddress,
        string label,
        ref uint[] destination)
    {
        var count = UserDataCount(registers, declaredCount, probeWrittenRegisters, shaderAddress, label);
        Array.Resize(ref destination, (int)count);
        registers.Values.AsSpan(0, (int)count).CopyTo(destination);
        return destination;
    }

    private ShaderSource PrepareSource(ulong codeAddress, ShaderStage stage, string label, UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, uint userDataBase)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramSourceRead);
        var registered = _registry.Require(codeAddress, label);
        var hash = ShaderIdentity.Compute(_context.Memory, registered, label);
        var userData = stage switch
        {
            ShaderStage.Pixel => UserData(
                registers,
                declaredCount,
                probeWrittenRegisters,
                codeAddress,
                label,
                ref _pixelUserData),
            ShaderStage.Compute => UserData(
                registers,
                declaredCount,
                probeWrittenRegisters,
                codeAddress,
                label,
                ref _computeUserData),
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "The generic source path has no user-data scratch storage."),
        };
        return new ShaderSource(registered, hash, userData, userDataBase, stage);
    }

    // A fused ES+GS program shares one scalar file. The GS continuation receives the
    // live shader-resource-table pointer in s[0:1], while the ES entry's ordinary
    // GS-bank user data moves to s8 and above. Older command streams do not always
    // program the live pointer, so retain the shader header as the zero-value fallback.
    internal static (uint[] UserData, uint UserDataBase) PrepareVertexUserData(
        RegisteredShader registered,
        VertexStageRegisters vertex)
    {
        uint[] destination = [];
        return PrepareVertexUserData(registered, vertex, ref destination);
    }

    private static (uint[] UserData, uint UserDataBase) PrepareVertexUserData(
        RegisteredShader registered,
        VertexStageRegisters vertex,
        ref uint[] destination)
    {
        if (!registered.IsFused)
        {
            return (
                UserData(
                    vertex.GeometryUserScalars,
                    vertex.GeometryResource2.UserScalarCount,
                    probeWrittenRegisters: true,
                    registered.CodeAddress,
                    "vertex",
                    ref destination),
                VertexUserDataBase);
        }

        var frontUserDataCount = UserDataCount(
            vertex.GeometryUserScalars,
            vertex.GeometryResource2.UserScalarCount,
            probeWrittenRegisters: true,
            registered.CodeAddress,
            "vertex front");
        var continuationUserDataAddress = vertex.GeometryUserDataAddress != 0
            ? vertex.GeometryUserDataAddress
            : registered.ContinuationUserDataAddress;
        Array.Resize(ref destination, checked((int)VertexUserDataBase + (int)frontUserDataCount));
        destination.AsSpan().Clear();
        destination[0] = (uint)continuationUserDataAddress;
        destination[1] = (uint)(continuationUserDataAddress >> 32);
        vertex.GeometryUserScalars.Values.AsSpan(0, (int)frontUserDataCount)
            .CopyTo(destination.AsSpan((int)VertexUserDataBase));
        return (destination, 0);
    }

    private ShaderSource PrepareVertexSource(VertexStageRegisters vertex)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramSourceRead);
        var registered = _registry.Require(vertex.ExportAddress, "vertex");
        var hash = ShaderIdentity.Compute(_context.Memory, registered, "vertex");
        var (userData, userDataBase) = PrepareVertexUserData(registered, vertex, ref _vertexUserData);
        if (registered.IsFused)
        {
            var selectedAddress = vertex.GeometryUserDataAddress != 0
                ? vertex.GeometryUserDataAddress
                : registered.ContinuationUserDataAddress;
            var traceKey = (
                vertex.ExportAddress,
                vertex.GeometryUserDataAddress,
                registered.ContinuationUserDataAddress);
            if (RenderTrace.Enabled)
            {
                RenderTrace.Write(
                    $"FusedVertexUserData shader=0x{vertex.ExportAddress:X16} " +
                    $"register_address=0x{vertex.GeometryUserDataAddress:X16} " +
                    $"header_address=0x{registered.ContinuationUserDataAddress:X16} " +
                    $"selected_address=0x{selectedAddress:X16} " +
                    $"source={(vertex.GeometryUserDataAddress != 0 ? "register" : "header-fallback")}");
            }

            if (LogGeometryUserData)
            {
                lock (_gate)
                {
                    if (_reportedFusedVertexUserData.Add(traceKey))
                    {
                        GeometryLog.Info(
                            $"FusedVertexUserData shader=0x{vertex.ExportAddress:X16} " +
                            $"register_address=0x{vertex.GeometryUserDataAddress:X16} " +
                            $"header_address=0x{registered.ContinuationUserDataAddress:X16} " +
                            $"selected_address=0x{selectedAddress:X16} " +
                            $"source={(vertex.GeometryUserDataAddress != 0 ? "register" : "header-fallback")}");
                    }
                }
            }
        }
        return new ShaderSource(registered, hash, userData, userDataBase, ShaderStage.Vertex);
    }

    public GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        UserConfigRegisters userConfig,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive,
        bool depthBound) =>
        GetGraphicsPrograms(
            vertex,
            pixel,
            shaderInterface,
            context,
            userConfig,
            targetExportMapping,
            uint.MaxValue,
            pixelActive,
            depthBound);

    public GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        UserConfigRegisters userConfig,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        uint boundColorSlots,
        bool pixelActive,
        bool depthBound)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var vertexSource = PrepareVertexSource(vertex);
        var meshActive = MergedGeometryProgramResolver.IsActive(
            context.ShaderStages,
            vertex.ExportAddress,
            vertex.GeometryAddress,
            vertexSource.Registered.IsFused,
            vertexSource.Registered.ContinuationAddress);
        if (meshActive)
        {
            if (!_host.MeshShadersSupported)
            {
                throw SubmissionScheduler.Fatal(
                    $"The fused geometry program needs VK_EXT_mesh_shader: shader=0x{vertex.ExportAddress:X16}.");
            }

            vertexSource = vertexSource with { Stage = ShaderStage.Mesh };
        }

        var clipSpace = ResolveClipSpace(context);
        var vertexInfo = meshActive
            ? new VertexInputInfo
            {
                PositionExportControl = shaderInterface.VertexOutputControl,
                ClipSpace = clipSpace,
            }
            : PrepareVertexInput(vertexSource, shaderInterface, clipSpace);
        vertexInfo.WaveSize = GraphicsWaveSizeResolver.Vertex(context.ShaderStages);
        if (meshActive)
        {
            vertexInfo.Mesh = MeshInputResolver.Resolve(
                vertex,
                vertexSource.Registered,
                shaderInterface,
                userConfig.GeometryEngineControl,
                (GuestPrimitiveType)userConfig.PrimitiveType,
                context.ShaderStages,
                context.RasterMode.ProvokingVertexLast,
                _host.MeshSubgroupSize);
            ValidateMeshHostLimits(vertex.ExportAddress, vertexInfo.Mesh, _host.MeshShaderCapabilities);
        }
        ShaderSource? pixelSource = null;
        PixelInputInfo? pixelInfo = null;
        Gen5PixelOutputBinding[] pixelOutputs = [];
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
            attributeCount = InterpolatedAttributeCount(pixelProgram);
            var inputCount = shaderInterface.PixelInputControl & 0x3Fu;
            if (inputCount == 0)
            {
                inputCount = Math.Min(attributeCount, MaxPixelInputs);
            }

            if (inputCount > MaxPixelInputs)
            {
                throw SubmissionScheduler.Fatal($"The pixel program declares too many interpolators: shader=0x{pixel.Address:X16} count={inputCount}.");
            }

            pixelOutputs = ResolveBoundTargets(
                pixel.Address,
                context,
                shaderInterface,
                targetExportMapping,
                boundColorSlots,
                depthBound ? pixelProgram.PixelColorExportMasks : null,
                out var outputModes,
                out var outputMappings);
            pixelInfo = PixelStageInputResolver.Resolve(_context, pixelSource.Registered, shaderInterface, outputModes, outputMappings, inputCount);
            // SPI_PS_INPUT_CNTL can map an input to any parameter export, beyond the input count;
            // the vertex program must declare every location the pixel program reads.
            attributeCount = Math.Max(attributeCount, ReadVertexOutputCount(pixelProgram, pixelInfo));
        }

        ShaderProgram vertexProgram;
        ShaderProgram pixelProgramHandle = default;
        ShaderStageResources vertexStage;
        ShaderStageResources pixelStage = default;
        lock (_gate)
        {
            var pushDataCursor = meshActive ? PushData.MeshDrawDwordCount : 0u;
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
                new StageCompileOptions { VertexInfo = vertexInfo, RequiredVertexOutputCount = (int)attributeCount },
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
        if (_drawSubstitutionsEnabled && !meshActive && pixelInfo is not null)
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
            PixelInput = pixelInfo ?? new PixelInputInfo(),
            SolidClear = solidClear,
            DisableBlending = disableBlending,
            PositionStream = meshActive ? null : FindPositionStream(vertexInfo),
        };
    }

    private static void ValidateMeshHostLimits(
        ulong shaderAddress,
        MeshInputInfo mesh,
        MeshShaderHostCapabilities capabilities)
    {
        if (!capabilities.Supported || mesh.HostSubgroupSize == 0 || mesh.WaveSize == 0)
        {
            throw SubmissionScheduler.Fatal(
                $"The mesh host configuration is unavailable: shader=0x{shaderAddress:X16}.");
        }

        var logicalThreads = checked((ulong)mesh.ThreadsX * mesh.ThreadsY * mesh.ThreadsZ);
        var hostThreads = checked(
            ((logicalThreads + mesh.WaveSize - 1u) / mesh.WaveSize) *
            Math.Min(mesh.HostSubgroupSize, mesh.WaveSize));
        var sharedBytes = checked((ulong)mesh.LocalDataShareDwords * sizeof(uint));
        if (hostThreads > capabilities.MaxWorkGroupInvocations ||
            hostThreads > capabilities.MaxWorkGroupSizeX ||
            mesh.MaxVertices > capabilities.MaxOutputVertices ||
            mesh.MaxPrimitives > capabilities.MaxOutputPrimitives ||
            sharedBytes > capabilities.MaxSharedMemorySize)
        {
            throw SubmissionScheduler.Fatal(
                $"The mesh program exceeds host limits: shader=0x{shaderAddress:X16} " +
                $"threads={hostThreads}/{capabilities.MaxWorkGroupInvocations} " +
                $"vertices={mesh.MaxVertices}/{capabilities.MaxOutputVertices} " +
                $"primitives={mesh.MaxPrimitives}/{capabilities.MaxOutputPrimitives} " +
                $"shared={sharedBytes}/{capabilities.MaxSharedMemorySize}.");
        }
    }

    // A mesh stage consumes its vertex data itself, but uses the same clip-space transform
    // as a conventional vertex stage when fixed-function clipping is disabled.
    private ClipSpaceTransform ResolveClipSpace(ContextRegisters context)
    {
        var clipSpace = default(ClipSpaceTransform);
        if (context.Clip.ClipDisable)
        {
            ref readonly var viewport = ref context.ScreenViewport.Viewports[0];
            var limits = _host.Limits;
            clipSpace = new ClipSpaceTransform(
                true,
                viewport.XScale,
                viewport.YScale,
                viewport.XOffset,
                viewport.YOffset,
                Math.Min(limits.MaxViewportWidth, MaxViewportDimension) * 0.5f,
                Math.Min(limits.MaxViewportHeight, MaxViewportDimension) * 0.5f);
        }

        return clipSpace;
    }

    // The fixed-function vertex tables of a conventional vertex draw.
    private VertexInputInfo PrepareVertexInput(
        ShaderSource source,
        ShaderInterfaceRegisters shaderInterface,
        ClipSpaceTransform clipSpace)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VertexInputResolution);
        return VertexInputResolver.ResolveVertexInputs(
            _context,
            source.Registered,
            source.UserData,
            shaderInterface.VertexOutputControl,
            clipSpace,
            source.Registered.IsFused ? (int)VertexUserDataBase : 0);
    }

    // One past the highest parameter location the pixel program reads, resolved as its translator does.
    private static uint ReadVertexOutputCount(Gen5ShaderProgram pixelProgram, PixelInputInfo info)
    {
        var attributes = pixelProgram.Instructions
            .Select(static instruction => instruction.Control)
            .OfType<Gen5InterpolationControl>()
            .Select(static control => control.Attribute)
            .Distinct()
            .Order()
            .ToArray();
        if (attributes.Length == 0)
        {
            return 0;
        }

        var controls = new uint[32];
        for (var index = 0u; index < (uint)controls.Length; index++)
        {
            controls[index] = index < info.InputCount && index < (uint)info.InterpolatorSettings.Length
                ? info.InterpolatorSettings[index]
                : index;
        }

        return Gen5PixelInputMapping.ResolveLocations(controls, attributes).Max() + 1;
    }

    private static uint InterpolatedAttributeCount(Gen5ShaderProgram program)
    {
        var maxAttribute = -1;
        foreach (var instruction in program.Instructions)
        {
            if (instruction.Control is Gen5InterpolationControl interpolation)
            {
                maxAttribute = Math.Max(maxAttribute, (int)interpolation.Attribute);
            }
        }

        return (uint)(maxAttribute + 1);
    }

    // The bound colour slots in order; each output mode names the kind the pixel program exports.
    private Gen5PixelOutputBinding[] ResolveBoundTargets(
        ulong pixelAddress,
        ContextRegisters context,
        ShaderInterfaceRegisters shaderInterface,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        uint boundColorSlots,
        uint? unwrittenExportMasks,
        out byte[] outputModes,
        out ColorComponentMap[] outputMappings)
    {
        outputModes = new byte[PixelInputInfo.TargetCount];
        outputMappings = new ColorComponentMap[PixelInputInfo.TargetCount];
        var outputs = new List<Gen5PixelOutputBinding>(ContextRegisters.ColorTargetCount);
        var location = 0u;
        for (var slot = 0u; slot < ContextRegisters.ColorTargetCount; slot++)
        {
            if ((boundColorSlots & (1u << checked((int)slot))) == 0)
            {
                continue;
            }

            if (unwrittenExportMasks is { } exportMasks &&
                RenderExecutor.IsUnwrittenColorTarget(context, slot, targetExportMapping[(int)slot], exportMasks))
            {
                continue;
            }

            if (slot != 0 && (context.RenderTargetMaskForSlot(slot) == 0 || context.ColorTargets[slot].BaseAddress == 0))
            {
                continue;
            }

            if (ColorTargetResolver.Resolve(context, slot, 0, ignoreTargetMask: false, out _) is null)
            {
                continue;
            }

            var words = context.ColorTargets[slot];
            if (!_host.TryResolveColorOutput((uint)words.Layout, (uint)words.NumberType, (uint)words.Order, out var kind, out var registerMapping))
            {
                throw SubmissionScheduler.Fatal($"The color target format has no pixel output kind: slot={slot} layout={(uint)words.Layout} numberType={(uint)words.NumberType} order={(uint)words.Order}.");
            }

            var boundMapping = targetExportMapping[(int)slot];
            var shaderMapping = new Gen5ColorComponentMapping(boundMapping.Packed);
            // Color exports and bound target slots are independently packed by the
            // shader-output and color-mask registers.
            var exportTarget = PixelExportRouting.ExportForSlot(shaderInterface, slot);
            var targetOutputMode = exportTarget >= 0
                ? shaderInterface.TargetOutputModes[exportTarget]
                : (byte)0;
            outputMappings[slot] = boundMapping;
            // Keep the upstream routing identity in the static key. The real SPI mode is
            // carried by the output binding and used by the shader translator.
            outputModes[slot] = (byte)(((uint)kind + 1) | (uint)((exportTarget + 1) << 4));
            if (RenderTrace.Enabled && RenderTrace.ColorPath())
            {
                var blend = context.BlendControls[slot];
                var shaderMask = (shaderInterface.ColorShaderMask >> checked((int)(slot * 4))) & 0xFu;
                RenderTrace.Write(
                    $"ColorPath shader=0x{pixelAddress:X16} slot={slot} export={exportTarget} host={outputs.Count} addr=0x{words.BaseAddress:X10} " +
                    $"targetMask=0x{context.RenderTargetMaskForSlot(slot):X1} shaderMask=0x{shaderMask:X1} mode={targetOutputMode} " +
                    $"layout={(uint)words.Layout} type={(uint)words.NumberType} order={(uint)words.Order} info=0x{words.Info:X8} " +
                    $"size={words.Width + 1}x{words.Height + 1} tile={(uint)words.TileMode} samples={1u << checked((int)words.SamplesLog2)} " +
                    $"kind={kind} mapping=0x{boundMapping.Packed:X2} registerMapping=0x{registerMapping.Packed:X2} " +
                    $"blend={(blend.Enable ? 1 : 0)} color={blend.ColorSourceFactor}/{blend.ColorFunction}/{blend.ColorDestinationFactor} " +
                    $"alpha={blend.AlphaSourceFactor}/{blend.AlphaFunction}/{blend.AlphaDestinationFactor} separateAlpha={(blend.SeparateAlpha ? 1 : 0)} " +
                    $"dcc={(words.DccEnabled ? 1 : 0)} fastClear={(words.FastClear ? 1 : 0)}");
            }

            outputs.Add(new Gen5PixelOutputBinding(
                slot,
                location++,
                kind,
                shaderMapping,
                targetOutputMode)
            {
            // No EXP target reaches 8 and above, so an unfed slot is declared but never written.
                ExportTarget = exportTarget >= 0 ? (uint)exportTarget : ContextRegisters.ColorTargetCount + slot,
            });
        }

        return outputs.ToArray();
    }

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
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var source = PrepareSource(compute.Address, ShaderStage.Compute, "compute", compute.UserScalars, compute.UserScalarCount, probeWrittenRegisters: false, userDataBase: 0);
        var input = ComputeStageInputResolver.Resolve(compute, source.Registered, dispatchInitiator, _host.ComputeSubgroupSize, dimensionX, dimensionY, dimensionZ);
        input.WorkgroupAxisMapping = _host.ResolveComputeWorkgroupAxisMapping(input.ThreadsX, input.ThreadsY, input.ThreadsZ);
        var systemRegisters = DecodeComputeSystemRegisters(compute);
        var program = _programs.Decode(source);
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
        lock (_gate)
        {
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
        lock (_gate)
        {
            PopulateGraphicsPipelineKey(
                colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
                vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts, _graphicsPipelineLookup,
                out var vertexStage, out var pixelStage);
            if (_graphicsPipelines.TryGetValue(_graphicsPipelineLookup, out var cached))
            {
                return cached;
            }

            // The reusable lookup object must never become a dictionary key. Keep the
            // host description separate too: some hosts retain its state after creation,
            // and neither they nor future probe resets may mutate an inserted key.
            var key = _graphicsPipelineLookup.Clone();
            var hostState = _graphicsPipelineLookup.Clone();
            var description = new GraphicsPipelineDescription
            {
                Rendering = hostState.Rendering,
                VertexInput = hostState.VertexInput,
                VertexInfo = vertexInput,
                VertexProgram = vertexProgram,
                VertexStage = vertexStage,
                PixelInfo = pixelInput,
                PixelProgram = pixelProgram,
                PixelStage = pixelStage,
                StaticParameters = hostState.StaticParameters,
            };
            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write(
                    $"PipelineCache create graphics vertex=0x{vertexProgram.Id:X16} pixel=0x{key.PixelProgramId:X16} colors={key.Rendering.ColorCount} " +
                    $"depth={description.StaticParameters.WithDepth} samples={description.StaticParameters.Samples}");

            }

            var created = _host.CreateGraphicsPipeline(description);
            _graphicsPipelines.Add(key, created);
            ShaderCacheCounters.CountGraphicsPipeline();
            return created;
        }
    }

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
        var key = new GraphicsPipelineKey
        {
            Rendering = new PipelineRenderingState(),
            VertexInput = new PipelineVertexInputState(),
            StaticParameters = new PipelineStaticParameters(),
        };
        PopulateGraphicsPipelineKey(
            colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
            vertexProgram, pixelProgram, noAttachmentSampleCounts, key, out var vertexStage, out var pixelStage);
        return new GraphicsPipelineDescription
        {
            Rendering = key.Rendering,
            VertexInput = key.VertexInput,
            VertexInfo = vertexInput,
            VertexProgram = vertexProgram,
            VertexStage = vertexStage,
            PixelInfo = pixelInput,
            PixelProgram = pixelProgram,
            PixelStage = pixelStage,
            StaticParameters = key.StaticParameters,
        };
    }

    private static void PopulateGraphicsPipelineKey(
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
        GraphicsPipelineKey key,
        out ShaderProgramInfo vertexStage,
        out ShaderProgramInfo? pixelStage)
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

        vertexStage = vertexInput.Stage.Program ?? throw SubmissionScheduler.Fatal("The vertex stage has no program.");
        pixelStage = pixelActive ? pixelInput!.Stage.Program ?? throw SubmissionScheduler.Fatal("The pixel stage has no program.") : null;
        var colorCount = (uint)colors.Length;
        key.SetProgramIds(vertexProgram.Id, pixelActive ? pixelProgram.Id : 0);
        var parameters = key.StaticParameters;
        parameters.Reset();
        parameters.ColorCount = colorCount;
        var renderingState = key.Rendering;
        renderingState.Reset();
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
            var exportTarget = PixelExportRouting.ExportForSlot(context.ShaderInterface, color.Slot);
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
        parameters.ProvokingVertexLast = mode.ProvokingVertexLast;
        if (!PolygonModeResolver.TryResolve(
                in mode,
                parameters.CullFront,
                parameters.CullBack,
                out var polygonMode,
                out var polygonModeError))
        {
            throw SubmissionScheduler.Fatal(polygonModeError);
        }

        parameters.PolygonMode = polygonMode;
        for (var index = 0; index < colors.Length; index++)
        {
            var slot = colors[index].Slot;
            var blend = context.BlendControls[slot];
            parameters.SetColorSourceBlend(index, (byte)blend.ColorSourceFactor);
            parameters.SetColorBlendFunction(index, (byte)blend.ColorFunction);
            parameters.SetColorDestinationBlend(index, (byte)blend.ColorDestinationFactor);
            parameters.SetAlphaSourceBlend(index, (byte)blend.AlphaSourceFactor);
            parameters.SetAlphaBlendFunction(index, (byte)blend.AlphaFunction);
            parameters.SetAlphaDestinationBlend(index, (byte)blend.AlphaDestinationFactor);
            parameters.SetSeparateAlphaBlend(index, blend.SeparateAlpha);
            parameters.SetBlendEnable(index, blend.Enable && !disableBlending);
            parameters.SetBlendBypass(index, ((context.ColorTargets[slot].Info >> 16) & 0x1) != 0);
        }

        if (vertexInput.Mesh.IsActive)
        {
            key.VertexInput.Reset();
        }
        else
        {
            PopulateVertexInputState(vertexInput, key.VertexInput);
        }
    }

    internal static GraphicsPipelineKey KeyOf(GraphicsPipelineDescription description) => new()
    {
        Rendering = description.Rendering,
        VertexProgramId = description.VertexProgram.Id,
        PixelProgramId = description.PixelInfo is not null ? description.PixelProgram.Id : 0,
        VertexInput = description.VertexInput,
        StaticParameters = description.StaticParameters,
    };

    private static void PopulateVertexInputState(VertexInputInfo info, PipelineVertexInputState state)
    {
        if (info.Buffers.Length > VertexInputInfo.MaxBuffers || info.Attributes.Length > VertexInputInfo.MaxBuffers)
        {
            throw SubmissionScheduler.Fatal($"The vertex input is too large: buffers={info.Buffers.Length} attributes={info.Attributes.Length}.");
        }

        state.Reset();
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
        lock (_gate)
        {
            if (_computePipelines.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (RenderTrace.Enabled && RenderTrace.Pipeline())
            {
                RenderTrace.Write($"PipelineCache create compute program=0x{program.Id:X16} hash=0x{stage.Hash:X16}");
            }

            var created = _host.CreateComputePipeline(new ComputePipelineDescription { Input = input, Program = program, Stage = stage });
            _computePipelines.Add(key, created);
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
        lock (_gate)
        {
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

            _computePipelines.Add(key, created);
            ShaderCacheCounters.CountComputePipeline();
            handle = created;
            return true;
        }
    }
}
