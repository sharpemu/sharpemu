// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.GnmMode.Libraries.VideoOut;

namespace SharpEmu.GnmMode.Shaders;

public sealed partial class GcnShaderFrontend : IShaderFrontend
{
    private const ulong LocalDataRingBytes = 4 * 1024 * 1024;
    private readonly GcnSampledImageDetiler? _imageDetiler;
    private readonly Func<ulong, ulong?>? _allocate;
    private readonly ImageDescriptorNormalizer _normalizeImageDescriptor = GcnImageDescriptor.Normalize;
    private readonly Lock _ringGate = new();
    private ulong _localDataRing;

    public GcnShaderFrontend(ICpuMemory? memory = null, Func<ulong, ulong?>? allocate = null)
    {
        if (memory is null) return;
        _allocate = allocate ?? (size =>
            GnmVideoOutExports.TryAllocateTrackedShadow(new CpuContext(memory, Generation.Gen4), size, out var address)
                ? address : null);
        _imageDetiler = new GcnSampledImageDetiler(memory, _allocate);
        _normalizeImageDescriptor = (words, stage, hash, source) =>
            GcnImageDescriptor.Normalize(words, stage, hash, source, _imageDetiler);
    }

    public static void ResetVertexFetchTargets()
    {
        GcnImageDescriptor.ResetLayouts();
        GnmVideoOutExports.ResetDisplayTargets();
        GcnStageResources.Reset();
    }

    public (ulong Address, ulong Bytes) TessellationLocalDataRing
    {
        get
        {
            lock (_ringGate)
            {
                if (_localDataRing == 0 && _allocate?.Invoke(LocalDataRingBytes) is { } address)
                    _localDataRing = address;
                return _localDataRing == 0 ? default : (_localDataRing, LocalDataRingBytes);
            }
        }
    }

    public ulong CacheDomain => 0x47434E31_0000001B;
    public uint VertexUserDataBase => 0;
    public bool HasVertexTables => false;
    public bool RequiresExplicitBufferLengths => true;
    public bool RequiresOrderedImagePreparation => _imageDetiler is not null;
    public bool SupportsParallelResolution => true;
    public bool RetainsTargetlessDraws => false;
    public BufferDescriptorNormalizer NormalizeBufferDescriptor => GcnBufferDescriptor.Normalize;
    public Func<long> ImageNormalizationVersion => NormalizationVersion;
    public ImageDescriptorNormalizer NormalizeImageDescriptor => _normalizeImageDescriptor;

    public uint[] NormalizeImageForResolution(ReadOnlySpan<uint> words, ShaderStage stage, ulong shaderHash, string source, bool allowPreparation) =>
        GcnImageDescriptor.Normalize(words, stage, shaderHash, source, _imageDetiler, deferPreparation: true, allowPreparation);

    public TextureRequestResolution ResolveTexture(ReadOnlySpan<uint> words, in ShaderImageShape shape,
        ulong shaderHash, int imageIndex) => GcnImageDescriptor.ResolveTexture(words, shape, shaderHash, imageIndex);

    public bool PrepareTexture(ReadOnlySpan<uint> words) => _imageDetiler?.Prepare(words) ?? true;

    public bool IsTexturePrepared(ReadOnlySpan<uint> words, bool ordered, out ulong source, out ulong sourceBytes)
    {
        source = sourceBytes = 0;
        return _imageDetiler?.IsPrepared(words, ordered, out source, out sourceBytes) ?? true;
    }

    public bool TryResolveImageCopy(CpuContext context, RegisteredShader shader, ReadOnlySpan<uint> userData,
        ComputeInputInfo input, Gen5ComputeSystemRegisters systemRegisters, uint x, uint y, uint z,
        out ImageRequest source, out ImageRequest destination) =>
        GcnImageCopy.TryResolve(context.Memory, shader, userData, input, systemRegisters, x, y, z, _allocate, out source, out destination);

