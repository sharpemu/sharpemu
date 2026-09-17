// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A compiled shader with no payload unless a real module was produced; the host counts it either way.
internal sealed class FakeCompiledShader(ShaderCompileRequest request, byte[]? spirv = null) : IGuestCompiledShader
{
    public ShaderCompileRequest Request => request;

    public byte[] Spirv => spirv ?? [];

    public byte[] Payload => [];

    public string PayloadFileExtension => "fake";
}

// Counts modules and pipelines; every guest read comes straight from the fake memory.
internal sealed class FakePipelineHost(ICpuMemory memory) : IShaderPipelineHost
{
    private ulong _nextHandle = 1;

    public List<(ShaderStage Stage, ulong Hash, ulong ProgramId)> Modules { get; } = new();

    public List<GraphicsPipelineDescription> GraphicsPipelines { get; } = new();

    public List<ComputePipelineDescription> ComputePipelines { get; } = new();

    public SampleCountFlags NoAttachmentSampleCounts { get; set; } = SampleCountFlags.Count1Bit | SampleCountFlags.Count4Bit;

    public uint MaxPushDescriptors => 32;

    public bool ComputeWave64Supported => true;

    public bool GraphicsSubgroupOperationsEnabled => true;

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

    public bool TryReadCleanGuestWord(ulong address, out uint word) => TryReadGuestWord(address, out word);

    public ulong CreateShaderModule(IGuestCompiledShader shader, ShaderStage stage, ulong hash, ulong programId)
    {
        Modules.Add((stage, hash, programId));
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
