// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.VideoOut;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A compiled shader with no payload unless a real module was produced; the host counts it either way.
internal sealed class FakeCompiledShader(ShaderCompileRequest request, byte[]? spirv = null) : IGuestCompiledShader
{
    public ShaderCompileRequest Request => request;

    public byte[] Spirv => spirv ?? [];

    public byte[] Payload => Spirv;

    public string PayloadFileExtension => "fake";
}

// Counts modules and pipelines; every guest read comes straight from the fake memory.
internal sealed class FakePipelineHost(ICpuMemory memory) : IShaderPipelineHost
{
    private ulong _nextHandle = 1;

    public int ImageNormalizations { get; private set; }

    public uint[] NormalizeImageDescriptor(IShaderFrontend frontend, ReadOnlySpan<uint> words,
        ShaderStage stage, ulong shaderHash, string source)
    {
        ImageNormalizations++;
        return frontend.NormalizeImageForResolution(words, stage, shaderHash, source, true);
    }

    public List<(ShaderStage Stage, ulong Hash, ulong ProgramId)> Modules { get; } = new();

    public List<IGuestCompiledShader> CompiledShaders { get; } = new();

    public List<GraphicsPipelineDescription> GraphicsPipelines { get; } = new();

    public List<ComputePipelineDescription> ComputePipelines { get; } = new();

    public SampleCountFlags NoAttachmentSampleCounts { get; set; } = SampleCountFlags.Count1Bit | SampleCountFlags.Count4Bit;

    public uint MaxPushDescriptors => 32;

    public bool ComputeWave64Supported => true;

    public bool GraphicsSubgroupOperationsEnabled => true;

    public bool SharedInt64AtomicsEnabled => false;

    public bool CallMarksSupported { get; set; }

    public long CallMarkSnapshots { get; set; }

    public Dictionary<uint, long> MarkedBits { get; } = [];

    public long CallMarkedAt(uint bit) => MarkedBits.GetValueOrDefault(bit);

    public bool ImageInt64AtomicsEnabled { get; set; }
    public bool MeshShadersSupported { get; set; }

    public MeshShaderLimits MeshLimits { get; set; }
    public uint MeshSubgroupSize { get; set; } = 32;

    public bool TableBlocks { get; set; }

    public bool ShaderSignedZeroInfNanPreserveFloat32Supported { get; set; }

    public ShaderPrewarmList? ShaderPrewarm { get; set; }

    public RenderHostLimits Limits => new(16384, 16384, 16384, 16384);

    public bool TryResolveColorOutput(uint dataFormat, uint numberType, uint componentSwap, out Gen5PixelOutputKind outputKind, out Gen5ColorComponentMapping componentMapping)
    {
        outputKind = Gen5PixelOutputKind.Float;
        componentMapping = default;
        return true;
    }

    public bool TryReadGuestWord(ulong address, out uint word)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        if (!memory.TryRead(address, bytes))
        {
            word = 0;
            return false;
        }

        word = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        return true;
    }

    // Addresses whose clean reads are refused, as for bytes a GPU image or buffer may still own.
    public Func<ulong, bool>? RefuseCleanRead { get; set; }

    public bool TryReadCleanGuestWord(ulong address, out uint word)
    {
        if (RefuseCleanRead?.Invoke(address) == true)
        {
            word = 0;
            return false;
        }

        return TryReadGuestWord(address, out word);
    }

    // Off by default: with resident reads the resource cache starts serving hits.
    public bool ResidentReads { get; set; }

    // A range the test names here stands for bytes the GPU still owns.
    public (ulong Address, ulong Size) GpuOwned { get; set; }

    public bool TryReadResidentGuestBytes(ulong address, Span<byte> destination, bool clean) =>
        ResidentReads && !CpuWriteNeedsGpuData(address, (ulong)destination.Length) && memory.TryRead(address, destination);

    // A pending known write the test names here; a read it covers whole is served from it.
    public (ulong Address, uint[] Words) Shadow { get; set; }

    public bool TryPeekResidentGuestBytes(ulong address, Span<byte> destination)
    {
        var (shadowAddress, words) = Shadow;
        var shadowBytes = words is null ? 0ul : (ulong)words.Length * sizeof(uint);
        if (shadowBytes != 0 && address >= shadowAddress && address + (ulong)destination.Length <= shadowAddress + shadowBytes)
        {
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(words.AsSpan()).Slice((int)(address - shadowAddress), destination.Length).CopyTo(destination);
            return true;
        }

        return TryReadResidentGuestBytes(address, destination, clean: false);
    }

    public bool CpuWriteNeedsGpuData(ulong address, ulong size) =>
        GpuOwned.Size != 0 && address + size > GpuOwned.Address && address < GpuOwned.Address + GpuOwned.Size;

    // Null keeps every evaluated dispatch on the GPU.
    public FakeKernelWriteStream? KernelWriteStream { get; set; }

    public KernelWriteRefusal TryPostKernelWrite(ulong address, ReadOnlySpan<uint> words) =>
        KernelWriteStream is { } stream ? KernelWritePolicy.TryPost(stream, address, words) : KernelWriteRefusal.Unsupported;

    public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
    {
        Modules.Add((stage, hash, programId));
        CompiledShaders.Add(shader);
        return _nextHandle++;
    }

    public PipelineHandle CreateGraphicsPipeline(GraphicsPipelineDescription description)
    {
        GraphicsPipelines.Add(description);
        var handle = _nextHandle++;
        return new PipelineHandle(handle, handle, UsesPushDescriptors: true);
    }

    public PipelineHandle CreateComputePipeline(ComputePipelineDescription description)
    {
        ComputePipelines.Add(description);
        var handle = _nextHandle++;
        return new PipelineHandle(handle, handle, UsesPushDescriptors: true);
    }

    // How many times each program reports "still compiling" before it is handed over,
    // standing in for the host shader compiler running on a worker thread.
    public Dictionary<ulong, int> PendingComputeCompiles { get; } = new();

    public bool TryCreateComputePipeline(ComputePipelineDescription description, out PipelineHandle handle)
    {
        handle = default;
        if (PendingComputeCompiles.TryGetValue(description.Program.Id, out var remaining) && remaining > 0)
        {
            PendingComputeCompiles[description.Program.Id] = remaining - 1;
            return false;
        }

        handle = CreateComputePipeline(description);
        return true;
    }
}