    public RegisteredShader ReadShader(CpuContext context, ulong codeAddress, string label, ReadOnlySpan<uint> userData = default)
    {
        var binary = GcnShaderBinary.Read(context.Memory, codeAddress);
        if (label == "compute" && binary.ShaderType != 4)
            throw new InvalidDataException($"Expected a GCN compute shader, found type {binary.ShaderType}.");
        // One VS can use different vertex layouts in successive deferred draws.
        // Its fetch target belongs to this draw's SGPR snapshot, not the code address.
        var (continuation, continuationSize) = label == "vertex" ? FetchShader(context.Memory, codeAddress, userData) : default;
        return new RegisteredShader(codeAddress, binary.InfoAddress, binary.CodeSize, 0, 0, 0, 0, 0, 0)
        {
            Frontend = this,
            FrontendUserData = userData.ToArray(),
        } with { ContinuationAddress = continuation, ContinuationSizeBytes = continuationSize };
    }

    public RegisteredShader? ReadMergedHull(CpuContext context, ulong localAddress, ulong hullAddress, ReadOnlySpan<uint> userData)
    {
        var local = GcnShaderBinary.Read(context.Memory, localAddress);
        var hull = GcnShaderBinary.Read(context.Memory, hullAddress);
        var localUserData = userData[..Math.Min(userData.Length, GcnTessellationProgram.StageUserScalars)];
        var hullUserData = userData.Length > GcnTessellationProgram.StageUserScalars
            ? userData[GcnTessellationProgram.StageUserScalars..] : [];
        var fetch = FetchShader(context.Memory, localAddress, localUserData);
        return new RegisteredShader(localAddress, local.InfoAddress, local.CodeSize, 0, 0, 0, 0, hullAddress, hull.CodeSize)
        {
            Frontend = this,
            FrontendUserData = userData.ToArray(),
            AdditionalRanges = fetch.Address == 0 ? [] : [fetch],
        };
    }

    private (ulong Address, uint SizeBytes) FetchShader(ICpuMemory memory, ulong codeAddress, ReadOnlySpan<uint> userData)
    {
        var fetch = userData.Length >= 2 ? userData[0] | ((ulong)userData[1] << 32) : 0;
        if (fetch == 0 || fetch == codeAddress || (fetch & 3) != 0 || !CallsFetchShader(memory, codeAddress))
            return default;
        if (TryDecodeFetch(memory, fetch, out _, out var size, out var failure))
            return (fetch, size);
        if (GnmDiagnostics.Log)
            Console.Error.WriteLine($"[GNMMODE][TRACE] VS fetch decode failed shader=0x{codeAddress:X} target=0x{fetch:X}: {failure}");
        return default;
    }

    private static readonly Func<long> NormalizationVersion = () => GcnImageDescriptor.NormalizationVersion;

    private const uint FetchShaderCall = 0xBE802100;
    private const int FetchShaderCallWindow = 12;

    private static bool CallsFetchShader(ICpuMemory memory, ulong codeAddress)
    {
        Span<uint> words = stackalloc uint[FetchShaderCallWindow];
        return !memory.TryRead(codeAddress, MemoryMarshal.AsBytes(words)) || words.Contains(FetchShaderCall);
    }

    private const uint FetchComponents = 4;
    private const uint MubufOpcodeMask = 0x7Fu << 18;
    private const uint LoadFormatXyzw = 3u << 18;

