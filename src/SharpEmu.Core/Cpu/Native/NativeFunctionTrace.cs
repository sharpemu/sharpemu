// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using SharpEmu.HLE.Host;

namespace SharpEmu.Core.Cpu.Native;

internal sealed unsafe class NativeFunctionTrace : IDisposable
{
    private readonly IHostMemory _memory;
    private readonly ulong[] _addresses;
    private readonly byte[][] _instructions = [[0x55], [0x48, 0x83, 0xC4, 0x30]];
    private readonly bool _serialization;
    private readonly bool _objectReference;
    private readonly bool _assetRead;
    private readonly bool _schemaWalk;
    private readonly OrderedDictionary<(int Thread, ulong Frame), (ulong Address, int Count, string Record)> _schemas = new();
    private readonly OrderedDictionary<(int Thread, ulong Frame), string> _assetStarts = new();
    private readonly ThreadLocal<string?> _referenceSource = new();
    private int _invalidReferences;
    private readonly string _assetFilter;
    private ulong _resumeCode;
    private int _armed;
    private int _hits;
    private int _unreadableReported;
    private readonly int[] _probeHits = new int[2];
    private int _nameFailures;
    private int _filteredHits;
    private readonly object _historyLock = new();
    private readonly Queue<string> _recentSerialization = new();
    private readonly NativeFunctionTrace[]? _probes;
    private NativeFunctionTrace? _historyOwner;
    private int _failureReported;

    internal NativeFunctionTrace(params NativeFunctionTrace[] probes)
    {
        if (probes.Length == 0 || probes.Any(probe => probe._probes != null || probe._armed != 0))
            throw new ArgumentException("Supply unarmed individual native probes.");
        _memory = probes[0]._memory;
        _assetFilter = "";
        _probes = probes.ToArray();
        var sites = new SortedDictionary<ulong, byte[]>();
        foreach (var probe in _probes)
        {
            for (var index = 0; index < probe._addresses.Length; index++)
            {
                var address = probe._addresses[index];
                var instruction = probe._instructions[index];
                if (sites.TryGetValue(address, out var existing) && !existing.AsSpan().SequenceEqual(instruction))
                    throw new ArgumentException("Native probes disagree on a shared instruction.");
                sites[address] = instruction;
            }
        }
        _addresses = sites.Keys.ToArray();
        _instructions = sites.Values.ToArray();
        if (_addresses.Length > 128) throw new ArgumentException("Too many native probe sites.");
        foreach (var probe in _probes) probe._historyOwner = this;
    }

    internal NativeFunctionTrace(IHostMemory memory, ulong entry, ulong exit, bool serialization = false, string assetFilter = "", bool objectReference = false, bool assetRead = false, bool schemaWalk = false)
    {
        _memory = memory;
        _addresses = [entry, exit];
        _serialization = serialization;
        _objectReference = objectReference;
        _assetRead = assetRead;
        _schemaWalk = schemaWalk;
        if ((serialization ? 1 : 0) + (objectReference ? 1 : 0) + (assetRead ? 1 : 0) + (schemaWalk ? 1 : 0) > 1)
            throw new ArgumentException("Select one native trace mode.");
        _assetFilter = assetFilter;
        if (serialization) _instructions = [[0x0F, 0xB6, 0x8C, 0x24, 0x10, 0x01, 0, 0], [0x48, 0x8B, 0x45, 0xB0]];
        if (objectReference) _instructions = [[0x48, 0x8B, 0x10], [0x8B, 0xB5, 0x38, 0xFE, 0xFF, 0xFF]];
        if (assetRead) _instructions = [[0x41, 0xF6, 0x44, 0x24, 0x08, 0x10], [0x48, 0x8B, 0xB5, 0x60, 0xFD, 0xFF, 0xFF]];
        if (schemaWalk) _instructions = [[0x0F, 0xB6, 0x8C, 0x24, 0x10, 0x01, 0, 0], [0x48, 0x8B, 0x43, 0x08]];
    }

