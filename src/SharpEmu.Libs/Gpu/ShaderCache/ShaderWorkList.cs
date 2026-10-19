// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal enum ShaderWorkKind : byte
{
    RecordedCompute = 1,
    StaticCompute = 2,
    RecordedGraphics = 3,
    StaticGraphics = 4,
    SeedCompute = 5,
    SeedGraphics = 6,
}

internal readonly record struct ShaderWorkItem(ShaderWorkKind Kind, ulong Identity, int First, int Second = -1, int State = -1)
{
    public bool IsCompute => Kind is ShaderWorkKind.RecordedCompute or ShaderWorkKind.StaticCompute or ShaderWorkKind.SeedCompute;
}

internal sealed record SeedComputeItem(StageRecord Record, ShaderCodeCapture Code);

internal sealed record SeedGraphicsItem(GraphicsPipelineRecord Record, ShaderCodeCapture VertexCode, ShaderCodeCapture? PixelCode);

internal sealed class ShaderWorkList
{
    private const int MaxStatesPerSignature = 8;

    private readonly HashSet<ulong> _identities;
    private readonly Dictionary<(ulong Hash, uint CodeSize), CachedProgram> _programsByCode;

    private ShaderWorkList(
        ShaderCacheFile file,
        List<ShaderWorkItem> items,
        HashSet<ulong> identities,
        List<StaticGraphicsState> states,
        List<SeedComputeItem> seedComputes,
        List<SeedGraphicsItem> seedGraphics,
        Dictionary<(ulong Hash, uint CodeSize), CachedProgram> programsByCode,
        bool secondVariant)
    {
        File = file;
        Items = items;
        States = states;
        SeedComputes = seedComputes;
        SeedGraphics = seedGraphics;
        SecondInterpolantVariant = secondVariant;
        _identities = identities;
        _programsByCode = programsByCode;
    }

    public ShaderCacheFile File { get; }

    public IReadOnlyList<ShaderWorkItem> Items { get; }

    public IReadOnlyList<StaticGraphicsState> States { get; }

    public IReadOnlyList<SeedComputeItem> SeedComputes { get; }

    public IReadOnlyList<SeedGraphicsItem> SeedGraphics { get; }

    public bool SecondInterpolantVariant { get; }

    public bool Contains(ulong identity) => _identities.Contains(identity);

    public bool TryGetProgram(ulong hash, uint codeSize, out CachedProgram program) =>
        _programsByCode.TryGetValue((hash, codeSize), out program!);

    public static ulong StaticGraphicsIdentity(ulong vertex, ulong pixel, ulong state) => Combine(2, vertex, pixel, state);

