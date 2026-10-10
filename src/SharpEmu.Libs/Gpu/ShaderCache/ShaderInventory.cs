// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO.Hashing;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;

namespace SharpEmu.Libs.Gpu.ShaderCache;

internal static class ShaderInventory
{
    private const int MaxPending = 1 << 16;
    private const int CodeSlackBytes = 256;

    private sealed class Pending
    {
        public required byte[] Header { get; init; }
        public required byte[] Code { get; init; }
        public required ulong CodeAddress { get; init; }
        public required ulong HeaderAddress { get; init; }
        public required uint CodeSize { get; init; }
        public Pending? Vertex { get; set; }
        public ulong Identity { get; set; }
    }

    private static readonly object _gate = new();
    private static readonly Queue<Pending> _pending = new();
    private static ShaderCacheFile? _file;
    private static Action<CachedProgram>? _created;
    private static Thread? _thread;
    private static long _dropped;

    [ThreadStatic]
    private static Pending? _lastVertex;

    public static void Attach(ShaderCacheFile file, Action<CachedProgram>? created)
    {
        lock (_gate)
        {
            _file = file;
            _created = created;
            if (_thread is null)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "SharpEmu shader inventory", Priority = ThreadPriority.BelowNormal };
                _thread.Start();
            }

            Monitor.PulseAll(_gate);
        }
    }

    public static void Detach(ShaderCacheFile file)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_file, file))
            {
                _file = null;
                _created = null;
            }
        }
    }

    public static void Capture(CpuContext context, ulong headerAddress, ulong codeAddress)
    {
        if (!ShaderCacheSettings.Enabled)
        {
            return;
        }

        Span<byte> head = stackalloc byte[AgcShaderHeader.FixedBytes];
        if (!context.Memory.TryRead(headerAddress, head) ||
            !AgcShaderHeader.TryPeekSizes(head, out var headerBytes, out var codeBytes, out var type) ||
            !AgcShaderHeader.IsSupported(type))
        {
            return;
        }

        var header = new byte[headerBytes];
        var code = new byte[codeBytes + CodeSlackBytes];
        if (!context.Memory.TryRead(headerAddress, header) ||
            (!context.Memory.TryRead(codeAddress, code) && !context.Memory.TryRead(codeAddress, code.AsSpan(0, codeBytes))))
        {
            return;
        }

        var pending = new Pending
        {
            Header = header,
            Code = code,
            CodeAddress = codeAddress,
            HeaderAddress = headerAddress,
            CodeSize = (uint)codeBytes,
        };
        switch ((AgcShaderType)type)
        {
            case AgcShaderType.Geometry:
                _lastVertex = pending;
                break;
            case AgcShaderType.Pixel:
                pending.Vertex = _lastVertex;
                _lastVertex = null;
                break;
        }

        lock (_gate)
        {
            if (_pending.Count >= MaxPending)
            {
                _dropped++;
                return;
            }

            _pending.Enqueue(pending);
            Monitor.PulseAll(_gate);
        }
    }

    public static bool TryHash(ReadOnlySpan<byte> code, uint codeSize, ulong codeAddress, out ulong hash)
    {
        hash = 0;
        if (code.Length < codeSize)
        {
            return false;
        }

        var replay = new ReplayCpuMemory([new CodeRange(codeAddress, code.ToArray())]);
        if (!ShaderIdentity.TryReadDeclaredHash(replay, codeAddress, out var declared))
        {
            return false;
        }

        hash = declared != 0 ? declared : XxHash3.HashToUInt64(code[..(int)codeSize]);
        return true;
    }

    public static bool TryCreateCapture(ReadOnlySpan<byte> code, uint codeSize, ulong codeAddress, out ShaderCodeCapture capture)
    {
        capture = null!;
        if (!TryHash(code, codeSize, codeAddress, out var hash))
        {
            return false;
        }

        var range = new CodeRange(codeAddress, code.ToArray());
        if (!Gen5ShaderTranslator.TryDecodeProgram(new CpuContext(new ReplayCpuMemory([range]), Generation.Gen5), codeAddress, out _, out _))
        {
            return false;
        }

        capture = new ShaderCodeCapture
        {
            Hash = hash,
            CodeSize = codeSize,
            Address = codeAddress,
            Generation = Generation.Gen5,
            Ranges = [range],
        };
        return true;
    }

    private static void Run()
    {
        while (true)
        {
            Pending pending;
            ShaderCacheFile file;
            Action<CachedProgram>? created;
            lock (_gate)
            {
                while (_file is null || _pending.Count == 0)
                {
                    Monitor.Wait(_gate);
                }

                pending = _pending.Dequeue();
                file = _file;
                created = _created;
            }

            try
            {
                Process(pending, file, created);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or InvalidOperationException or OverflowException)
            {
                Console.Error.WriteLine($"[SHADER CACHE][WARN] A created shader could not be recorded: {exception.Message}");
            }
        }
    }

    private static void Process(Pending pending, ShaderCacheFile file, Action<CachedProgram>? created)
    {
        if (!AgcShaderHeader.TryParse(pending.Header, pending.HeaderAddress, out var header) ||
            !TryCreateCapture(pending.Code, pending.CodeSize, pending.CodeAddress, out var capture))
        {
            return;
        }

        var program = new InventoryProgram { Code = capture.Key, Source = InventorySource.Created, Header = header.Bytes };
        var identity = file.RecordProgram(program, capture);
        pending.Identity = identity ?? ShaderCacheFile.ProgramIdentity(program);
        if (header.Type == AgcShaderType.Pixel && pending.Vertex is { Identity: not 0 } vertex)
        {
            file.RecordPair(vertex.Identity, pending.Identity);
        }

        if (identity is { } recorded && header.Type == AgcShaderType.Compute && created is not null &&
            file.TryGetProgram(recorded, out var cached))
        {
            created(cached);
        }
    }
}
