// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
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
    private const uint VertexUserDataBase = 8;
    private const uint MaxPixelInputs = 32;
    private const uint MaxViewportDimension = 16384;

    private readonly CpuContext _context;
    private readonly IShaderPipelineHost _host;
    private readonly ShaderHeaderRegistry _registry;
    private readonly ShaderProgramCache _programs;
    private readonly Dictionary<GraphicsPipelineKey, PipelineHandle> _graphicsPipelines = new();
    private readonly Dictionary<ComputePipelineKey, PipelineHandle> _computePipelines = new();
    private readonly object _gate = new();
    private readonly bool _strictShaders = Environment.GetEnvironmentVariable("SHARPEMU_STRICT_COMPUTE") != "0";
    private readonly HashSet<(ShaderStage Stage, ulong Hash, uint CodeSize)> _reportedShaderSkips = [];

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

    // The user data of one stage: the count from its resource register, else the registers the guest wrote.
    private static uint[] UserData(UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, ulong shaderAddress, string label)
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

        return registers.Values.AsSpan(0, (int)count).ToArray();
    }

    private ShaderSource PrepareSource(ulong codeAddress, ShaderStage stage, string label, UserScalarRegisters registers, uint declaredCount, bool probeWrittenRegisters, uint userDataBase)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramSourceRead);
        var registered = _registry.Require(codeAddress, label);
        var hash = ShaderIdentity.Compute(_context.Memory, codeAddress, registered.CodeRanges, label);
        var userData = UserData(registers, declaredCount, probeWrittenRegisters, codeAddress, label);
        return new ShaderSource(registered, hash, userData, userDataBase, stage);
    }

    public GraphicsPrograms GetGraphicsPrograms(
        VertexStageRegisters vertex,
        PixelStageRegisters pixel,
        ShaderInterfaceRegisters shaderInterface,
        ContextRegisters context,
        ReadOnlySpan<ColorComponentMap> targetExportMapping,
        bool pixelActive)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.ProgramPreparation);
        var vertexSource = PrepareSource(
            vertex.ExportAddress, ShaderStage.Vertex, "vertex", vertex.GeometryUserScalars, vertex.GeometryResource2.UserScalarCount,
            probeWrittenRegisters: true, VertexUserDataBase);
        var vertexInfo = PrepareVertexInput(vertexSource, shaderInterface, context);
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

            pixelOutputs = ResolveBoundTargets(context, out var outputModes, out var outputMappings);
            pixelInfo = PixelStageInputResolver.Resolve(_context, pixelSource.Registered, shaderInterface, outputModes, outputMappings, inputCount);
        }

        ShaderProgram vertexProgram;
        ShaderProgram pixelProgramHandle = default;
        ShaderStageResources vertexStage;
        ShaderStageResources pixelStage = default;
        lock (_gate)
        {
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
        if (pixelInfo is not null)
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
            PositionStream = FindPositionStream(vertexInfo),
        };
    }

    // The vertex tables of the draw and the clip-space transform when clipping is off.
    private VertexInputInfo PrepareVertexInput(ShaderSource source, ShaderInterfaceRegisters shaderInterface, ContextRegisters context)
    {
        using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VertexInputResolution);
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

        return VertexInputResolver.ResolveVertexInputs(_context, source.Registered, source.UserData,
            shaderInterface.VertexOutputControl, clipSpace);
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
    private Gen5PixelOutputBinding[] ResolveBoundTargets(ContextRegisters context, out byte[] outputModes, out ColorComponentMap[] outputMappings)
    {
        outputModes = new byte[PixelInputInfo.TargetCount];
        outputMappings = new ColorComponentMap[PixelInputInfo.TargetCount];
        var outputs = new List<Gen5PixelOutputBinding>(ContextRegisters.ColorTargetCount);
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

            var words = context.ColorTargets[slot];
            if (!_host.TryResolveColorOutput((uint)words.Layout, (uint)words.NumberType, (uint)words.Order, out var kind, out var mapping))
            {
                throw SubmissionScheduler.Fatal($"The color target format has no pixel output kind: slot={slot} layout={(uint)words.Layout} numberType={(uint)words.NumberType} order={(uint)words.Order}.");
            }

            outputModes[slot] = (byte)((uint)kind + 1);
            outputMappings[slot] = new ColorComponentMap(mapping.Packed);
            outputs.Add(new Gen5PixelOutputBinding(slot, (uint)outputs.Count, kind, mapping));
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
        var input = ComputeStageInputResolver.Resolve(compute, source.Registered, dispatchInitiator, !_host.ComputeWave64Supported, dimensionX, dimensionY, dimensionZ);
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
        var description = BuildGraphicsDescription(
            colors, in depth, vertexInput, pixelInput, context, in rendering, topology, primitiveRestartEnabled, disableBlending,
            vertexProgram, pixelProgram, _host.NoAttachmentSampleCounts);
        var key = KeyOf(description);
        lock (_gate)
        {
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
        var parameters = new PipelineStaticParameters { ColorCount = colorCount };
        var renderingState = new PipelineRenderingState { ColorCount = colorCount };
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
            var exported = pixelStage is null || ((pixelStage.PixelColorExportMasks >> (int)(color.Slot * 4)) & 0xFu) != 0;
            parameters.SetColorMask(index, exported ? color.Resolution.ExportMapping.ApplyMask(context.RenderTargetMaskForSlot(color.Slot)) : 0);
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

        return new GraphicsPipelineDescription
        {
            Rendering = renderingState,
            VertexInput = BuildVertexInputState(vertexInput),
            VertexInfo = vertexInput,
            VertexProgram = vertexProgram,
            VertexStage = vertexStage,
            PixelInfo = pixelInput,
            PixelProgram = pixelProgram,
            PixelStage = pixelStage,
            StaticParameters = parameters,
        };
    }

    internal static GraphicsPipelineKey KeyOf(GraphicsPipelineDescription description) => new()
    {
        Rendering = description.Rendering,
        VertexProgramId = description.VertexProgram.Id,
        PixelProgramId = description.PixelInfo is not null ? description.PixelProgram.Id : 0,
        VertexInput = description.VertexInput,
        StaticParameters = description.StaticParameters,
    };

    private static PipelineVertexInputState BuildVertexInputState(VertexInputInfo info)
    {
        if (info.Buffers.Length > VertexInputInfo.MaxBuffers || info.Attributes.Length > VertexInputInfo.MaxBuffers)
        {
            throw SubmissionScheduler.Fatal($"The vertex input is too large: buffers={info.Buffers.Length} attributes={info.Attributes.Length}.");
        }

        var state = new PipelineVertexInputState
        {
            BindingCount = (byte)info.Buffers.Length,
            AttributeCount = (byte)info.Attributes.Length,
        };
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

        return state;
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
}