    internal static NativeFunctionTrace? FromEnvironment()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TRACE_NATIVE_FUNCTION") != "1") return null;
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Native function tracing requires Windows x64.");
        static ulong Address(string name)
        {
            var text = Environment.GetEnvironmentVariable(name)?.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            if (!ulong.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) || address == 0)
                throw new InvalidOperationException($"{name} requires a nonzero hexadecimal address.");
            return address;
        }
        var modes = new[] { "SERIALIZATION", "OBJECT_REFERENCE", "ASSET_READ", "SCHEMA_WALK" }
            .Where(mode => Environment.GetEnvironmentVariable($"SHARPEMU_TRACE_NATIVE_{mode}") == "1").ToArray();
        NativeFunctionTrace trace;
        if (modes.Length > 1)
        {
            trace = new NativeFunctionTrace(modes.Select(mode => new NativeFunctionTrace(HostPlatform.Current.Memory,
                Address($"SHARPEMU_TRACE_NATIVE_{mode}_ENTRY_ADDRESS"),
                Address($"SHARPEMU_TRACE_NATIVE_{mode}_RETURN_ADDRESS"),
                serialization: mode == "SERIALIZATION",
                assetFilter: Environment.GetEnvironmentVariable("SHARPEMU_TRACE_NATIVE_ASSET_FILTER") ?? "",
                objectReference: mode == "OBJECT_REFERENCE", assetRead: mode == "ASSET_READ", schemaWalk: mode == "SCHEMA_WALK")).ToArray());
        }
        else trace = new NativeFunctionTrace(HostPlatform.Current.Memory,
            Address("SHARPEMU_TRACE_NATIVE_ENTRY_ADDRESS"), Address("SHARPEMU_TRACE_NATIVE_RETURN_ADDRESS"),
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_NATIVE_SERIALIZATION") == "1",
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_NATIVE_ASSET_FILTER") ?? "",
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_NATIVE_OBJECT_REFERENCE") == "1",
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_NATIVE_ASSET_READ") == "1",
            Environment.GetEnvironmentVariable("SHARPEMU_TRACE_NATIVE_SCHEMA_WALK") == "1");
        trace.Install();
        return trace;
    }

    internal void Install()
    {
        ValidateInstructions();
        _resumeCode = _memory.Allocate(0, 4096, HostPageProtection.ReadWrite);
        if (_resumeCode == 0) throw new InvalidOperationException("Native trace resume allocation failed.");
        try
        {
            for (var index = 0; index < _addresses.Length; index++)
            {
                var bytes = new Span<byte>((void*)(_resumeCode + (ulong)index * 32), 32);
                var instruction = _instructions[index];
                instruction.CopyTo(bytes);
                ReadOnlySpan<byte> jump = [0xFF, 0x25, 0, 0, 0, 0];
                jump.CopyTo(bytes[instruction.Length..]);
                BinaryPrimitives.WriteUInt64LittleEndian(bytes[(instruction.Length + 6)..],
                    _addresses[index] + (ulong)instruction.Length);
            }
            if (!_memory.Protect(_resumeCode, 4096, HostPageProtection.ReadExecute, out _))
                throw new InvalidOperationException("Native trace resume protection failed.");
            _memory.FlushInstructionCache(_resumeCode, 4096);
            for (var index = 0; index < _addresses.Length; index++)
            {
                // Count the site before writing so cleanup also covers a failed protection restore.
                _armed = index + 1;
                WriteInstruction(index, 0xCC);
                Console.Error.WriteLine($"[LOADER][TRACE] NativeFunction armed site={index} address=0x{_addresses[index]:X16}");
            }
        }
        catch { Dispose(); throw; }
    }

    private void ValidateInstructions()
    {
        if (_probes != null)
        {
            foreach (var probe in _probes) probe.ValidateInstructions();
            for (var index = 1; index < _addresses.Length; index++)
                if (_addresses[index] < _addresses[index - 1] + (ulong)_instructions[index - 1].Length)
                    throw new InvalidOperationException("Native probe instructions overlap. No trace was armed.");
            return;
        }
        ReadOnlySpan<byte> prefix = [0x55, 0x48, 0x89, 0xE5, 0x41, 0x57, 0x41, 0x56, 0x41, 0x54];
        if (_serialization || _objectReference || _assetRead || _schemaWalk) prefix = _instructions[0];
        var exitLength = _instructions[1].Length;
        Span<byte> actual = stackalloc byte[Math.Max(prefix.Length, exitLength)];
        if (_addresses[0] > ulong.MaxValue - (ulong)prefix.Length || _addresses[1] > ulong.MaxValue - (ulong)exitLength ||
            !Read(_addresses[0], actual[..prefix.Length]) || !actual[..prefix.Length].SequenceEqual(prefix) ||
            !Read(_addresses[1], actual[..exitLength]) || !actual[..exitLength].SequenceEqual(_instructions[1]) ||
            (_addresses[0] < _addresses[1] + (ulong)exitLength && _addresses[1] < _addresses[0] + (ulong)prefix.Length))
            throw new InvalidOperationException("Native trace instruction validation failed. No trace was armed.");
    }

    internal bool TryHandle(ulong address, void* context)
    {
        var index = Array.IndexOf(_addresses, address);
        if (index < 0 || index >= _armed) return false;
        if (_probes == null) TraceSite(index, context);
        else foreach (var probe in _probes)
        {
            var probeIndex = Array.IndexOf(probe._addresses, address);
            if (probeIndex >= 0) probe.TraceSite(probeIndex, context);
        }
        *(ulong*)((byte*)context + 248) = _resumeCode + (ulong)index * 32;
        return true;
    }

    private void TraceSite(int index, void* context)
    {
        if (_schemaWalk)
        {
            TraceSchemaWalk(index, context);
            return;
        }
        if (_assetRead)
        {
            TraceAssetRead(index, context);
            return;
        }
        if (_objectReference)
        {
            TraceObjectReference(index, context);
            return;
        }
        if (_serialization)
        {
            TraceSerialization(index, context);
            return;
        }
        static ulong Register(void* state, int offset) => *(ulong*)((byte*)state + offset);
        var hit = Interlocked.Increment(ref _hits);
        if (hit <= 64)
        {
            var stack = Register(context, 152);
            var frame = Register(context, 160);
            Span<byte> callerBytes = stackalloc byte[8];
            var callerAddress = index == 0 ? stack : frame <= ulong.MaxValue - 8 ? frame + 8 : 0;
            var readable = Read(callerAddress, callerBytes);
            var caller = readable ? BinaryPrimitives.ReadUInt64LittleEndian(callerBytes) : 0;
            Console.Error.WriteLine($"[LOADER][TRACE] NativeFunction hit={hit} site={index} thread={Environment.CurrentManagedThreadId} " +
                $"rax=0x{Register(context, 120):X16} rdi=0x{Register(context, 176):X16} rsi=0x{Register(context, 168):X16} " +
                $"rdx=0x{Register(context, 136):X16} caller=0x{caller:X16} callerReadable={readable}");
        }
    }

    private void TraceSerialization(int index, void* context)
    {
        if (Interlocked.Increment(ref _probeHits[index]) == 1)
            Console.Error.WriteLine($"[LOADER][TRACE] NativeSerialization probe-hit site={index} thread={Environment.CurrentManagedThreadId}");
        static ulong Register(void* state, int offset) => *(ulong*)((byte*)state + offset);
        static bool Pointer(ulong address, ulong offset, out ulong value)
        {
            value = 0;
            Span<byte> bytes = stackalloc byte[8];
            if (address == 0 || address > ulong.MaxValue - offset || !Read(address + offset, bytes)) return false;
            value = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            return true;
        }

        var frame = Register(context, 160);
        var stack = Register(context, 152);
        var wrapper = Register(context, 224);
        var readable = index == 0 || (frame >= 0x50 && Pointer(frame - 0x50, 0, out wrapper));
        ulong adapter = 0;
        readable = readable && Pointer(wrapper, 0, out adapter);
        // Both probes use the verified archive wrapper layout, not arbitrary pointer scanning.
        if (!readable || !Pointer(adapter, 8, out var archive) ||
            !Pointer(archive, 0x98, out var proxy) || !Pointer(proxy, 0xF0, out var reader) ||
            !Pointer(reader, 0x98, out var file) || !Pointer(file, 0x10, out var nameAddress))
        {
            if (Interlocked.Exchange(ref _unreadableReported, 1) == 0)
                Console.Error.WriteLine("[LOADER][TRACE] NativeSerialization archive layout unreadable; verify probe addresses and layout.");
            return;
        }

        if (!TryReadAssetName(nameAddress, out var name))
        {
            if (Interlocked.Increment(ref _nameFailures) <= 4)
                Console.Error.WriteLine($"[LOADER][TRACE] NativeSerialization name-unreadable site={index} file=0x{file:X} address=0x{nameAddress:X}");
            return;
        }
        var matches = name.Contains(_assetFilter, StringComparison.OrdinalIgnoreCase);
        if (!matches)
        {
            if (Interlocked.Increment(ref _filteredHits) <= 4)
                Console.Error.WriteLine($"[LOADER][TRACE] NativeSerialization filtered site={index} asset={name} filter={_assetFilter}");
        }
        var hit = matches ? Interlocked.Increment(ref _hits) : 0;
        var writeLog = matches && hit <= 64;

        var cursorReadable = Pointer(reader, 8, out var cursorState) &&
            Pointer(cursorState, 0, out _);
        Pointer(cursorState, 0, out var cursor);
        var endReadable = Pointer(cursorState, 8, out var end);
        var startReadable = Pointer(cursorState, 16, out var start);
        var baseReadable = Pointer(reader, 0xE0, out var basePosition);
        Span<byte> headerBytes = stackalloc byte[2];
        var headerReadable = cursorReadable && endReadable && cursor <= end && end - cursor >= 2 && Read(cursor, headerBytes);
        var header = headerReadable ? BinaryPrimitives.ReadUInt16LittleEndian(headerBytes) : 0;
        var positionValid = cursorReadable && startReadable && baseReadable && cursor >= start && basePosition <= ulong.MaxValue - (cursor - start);
        var position = positionValid ? basePosition + cursor - start : 0;
        Pointer(frame, 0, out var parentFrame);
        var correlationFrame = index == 0 ? frame : parentFrame;
        RecordSerialization($"[LOADER][TRACE] NativeSerialization hit={hit} site={(index == 0 ? "schema" : "header")} " +
            $"thread={Environment.CurrentManagedThreadId} frame=0x{correlationFrame:X} asset={name} reader=0x{reader:X} " +
            $"position=0x{position:X} positionValid={positionValid} cursor=0x{cursor:X} end=0x{end:X} " +
            $"start=0x{start:X} basePosition=0x{basePosition:X} nextWord=0x{header:X4} headerReadable={headerReadable}", writeLog);
        if (writeLog && startReadable && endReadable && end >= start)
        {
            Span<byte> snapshot = stackalloc byte[192];
            var length = (int)Math.Min((ulong)snapshot.Length, end - start);
            var snapshotReadable = length > 0 && Read(start, snapshot[..length]);
            Console.Error.WriteLine($"[LOADER][TRACE] NativeSerialization buffer thread={Environment.CurrentManagedThreadId} " +
                $"reader=0x{reader:X} basePosition=0x{basePosition:X} length={length} readable={snapshotReadable} " +
                $"bytes={(snapshotReadable ? Convert.ToHexString(snapshot[..length]) : "unreadable")}");
        }
        if (index != 0) return;
        var cache = Register(context, 120);
        Span<byte> countBytes = stackalloc byte[4];
        var countReadable = Read(cache, countBytes);
        var count = countReadable ? BinaryPrimitives.ReadInt32LittleEndian(countBytes) : -1;
        Pointer(stack, 0x100, out var fragments);
        if (fragments == 0 && stack <= ulong.MaxValue - 0x80) fragments = stack + 0x80;
        var fragmentReadable = fragments != 0 && Read(fragments, countBytes);
        var fragment = fragmentReadable ? BinaryPrimitives.ReadUInt32LittleEndian(countBytes) : 0;
        RecordSerialization($"[LOADER][TRACE] NativeSerialization schema thread={Environment.CurrentManagedThreadId} " +
            $"frame=0x{frame:X} owner=0x{Register(context, 240):X} cache=0x{cache:X} count={count} countReadable={countReadable} " +
            $"firstFragment=0x{fragment:X8} fragmentReadable={fragmentReadable}", writeLog);
    }

    private void TraceObjectReference(int site, void* context)
    {
        var frame = *(ulong*)((byte*)context + 160);
        var owner = *(ulong*)((byte*)context + 216);
        if (site == 0)
        {
            var state = *(ulong*)((byte*)context + 120);
            var archive = *(ulong*)((byte*)context + 176);
            Span<byte> cursors = stackalloc byte[24];
            var readable = Read(state, cursors);
            var cursor = readable ? BinaryPrimitives.ReadUInt64LittleEndian(cursors) : 0;
            var end = readable ? BinaryPrimitives.ReadUInt64LittleEndian(cursors[8..]) : 0;
            var start = readable ? BinaryPrimitives.ReadUInt64LittleEndian(cursors[16..]) : 0;
            Span<byte> bytes = stackalloc byte[32];
            var length = readable && end >= cursor ? (int)Math.Min(32UL, end - cursor) : 0;
            var bytesReadable = length > 0 && Read(cursor, bytes[..length]);
            _referenceSource.Value = $"frame=0x{frame:X} owner=0x{owner:X} archive=0x{archive:X} " +
                $"state=0x{state:X} cursor=0x{cursor:X} end=0x{end:X} start=0x{start:X} cursorReadable={readable} " +
                $"bytes={(bytesReadable ? Convert.ToHexString(bytes[..length]) : "unreadable")}";
            return;
        }
        Span<byte> valueBytes = stackalloc byte[4];
        Span<byte> tableBytes = stackalloc byte[40];
        var valueReadable = frame >= 0x1C8 && Read(frame - 0x1C8, valueBytes);
        var tablesReadable = Read(owner, tableBytes);
        var value = valueReadable ? BinaryPrimitives.ReadInt32LittleEndian(valueBytes) : 0;
        var imports = tablesReadable ? BinaryPrimitives.ReadInt32LittleEndian(tableBytes[16..]) : -1;
        var exports = tablesReadable ? BinaryPrimitives.ReadInt32LittleEndian(tableBytes[32..]) : -1;
        var invalid = valueReadable && tablesReadable &&
            (value < 0 ? imports < 0 || ~value >= imports : value > 0 && (exports < 0 || value > exports));
        var hit = Interlocked.Increment(ref _hits);
        var writeLog = hit <= 8 || invalid && Interlocked.Increment(ref _invalidReferences) <= 16;
        RecordSerialization($"[LOADER][TRACE] NativeObjectReference hit={hit} thread={Environment.CurrentManagedThreadId} " +
            $"frame=0x{frame:X} owner=0x{owner:X} raw=0x{unchecked((uint)value):X8} imports={imports} exports={exports} " +
            $"valueReadable={valueReadable} tablesReadable={tablesReadable} invalid={invalid} source=({_referenceSource.Value ?? "missing"})", writeLog);
        _referenceSource.Value = null;
        if (invalid) DumpFirstFailure();
    }

    private void TraceAssetRead(int site, void* context)
    {
        static bool Word(ulong address, out ulong value)
        {
            Span<byte> bytes = stackalloc byte[8];
            var valid = address != 0 && address <= ulong.MaxValue - 8 && Read(address, bytes);
            value = valid ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
            return valid;
        }
        var frame = *(ulong*)((byte*)context + 160);
        var reader = *(ulong*)((byte*)context + 232);
        var key = (Environment.CurrentManagedThreadId, frame);
        var readerValid = reader != 0 && reader <= ulong.MaxValue - 0xE8;
        ulong state = 0, cursor = 0, end = 0, start = 0, positionBase = 0, expectedAddress = 0, expected = 0;
        var cursorValid = readerValid && Word(reader + 8, out state) && state != 0 && state <= ulong.MaxValue - 24 &&
            Word(state, out cursor) && Word(state + 8, out end) && Word(state + 16, out start);
        var positionValid = cursorValid && Word(reader + 0xE0, out positionBase) && cursor >= start &&
            positionBase <= ulong.MaxValue - (cursor - start);
        var expectedValid = frame >= 0x280 && Word(frame - 0x280, out expectedAddress) && Word(expectedAddress, out expected);
        Span<byte> sample = stackalloc byte[128];
        var length = cursorValid && cursor <= end ? (int)Math.Min((ulong)sample.Length, end - cursor) : 0;
        var sampleValid = length > 0 && Read(cursor, sample[..length]);
        var record = $"thread={key.Item1} frame=0x{frame:X} reader=0x{reader:X} " +
            $"cursor=0x{cursor:X} start=0x{start:X} end=0x{end:X} cursorValid={cursorValid} " +
            $"position=0x{(positionValid ? positionBase + cursor - start : 0):X} positionValid={positionValid} " +
            $"expected={expected} expectedValid={expectedValid} bytes={(sampleValid ? Convert.ToHexString(sample[..length]) : "unreadable")}";
        if (site == 0)
        {
            lock (_historyLock)
            {
                RememberFrame(_assetStarts, key, record);
            }
            RecordSerialization($"[LOADER][TRACE] NativeAssetRead begin {record}", Interlocked.Increment(ref _hits) <= 8);
            return;
        }
        var nameValid = TryReadAssetName(*(ulong*)((byte*)context + 136), out var name);
        string? before;
        lock (_historyLock) _assetStarts.Remove(key, out before);
        if (Interlocked.Increment(ref _invalidReferences) > 16) return;
        Console.Error.WriteLine($"[LOADER][TRACE] NativeAssetRead mismatch asset={name} nameValid={nameValid} {record}");
        Console.Error.WriteLine($"[LOADER][TRACE] NativeAssetRead prior {before ?? "unavailable"}");
        DumpFirstFailure();
    }

    private void TraceSchemaWalk(int site, void* context)
    {
        var frame = *(ulong*)((byte*)context + 160);
        var stack = *(ulong*)((byte*)context + 152);
        var key = (Environment.CurrentManagedThreadId, frame);
        Span<byte> bytes = stackalloc byte[64];
        if (site == 0)
        {
            var cache = *(ulong*)((byte*)context + 120);
            var valid = Read(cache, bytes);
            var count = valid ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : -1;
            var record = $"cache=0x{cache:X} count={count} readable={valid} headerAndEntries={(valid ? Convert.ToHexString(bytes) : "unreadable")}";
            ulong fragments = 0;
            if (stack <= ulong.MaxValue - 0x108 && Read(stack + 0x100, bytes[..8]))
                fragments = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
            if (fragments == 0 && stack <= ulong.MaxValue - 0x80) fragments = stack + 0x80;
            valid = Read(fragments, bytes);
            record += $" fragments=0x{fragments:X} fragmentReadable={valid} fragmentBytes={(valid ? Convert.ToHexString(bytes) : "unreadable")}";
            lock (_historyLock)
                RememberFrame(_schemas, key, (cache, count, record));
            RecordSerialization($"[LOADER][TRACE] NativeSchemaWalk begin thread={key.Item1} frame=0x{frame:X} {record}",
                Interlocked.Increment(ref _hits) <= 8);
            return;
        }
        var descriptor = *(ulong*)((byte*)context + 144);
        var readable = Read(descriptor, bytes[..16]);
        var packed = readable ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]) : 0;
        var property = readable ? BinaryPrimitives.ReadUInt64LittleEndian(bytes) : 0;
        var propertyPath = readable && (packed & 0xFF00000000UL) == 0;
        (ulong Address, int Count, string Record) schema;
        bool found;
        lock (_historyLock) found = _schemas.TryGetValue(key, out schema);
        var indexValid = found && schema.Address <= ulong.MaxValue - 8 && descriptor >= schema.Address + 8 &&
            (descriptor - schema.Address - 8) % 16 == 0;
        var fragment = *(ulong*)((byte*)context + 240);
        var fragmentValid = Read(fragment, bytes[..16]);
        var entryIndex = indexValid ? (descriptor - schema.Address - 8) / 16 : 0;
        var outside = indexValid && schema.Count >= 0 && entryIndex >= (ulong)schema.Count;
        var invalid = readable && (packed == ulong.MaxValue || propertyPath && property == 0 || outside);
        RecordSerialization($"[LOADER][TRACE] NativeSchemaWalk descriptor thread={key.Item1} frame=0x{frame:X} " +
            $"readable={readable} packed=0x{packed:X16} property=0x{property:X} propertyPath={propertyPath} invalid={invalid} " +
            $"descriptor=0x{descriptor:X} entryIndex={entryIndex} indexValid={indexValid} count={schema.Count} outside={(indexValid && schema.Count >= 0 && entryIndex >= (ulong)schema.Count)} " +
            $"remaining={*(ulong*)((byte*)context + 216)} fragment=0x{fragment:X} fragmentReadable={fragmentValid} " +
            $"fragmentBytes={(fragmentValid ? Convert.ToHexString(bytes[..16]) : "unreadable")} start=({(found ? schema.Record : "missing")})",
            invalid && Interlocked.Increment(ref _invalidReferences) <= 16);
        if (invalid) DumpFirstFailure();
    }

    internal static void RememberFrame<TKey, TValue>(OrderedDictionary<TKey, TValue> records, TKey key, TValue value)
        where TKey : notnull
    {
        records.Remove(key);
        if (records.Count >= 128) records.RemoveAt(0);
        records.Add(key, value);
    }

    private void RecordSerialization(string message, bool writeLog)
    {
        if (_historyOwner != null)
        {
            _historyOwner.RecordSerialization(message, writeLog);
            return;
        }
        lock (_historyLock)
        {
            if (_recentSerialization.Count == 32) _recentSerialization.Dequeue();
            _recentSerialization.Enqueue(message);
        }
        if (writeLog) Console.Error.WriteLine(message);
    }

    internal void DumpRecentSerialization()
    {
        if (_probes == null && !_serialization && !_objectReference && !_assetRead && !_schemaWalk) return;
        string[] messages;
        lock (_historyLock) messages = _recentSerialization.ToArray();
        Console.Error.WriteLine($"[LOADER][TRACE] NativeSerialization recent-begin count={messages.Length}");
        foreach (var message in messages) Console.Error.WriteLine(message);
        Console.Error.WriteLine("[LOADER][TRACE] NativeSerialization recent-end");
    }

    private void DumpFirstFailure()
    {
        var owner = _historyOwner ?? this;
        if (Interlocked.Exchange(ref owner._failureReported, 1) == 0) owner.DumpRecentSerialization();
    }

    internal static bool TryReadAssetName(ulong address, out string name)
    {
        name = "";
        if (address == 0) return false;
        Span<char> characters = stackalloc char[256];
        Span<byte> bytes = stackalloc byte[2];
        for (var index = 0; index < characters.Length; index++)
        {
            var offset = (ulong)index * 2;
            if (address > ulong.MaxValue - offset || !Read(address + offset, bytes)) return false;
            var character = (char)BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            if (character == '\0')
            {
                name = new string(characters[..index]);
                return true;
            }
            characters[index] = char.IsControl(character) ? '?' : character;
        }
        return false;
    }

    private void WriteInstruction(int index, byte value)
    {
        var address = _addresses[index];
        if (!_memory.Protect(address, 1, HostPageProtection.ReadWriteExecute, out var protection))
            throw new InvalidOperationException("Native trace code protection failed.");
        *(byte*)address = value;
        if (!_memory.ProtectRaw(address, 1, protection, out _))
            throw new InvalidOperationException("Native trace code protection restore failed.");
        _memory.FlushInstructionCache(address, 1);
    }

    public void Dispose()
    {
        while (_armed > 0)
        {
            var index = _armed - 1;
            WriteInstruction(index, _instructions[index][0]);
            _armed--;
        }
        if (_resumeCode != 0 && _memory.Free(_resumeCode)) _resumeCode = 0;
    }

    private static bool Read(ulong address, Span<byte> bytes)
    {
        fixed (byte* destination = bytes)
            return ReadProcessMemory((nint)(-1), (void*)address, destination, (nuint)bytes.Length, out var read) && read == (nuint)bytes.Length;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, void* address, void* destination, nuint size, out nuint read);
}
