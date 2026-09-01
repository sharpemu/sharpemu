// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial handles translated AGC compute dispatches from packet decoding through GPU submission.
public static partial class AgcExports
{
    private const uint ComputePgmRsrc2 = 0x213;
    private const uint ComputeStartX = 0x204;
    private const uint ComputeStartY = 0x205;
    private const uint ComputeStartZ = 0x206;
    private const uint ComputeNumThreadX = 0x207;
    private const uint ComputeNumThreadY = 0x208;
    private const uint ComputeNumThreadZ = 0x209;

    private const uint ComputeUserDataRegister = 0x240;

    private static readonly HashSet<ulong> _tracedComputeShaders = new();

    private static readonly HashSet<(ulong Address, uint X, uint Y, uint Z)>
        _tracedDispatchArguments = new();
    private static readonly HashSet<(ulong Address, uint Initiator, string Reason)>
        _rejectedDispatchArguments = new();

    private static readonly ConcurrentDictionary<
        (ulong Cs, uint Checksum, ulong State, uint LocalX, uint LocalY, uint LocalZ,
         uint WaveLanes, ulong AliasAlignment),
        IGuestCompiledShader> _computeShaderCache = new();

    private static readonly ulong? _traceComputeShaderAddress = ParseOptionalHexAddress(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_COMPUTE_SHADER_ADDRESS"));

    private readonly record struct ComputeImageWriter(
        ulong Sequence,
        ulong ShaderAddress,
        string Opcode);

    private readonly record struct ComputeDispatch(
        uint GroupCountX,
        uint GroupCountY,
        uint GroupCountZ,
        uint BaseGroupX,
        uint BaseGroupY,
        uint BaseGroupZ,
        uint WaveLaneCount,
        bool IsIndirect,
        uint ThreadCountX,
        uint ThreadCountY,
        uint ThreadCountZ);

#if DEBUG
    private static void ValidateDispatchInitiators()
    {
        const uint threadCount = 0x00F0_0100u;
        const uint localSize = 64u;
        var initiator = DirectDispatchInitiator(0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 5)) == 0);
        System.Diagnostics.Debug.Assert((initiator & (1u << 6)) != 0);
        System.Diagnostics.Debug.Assert(threadCount * localSize == 0x3C00_4000u);
        System.Diagnostics.Debug.Assert(CeilDivide(20, 8) == 3);
        System.Diagnostics.Debug.Assert(CeilDivide(12, 8) == 2);
    }