// Remembers every request; compiles nothing unless a module factory was given.
internal sealed class FakeShaderCompiler(Func<ShaderCompileRequest, byte[]>? compile = null) : IGuestGpuBackend
{
    private static Exception Unsupported() => new NotSupportedException("The fake compiler does not implement this call.");

    public List<ShaderCompileRequest> Requests { get; } = new();

    public string? Rejection { get; set; }

    public List<FakeCompiledShader> Shaders { get; } = new();

    public int Compilations { get; private set; }

    public string BackendName => "Fake";

    public bool LinkedShaderCallsSupported { get; set; } = true;


    public bool TryCompileProgram(ShaderCompileRequest request, out IGuestCompiledShader? shader, out string error)
    {
        Requests.Add(request);
        if (Rejection is { } rejection)
        {
            shader = null;
            error = rejection;
            return false;
        }
        var compiled = new FakeCompiledShader(request, compile?.Invoke(request));
        Shaders.Add(compiled);
        shader = compiled;
        error = string.Empty;
        return true;
    }

    public void CountShaderCompilation() => Compilations++;

    public IGuestCompiledShader GetDepthOnlyFragmentShader() => throw Unsupported();

    public void EnsureStarted(uint width, uint height) => throw Unsupported();




    public void HideSplashScreen() => throw Unsupported();

    public void Submit(byte[] bgraFrame, uint width, uint height) => throw Unsupported();

    public void SubmitGuestDraw(GuestDrawKind drawKind, uint width, uint height) => throw Unsupported();

    public bool TrySubmitGuestImage(int videoOutHandle, int displayBufferIndex, ulong address, uint width, uint height, uint pitchInPixel, ulong flipRequestId) => throw Unsupported();

    public void SubmitCommandStream(ICpuMemory memory, uint queue, ulong address, uint dwordCount, ulong submissionId, object? geometrySnapshots) => throw Unsupported();

    public IdleOutcome SubmitDone(ICpuMemory memory) => throw Unsupported();

    public void RegisterKnownDisplayBuffer(ulong address, uint guestFormat) => throw Unsupported();

    public bool IsGpuGuestImageAvailable(ulong address, uint format, uint numberType) => false;

    public (long Draws, double DrawMs, long Pipelines, long ShaderCompilations) ReadAndResetPerfCounters() => throw Unsupported();

    public void RequestClose() => throw Unsupported();
}

// A command stream in miniature: queued items run in order, and a queued store lands when its turn comes.
internal sealed class FakeKernelWriteStream(ICpuMemory memory) : IKernelWriteStream
{
    private sealed record Item(string Name, ulong Address, ulong Size, bool GpuWrite, byte[]? Store);

    private readonly List<Item> _queued = new();

    public List<string> Ran { get; } = new();

    public bool OnStreamThread { get; set; } = true;

    public bool InCrossQueueOrderWindow { get; set; }

    public (ulong Address, ulong Size) ImageBytes { get; set; }

    public (ulong Address, ulong Size) SurfaceMetadata { get; set; }

    public void QueueGpuWrite(string name, ulong address, ulong size) => _queued.Add(new Item(name, address, size, true, null));

    public void QueueGpuRead(string name, ulong address, ulong size) => _queued.Add(new Item(name, address, size, false, null));

    public void RunAll()
    {
        foreach (var item in _queued)
        {
            Ran.Add(item.Name);
            if (item.Store is { } store)
            {
                Assert.True(memory.TryWrite(item.Address, store));
            }
        }

        _queued.Clear();
    }

