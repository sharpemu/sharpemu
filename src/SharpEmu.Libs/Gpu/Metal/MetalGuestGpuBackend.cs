// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Libs.Gpu.Metal;

/// <summary>
/// Metal backend for the guest-GPU seam: MSL codegen via
/// SharpEmu.ShaderCompiler.Metal, rendering via the Metal presenter — the full
/// surface (presentation, guest images, ordered flips, translated draws, and
/// compute) with no Vulkan, MoltenVK, or windowing-library dependency.
/// </summary>
internal sealed class MetalGuestGpuBackend : IGuestGpuBackend, IGuestImageSnapshotBackend
{
    // Only a backend with CPU image snapshots arms the guest image write tracker.
    public MetalGuestGpuBackend() => SharpEmu.HLE.GuestImageWriteTracker.Configure(true);

    public string BackendName => "Metal";


    private static readonly IGuestCompiledShader DepthOnlyFragmentShader =
        new MetalCompiledGuestShader(new Gen5MslShader(
            MslFixedShaders.CreateDepthOnlyFragment(),
            "depth_only_fs",
            Gen5MslStage.Pixel,
            AttributeCount: 0));


    public bool TryCompileProgram(ShaderCompileRequest request, out IGuestCompiledShader? shader, out string error)
    {
        shader = null;
        if (!Gen5MslTranslator.TryCompileProgram(request, out var compiled, out error))
        {
            return false;
        }

        shader = new MetalCompiledGuestShader(compiled);
        return true;
    }

    public IGuestCompiledShader GetDepthOnlyFragmentShader() =>
        DepthOnlyFragmentShader;

    public void EnsureStarted(uint width, uint height) =>
        MetalVideoPresenter.EnsureStarted(width, height);

    public void HideSplashScreen() =>
        MetalVideoPresenter.HideSplashScreen();

    public void Submit(byte[] bgraFrame, uint width, uint height) =>
        MetalVideoPresenter.Submit(bgraFrame, width, height);

    public bool TrySubmitGuestImage(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel,
        ulong flipRequestId)
    {
        // With a command stream the flip queues behind the graphics submissions before it.
        CommandStreamQueue? commandStream;
        lock (_commandStreamGate)
        {
            commandStream = _commandStream;
        }

        if (commandStream is not null)
        {
            return commandStream.TryEnqueueFlipPreparation(videoOutHandle, displayBufferIndex, flipRequestId);
        }

        var submitted = MetalVideoPresenter.TrySubmitGuestImage(address, width, height, pitchInPixel);
        if (submitted)
        {
            VideoOut.VideoOutExports.MarkFlipPresented(flipRequestId);
        }

        return submitted;
    }

    // The worker runs the interpreter and retries blocked submissions.
    private readonly object _commandStreamGate = new();
    private CommandStreamQueue? _commandStream;
    private CommandStreamWorker? _commandStreamWorker;

    private CommandStreamQueue EnsureCommandStream(ICpuMemory memory)
    {
        lock (_commandStreamGate)
        {
            if (_commandStream is { } existing)
            {
                return existing;
            }

            var host = new MetalCommandStreamHost(memory, this, this);
            var queue = new CommandStreamQueue(host);
            host.AttachQueue(queue);
            _commandStreamWorker = new CommandStreamWorker(queue, host, static () => false, cancelBlockedAtStop: true, "SharpEmu Metal command stream");
            _commandStreamWorker.Start();
            _commandStream = queue;
            return queue;
        }
    }