    private static Gen5ShaderProgram WidenFetchLoads(Gen5ShaderProgram function)
    {
        static bool IsNarrowLoad(Gen5ShaderInstruction instruction) =>
            instruction.Control is Gen5BufferMemoryControl { Typed: false, DwordCount: < FetchComponents } &&
            instruction.Opcode.StartsWith("BufferLoadFormat", StringComparison.Ordinal);
        var touched = new Dictionary<uint, int>();
        for (var index = 0; index < function.Instructions.Count; index++)
            foreach (var operand in function.Instructions[index].Sources.Concat(function.Instructions[index].Destinations))
                if (operand.Kind == Gen5OperandKind.VectorRegister)
                    touched[operand.Value] = touched.ContainsKey(operand.Value) ? -1 : index;
        var instructions = function.Instructions.Select((instruction, index) =>
        {
            if (!IsNarrowLoad(instruction))
                return instruction;
            var control = (Gen5BufferMemoryControl)instruction.Control!;
            for (var register = control.VectorData + control.DwordCount; register < control.VectorData + FetchComponents; register++)
                if (register > 255 || touched.TryGetValue(register, out var owner) && owner != index)
                    return instruction;
            return instruction with
            {
                Opcode = "BufferLoadFormatXyzw",
                Words = [(instruction.Words[0] & ~MubufOpcodeMask) | LoadFormatXyzw, .. instruction.Words.Skip(1)],
                Destinations = Enumerable.Range((int)control.VectorData, (int)FetchComponents)
                    .Select(register => Gen5Operand.Vector((uint)register)).ToArray(),
                Control = control with { DwordCount = FetchComponents },
            };
        }).ToArray();
        return function with { Instructions = instructions };
    }

    public bool TryDecode(CpuContext context, RegisteredShader shader, ShaderStage stage, out Gen5ShaderProgram program, out string error)
    {
        if (stage == ShaderStage.Compute && shader.IsFused)
        {
            program = default!;
            var userData = shader.FrontendUserData.Span;
            var localData = userData[..Math.Min(userData.Length, GcnTessellationProgram.StageUserScalars)];
            var hullData = userData.Length > GcnTessellationProgram.StageUserScalars
                ? userData[GcnTessellationProgram.StageUserScalars..] : [];
            if (!TryDecodeProgram(context, shader.CodeAddress, shader.CodeSizeBytes,
                    shader.AdditionalRanges is [var fetch, ..] ? fetch.Address : 0, localData, out var local, out error) ||
                !TryDecodeProgram(context, shader.ContinuationAddress, shader.ContinuationSizeBytes, 0, hullData, out var hull, out error))
                return false;
            return GcnTessellationProgram.TryJoinHull(local, hull, shader.CodeAddress, shader.ContinuationAddress, out program, out error);
        }
        if (!TryDecode(context, shader, out program, out error))
            return false;
        return stage != ShaderStage.TessellationEvaluation ||
            GcnTessellationProgram.TryPrepareDomain(program, shader.CodeAddress, out program, out error);
    }

    public bool TryDecode(CpuContext context, RegisteredShader shader, out Gen5ShaderProgram program, out string error) =>
        TryDecodeProgram(context, shader.CodeAddress, shader.CodeSizeBytes, shader.ContinuationAddress, shader.FrontendUserData.Span,
            out program, out error);

