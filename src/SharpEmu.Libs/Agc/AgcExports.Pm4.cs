// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Agc;

// This partial owns submitted PM4 stream decoding.
public static partial class AgcExports
{
    private static readonly bool _traceFramePackets = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_TRACE_FRAME_PACKETS"),
        "1",
        StringComparison.Ordinal);

    private static readonly HashSet<uint> KnownPm4Opcodes =
    [
        ItNop, ItSetBase, ItIndexBufferSize, ItIndexBase, ItDrawIndirect,
        ItDrawIndexIndirect, ItDrawIndex2, ItIndexType, ItDrawIndexAuto,
        ItNumInstances, ItDrawIndexMultiAuto, ItDrawIndexOffset2, ItWriteData,
        ItAtomicMem, ItMemSemaphore, ItCopyData,
        ItDispatchDirect, ItDispatchIndirect, ItSetPredication, ItCondExec,
        ItWaitRegMem,
        ItIndirectBuffer, ItCondWrite, ItEventWrite, ItReleaseMem, ItDmaData,
        ItRewind, ItSetShRegIndirect, ItSetUconfigRegIndirect,
        ItSetContextReg, ItSetShReg, ItSetUconfigReg,
        ItSetUconfigRegIndex, ItGetLodStats,
        ItSetContextRegIndirect,
    ];

    private static readonly HashSet<(int Handle, int Index, ulong Address, string Path)> _tracedDisplayBuffers = new();
    private static readonly HashSet<uint> _tracedSubmittedDrawOpcodes = new();

    private static readonly HashSet<uint> _seenUnknownOpcodes = new();

    private static long _packetParseFailureTraceCount;

    // Returns true when parsing stops on a wait or a terminal queue fault.
    // Unsupported synchronization packets must not let later GPU work run.
    private static bool ParseSubmittedDcb(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (!DcbParseProfile.Enabled)
        {
            return ParseSubmittedDcbChain(
                ctx, gpuState, state, commandAddress, dwordCount, tracePackets);
        }

        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var suspended = false;
        try
        {
            suspended = ParseSubmittedDcbChain(
                ctx, gpuState, state, commandAddress, dwordCount, tracePackets);
            return suspended;
        }
        finally
        {
            DcbParseProfile.RecordParse(
                dwordCount,
                System.Diagnostics.Stopwatch.GetTimestamp() - started,
                suspended);
        }
    }

    private static bool ParseSubmittedDcbChain(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        if (commandAddress == 0 || dwordCount == 0 || dwordCount > 1_000_000)
        {
            return false;
        }

        using var guestQueueScope = GuestGpu.Current.EnterGuestQueue(
            state.QueueName,
            state.ActiveSubmissionId);
        // A submission is one link of a chain, not necessarily the whole stream:
        // when a title's command arena fills mid-frame it continues in a fresh
        // buffer and links the two with an INDIRECT_BUFFER packet, then submits
        // only the first link. Stopping at the end of the submitted window drops
        // every packet past the switch -- including the flip and the end-of-frame
        // completion labels the guest is waiting on.
        for (var chainDepth = 0; ; chainDepth++)
        {
            if (chainDepth > MaxSubmittedChainDepth)
            {
                TraceAgc(
                    $"agc.dcb_chain_depth_exceeded queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} addr=0x{commandAddress:X16}");
                return false;
            }

            state.PendingChainAddress = 0;
            state.PendingChainDwords = 0;
            state.LastParsedAddress = commandAddress;
            var windowByteCount = checked((int)(dwordCount * sizeof(uint)));
            var rented = GuestDataPool.Shared.Rent(windowByteCount);
            bool suspended;
            try
            {
                _dcbWindowLease = DcbWindowInvalidationRegistry.Register(
                    commandAddress,
                    (ulong)windowByteCount);
                if (ctx.Memory.TryRead(commandAddress, rented.AsSpan(0, windowByteCount)))
                {
                    _dcbWindowBuffer = rented;
                    _dcbWindowStart = commandAddress;
                    _dcbWindowByteLength = windowByteCount;
                }
                else
                {
                    DropCurrentDcbWindow();
                }

                suspended = ParseSubmittedDcbCore(
                    ctx,
                    gpuState,
                    state,
                    commandAddress,
                    dwordCount,
                    tracePackets);
            }
            finally
            {
                DropCurrentDcbWindow();
                GuestDataPool.Shared.Return(rented);
            }

            // Record only what was actually parsed, not the full declared
            // size — else the orphan sweep either starves a suspended
            // queue's remaining packets or double-runs ones it already ran.
            if (!state.IsForceSubmittedRing && state.LastParsedAddress > commandAddress)
            {
                var consumedDwords = (uint)Math.Min(
                    (state.LastParsedAddress - commandAddress) / sizeof(uint),
                    dwordCount);
                RecordGameSubmittedRange(commandAddress, consumedDwords);
            }

            if (suspended)
            {
                return true;
            }

            var chainAddress = state.PendingChainAddress;
            var chainDwords = state.PendingChainDwords;
            if (chainAddress == 0 || chainDwords == 0 || chainDwords > 1_000_000)
            {
                if (state.IndirectCallReturn is not { } returnTarget)
                {
                    return false;
                }

                state.IndirectCallReturn = null;
                commandAddress = returnTarget.Address;
                dwordCount = returnTarget.Dwords;
                state.RingChunkBase = returnTarget.RingChunkBase;
                continue;
            }

            commandAddress = chainAddress;
            dwordCount = chainDwords;
        }
    }

    // Deep enough for a title that links one continuation buffer per frame,
    // shallow enough that a self-referencing chain cannot spin forever.
    private const int MaxSubmittedChainDepth = 64;

    private static bool ParseSubmittedDcbCore(
        CpuContext ctx,
        SubmittedGpuState gpuState,
        SubmittedDcbState state,
        ulong commandAddress,
        uint dwordCount,
        bool tracePackets)
    {
        var offset = 0u;
        while (offset < dwordCount)
        {
            var currentAddress = commandAddress + ((ulong)offset * sizeof(uint));
            if (!TryReadUInt32(ctx, currentAddress, out var header))
            {
                TracePacketParseFailure(state, currentAddress, offset, 0, "header-read");
                return false;
            }

            var packetType = header >> 30;
            if (packetType == 2)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.packet dw={offset} addr=0x{currentAddress:X16} " +
                        $"header=0x{header:X8} len=1 type=2");
                }

                offset++;
                continue;
            }

            if (header == 0 &&
                (state.FollowedChunkAdvance || state.IsForceSubmittedRing) &&
                _gpuWaitSuspendEnabled)
            {
                // Ring memory the game has not appended to yet — the bound the
                // CP's write pointer would impose. Park until it is written.
                return SuspendOnUnwrittenRingWord(
                    ctx, state, commandAddress, currentAddress, offset, tracePackets);
            }

            if (packetType != 3)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"packet-type-{packetType}");
                return false;
            }

            var length = Pm4Length(header);
            if (length == 0 || offset + length > dwordCount)
            {
                TracePacketParseFailure(
                    state,
                    currentAddress,
                    offset,
                    header,
                    $"length-{length}-remaining-{dwordCount - offset}");
                return false;
            }

            var op = (header >> 8) & 0xFFu;
            var register = (header >> 2) & 0x3Fu;
            if (!KnownPm4Opcodes.Contains(op) && _seenUnknownOpcodes.Add(op))
            {
                TryReadUInt32(ctx, currentAddress + 4, out var unknownPayload0);
                TryReadUInt32(ctx, currentAddress + 8, out var unknownPayload1);
                var possibleTarget = ((ulong)(unknownPayload1 & 0xFFFFu) << 32) | unknownPayload0;
                Console.Error.WriteLine(
                    $"[LOADER][WARN] agc.dcb.unknown_opcode op=0x{op:X2} reg=0x{register:X2} " +
                    $"len={length} addr=0x{currentAddress:X16} queue={state.QueueName} " +
                    $"payload0=0x{unknownPayload0:X8} payload1=0x{unknownPayload1:X8} " +
                    $"possible_target=0x{possibleTarget:X16}");
            }
            if (_traceFramePackets && ReferenceEquals(state, gpuState.Graphics))
            {
                var packetKey = (op, op == ItNop ? register : uint.MaxValue);
                state.FramePacketCounts[packetKey] =
                    state.FramePacketCounts.TryGetValue(packetKey, out var packetCount)
                        ? packetCount + 1
                        : 1;
                state.FramePacketCount++;
            }
            if (tracePackets)
            {
                TraceSubmittedPacket(ctx, currentAddress, offset, header, length, op, register);
            }

            if (_traceDraws)
            {
                CountSubmittedOpcode(op, register);
            }

            if ((header & 1u) != 0 && state.PredicateSkip)
            {
                if (tracePackets)
                {
                    TraceAgc(
                        $"agc.dcb.predicated_skip queue={state.QueueName} " +
                        $"packet=0x{currentAddress:X16} op=0x{op:X2} len={length}");
                }

                offset += length;
                continue;
            }

            var isAcquireMem = op == ItNop && register == RAcquireMem && length >= 8;
            // Flush coalesced ACQUIRE_MEM only before packets that consume guest
            // resources (draw/dispatch/dma/flip). Flushing before every register
            // write produced a storm of tiny ordered actions during load.
            if (!isAcquireMem &&
                PacketRequiresPendingAcquireFlush(op, register, length))
            {
                FlushPendingAcquireInvalidation(ctx, state, tracePackets);
            }

            if (op == ItSetPredication)
            {
                ApplySubmittedPredication(ctx, state, currentAddress, length, tracePackets);
                offset += length;
                continue;
            }

            if (op == ItRewind && length >= 2)
            {
                if (HandleSubmittedRewind(
                        ctx,
                        state,
                        commandAddress,
                        currentAddress,
                        offset,
                        length,
                        dwordCount,
                        tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // suspended until RewindPatchSetRewindState
                }

                offset += length;
                continue;
            }

            if (op == ItIndirectBuffer &&
                length >= 4 &&
                TryReadUInt32(ctx, currentAddress + 4, out var chainLow) &&
                TryReadUInt32(ctx, currentAddress + 8, out var chainHigh) &&
                TryReadUInt32(ctx, currentAddress + 12, out var chainDwords))
            {
                var chainAddress = ((ulong)(chainHigh & 0xFFFFu) << 32) | chainLow;
                var chainLength = chainDwords & 0xFFFFFu;
                // Titles emit a zeroed INDIRECT_BUFFER as padding for a branch they
                // decided not to take. Only a populated one redirects the stream.
                if (chainAddress != 0 && chainLength != 0)
                {
                    var jumpMode = (chainDwords >> 20) & 0x1u;
                    if (jumpMode == 0 && offset + length < dwordCount)
                    {
                        if (state.IndirectCallReturn is not null)
                        {
                            StopSubmittedQueue(
                                ctx,
                                state,
                                currentAddress,
                                ItIndirectBuffer,
                                "nested indirect calls are not supported by AGC");
                            return true;
                        }

                        state.IndirectCallReturn = (
                            currentAddress + ((ulong)length * sizeof(uint)),
                            dwordCount - (offset + length),
                            state.RingChunkBase);
                    }

                    state.PendingChainAddress = chainAddress;
                    state.PendingChainDwords = chainLength;
                    state.RingChunkBase = chainAddress;
                    TraceAgc(
                        $"agc.dcb_chain queue={state.QueueName} " +
                        $"submission={state.ActiveSubmissionId} " +
                        $"packet=0x{currentAddress:X16} " +
                        $"mode={(jumpMode == 0 ? "call" : "chain")} " +
                        $"target=0x{chainAddress:X16} dwords={chainLength}");

                    // A call keeps the parent continuation. A chain replaces it.
                    return false;
                }

                // target=1, size=0 is the ring-chunk-advance sentinel: continue
                // at the next contiguous chunk. Distinct from padding (target=0).
                if (chainAddress == 1 && state.RingChunkBase != 0)
                {
                    var nextChunk = state.RingChunkBase + RingChunkBytes;
                    TraceAgc(
                        $"agc.dcb.chunk_advance from=0x{currentAddress:X16} " +
                        $"next=0x{nextChunk:X16}");

                    state.PendingChainAddress = nextChunk;
                    state.PendingChainDwords = RingChunkBytes / sizeof(uint);
                    state.RingChunkBase = nextChunk;
                    state.FollowedChunkAdvance = true;
                    return false;
                }
            }

            if (op == ItNop &&
                register is RDrawReset or RAcbReset &&
                length >= 2)
            {
                ResetSubmittedParserState(state);
                TraceAgc(
                    $"agc.queue_reset queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"kind={(register == RDrawReset ? "draw" : "acb")} " +
                    $"packet=0x{currentAddress:X16}");
            }

            if (isAcquireMem)
            {
                ApplySubmittedAcquireMem(
                    ctx,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItSetShReg &&
                TryReadTextureDescriptor(ctx, currentAddress, length, out var texture))
            {
                state.PresenterTexture = texture;
            }

            ApplySubmittedRegisters(ctx, state, currentAddress, length, op, register);
            ApplySubmittedCompositeDepthExtent(
                ctx,
                state,
                currentAddress,
                dwordCount - offset,
                length,
                op);

            if (op == ItSetBase &&
                length >= 4 &&
                TryReadUInt32(ctx, currentAddress + 4, out var baseSelector) &&
                baseSelector == 1 &&
                TryReadUInt64(ctx, currentAddress + 8, out var indirectArgsAddress))
            {
                state.IndirectArgsAddress = indirectArgsAddress;
            }

            if (op == ItEventWrite &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + sizeof(uint), out var eventTypeRaw))
            {
                // IT_EVENT_WRITE has no interrupt selector on hardware; EOP
                // interrupts come from RELEASE_MEM only. Delivering kernel
                // events here would over-count completions.
                if (tracePackets)
                {
                    TraceAgc($"agc.dcb.event type=0x{eventTypeRaw & 0x3Fu:X2} queues=none");
                }
            }

            if (op == ItNop && register == RReleaseMem && length >= 7)
            {
                ApplySubmittedReleaseMem(ctx, gpuState, state, currentAddress, tracePackets);
            }

            if (op == ItReleaseMem && length >= 8)
            {
                ApplySubmittedStandardReleaseMem(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItCondWrite && length >= 9)
            {
                ApplySubmittedCondWrite(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    tracePackets);
            }

            if (op == ItAtomicMem &&
                HandleSubmittedAtomicMem(
                    ctx,
                    gpuState,
                    state,
                    commandAddress,
                    currentAddress,
                    offset,
                    length,
                    dwordCount,
                    tracePackets))
            {
                return true;
            }

            if (op == ItMemSemaphore)
            {
                if (HandleSubmittedMemSemaphore(
                        ctx,
                        gpuState,
                        state,
                        commandAddress,
                        currentAddress,
                        offset,
                        length,
                        dwordCount,
                        tracePackets))
                {
                    return true;
                }
            }

            if (op == ItCopyData &&
                HandleSubmittedCopyData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    tracePackets))
            {
                return true;
            }

            if (op == ItNop && register == RWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: false,
                    tracePacket: tracePackets);
            }

            if (op == ItWriteData && length >= 4)
            {
                ApplySubmittedWriteData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    standardPacket: true,
                    tracePacket: tracePackets);
            }

            if (op == ItNop && register == RDmaData && length >= 7)
            {
                var targets = GetRenderTargets(state.CxRegisters);
                TrackColorMetadataAddresses(state.CxRegisters, targets);

                ApplySubmittedDmaData(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    compactLayout: length == 7,
                    tracePacket: tracePackets);
            }

            if (op == ItDmaData && length >= 7)
            {
                ApplySubmittedStandardDmaData(ctx, gpuState, state, currentAddress);
            }

            if (op == ItIndexBase &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBaseLo) &&
                TryReadUInt32(ctx, currentAddress + 8, out var indexBaseHi))
            {
                state.IndexBufferAddress =
                    indexBaseLo | ((ulong)indexBaseHi << 32);
            }

            if (op == ItIndexBufferSize &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexBufferCount))
            {
                state.IndexBufferCount = indexBufferCount;
            }

            if (op == ItNop &&
                register == RIndexCount &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var customIndexCount))
            {
                state.IndexBufferCount = customIndexCount;
            }

            if (op == ItIndexType &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var indexSize))
            {
                state.IndexSize = indexSize & 0x3;
            }

            if (op == ItNumInstances &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var instanceCount))
            {
                state.InstanceCount = Math.Max(instanceCount, 1);
            }

            if (op == ItNop &&
                register is RWaitMem32 or RWaitMem64 &&
                length >= (register == RWaitMem32 ? 6u : 9u))
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: register == RWaitMem64, isStandard: false,
                        tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (op == ItWaitRegMem && length >= 7)
            {
                if (HandleSubmittedWaitRegMem(
                        ctx, state, commandAddress, currentAddress, offset, length,
                        dwordCount, is64Bit: false, isStandard: true, tracePackets))
                {
                    FlushPendingAcquireInvalidation(ctx, state, tracePackets);
                    return true; // DCB suspended until the awaited label is written
                }
            }

            if (TryReadSubmittedDrawCount(
                    ctx,
                    gpuState,
                    state,
                    currentAddress,
                    length,
                    op,
                    out var indexCount) &&
                indexCount != 0)
            {
                state.FrameDrawCount++;
                if (_traceAgcShader)
                {
                    lock (_submitTraceGate)
                    {
                        if (_tracedSubmittedDrawOpcodes.Add(op))
                        {
                            TraceAgcShader(
                                $"agc.draw_packet op=0x{op:X2} count={indexCount}");
                        }
                    }
                }

                var indexed = op is
                    ItDrawIndex2 or
                    ItDrawIndexOffset2 or
                    ItDrawIndexIndirect or
                    ItDrawIndexIndirectMulti;
                state.SawIndexedDraw |= indexed;
                var drawStarted = DcbParseProfile.Begin();
                try
                {
                    TryTranslateGuestDraw(ctx, gpuState, state, indexCount, indexed);
                }
                finally
                {
                    DcbParseProfile.RecordDraw(drawStarted);
                    state.CurrentIndexSnapshot = null;
                    state.CurrentVertexSnapshot = null;
                }
            }

            if (op == ItNop &&
                register == RDrawIndexAuto &&
                length >= 2 &&
                TryReadUInt32(ctx, currentAddress + 4, out var autoIndexCount) &&
                autoIndexCount != 0)
            {
                state.FrameDrawCount++;
                var drawStarted = DcbParseProfile.Begin();
                TryTranslateGuestDraw(
                    ctx,
                    gpuState,
                    state,
                    autoIndexCount,
                    indexed: false);
                DcbParseProfile.RecordDraw(drawStarted);
            }

            if (op is ItDispatchDirect or ItDispatchIndirect)
            {
                if (TryReadComputeDispatch(
                        ctx,
                        state,
                        currentAddress,
                        length,
                        op,
                        out var dispatch,
                        out _))
                {
                    state.FrameDispatchCount++;
                    var dispatchStarted = DcbParseProfile.Begin();
                    ObserveComputeDispatch(ctx, gpuState, state, dispatch);
                    DcbParseProfile.RecordDispatch(dispatchStarted);
                }
            }

            if (op == ItNop &&
                register == RWaitFlipDone &&
                length >= 3 &&
                TryReadUInt32(ctx, currentAddress + 4, out var waitVideoOutHandle) &&
                TryReadUInt32(ctx, currentAddress + 8, out var waitDisplayBufferIndex))
            {
                var waitSequence = GuestGpu.Current.SubmitOrderedGuestFlipWait(
                    unchecked((int)waitVideoOutHandle),
                    unchecked((int)waitDisplayBufferIndex));
                TraceAgcShader(
                    $"agc.flip_wait_safe queue={state.QueueName} " +
                    $"submission={state.ActiveSubmissionId} " +
                    $"handle={waitVideoOutHandle} index={waitDisplayBufferIndex} " +
                    $"work_sequence={waitSequence}");
            }

            if (op == ItNop && register == RFlip && length >= 6)
            {
                var flipStarted = DcbParseProfile.Begin();
                TraceFramePacketSummary(state);
                SyncCpuWrittenGuestImages(ctx);
                GpuWaitRegistry.AdvanceFrame();
                if (!TryReadUInt32(ctx, currentAddress + 4, out var videoOutHandle) ||
                    !TryReadUInt32(ctx, currentAddress + 8, out var displayBufferIndexRaw) ||
                    !TryReadUInt32(ctx, currentAddress + 12, out var flipMode) ||
                    !TryReadUInt32(ctx, currentAddress + 16, out var flipArgLo) ||
                    !TryReadUInt32(ctx, currentAddress + 20, out var flipArgHi))
                {
                    return false;
                }

                var flipArg = unchecked((long)(((ulong)flipArgHi << 32) | flipArgLo));
                var displayBufferIndex = unchecked((int)displayBufferIndexRaw);
                var handle = unchecked((int)videoOutHandle);
                if (state.PendingTargetlessDraw is { } pendingComposite &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var pendingDisplayBuffer) &&
                    state.KnownRenderTargets.TryGetValue(
                        pendingDisplayBuffer.Address,
                        out var pendingDisplayTarget))
                {
                    var textures = CreateGuestDrawTextures(
                        ctx,
                        pendingComposite.Textures,
                        out _);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffers(pendingComposite);
                    var vertexBuffers =
                        CreateGuestVertexBuffers(pendingComposite.VertexInputs);
                    ProvideRenderTargetInitialData(ctx, pendingDisplayTarget);
                    GuestGpu.Current.SubmitOffscreenTranslatedDraw(
                        pendingComposite.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        pendingComposite.AttributeCount,
                        [CreateGuestRenderTarget(pendingDisplayTarget)],
                        pendingComposite.VertexShader,
                        pendingComposite.VertexCount,
                        pendingComposite.InstanceCount,
                        pendingComposite.PrimitiveType,
                        pendingComposite.IndexBuffer,
                        vertexBuffers,
                        pendingComposite.RenderState,
                        pendingComposite.DepthTarget,
                        pendingComposite.PixelShaderAddress,
                        pendingComposite.BaseVertex);
                    TraceAgcShader(
                        $"agc.deferred_composite ps=0x{pendingComposite.PixelShaderAddress:X16} " +
                        $"src=0x{pendingComposite.Textures.FirstOrDefault()?.Descriptor.Address ?? 0:X16} " +
                        $"dst=0x{pendingDisplayTarget.Address:X16} " +
                        $"size={pendingDisplayTarget.Width}x{pendingDisplayTarget.Height}");
                    state.PendingTargetlessDraw = null;
                    state.TranslatedDraw = null;
                }

                if (VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var cachedDisplayBuffer) &&
                    GuestGpu.Current.TrySubmitOrderedGuestImageFlip(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer.Address,
                        cachedDisplayBuffer.Width,
                        cachedDisplayBuffer.Height,
                        cachedDisplayBuffer.PitchInPixel))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        cachedDisplayBuffer,
                        "gpu-cache");
                }
                else if (state.SawIndexedDraw &&
                    state.TranslatedDraw is { } translatedDraw &&
                    VideoOutExports.TryGetDisplayBufferInfo(
                        handle,
                        displayBufferIndex,
                        out var translatedDisplayBuffer))
                {
                    TraceDisplayBuffer(
                        handle,
                        displayBufferIndex,
                        translatedDisplayBuffer,
                        "draw-fallback");
                    var textures = CreateGuestDrawTextures(ctx, translatedDraw.Textures, out var fallbackTextureCount);
                    var globalMemoryBuffers =
                        CreateTranslatedDrawGlobalBuffersForPresent(ctx, translatedDraw);
                    GuestGpu.Current.SubmitTranslatedDraw(
                        translatedDraw.PixelShader,
                        textures,
                        globalMemoryBuffers,
                        translatedDisplayBuffer.Width,
                        translatedDisplayBuffer.Height,
                        translatedDraw.AttributeCount);
                    TraceAgcShader(
                        $"agc.shader_present ps=0x{translatedDraw.PixelShaderAddress:X16} " +
                        $"spirv={translatedDraw.PixelShader.Payload.Length} textures={textures.Count} " +
                        $"global_buffers={globalMemoryBuffers.Count} " +
                        $"fallback={fallbackTextureCount} {translatedDisplayBuffer.Width}x{translatedDisplayBuffer.Height}");

                    for (var i = 0; i < translatedDraw.Textures.Count; i++)
                    {
                        var binding = translatedDraw.Textures[i];
                        var d = binding.Descriptor;

                        TraceAgcShader(
                            $"agc.present_desc[{i}] " +
                            $"addr=0x{d.Address:X16} " +
                            $"size={d.Width}x{d.Height} " +
                            $"fmt={d.Format} " +
                            $"num={d.NumberType} " +
                            $"type={d.Type} " +
                            $"tile={d.TileMode} " +
                            $"storage={binding.IsStorage}");
                    }
                }
                else if (state.SawIndexedDraw && state.PresenterTexture is { } sourceTexture)
                {
                    _ = TrySoftwarePresent(
                        ctx,
                        sourceTexture,
                        unchecked((int)videoOutHandle),
                        displayBufferIndex);
                }
                else if (state.SawIndexedDraw &&
                         state.GuestDrawKind != GuestDrawKind.None &&
                         VideoOutExports.TryGetDisplayBufferInfo(
                             handle,
                             displayBufferIndex,
                             out var displayBuffer))
                {
                    GuestGpu.Current.SubmitGuestDraw(
                        state.GuestDrawKind,
                        displayBuffer.Width,
                        displayBuffer.Height);
                }

                // A SetFlip reached via the orphan force-submit path is the
                // game's own packet, physically shared with a real queue's
                // ring — force-submitting it can race ahead of that queue's
                // natural parse and present it twice once the real queue
                // catches up. Only a genuine game queue may flip.
                if (!state.IsForceSubmittedRing)
                {
                    _ = VideoOutExports.SubmitFlipFromAgc(ctx, handle, displayBufferIndex, unchecked((int)flipMode), flipArg);
                    ResetVideoDrawChainAtFlip();
                }

                state.SawIndexedDraw = false;
                state.GuestDrawKind = GuestDrawKind.None;
                if (state.PendingTargetlessDraw is { } unusedPendingDraw)
                {
                    ReturnPooledDrawArrays(
                        unusedPendingDraw,
                        globals: true,
                        vertex: true,
                        index: true);
                    state.PendingTargetlessDraw = null;
                }
                state.TranslatedDraw = null;
                DcbParseProfile.RecordFlip(flipStarted);
            }

            offset += length;
            state.LastParsedAddress = commandAddress + (ulong)offset * sizeof(uint);
        }

        FlushPendingAcquireInvalidation(ctx, state, tracePackets);
        return false;
    }

    private static void TraceFramePacketSummary(SubmittedDcbState state)
    {
        if (!_traceFramePackets)
        {
            return;
        }

        var flip = ++state.FlipCount;
        if (flip <= 8 || flip % 60 == 0 || state.FrameDrawCount == 0)
        {
            var opcodes = string.Join(
                ',',
                state.FramePacketCounts
                    .OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key.Op)
                    .Take(32)
                    .Select(entry => entry.Key.Register == uint.MaxValue
                        ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                        : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
            Console.Error.WriteLine(
                $"[FRAMEPKT] flip={flip} submission={state.ActiveSubmissionId} " +
                $"packets={state.FramePacketCount} draws={state.FrameDrawCount} " +
                $"dispatches={state.FrameDispatchCount} opcodes=[{opcodes}]");
        }

        state.FramePacketCounts.Clear();
        state.FramePacketCount = 0;
        state.FrameDrawCount = 0;
        state.FrameDispatchCount = 0;
    }

    private static void TracePacketParseFailure(
        SubmittedDcbState state,
        ulong address,
        uint offset,
        uint header,
        string reason)
    {
        if (!_traceFramePackets ||
            Interlocked.Increment(ref _packetParseFailureTraceCount) > 128)
        {
            return;
        }

        Console.Error.WriteLine(
            $"[FRAMEPKT] parse-failure queue={state.QueueName} " +
            $"submission={state.ActiveSubmissionId} offset={offset} " +
            $"address=0x{address:X16} header=0x{header:X8} reason={reason}");
    }

    private static void TraceDisplayBuffer(
        int handle,
        int index,
        VideoOutExports.DisplayBufferInfo buffer,
        string path)
    {
        lock (_submitTraceGate)
        {
            if (!_tracedDisplayBuffers.Add((handle, index, buffer.Address, path)))
            {
                return;
            }
        }

        TraceAgcShader(
            $"agc.display_buffer handle={handle} index={index} " +
            $"addr=0x{buffer.Address:X16} fmt=0x{buffer.PixelFormat:X16} " +
            $"tile={buffer.TilingMode} size={buffer.Width}x{buffer.Height} " +
            $"pitch={buffer.PitchInPixel} path={path}");
    }

    private static readonly Dictionary<(uint Op, uint Register), long> _submittedOpcodeCounts = new();
    private static long _submittedOpcodeTotal;
    private static int _depthRegisterStateTraceCount;

    private static void CountSubmittedOpcode(uint op, uint register)
    {
        var key = (op, op == ItNop ? register : uint.MaxValue);
        lock (_submittedOpcodeCounts)
        {
            _submittedOpcodeCounts[key] =
                _submittedOpcodeCounts.TryGetValue(key, out var count) ? count + 1 : 1;
            if (++_submittedOpcodeTotal % 500_000 == 0)
            {
                var summary = string.Join(
                    ' ',
                    _submittedOpcodeCounts
                        .OrderByDescending(entry => entry.Value)
                        .Select(entry => entry.Key.Register == uint.MaxValue
                            ? $"0x{entry.Key.Op:X2}:{entry.Value}"
                            : $"0x{entry.Key.Op:X2}/r{entry.Key.Register}:{entry.Value}"));
                Console.Error.WriteLine($"[PKT] total={_submittedOpcodeTotal} {summary}");
            }
        }
    }

    private static void ApplySubmittedRegisters(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint packetLength,
        uint op,
        uint register)
    {
        if (op is ItSetShReg or ItSetContextReg or ItSetUconfigReg or ItSetUconfigRegIndex)
        {
            if (packetLength < 3 ||
                !TryReadUInt32(ctx, packetAddress + sizeof(uint), out var startRegister))
            {
                return;
            }

            if (op == ItSetUconfigRegIndex)
            {
                startRegister &= 0x0FFF_FFFFu;
            }

            var directDestination = op switch
            {
                ItSetShReg => state.ShRegisters,
                ItSetContextReg => state.CxRegisters,
                _ => state.UcRegisters,
            };
            for (uint index = 0; index < packetLength - 2; index++)
            {
                if (!TryReadUInt32(
                        ctx,
                        packetAddress + 8 + ((ulong)index * sizeof(uint)),
                        out var value))
                {
                    return;
                }

                directDestination[startRegister + index] = value;
                if (op == ItSetContextReg &&
                    startRegister + index is DbZInfo or DbDepthSizeXy)
                {
                    // A standalone attachment or extent write supersedes the
                    // extent carried by an earlier composite binding packet.
                    state.CompositeDepthSizeXy = null;
                }
                if (op is ItSetUconfigReg or ItSetUconfigRegIndex)
                {
                    ApplyUcIndexTypeIfNeeded(state, startRegister + index, value);
                }

                if (op == ItSetContextReg)
                {
                    TraceSubmittedDepthRegisterState(
                        state,
                        packetAddress,
                        "direct",
                        startRegister + index,
                        value);
                }
            }

            return;
        }

        Dictionary<uint, uint> destination;
        uint registerCount;
        ulong registersAddress;
        uint indirectRegister;
        if (op is ItSetContextRegIndirect or ItSetShRegIndirect or ItSetUconfigRegIndirect)
        {
            if (packetLength < 5 ||
                !TryReadUInt64(ctx, packetAddress + 4, out registersAddress) ||
                !TryReadUInt32(ctx, packetAddress + 16, out registerCount))
            {
                return;
            }

            registersAddress &= ~0x3UL;
            registerCount &= 0x3FFFu;
            indirectRegister = op switch
            {
                ItSetContextRegIndirect => RCxRegsIndirect,
                ItSetShRegIndirect => RShRegsIndirect,
                _ => RUcRegsIndirect,
            };
        }
        else if (op == ItNop &&
                 register is RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect &&
                 packetLength >= 4 &&
                 TryReadUInt32(ctx, packetAddress + sizeof(uint), out registerCount) &&
                 TryReadUInt64(ctx, packetAddress + 8, out registersAddress))
        {
            // Accept command buffers produced by older builds. New packets use
            // the native five-dword SET_*_REG_INDIRECT encoding above.
            indirectRegister = register;
        }
        else
        {
            return;
        }

        destination = indirectRegister switch
        {
            RCxRegsIndirect => state.CxRegisters,
            RShRegsIndirect => state.ShRegisters,
            _ => state.UcRegisters,
        };
        for (uint index = 0; index < registerCount; index++)
        {
            var entryAddress = registersAddress + ((ulong)index * 8);
            uint registerOffset;
            uint value;
            if (!TryReadUInt32(ctx, entryAddress, out registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + sizeof(uint), out value))
            {
                return;
            }

            // The indirect table has an explicit count; offset zero is a real
            // context-register index (DB_RENDER_CONTROL), not a terminator.
            // Dropping it leaves stale depth/render-control state active in
            // later passes.
            registerOffset &= ~0x7000_0000u;
            if (indirectRegister == RUcRegsIndirect && registerOffset == DbDepthSizeXy)
            {
                // Indirect UC tables may carry recognized context registers.
                // Apply the depth extent to the state consumed by draw setup
                // instead of leaving a stale value in the context register set.
                state.CxRegisters[registerOffset] = value;
                state.CompositeDepthSizeXy = null;
                TraceSubmittedDepthRegisterState(
                    state,
                    entryAddress,
                    "indirect-uc",
                    registerOffset,
                    value);
                continue;
            }

            destination[registerOffset] = value;
            if (indirectRegister == RCxRegsIndirect)
            {
                TraceSubmittedDepthRegisterState(
                    state,
                    entryAddress,
                    "indirect",
                    registerOffset,
                    value);
            }
            if (indirectRegister == RUcRegsIndirect)
            {
                ApplyUcIndexTypeIfNeeded(state, registerOffset, value);
            }
        }
    }

    private static void ApplySubmittedCompositeDepthExtent(
        CpuContext ctx,
        SubmittedDcbState state,
        ulong packetAddress,
        uint remainingDwords,
        uint packetLength,
        uint op)
    {
        const uint DbDepthInfo = 0x00Fu;
        const uint compositeDwordCount = 24;
        if (op != ItSetContextReg ||
            packetLength != 10 ||
            remainingDwords < compositeDwordCount ||
            !TryReadUInt32(ctx, packetAddress + 4, out var startRegister) ||
            startRegister != DbZInfo ||
            !TryReadUInt32(ctx, packetAddress + 40, out var depthInfoHeader) ||
            depthInfoHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 44, out var depthInfoRegister) ||
            depthInfoRegister != DbDepthInfo ||
            !TryReadUInt32(ctx, packetAddress + 52, out var depthViewHeader) ||
            depthViewHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 56, out var depthViewRegister) ||
            depthViewRegister != DbDepthView ||
            !TryReadUInt32(ctx, packetAddress + 64, out var htileBaseHeader) ||
            htileBaseHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 68, out var htileBaseRegister) ||
            htileBaseRegister != DbHtileDataBase ||
            !TryReadUInt32(ctx, packetAddress + 76, out var htileSurfaceHeader) ||
            htileSurfaceHeader != Pm4(3, ItSetContextReg, 0) ||
            !TryReadUInt32(ctx, packetAddress + 80, out var htileSurfaceRegister) ||
            htileSurfaceRegister != DbHtileSurface ||
            !TryReadUInt32(ctx, packetAddress + 88, out var extentHeader) ||
            extentHeader != Pm4(2, ItNop, 0) ||
            !TryReadUInt32(ctx, packetAddress + 92, out var sizeXy) ||
            sizeXy == 0)
        {
            return;
        }

        // The final NOP payload belongs to the composite depth binding and
        // carries x/y maxima, not padding. Keep it separate from independent
        // DB_DEPTH_SIZE_XY state so later standalone writes can supersede it.
        state.CompositeDepthSizeXy = sizeXy;
        TraceSubmittedDepthRegisterState(
            state,
            packetAddress,
            "composite",
            DbDepthSizeXy,
            sizeXy);
    }

    private static void TraceSubmittedDepthRegisterState(
        SubmittedDcbState state,
        ulong packetAddress,
        string source,
        uint registerOffset,
        uint value)
    {
        if (!_traceDepthMetadata ||
            registerOffset is not (DbZInfo or
                                    DbZReadBase or
                                    DbZWriteBase or
                                    DbZReadBaseHi or
                                    DbZWriteBaseHi or
                                    DbDepthSizeXy) ||
            Interlocked.Increment(ref _depthRegisterStateTraceCount) > 4096)
        {
            return;
        }

        state.CxRegisters.TryGetValue(DbZReadBase, out var readBase);
        state.CxRegisters.TryGetValue(DbZWriteBase, out var writeBase);
        state.CxRegisters.TryGetValue(DbZReadBaseHi, out var readBaseHi);
        state.CxRegisters.TryGetValue(DbZWriteBaseHi, out var writeBaseHi);
        state.CxRegisters.TryGetValue(DbDepthSizeXy, out var rawSizeXy);
        var readAddress =
            ((ulong)(readBaseHi & 0xFFu) << 40) | ((ulong)readBase << 8);
        var writeAddress =
            ((ulong)(writeBaseHi & 0xFFu) << 40) | ((ulong)writeBase << 8);
        var effectiveSizeXy = state.CompositeDepthSizeXy ?? rawSizeXy;
        var width = (effectiveSizeXy & 0x3FFFu) + 1;
        var height = ((effectiveSizeXy >> 16) & 0x3FFFu) + 1;
        var composite = state.CompositeDepthSizeXy is { } compositeSize
            ? $"0x{compositeSize:X8}"
            : "none";

        Console.Error.WriteLine(
            $"[LOADER][TRACE] agc.depth_register_state " +
            $"queue={state.QueueName} submission={state.ActiveSubmissionId} " +
            $"packet=0x{packetAddress:X16} source={source} " +
            $"reg=0x{registerOffset:X3} value=0x{value:X8} " +
            $"read=0x{readAddress:X16} write=0x{writeAddress:X16} " +
            $"raw_size=0x{rawSizeXy:X8} composite={composite} " +
            $"effective={width}x{height}");
    }

    /// <summary>
    /// Test-only view of a parsed graphics context register. False when the
    /// register was never written.
    /// </summary>
    internal static bool TryGetGraphicsContextRegisterForTests(
        CpuContext ctx,
        uint registerOffset,
        out uint value)
    {
        value = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.CxRegisters.TryGetValue(registerOffset, out value);
        }
    }

    internal static bool TryGetGraphicsCompositeDepthSizeForTests(
        CpuContext ctx,
        out uint sizeXy)
    {
        sizeXy = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            if (gpuState.Graphics.CompositeDepthSizeXy is not { } value)
            {
                return false;
            }

            sizeXy = value;
            return true;
        }
    }

    /// <summary>
    /// SH-register counterpart of <see cref="TryGetGraphicsContextRegisterForTests"/>;
    /// the shader stage addresses live here.
    /// </summary>
    internal static bool TryGetGraphicsShRegisterForTests(
        CpuContext ctx,
        uint registerOffset,
        out uint value)
    {
        value = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            return gpuState.Graphics.ShRegisters.TryGetValue(registerOffset, out value);
        }
    }

    internal static bool TryGetGraphicsIndexStateForTests(
        CpuContext ctx,
        out ulong address,
        out uint count,
        out uint offset)
    {
        address = 0;
        count = 0;
        offset = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            address = gpuState.Graphics.IndexBufferAddress;
            count = gpuState.Graphics.IndexBufferCount;
            offset = gpuState.Graphics.DrawIndexOffset;
            return true;
        }
    }

    internal static bool TryGetGraphicsIndexSizeForTests(
        CpuContext ctx,
        out uint indexSize)
    {
        indexSize = 0;
        if (!_submittedGpuStates.TryGetValue(ctx.Memory, out var gpuState))
        {
            return false;
        }

        lock (gpuState.Gate)
        {
            indexSize = gpuState.Graphics.IndexSize;
            return true;
        }
    }

    /// <summary>
    /// GraphicsDcbSetIndexSize writes VGT_INDEX_TYPE via SET_UCONFIG_REG.
    /// Mirror that into <see cref="SubmittedDcbState.IndexSize"/>.
    /// </summary>
    private static void ApplyUcIndexTypeIfNeeded(
        SubmittedDcbState state,
        uint registerOffset,
        uint value)
    {
        if (registerOffset == VgtIndexType)
        {
            state.IndexSize = value & 0x3;
        }
    }

    private static void TraceSubmittedPacket(
        CpuContext ctx,
        ulong packetAddress,
        uint dwordOffset,
        uint header,
        uint length,
        uint op,
        uint register)
    {
        TraceAgc(
            $"agc.dcb.packet dw={dwordOffset} addr=0x{packetAddress:X16} header=0x{header:X8} len={length} op=0x{op:X2} reg=0x{register:X2}");

        var payloadCount = Math.Min(length - 1, 32u);
        for (uint i = 0; i < payloadCount; i++)
        {
            if (!TryReadUInt32(ctx, packetAddress + ((ulong)(i + 1) * sizeof(uint)), out var value))
            {
                return;
            }

            TraceAgc($"agc.dcb.payload dw={dwordOffset + i + 1} value=0x{value:X8}");
        }

        if (op != ItNop ||
            register is not (RCxRegsIndirect or RShRegsIndirect or RUcRegsIndirect) ||
            length < 4 ||
            !TryReadUInt32(ctx, packetAddress + 4, out var registerCount) ||
            !TryReadUInt64(ctx, packetAddress + 8, out var registersAddress))
        {
            return;
        }

        var registerSpace = register == RCxRegsIndirect ? "cx" : register == RShRegsIndirect ? "sh" : "uc";
        var tracedCount = Math.Min(registerCount, 256u);
        TraceAgc($"agc.dcb.indirect space={registerSpace} regs=0x{registersAddress:X16} count={registerCount}");
        for (uint i = 0; i < tracedCount; i++)
        {
            var entryAddress = registersAddress + ((ulong)i * 8);
            if (!TryReadUInt32(ctx, entryAddress, out var registerOffset) ||
                !TryReadUInt32(ctx, entryAddress + 4, out var value))
            {
                TraceAgc($"agc.dcb.indirect_read_failed space={registerSpace} index={i} addr=0x{entryAddress:X16}");
                return;
            }

            TraceAgc($"agc.dcb.reg space={registerSpace} index={i} offset=0x{registerOffset:X4} value=0x{value:X8}");
        }

        if (tracedCount != registerCount)
        {
            TraceAgc($"agc.dcb.indirect_truncated space={registerSpace} traced={tracedCount} total={registerCount}");
        }
    }
}