    public void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots)
    {
        var commandStream = EnsureCommandStream(memory);
        if (queue == 0)
        {
            commandStream.EnqueueGraphics(address, dwordCount, submissionId, geometrySnapshots);
        }
        else
        {
            commandStream.EnqueueCompute(queue, address, dwordCount, submissionId, geometrySnapshots);
        }
    }

    public IdleOutcome SubmitDone(ICpuMemory memory) => EnsureCommandStream(memory).Done();

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) =>
        MetalVideoPresenter.RegisterKnownDisplayBuffer(address, guestFormat);

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) =>
        MetalVideoPresenter.IsGuestImageAvailable(address, format, numberType);

    public bool TrySubmitGuestImageBlit(GuestRenderTarget source, GuestRenderTarget destination) =>
        MetalVideoPresenter.TrySubmitGuestImageBlit(
            source.Address,
            source.Width,
            source.Height,
            source.Format,
            source.NumberType,
            destination.Address,
            destination.Width,
            destination.Height,
            destination.Format,
            destination.NumberType);

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) =>
        MetalVideoPresenter.SubmitGuestDraw(drawKind, width, height);

    public void SubmitTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint width,
        uint height,
        uint attributeCount,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null) =>
        MetalVideoPresenter.SubmitTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            width,
            height,
            attributeCount,
            vertexShader is null ? null : Msl(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState);

    public void SubmitDepthOnlyTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        GuestDepthTarget depthTarget,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        ulong shaderAddress = 0,
        int baseVertex = 0,
        IReadOnlyList<GuestStageBindings>? stageBindings = null) =>
        MetalVideoPresenter.SubmitDepthOnlyTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            depthTarget,
            vertexShader is null ? null : Msl(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            shaderAddress,
            baseVertex,
            stageBindings);

    public void SubmitOffscreenTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        IReadOnlyList<GuestRenderTarget> targets,
        IGuestCompiledShader? vertexShader = null,
        uint vertexCount = 3,
        uint instanceCount = 1,
        uint primitiveType = 4,
        GuestIndexBuffer? indexBuffer = null,
        IReadOnlyList<GuestVertexBuffer>? vertexBuffers = null,
        GuestRenderState? renderState = null,
        GuestDepthTarget? depthTarget = null,
        ulong shaderAddress = 0,
        int baseVertex = 0,
        IReadOnlyList<GuestStageBindings>? stageBindings = null) =>
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            targets,
            vertexShader is null ? null : Msl(vertexShader),
            vertexCount,
            instanceCount,
            primitiveType,
            indexBuffer,
            vertexBuffers,
            renderState,
            depthTarget,
            shaderAddress,
            baseVertex,
            stageBindings);

    public void SubmitStorageTranslatedDraw(
        IGuestCompiledShader pixelShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint attributeCount,
        uint width,
        uint height,
        ulong shaderAddress = 0) =>
        MetalVideoPresenter.SubmitStorageTranslatedDraw(
            Msl(pixelShader),
            textures,
            globalMemoryBuffers,
            attributeCount,
            width,
            height,
            shaderAddress);

    private static MetalCompiledGuestShader Msl(IGuestCompiledShader shader) =>
        shader as MetalCompiledGuestShader ??
        throw new InvalidOperationException(
            $"shader handle of type {shader.GetType().Name} was not compiled by the Metal backend");

    public long SubmitComputeDispatch(
        ulong shaderAddress,
        IGuestCompiledShader computeShader,
        IReadOnlyList<GuestDrawTexture> textures,
        IReadOnlyList<GuestMemoryBuffer> globalMemoryBuffers,
        uint groupCountX,
        uint groupCountY,
        uint groupCountZ,
        uint baseGroupX,
        uint baseGroupY,
        uint baseGroupZ,
        uint localSizeX,
        uint localSizeY,
        uint localSizeZ,
        bool isIndirect,
        bool writesGlobalMemory,
        uint threadCountX = uint.MaxValue,
        uint threadCountY = uint.MaxValue,
        uint threadCountZ = uint.MaxValue,
        GuestStageBindings? stageBindings = null)
    {
        // The translated kernel bakes its threadgroup size and thread bounds;
        // localSize, isIndirect and the thread counts are folded in before submission.
        _ = (localSizeX, localSizeY, localSizeZ, isIndirect, threadCountX, threadCountY, threadCountZ);
        return MetalVideoPresenter.SubmitComputeDispatch(
            shaderAddress,
            Msl(computeShader),
            textures,
            globalMemoryBuffers,
            groupCountX,
            groupCountY,
            groupCountZ,
            baseGroupX,
            baseGroupY,
            baseGroupZ,
            writesGlobalMemory,
            stageBindings);
    }

    private long _perfShaderCompilations;

    public long SubmitGlobalDataShareFill(ulong offset, ulong size, byte value) =>
        MetalVideoPresenter.SubmitGlobalDataShareFill(offset, size, value);

    public long SubmitGlobalDataShareCopyFromGuest(ulong offset, byte[] bytes) =>
        MetalVideoPresenter.SubmitGlobalDataShareCopyFromGuest(offset, bytes);

    public long SubmitGlobalDataShareCopyToGuest(ulong guestAddress, ulong offset, ulong size) =>
        MetalVideoPresenter.SubmitGlobalDataShareCopyToGuest(guestAddress, offset, size);

    public void ReadGlobalDataShare(Span<uint> destination, uint wordOffset, uint wordCount) =>
        MetalVideoPresenter.ReadGlobalDataShare(destination, wordOffset, wordCount);

    public long SubmitGpuSynchronization(string debugName) =>
        MetalVideoPresenter.SubmitOrderedGpuWait(debugName);

    public bool IsGuestImageUploadKnown(ulong address, uint format, uint numberType) =>
        MetalVideoPresenter.IsGuestImageUploadKnown(address, format, numberType);

    public bool GuestImageWantsInitialData(ulong address) =>
        MetalVideoPresenter.GuestImageWantsInitialData(address);

    public void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels) =>
        MetalVideoPresenter.ProvideGuestImageInitialData(address, rgbaPixels);

    public void SubmitGuestImageFill(ulong address, uint fillValue) =>
        MetalVideoPresenter.SubmitGuestImageFill(address, fillValue);

    public void SubmitGuestImageWrite(ulong address, byte[] pixels, uint rowOffset = 0) =>
        MetalVideoPresenter.SubmitGuestImageWrite(address, pixels);

    public void RequestCpuWrittenGuestImageSync(ulong scopeAddress = 0, ulong scopeByteCount = ulong.MaxValue) =>
        MetalVideoPresenter.RequestCpuWrittenGuestImageSync(scopeAddress, scopeByteCount);

    public bool TryGetGuestImageExtent(ulong address, out uint width, out uint height, out ulong byteCount) =>
        MetalVideoPresenter.TryGetGuestImageExtent(address, out width, out height, out byteCount);

    public IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents() =>
        MetalVideoPresenter.GetGuestImageExtents();

    public bool IsTextureContentCached(in TextureCacheLookupIdentity identity) =>
        MetalVideoPresenter.IsTextureContentCached(identity);

    public void AttachGuestMemory(SharpEmu.HLE.ICpuMemory memory) =>
        MetalVideoPresenter.AttachGuestMemory(memory);

    // Over-alignment is always valid, and 256 covers every Metal buffer-offset
    // requirement (Intel Macs need 256 for constant buffers; Apple GPUs less).

    public void CountShaderCompilation() =>
        Interlocked.Increment(ref _perfShaderCompilations);

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters()
    {
        var (draws, drawMs, pipelines) = MetalVideoPresenter.ReadAndResetDrawPerfCounters();
        return (draws, drawMs, pipelines, Interlocked.Exchange(ref _perfShaderCompilations, 0));
    }

    // The worker drains before the presenter closes, so its last flips still reach the queue.
    public void RequestClose()
    {
        CommandStreamWorker? worker;
        lock (_commandStreamGate)
        {
            worker = _commandStreamWorker;
        }

        worker?.Stop();
        Console.Error.WriteLine($"[LOADER][PERF] {ShaderCacheCounters.Summary()}");
        MetalVideoPresenter.RequestClose();
    }

}