    public bool HasPendingPublication(ulong address, ulong size) => false;

    public bool HasPendingRange(ulong address, ulong size) => false;

    public bool HasPendingGpuWrite(ulong address, ulong size) =>
        _queued.Any(item => item.GpuWrite && address < item.Address + item.Size && item.Address < address + size);

    public bool HasImageBytes(ulong address, ulong size) =>
        ImageBytes.Size != 0 && address < ImageBytes.Address + ImageBytes.Size && ImageBytes.Address < address + size;

    public bool HasSurfaceMetadata(ulong address, ulong size) =>
        SurfaceMetadata.Size != 0 && address < SurfaceMetadata.Address + SurfaceMetadata.Size && SurfaceMetadata.Address < address + size;

    public void PostWrite(ulong address, ReadOnlySpan<byte> data) => _queued.Add(new Item("store", address, (ulong)data.Length, false, data.ToArray()));
}

// Guest memory with registered shader code and headers, and the caches over it.
internal sealed class PipelineTestGuest
{
    public const ulong MemoryBase = 0x1_0000_0000;
    public const int MemorySize = 0x10_0000;
    public const ulong HeaderBytes = 0x60;
    private const ulong ShaderSizeOffset = 0x44;
    private const ulong UserDataOffset = 0x08;
    private const ulong InputSemanticsOffset = 0x30;
    private const ulong InputSemanticsCountOffset = 0x50;

    public static readonly uint[] EndProgram = [0xBF810000];

    // buffer_load_format_x v0, off, s[0:3], 0 then s_endpgm: one formatted buffer from the user data.
    public static readonly uint[] FormatLoadProgram = [0xE0000000, 0x80000000, 0xBF810000];

    private readonly Dictionary<ulong, ulong> _headers = new();

    public PipelineTestGuest(Func<ShaderCompileRequest, byte[]>? compile = null)
    {
        Memory = new FakeCpuMemory(MemoryBase, MemorySize);
        Context = new CpuContext(Memory, Generation.Gen5);
        Host = new FakePipelineHost(Memory);
        Compiler = new FakeShaderCompiler(compile);
        Registry = new ShaderHeaderRegistry(Context, code => _headers.TryGetValue(code, out var header) ? header : 0, _ => null);
        Programs = new ShaderProgramCache(Context, Compiler, Host);
    }

    public FakeCpuMemory Memory { get; }

    public CpuContext Context { get; }

    public FakePipelineHost Host { get; }

    public FakeShaderCompiler Compiler { get; }

    public ShaderHeaderRegistry Registry { get; }

    public ShaderProgramCache Programs { get; }

    // Writes the code and a header that names its size; extra header fields are optional.
    public void RegisterProgram(ulong codeAddress, ulong headerAddress, uint[] words, ulong userDataAddress = 0, ulong inputSemanticsAddress = 0, uint inputSemanticsCount = 0)
    {
        var code = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(index * sizeof(uint)), words[index]);
        }

        Write(codeAddress, code);
        var header = new byte[HeaderBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan((int)ShaderSizeOffset), (uint)code.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan((int)UserDataOffset), userDataAddress);
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan((int)InputSemanticsOffset), inputSemanticsAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan((int)InputSemanticsCountOffset), inputSemanticsCount);
        Write(headerAddress, header);
        _headers[codeAddress] = headerAddress;
        ShaderHeaderRegistry.NoteRegistration();
    }

    public void Write(ulong address, byte[] bytes)
    {
        if (!Memory.TryWrite(address, bytes))
        {
            throw new InvalidOperationException($"The fake memory cannot take {bytes.Length} bytes at 0x{address:X}.");
        }
    }

    public void WriteWords(ulong address, params uint[] words)
    {
        var bytes = new byte[words.Length * sizeof(uint)];
        for (var index = 0; index < words.Length; index++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(index * sizeof(uint)), words[index]);
        }

        Write(address, bytes);
    }

    public ShaderSource Source(ulong codeAddress, ShaderStage stage, uint[] userData, uint userDataBase = 0)
    {
        var registered = Registry.Require(codeAddress, stage.ToString().ToLowerInvariant());
        var hash = ShaderIdentity.Compute(Memory, codeAddress, registered.CodeRanges, stage.ToString());
        return new ShaderSource(registered, hash, userData, userDataBase, stage);
    }

    public static StageCompileOptions ComputeOptions(uint threadsX = 64) => new()
    {
        ComputeInfo = new ComputeInputInfo { ThreadsX = threadsX, ThreadsY = 1, ThreadsZ = 1, WaveSize = 32, GroupIdX = true, ThreadIdCount = 1 },
    };

    // The four words of a raw buffer descriptor with the given format and record count.
    public static uint[] BufferDescriptor(ulong address, uint stride, uint records, uint format) =>
    [
        (uint)address,
        (uint)((address >> 32) & 0xFFFF) | (stride << 16),
        records,
        (4u | (5u << 3) | (6u << 6) | (7u << 9)) | (format << 12),
    ];
}