#endif

    /// <summary>
    /// Guest storage buffers for a compute dispatch followed by its initial
    /// scalar registers. Dispatch-specific SGPR values remain runtime data so
    /// one translated pipeline serves every matching shader/resource shape.
    /// </summary>
    private static IReadOnlyList<GuestMemoryBuffer> CreateTranslatedComputeGlobalBuffers(
        Gen5ShaderEvaluation evaluation)
    {
        var buffers = CreateGuestMemoryBuffers(evaluation.GlobalMemoryBindings);
        if (_bakeScalars)
        {
            return buffers;
        }

        var combined = new List<GuestMemoryBuffer>(buffers.Count + 1);
        combined.AddRange(buffers);
        combined.Add(new GuestMemoryBuffer(
            0,
            PackRuntimeScalarState(
                evaluation.InitialScalarRegisters,
                evaluation.GlobalMemoryBindings),
            GetRuntimeScalarBufferLength(evaluation.GlobalMemoryBindings.Count),
            Pooled: true));
        return combined;
    }

    private static bool TryReadComputeDispatch(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint opcode,
        out ComputeDispatch dispatch,
        out ulong indirectDimsRetryAddress)
    {
        dispatch = default;
        // Non-zero only when this is an INDIRECT dispatch whose dimensions read as
        // zero — meaning the producing GPU dispatch that computes them has not run
        // yet. The caller suspends on this address instead of dropping the work.
        indirectDimsRetryAddress = 0;
        ulong dimensionsAddress;
        uint initiator;
        string dispatchSource;
        if (opcode == ItDispatchDirect)
        {
            if (packetLength < 5 ||
                !TryReadUInt32(ctx, packetAddress + 16, out initiator))
            {
                return false;
            }

            dimensionsAddress = packetAddress + 4;
            dispatchSource = "direct";
        }
        else if (packetLength >= 4)
        {
            if (!TryReadUInt64(ctx, packetAddress + 4, out dimensionsAddress) ||
                !TryReadUInt32(ctx, packetAddress + 12, out initiator))
            {
                return false;
            }

            dispatchSource = "absolute-indirect";
        }
        else
        {
            if (packetLength < 3 ||
                state.IndirectArgsAddress == 0 ||
                !TryReadUInt32(ctx, packetAddress + 4, out var dataOffset) ||
                !TryReadUInt32(ctx, packetAddress + 8, out initiator))
            {
                return false;
            }

            dimensionsAddress = state.IndirectArgsAddress + dataOffset;
            dispatchSource = "base-indirect";
        }

        if ((initiator & 1) == 0 ||
            !TryReadUInt32(ctx, dimensionsAddress, out var dispatchEndX) ||
            !TryReadUInt32(ctx, dimensionsAddress + 4, out var dispatchEndY) ||
            !TryReadUInt32(ctx, dimensionsAddress + 8, out var dispatchEndZ))
        {
            return false;
        }

        if (dispatchEndX == 0 || dispatchEndY == 0 || dispatchEndZ == 0)
        {
            // For indirect dispatches (both absolute and base), zero dimensions are a valid outcome
            // of GPU culling passes (0 workgroups). VulkanVideoPresenter handles groupCount = 0 as a clean no-op.
            if (opcode == ItDispatchIndirect || dispatchSource is "absolute-indirect" or "base-indirect")
            {
                var waveCount = (initiator & (1u << 15)) != 0 ? 32u : 64u;
                dispatch = new ComputeDispatch(
                    0, 0, 0,
                    0, 0, 0,
                    waveCount,
                    IsIndirect: true,
                    0, 0, 0);
                return true;
            }

            return RejectComputeDispatch(
                dimensionsAddress,
                initiator,
                dispatchSource,
                dispatchEndX,
                dispatchEndY,
                dispatchEndZ,
                "zero-dimension");
        }

        // When FORCE_START_AT_000 is clear, RDNA2 interprets the three packet
        // values as end coordinates, not group counts. Vulkan expresses the
        // same operation as vkCmdDispatchBase(base, end - base). Ignoring the
        // COMPUTE_START registers turned small high-base clears into apparent
        // multi-million/billion-group dispatches and forced an unsafe cap.
        const uint forceStartAtZero = 1u << 2;
        const uint partialThreadGroupEnabled = 1u << 1;
        const uint useThreadDimensions = 1u << 5;
        uint baseGroupX = 0;
        uint baseGroupY = 0;
        uint baseGroupZ = 0;
        if ((initiator & forceStartAtZero) == 0)
        {
            state.ShRegisters.TryGetValue(ComputeStartX, out baseGroupX);
            state.ShRegisters.TryGetValue(ComputeStartY, out baseGroupY);
            state.ShRegisters.TryGetValue(ComputeStartZ, out baseGroupZ);
        }

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        uint groupCountX;
        uint groupCountY;
        uint groupCountZ;
        var threadCountX = uint.MaxValue;
        var threadCountY = uint.MaxValue;
        var threadCountZ = uint.MaxValue;
        if ((initiator & useThreadDimensions) != 0)
        {
            // In thread-dimension mode the packet contains thread counts, not
            // group end coordinates. Vulkan still dispatches whole workgroups,
            // so round up and pass the exact exclusive thread bounds to the
            // translated shader. Its entry guard disables invocations in the
            // partially populated final group before any guest instruction.
            var startThreadX = (ulong)baseGroupX * localSizeX;
            var startThreadY = (ulong)baseGroupY * localSizeY;
            var startThreadZ = (ulong)baseGroupZ * localSizeZ;
            if ((ulong)dispatchEndX <= startThreadX ||
                (ulong)dispatchEndY <= startThreadY ||
                (ulong)dispatchEndZ <= startThreadZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"thread-end-not-after-base(" +
                    $"{startThreadX}x{startThreadY}x{startThreadZ})");
            }

            groupCountX = CeilDivide((ulong)dispatchEndX - startThreadX, localSizeX);
            groupCountY = CeilDivide((ulong)dispatchEndY - startThreadY, localSizeY);
            groupCountZ = CeilDivide((ulong)dispatchEndZ - startThreadZ, localSizeZ);
            threadCountX = dispatchEndX;
            threadCountY = dispatchEndY;
            threadCountZ = dispatchEndZ;
        }
        else
        {
            if (dispatchEndX <= baseGroupX ||
                dispatchEndY <= baseGroupY ||
                dispatchEndZ <= baseGroupZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"end-not-after-base({baseGroupX}x{baseGroupY}x{baseGroupZ})");
            }

            groupCountX = dispatchEndX - baseGroupX;
            groupCountY = dispatchEndY - baseGroupY;
            groupCountZ = dispatchEndZ - baseGroupZ;
        }

        if ((initiator & partialThreadGroupEnabled) != 0)
        {
            var partialSizeX = GetComputePartialSize(state.ShRegisters, ComputeNumThreadX);
            var partialSizeY = GetComputePartialSize(state.ShRegisters, ComputeNumThreadY);
            var partialSizeZ = GetComputePartialSize(state.ShRegisters, ComputeNumThreadZ);
            if (partialSizeX == 0 || partialSizeX > localSizeX ||
                partialSizeY == 0 || partialSizeY > localSizeY ||
                partialSizeZ == 0 || partialSizeZ > localSizeZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"invalid-partial-size({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }

            if (partialSizeX != localSizeX ||
                partialSizeY != localSizeY ||
                partialSizeZ != localSizeZ)
            {
                return RejectComputeDispatch(
                    dimensionsAddress,
                    initiator,
                    dispatchSource,
                    dispatchEndX,
                    dispatchEndY,
                    dispatchEndZ,
                    $"unrepresentable-partial-group({partialSizeX}x{partialSizeY}x{partialSizeZ}/" +
                    $"{localSizeX}x{localSizeY}x{localSizeZ})");
            }
        }

        var waveLaneCount = (initiator & (1u << 15)) != 0 ? 32u : 64u;

        if (_traceAgcShader &&
            ((ulong)groupCountX * groupCountY * groupCountZ >= 1_000_000UL ||
             groupCountX >= 1_000_000u))
        {
            lock (_submitTraceGate)
            {
                if (_tracedDispatchArguments.Add(
                        (dimensionsAddress, groupCountX, groupCountY, groupCountZ)))
                {
                    TraceAgcShader(
                        $"agc.dispatch_args source={dispatchSource} op=0x{opcode:X2} " +
                        $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                        $"packet=0x{packetAddress:X16} len={packetLength} " +
                        $"dims=0x{dimensionsAddress:X16} " +
                        $"raw={dispatchEndX:X8}/{dispatchEndY:X8}/{dispatchEndZ:X8} " +
                        $"base={baseGroupX:X8}/{baseGroupY:X8}/{baseGroupZ:X8} " +
                        $"count={groupCountX:X8}/{groupCountY:X8}/{groupCountZ:X8} " +
                        $"wave={waveLaneCount} " +
                        $"initiator=0x{initiator:X8} " +
                        $"indirect_base=0x{state.IndirectArgsAddress:X16}");
                }
            }
        }

        dispatch = new ComputeDispatch(
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            waveLaneCount,
            IsIndirect: opcode == ItDispatchIndirect,
            threadCountX,
            threadCountY,
            threadCountZ);
        return true;
    }

    private static uint CeilDivide(ulong value, uint divisor) =>
        checked((uint)((value + divisor - 1) / divisor));

    private static bool RejectComputeDispatch(
        ulong dimensionsAddress,
        uint initiator,
        string source,
        uint rawX,
        uint rawY,
        uint rawZ,
        string reason)
    {
        lock (_submitTraceGate)
        {
            if (_rejectedDispatchArguments.Count < 256 &&
                _rejectedDispatchArguments.Add((dimensionsAddress, initiator, reason)))
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.dispatch_reject source={source} " +
                    $"dims=0x{dimensionsAddress:X16} raw={rawX:X8}/{rawY:X8}/{rawZ:X8} " +
                    $"initiator=0x{initiator:X8} reason={reason}");
            }
        }

        return false;
    }

    private static void ObserveComputeDispatch(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ComputeDispatch dispatch)
    {
        // A zero-size indirect dispatch does no work. Return before shader
        // evaluation copies resource data. Some command streams contain many
        // such dispatches, and unnecessary copies can stop progress for seconds.
        if (dispatch.GroupCountX == 0 ||
            dispatch.GroupCountY == 0 ||
            dispatch.GroupCountZ == 0)
        {
            return;
        }

        if (!TryGetShaderAddress(
                state.ShRegisters,
                ComputePgmLo,
                ComputePgmHi,
                out var shaderAddress))
        {
            return;
        }

        var sequence = ++gpuState.WorkSequence;
        ulong shaderHeader;
        lock (_submitTraceGate)
        {
            _shaderHeadersByCode.TryGetValue(shaderAddress, out shaderHeader);
        }

        var computeSystemRegisters = DecodeComputeSystemRegisters(state.ShRegisters);
        state.ShRegisters.TryGetValue(ComputeShaderChksum, out var shaderChecksum);
        if (!Gen5ShaderTranslator.TryCreateState(
                ctx,
                shaderAddress,
                shaderHeader,
                state.ShRegisters,
                ComputeUserDataRegister,
                out var shaderState,
                out var error,
                computeSystemRegisters,
                shaderChecksum: shaderChecksum) ||
            !Gen5ShaderScalarEvaluator.TryEvaluate(
                ctx,
                shaderState,
                out var evaluation,
                out error,
                profileStage: Gen5ShaderEvaluationStage.Compute))
        {
            lock (_submitTraceGate)
            {
                if (_tracedComputeShaders.Add(shaderAddress))
                {
                    TraceAgcShader(
                        $"agc.compute_shader cs=0x{shaderAddress:X16} error={error}");
                }
            }

            return;
        }

        var bindings = evaluation.ImageBindings;
        var traceConstantFill =
            _traceMetaSurfaces && HasConstantFillOpcodeSequence(shaderState.Program);
        var describeBindings =
            _traceAgcShader ||
            _traceComputeShaderAddress == shaderAddress ||
            traceConstantFill;
        var descriptions = describeBindings
            ? new List<string>(bindings.Count)
            : null;
        var translatedBindings = new List<TranslatedImageBinding>(bindings.Count);
        var hasStorageBinding = false;
        foreach (var binding in bindings)
        {
            var isStorage = Gen5ShaderTranslator.RequiresStorageImage(binding, bindings);
            var writesStorage = Gen5ShaderTranslator.IsStorageImageOperation(binding.Opcode);
            var descriptorValid = TryDecodeTextureDescriptor(binding.ResourceDescriptor, out var texture);
            if (!descriptorValid)
            {
                texture = CreateFallbackTextureDescriptor(
                    binding.ResourceDescriptor,
                    binding.Control.Dimension);
            }

            translatedBindings.Add(
                new TranslatedImageBinding(
                    texture,
                    isStorage,
                    binding.MipLevel ?? 0,
                NormalizeSamplerDescriptorForImageOperation(
                    binding.SamplerDescriptor),
                    Gen5ShaderTranslator.IsArrayedImageBinding(binding),
                    binding.ResourceDescriptor));
            hasStorageBinding |= isStorage;

            if (descriptions is not null)
            {
                var descriptorState = descriptorValid ? string.Empty : "/invalid-desc";
                descriptions.Add(
                    $"{binding.Opcode}@0x{binding.Pc:X}:" +
                    $"0x{texture.Address:X16}:{texture.Width}x{texture.Height}:" +
                    $"fmt{texture.Format}/num{texture.NumberType}/tile{texture.TileMode}" +
                    $"{descriptorState}/{ProbeTexture(ctx, texture)}");
            }
            if (writesStorage && descriptorValid && texture.Address != 0)
            {
                gpuState.ComputeImageWriters[texture.Address] = new ComputeImageWriter(
                    sequence,
                    shaderAddress,
                    binding.Opcode);

                TraceAgcShader(
                    $"agc.compute_writer addr=0x{texture.Address:X16} " +
                    $"fmt={texture.Format} num={texture.NumberType} tile={texture.TileMode} " +
                    $"size={texture.Width}x{texture.Height} " +
                    $"cs=0x{shaderAddress:X16} op={binding.Opcode}");
            }
        }

        var localSizeX = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadX);
        var localSizeY = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadY);
        var localSizeZ = GetComputeLocalSize(state.ShRegisters, ComputeNumThreadZ);
        if (traceConstantFill)
        {
            var scalars = evaluation.InitialScalarRegisters;
            var rawBaseAddress = scalars.Count >= 2
                ? scalars[0] | ((ulong)(scalars[1] & 0xFFFFu) << 32)
                : 0;
            var reason = GetConstantFillDiagnosticReason(
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ);
            var scalarState = string.Join(
                ',',
                scalars.Take(12).Select((value, index) => $"s{index}=0x{value:X8}"));
            var globalState = evaluation.GlobalMemoryBindings.Count == 0
                ? "none"
                : string.Join(
                    ',',
                    evaluation.GlobalMemoryBindings.Select(binding =>
                        $"s{binding.ScalarAddress}:0x{binding.BaseAddress:X16}+" +
                        $"0x{binding.DataLength:X}:w={binding.Writable}:" +
                        $"wb={binding.WriteBackToGuest}"));
            var imageState = descriptions is { Count: > 0 }
                ? string.Join(',', descriptions)
                : "none";
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.constant_fill_dispatch " +
                $"seq={sequence} cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} " +
                $"source={(dispatch.IsIndirect ? "indirect" : "direct")} " +
                $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                $"local={localSizeX}x{localSizeY}x{localSizeZ} " +
                $"raw_dst=0x{rawBaseAddress:X16} reason={reason} " +
                $"globals=[{globalState}] images=[{imageState}] scalars=[{scalarState}]");
        }

        if (_traceComputeShaderAddress == shaderAddress && descriptions is not null)
        {
            var globalHeads = evaluation.GlobalMemoryBindings.Count == 0
                ? string.Empty
                : $" global_heads=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                    binding =>
                        $"0x{binding.BaseAddress:X16}:{binding.DataLength}:" +
                        Convert.ToHexString(binding.Data.AsSpan(
                            0,
                            Math.Min(binding.DataLength, 512)))))}]";
            Console.Error.WriteLine(
                $"[LOADER][TRACE] agc.compute_dispatch_trace seq={sequence} " +
                $"cs=0x{shaderAddress:X16} " +
                $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                $"local={localSizeX}x{localSizeY}x{localSizeZ}" +
                globalHeads +
                $" bindings=[{string.Join(',', descriptions)}]");
        }

        var writesGlobalMemory = evaluation.GlobalMemoryBindings.Any(static binding =>
            binding.Writable);
        var gpuDispatch = false;
        var evaluationHandledByCpu = false;
        var computeError = string.Empty;
        // Empty SRT/EUD with a recorded null-base scalar pointer fallback
        // produces Address-0 storage that can lose the Vulkan device on submit.
        var emptyResourceTables =
            shaderState.Metadata is
            {
                ShaderResourceTableSizeDwords: 0,
                ExtendedUserDataSizeDwords: 0,
            };
        if (emptyResourceTables &&
            (Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress) ||
             (translatedBindings.All(static binding => binding.Descriptor.Address == 0) &&
              !evaluation.GlobalMemoryBindings.Any(static binding => binding.BaseAddress != 0))))
        {
            computeError = Gen5ShaderScalarEvaluator.WasEmptySrtScalarPointerFallback(shaderAddress)
                ? "empty-srt-scalar-pointer-fallback"
                : "empty-srt-no-usable-resources";
            lock (_submitTraceGate)
            {
                if (_tracedComputeShaders.Add(shaderAddress))
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] agc.compute_reject cs=0x{shaderAddress:X16} " +
                        $"source={(dispatch.IsIndirect ? "indirect" : "direct")} " +
                        $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                        $"reason={computeError}");
                }
            }
        }
        else if (!hasStorageBinding &&
            writesGlobalMemory &&
            TrySubmitMaskedDwordCopyKernel(
                ctx,
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var semanticCopySequence,
                out var copyDescription))
        {
            gpuDispatch = true;
            evaluationHandledByCpu = true;
            TraceAgcShader(
                $"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                copyDescription);
            // The scalar evaluator snapshots guest buffers while parsing the
            // command stream.  Do not let another submission (or the CPU)
            // observe that snapshot until the semantic replacement has
            // reached the same CPU-visible completion point as a translated
            // writable-buffer dispatch below.  Returning early here allowed
            // the guest to reuse a transient heap while its delayed clear was
            // still queued, so the clear could erase newly constructed CPU
            // objects.  Waiting on the work sequence also retires preceding
            // Vulkan writes before the next evaluator snapshot is captured.
            if (!GuestGpu.Current.WaitForGuestWork(semanticCopySequence))
            {
                computeError =
                    $"semantic-global-write-sync-timeout sequence={semanticCopySequence}";
            }
        }
        else if (!hasStorageBinding &&
            writesGlobalMemory &&
            TrySubmitConstantFillKernel(
                ctx,
                shaderState.Program,
                evaluation,
                dispatch,
                localSizeX,
                localSizeY,
                localSizeZ,
                out var semanticFillSequence,
                out var fillDescription))
        {
            gpuDispatch = true;
            evaluationHandledByCpu = true;
            TraceAgcShader(
                $"agc.compute_semantic_fast_path cs=0x{shaderAddress:X16} " +
                $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
                fillDescription);
            // Same CPU-visibility ordering requirement as the masked-copy
            // replacement above.
            if (!VulkanVideoPresenter.WaitForGuestWork(semanticFillSequence))
            {
                computeError =
                    $"semantic-global-write-sync-timeout sequence={semanticFillSequence}";
            }
        }
        else if ((hasStorageBinding || writesGlobalMemory) &&
            (ulong)localSizeX * localSizeY * localSizeZ <= 1024)
        {
            var shaderKey = (
                Cs: shaderAddress,
                Checksum: shaderState.ShaderChecksum,
                State: _bakeScalars
                    ? ComputeShaderStateFingerprint(evaluation)
                    : ComputeShaderStructuralFingerprint(evaluation),
                LocalX: localSizeX,
                LocalY: localSizeY,
                LocalZ: localSizeZ,
                WaveLanes: dispatch.WaveLaneCount,
                AliasAlignment: _storageBufferOffsetAlignment);
            var guestGlobalBufferCount = evaluation.GlobalMemoryBindings.Count;
            var totalGlobalBufferCount = _bakeScalars
                ? guestGlobalBufferCount
                : guestGlobalBufferCount + 1;
            _computeShaderCache.TryGetValue(shaderKey, out var computeShader);

            if (computeShader is null &&
                GuestGpu.Current.TryCompileComputeShader(
                    shaderState,
                    evaluation,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    out computeShader,
                    out computeError,
                    totalGlobalBufferCount,
                    initialScalarBufferIndex: _bakeScalars
                        ? -1
                        : guestGlobalBufferCount,
                    waveLaneCount: dispatch.WaveLaneCount,
                    storageBufferOffsetAlignment:
                        _storageBufferOffsetAlignment))
            {
                DumpCompiledShader(
                    "cs",
                    shaderAddress,
                    shaderKey.State,
                    computeShader!,
                    shaderState.Program);
            }

            if (computeShader is not null)
            {
                _computeShaderCache.TryAdd(shaderKey, computeShader);

                var textures = CreateGuestDrawTextures(
                    ctx,
                    translatedBindings,
                    out _);
                var globalMemoryBuffers =
                    CreateTranslatedComputeGlobalBuffers(evaluation);
                GuestGpu.Current.SubmitComputeDispatch(
                    shaderAddress,
                    computeShader,
                    textures,
                    globalMemoryBuffers,
                    dispatch.GroupCountX,
                    dispatch.GroupCountY,
                    dispatch.GroupCountZ,
                    dispatch.BaseGroupX,
                    dispatch.BaseGroupY,
                    dispatch.BaseGroupZ,
                    localSizeX,
                    localSizeY,
                    localSizeZ,
                    dispatch.IsIndirect,
                    writesGlobalMemory,
                    dispatch.ThreadCountX,
                    dispatch.ThreadCountY,
                    dispatch.ThreadCountZ);
                // Vulkan queue order keeps dependent dispatches coherent. CPU visibility is
                // published by explicit PM4 release/write actions instead of per dispatch.
                gpuDispatch = true;
            }
        }

        const int blitCount = 0;

        if (gpuDispatch)
        {
            var metadataBindings = evaluation.GlobalMemoryBindings
                .Where(binding =>
                    gpuState.HtileMetadata.IsRegistered(binding.BaseAddress))
                .ToArray();
            var hasMetadataWrite = metadataBindings.Any(static binding => binding.Writable);
            var instructionsByPc = hasMetadataWrite
                ? shaderState.Program.Instructions.ToDictionary(
                    static instruction => instruction.Pc,
                    static instruction => instruction.Opcode)
                : null;
            var hasWriteOnlyMetadataAccess =
                instructionsByPc is not null &&
                metadataBindings.All(binding =>
                    IsHtileMetadataWriteOnlyAccess(
                        binding,
                        instructionsByPc));
            if (hasMetadataWrite &&
                hasWriteOnlyMetadataAccess &&
                !shaderState.Program.Instructions.Any(static instruction =>
                    instruction.Opcode.Contains("Xor", StringComparison.Ordinal)))
            {
                foreach (var binding in evaluation.GlobalMemoryBindings)
                {
                    if (!binding.Writable ||
                        !gpuState.HtileMetadata.IsRegistered(binding.BaseAddress))
                    {
                        continue;
                    }

                    MarkHtileMetadataClear(
                        gpuState,
                        binding.BaseAddress,
                        _traceDepthMetadata
                            ? $"compute:0x{shaderAddress:X16}"
                            : string.Empty);
                }
            }
        }

        lock (_submitTraceGate)
        {
            if (_traceAgcShader &&
                descriptions is not null &&
                _tracedComputeShaders.Add(shaderAddress))
            {
                var globalBuffers = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_buffers=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding => $"0x{binding.BaseAddress:X16}:{binding.DataLength}"))}]";
                var scalarProbe = string.Join(
                    ',',
                    evaluation.InitialScalarRegisters
                        .Take(16)
                        .Select((value, index) => $"s{index}={value:X8}"));
                var globalProbes = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_heads=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"0x{binding.BaseAddress:X16}:" +
                            Convert.ToHexString(binding.Data.AsSpan(
                                0,
                                Math.Min(binding.DataLength, 16)))))}]";
                var globalDescriptors = evaluation.GlobalMemoryBindings.Count == 0
                    ? string.Empty
                    : $" global_descriptors=[{string.Join(',', evaluation.GlobalMemoryBindings.Select(
                        binding =>
                            $"s{binding.ScalarAddress}=" +
                            string.Join(':', evaluation.ScalarRegisters
                                .Skip(checked((int)binding.ScalarAddress))
                                .Take(4)
                                .Select(value => $"{value:X8}"))))}]";
                var opcodes = string.Join(
                    ',',
                    shaderState.Program.Instructions
                        .Select(instruction => instruction.Opcode)
                        .Distinct()
                        .Take(48));
                TraceAgcShader(
                    $"agc.compute_shader cs=0x{shaderAddress:X16} " +
                    $"groups={dispatch.GroupCountX}x{dispatch.GroupCountY}x{dispatch.GroupCountZ} " +
                    $"base={dispatch.BaseGroupX}x{dispatch.BaseGroupY}x{dispatch.BaseGroupZ} " +
                    $"wave={dispatch.WaveLaneCount} " +
                    $"local={localSizeX}x{localSizeY}x{localSizeZ} " +
                    $"sys={DescribeComputeSystemRegisters(computeSystemRegisters)} " +
                    $"gpu={gpuDispatch} blits={blitCount} globals={evaluation.GlobalMemoryBindings.Count} " +
                    $"global_writes={writesGlobalMemory}" +
                    (computeError.Length == 0 ? string.Empty : $" error={computeError}") +
                    $" sgprs=[{scalarProbe}]" +
                    globalBuffers +
                    globalProbes +
                    globalDescriptors +
                    $" opcodes=[{opcodes}]" +
                    $" bindings=[{string.Join(',', descriptions)}]");
            }
        }

        // Rejected/CPU-handled dispatches never hand evaluation's pooled buffers to a
        // consumer that would return them; reclaim here to keep GuestDataPool.Shared bounded.
        if (evaluationHandledByCpu || !gpuDispatch)
        {
            ReturnPooledEvaluationArrays(evaluation);
        }
    }

    private static Gen5ComputeSystemRegisters DecodeComputeSystemRegisters(
        IReadOnlyDictionary<uint, uint> registers)
    {
        registers.TryGetValue(ComputePgmRsrc2, out var rsrc2);
        var nextRegister = (rsrc2 >> 1) & 0x1Fu;
        uint? workGroupX = null;
        uint? workGroupY = null;
        uint? workGroupZ = null;
        uint? threadGroupSize = null;

        if ((rsrc2 & (1u << 7)) != 0)
        {
            workGroupX = nextRegister++;
        }

        if ((rsrc2 & (1u << 8)) != 0)
        {
            workGroupY = nextRegister++;
        }

        if ((rsrc2 & (1u << 9)) != 0)
        {
            workGroupZ = nextRegister++;
        }

        if ((rsrc2 & (1u << 10)) != 0)
        {
            threadGroupSize = nextRegister++;
        }

        return new Gen5ComputeSystemRegisters(
            workGroupX,
            workGroupY,
            workGroupZ,
            threadGroupSize);
    }

    private static string DescribeComputeSystemRegisters(Gen5ComputeSystemRegisters registers) =>
        $"x={DescribeRegister(registers.WorkGroupXRegister)}," +
        $"y={DescribeRegister(registers.WorkGroupYRegister)}," +
        $"z={DescribeRegister(registers.WorkGroupZRegister)}," +
        $"size={DescribeRegister(registers.ThreadGroupSizeRegister)}";

    private static string DescribeRegister(uint? register) =>
        register.HasValue ? $"s{register.Value}" : "-";

    private static uint GetComputeLocalSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register)
    {
        return registers.TryGetValue(register, out var value)
            ? Math.Max(value & 0xFFFFu, 1u)
            : 1u;
    }

    private static uint GetComputePartialSize(
        IReadOnlyDictionary<uint, uint> registers,
        uint register) =>
        registers.TryGetValue(register, out var value)
            ? value >> 16
            : 0u;
}