    public static ShaderWorkList Build(ShaderCacheFile file, bool includeStaticGraphics, ShaderSeed? seed = null)
    {
        var items = new List<ShaderWorkItem>();
        var seen = new HashSet<ulong>();
        void Add(ShaderWorkItem item)
        {
            if (seen.Add(item.Identity))
            {
                items.Add(item);
            }
        }

        var programsByCode = new Dictionary<(ulong Hash, uint CodeSize), CachedProgram>();
        var codes = new Dictionary<(ulong Hash, uint CodeSize), ShaderCodeCapture>();
        foreach (var program in file.Programs)
        {
            programsByCode.TryAdd((program.Code.Hash, program.Code.CodeSize), program);
            codes.TryAdd((program.Code.Hash, program.Code.CodeSize), program.Code);
        }

        var recordedComputes = new HashSet<ulong>();
        for (var index = 0; index < file.Computes.Count; index++)
        {
            var compute = file.Computes[index];
            codes.TryAdd((compute.Code.Hash, compute.Code.CodeSize), compute.Code);
            recordedComputes.Add(ShaderSeed.ComputeIdentity(compute.Record));
            Add(new ShaderWorkItem(ShaderWorkKind.RecordedCompute, compute.Identity, index));
        }

        var recordedGraphics = new HashSet<ulong>();
        foreach (var graphics in file.Graphics)
        {
            codes.TryAdd((graphics.VertexCode.Hash, graphics.VertexCode.CodeSize), graphics.VertexCode);
            if (graphics.PixelCode is { } pixelCode)
            {
                codes.TryAdd((pixelCode.Hash, pixelCode.CodeSize), pixelCode);
            }

            recordedGraphics.Add(ShaderSeed.GraphicsIdentity(graphics.Record));
        }

        var seedComputes = new List<SeedComputeItem>();
        var seedGraphics = new List<SeedGraphicsItem>();
        if (seed is not null)
        {
            foreach (var record in seed.Computes)
            {
                var identity = ShaderSeed.ComputeIdentity(record);
                if (!recordedComputes.Contains(identity) && codes.TryGetValue((record.Hash, record.CodeSize), out var code))
                {
                    seedComputes.Add(new SeedComputeItem(record, code));
                    Add(new ShaderWorkItem(ShaderWorkKind.SeedCompute, Combine(3, identity), seedComputes.Count - 1));
                }
            }
        }

        for (var index = 0; index < file.Programs.Count; index++)
        {
            var program = file.Programs[index];
            if (StaticStageInputs.TryParse(program, out var header) && header.Type == AgcShaderType.Compute)
            {
                Add(new ShaderWorkItem(ShaderWorkKind.StaticCompute, Combine(1, program.Identity), index));
            }
        }

        for (var index = 0; index < file.Graphics.Count; index++)
        {
            Add(new ShaderWorkItem(ShaderWorkKind.RecordedGraphics, file.Graphics[index].Identity, index));
        }

        if (seed is not null)
        {
            foreach (var record in seed.Graphics)
            {
                var identity = ShaderSeed.GraphicsIdentity(record);
                ShaderCodeCapture? pixelCode = null;
                if (!recordedGraphics.Contains(identity) &&
                    codes.TryGetValue((record.Vertex.Hash, record.Vertex.CodeSize), out var vertexCode) &&
                    (record.Pixel is null || codes.TryGetValue((record.Pixel.Hash, record.Pixel.CodeSize), out pixelCode)))
                {
                    seedGraphics.Add(new SeedGraphicsItem(record, vertexCode, pixelCode));
                    Add(new ShaderWorkItem(ShaderWorkKind.SeedGraphics, Combine(4, identity), seedGraphics.Count - 1));
                }
            }
        }

        var states = new List<StaticGraphicsState>();
        var pack = ShaderSeed.Learn(file);
        if (seed is not null)
        {
            pack = pack.MergedWith(seed);
        }

        if (includeStaticGraphics)
        {
            AddStaticGraphics(file, pack, Add, states);
        }

        return new ShaderWorkList(file, items, seen, states, seedComputes, seedGraphics, programsByCode, pack.SecondInterpolantVariant);
    }

    public static ulong Combine(params ulong[] values)
    {
        var hash = new XxHash3();
        foreach (var value in values)
        {
            hash.Append(BitConverter.GetBytes(value));
        }

        return hash.GetCurrentHashAsUInt64();
    }

    private static void AddStaticGraphics(
        ShaderCacheFile file,
        ShaderSeed pack,
        Action<ShaderWorkItem> add,
        List<StaticGraphicsState> states)
    {
        var programIndexByIdentity = new Dictionary<ulong, int>();
        for (var index = 0; index < file.Programs.Count; index++)
        {
            programIndexByIdentity[file.Programs[index].Identity] = index;
        }

        var pairs = new List<(int Vertex, int Pixel)>();
        foreach (var (vertexIdentity, pixelIdentity) in pack.Pairs)
        {
            if (programIndexByIdentity.TryGetValue(vertexIdentity, out var vertex) &&
                programIndexByIdentity.TryGetValue(pixelIdentity, out var pixel))
            {
                pairs.Add((vertex, pixel));
            }
        }

        var palettes = new Dictionary<ulong, int[]>();
        foreach (var (vertex, pixel) in pairs)
        {
            if (!StaticStageInputs.TryParse(file.Programs[vertex], out var vertexHeader) ||
                vertexHeader.Type != AgcShaderType.Geometry || vertexHeader.HasVertexFetchTables ||
                !StaticStageInputs.TryParse(file.Programs[pixel], out var pixelHeader))
            {
                continue;
            }

            var signature = StaticStageInputs.PixelSignature(pixelHeader);
            if (!palettes.TryGetValue(signature, out var palette))
            {
                palette = pack.Palette(signature, MaxStatesPerSignature)
                    .Select(state =>
                    {
                        states.Add(state);
                        return states.Count - 1;
                    })
                    .ToArray();
                palettes[signature] = palette;
            }

            foreach (var state in palette)
            {
                add(new ShaderWorkItem(
                    ShaderWorkKind.StaticGraphics,
                    StaticGraphicsIdentity(file.Programs[vertex].Identity, file.Programs[pixel].Identity, states[state].Identity),
                    vertex,
                    pixel,
                    state));
            }
        }
    }
}