    private bool TryDecodeProgram(CpuContext context, ulong codeAddress, uint codeSize, ulong fetchAddress,
        ReadOnlySpan<uint> userData, out Gen5ShaderProgram program, out string error)
    {
        if (!GcnShaderDecoder.TryDecode(context.Memory, codeAddress, codeSize, out program, out error))
            return false;
        if (!userData.IsEmpty)
            program = GcnShaderDecoder.WithVolumeImages(program, context.Memory, userData);
        // Pixel reads must see GPU-produced attachments, not stale guest buffer bytes.
        var sampledLoads = program.Instructions.Any(instruction => instruction.Control is Gen5ExportControl { Target: <= 8 });
        var call = program.Instructions.Take(6).FirstOrDefault(instruction => instruction.Opcode == "SSwappcB64");
        if (call is not null)
        {
            if (call.Sources is not [{ Kind: Gen5OperandKind.ScalarRegister, Value: 0 }] ||
                call.Destinations is not [{ Kind: Gen5OperandKind.ScalarRegister, Value: 0 }])
            {
                error = $"unsupported-gcn-vertex-call-registers pc=0x{call.Pc:X}";
                return false;
            }
            if (fetchAddress == 0 || !TryDecodeFetch(context.Memory,
                    fetchAddress, out var function, out _, out error))
            {
                error = $"gcn-vertex-fetch-unresolved call=0x{call.Pc:X} target=0x{fetchAddress:X} {error}";
                return false;
            }
            function = WidenFetchLoads(function);
            Gen5ShaderInstruction[] registers = [.. program.Instructions, .. function.Instructions];
            var callPc = call.Pc;
            if (!GcnAluLowering.TryLower(program, registers, out program, out error) ||
                !GcnAluLowering.TryLower(function, registers, out function, out error))
                return false;
            if (!GcnStorageImageLowering.TryLower(function, out function, out error))
                return false;
            // The generic shader-call linker understands return-address handoff
            // and local branches. PS4's fetch address is already resolved from
            // the GNM user registers, rather than read from a shader-call table.
            var used = new bool[104];
            foreach (var instruction in program.Instructions.Concat(function.Instructions))
            {
                foreach (var operand in instruction.Sources.Concat(instruction.Destinations))
                    if (operand.Kind == Gen5OperandKind.ScalarRegister && operand.Value < 104)
                        for (var i = operand.Value; i < Math.Min(104, operand.Value + instruction.DestinationWidth); i++)
                            used[i] = true;
            }
            var argumentRegister = -1;
            for (var candidate = 102; candidate >= 2; candidate -= 2)
                if (!used[candidate] && !used[candidate + 1])
                {
                    argumentRegister = candidate;
                    break;
                }
            if (argumentRegister < 0)
            {
                error = "gcn-vertex-fetch-no-free-argument-register-pair";
                return false;
            }
            if (!GcnStorageImageLowering.TryLower(program, out program, out error, sampledLoads))
                return false;
            call = program.Instructions.First(instruction => instruction.Opcode == "SSwappcB64" && instruction.ProgramOffset == callPc);
            var site = new ShaderCallSite(call.Pc, 0, (uint)argumentRegister, 0,
                codeAddress + call.ProgramOffset + (uint)call.Words.Count * 4,
                [new ShaderCallTarget(fetchAddress, 0, function)]);
            try
            {
                var linked = Gen5ShaderCallLinker.Link(program, [site]);
                program = linked with
                {
                    VertexIndexRegister = 0,
                    InstanceIndexRegisters = [1, 2, 3],
                    // The draw selects one fetch target before compilation. Its descriptors
                    // can use ordinary resource tracking instead of generic call dispatch.
                    FunctionBufferAccesses = new HashSet<uint>(),
                    Instructions = linked.Instructions.Select(instruction => instruction.Control switch
                    {
                        ShaderCallMatchControl => instruction with
                        {
                            Encoding = Gen5ShaderEncoding.Sopp,
                            Opcode = "SNop",
                            Words = [0xBF800000],
                            Control = null,
                        },
                        // The single target always matches; its dispatch fault is unreachable.
                        ShaderCallFaultControl => instruction with { Control = null },
                        _ => instruction,
                    }).ToArray(),
                };
            }
            catch (InvalidOperationException exception)
            {
                error = $"gcn-vertex-fetch-link-failed {exception.Message}";
                return false;
            }
            if (GnmDiagnostics.Log)
                Console.Error.WriteLine($"[GNMMODE][GPU] Linked GCN vertex fetch shader=0x{codeAddress:X} " +
                    $"target=0x{fetchAddress:X} functionInstructions={function.Instructions.Count} " +
                    $"linkedInstructions={program.Instructions.Count} argument=s{argumentRegister}.");
            return true;
        }
        if (!GcnAluLowering.TryLower(program, program.Instructions, out program, out error) ||
            !GcnStorageImageLowering.TryLower(program, out program, out error, sampledLoads)) return false;
        program = program with { VertexIndexRegister = 0, InstanceIndexRegisters = [1, 2, 3] };
        return true;
    }
}
