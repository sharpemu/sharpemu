// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

internal sealed class GuestOwnershipTrace
{
    internal sealed class CapturePoint
    {
        public string Caller { get; set; } = "";
        public string Import { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Returned { get; set; }
        public int Capacity { get; set; } = 4096;
        public Region[] Regions { get; set; } = [];
    }

    internal sealed class Region
    {
        public string Name { get; set; } = "";
        public string Address { get; set; } = "";
        public string Length { get; set; } = "40";
        public string? End { get; set; }
        public bool FollowPointers { get; set; }
    }

    private sealed class PointState(CapturePoint point)
    {
        public CapturePoint Point { get; } = point;
        public int Count;
        public long Dropped;
    }

    private readonly record struct RegionSnapshot(string Name, ulong Address, ulong Requested,
        bool Readable, bool Truncated, byte[] Bytes);
    private readonly record struct Capture(long Timestamp, uint HostThread, ulong GuestThread,
        ulong Caller, string Name, bool Returned, ulong Result, Dictionary<string, ulong> Registers,
        RegionSnapshot[] Regions);

    private const int MaximumRegionBytes = 256 * 1024;
    private const long MaximumStoredBytes = 128 * 1024 * 1024;
    private readonly object _gate = new();
    private readonly Dictionary<ulong, PointState> _points = new();
    private readonly HashSet<string> _imports = new(StringComparer.Ordinal);
    private readonly List<Capture> _captures = [];
    private long _storedBytes;
    private long _budgetDropped;

    internal GuestOwnershipTrace(CapturePoint[] points)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Length is 0 or > 32) throw new ArgumentException("Use 1 to 32 ownership capture points.");
        foreach (var point in points)
        {
            if (point is null || string.IsNullOrWhiteSpace(point.Caller) || !TryHex(point.Caller, out var caller) || caller == 0 ||
                string.IsNullOrWhiteSpace(point.Import) || string.IsNullOrWhiteSpace(point.Name) ||
                point.Capacity is < 1 or > 16384 || point.Regions is null || point.Regions.Length > 16 ||
                !_points.TryAdd(caller, new PointState(point)))
                throw new ArgumentException("The ownership capture point is invalid or repeated.");
            foreach (var region in point.Regions)
            {
                if (region is null || string.IsNullOrWhiteSpace(region.Name) ||
                    string.IsNullOrWhiteSpace(region.Address) ||
                    (region.End is null ? string.IsNullOrWhiteSpace(region.Length) : string.IsNullOrWhiteSpace(region.End)))
                    throw new ArgumentException("Each ownership region needs a name, an address, and a length or end.");
            }
            _imports.Add(point.Import);
        }
    }

    internal static GuestOwnershipTrace Load(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new ArgumentException("The ownership plan exceeds 64 KiB.");
        return new GuestOwnershipTrace(JsonSerializer.Deserialize<CapturePoint[]>(File.ReadAllText(path))
            ?? throw new ArgumentException("The ownership plan is empty."));
    }

    internal bool IncludesImport(string name) => _imports.Contains(name);
    internal bool IncludesCaller(ulong caller) => _points.ContainsKey(caller);

    internal bool ShouldCapture(string import, ulong caller, bool returned)
    {
        if (!_points.TryGetValue(caller, out var state) || state.Point.Import != import ||
            state.Point.Returned != returned) return false;
        lock (_gate)
        {
            if (state.Count >= state.Point.Capacity) { state.Dropped++; return false; }
            if (_storedBytes >= MaximumStoredBytes) { _budgetDropped++; return false; }
            return true;
        }
    }

    internal void Record(CpuContext context, string import, ulong caller, bool returned, ulong result,
        uint hostThread, Dictionary<string, ulong> registers)
    {
        if (!_points.TryGetValue(caller, out var state) || state.Point.Import != import ||
            state.Point.Returned != returned) return;
        lock (_gate)
        {
            if (state.Count >= state.Point.Capacity) { state.Dropped++; return; }
            if (_storedBytes >= MaximumStoredBytes) { _budgetDropped++; return; }
            var regions = new List<RegionSnapshot>();
            foreach (var region in state.Point.Regions)
            {
                var valid = TryEvaluate(context, registers, region.Address, out var address);
                ulong length;
                if (region.End is { } end)
                {
                    valid &= TryEvaluate(context, registers, end, out var endAddress) && endAddress >= address;
                    length = valid ? endAddress - address : 0;
                }
                else
                {
                    valid &= TryEvaluate(context, registers, region.Length, out length);
                }
                var snapshot = ReadRegion(context, region.Name, address, length, valid);
                regions.Add(snapshot);
                if (!region.FollowPointers || !snapshot.Readable) continue;
                for (var offset = 0; offset + sizeof(ulong) <= snapshot.Bytes.Length; offset += sizeof(ulong))
                {
                    if (_storedBytes >= MaximumStoredBytes) { _budgetDropped++; break; }
                    var pointer = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(snapshot.Bytes.AsSpan(offset));
                    regions.Add(ReadRegion(context, $"{region.Name}[{offset / 8}]", pointer, 64, pointer != 0));
                }
            }
            state.Count++;
            _storedBytes += 512;
            _captures.Add(new Capture(Stopwatch.GetTimestamp(), hostThread,
                GuestThreadExecution.CurrentGuestThreadHandle, caller, state.Point.Name, returned, result,
                registers, regions.ToArray()));
        }
    }

    private RegionSnapshot ReadRegion(CpuContext context, string name, ulong address, ulong length, bool valid)
    {
        var available = Math.Max(0, MaximumStoredBytes - _storedBytes);
        var count = (int)Math.Min((ulong)Math.Min(available, MaximumRegionBytes), length);
        var bytes = new byte[count];
        var readable = valid && (length == 0 || (count != 0 && address != 0 && address <= ulong.MaxValue - (ulong)count &&
            context.Memory.TryRead(address, bytes)));
        _storedBytes += count + 128;
        return new RegionSnapshot(name, address, length, readable, (ulong)count != length,
            readable ? bytes : []);
    }

    // '/' reads an eight-byte pointer at an offset. Arithmetic and pointer operations run left to right.
    internal static bool TryEvaluate(CpuContext context, IReadOnlyDictionary<string, ulong> registers,
        string expression, out ulong value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(expression) || expression.Length > 256) return false;
        var position = expression.IndexOfAny(['+', '/', '-', '*']);
        var root = position < 0 ? expression : expression[..position];
        if (!registers.TryGetValue(root, out value) && !TryHex(root, out value)) return false;
        var operations = 0;
        while (position >= 0)
        {
            if (++operations > 16) return false;
            var operation = expression[position++];
            var next = expression.IndexOfAny(['+', '/', '-', '*'], position);
            var operand = next < 0 ? expression[position..] : expression[position..next];
            if (!TryHex(operand, out var offset)) return false;
            if (operation == '-')
            {
                if (offset > value) return false;
                value -= offset;
            }
            else if (operation == '*')
            {
                if (offset != 0 && value > ulong.MaxValue / offset) return false;
                value *= offset;
            }
            else
            {
                if (offset > ulong.MaxValue - value) return false;
                value += offset;
            }
            if (operation == '/' && (value == 0 || value > ulong.MaxValue - 8 || !context.TryReadUInt64(value, out value)))
                return false;
            position = next;
        }
        return true;
    }

    private static bool TryHex(string text, out ulong value) => ulong.TryParse(
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text,
        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);

    internal void Write(TextWriter writer)
    {
        lock (_gate)
        {
            writer.WriteLine($"[LOADER][TRACE] ownership-summary count={_captures.Count} bytes={_storedBytes} budget_dropped={_budgetDropped} frequency={Stopwatch.Frequency} atomic=False");
            foreach (var state in _points.Values)
                writer.WriteLine($"[LOADER][TRACE] ownership-point name={state.Point.Name} count={state.Count} dropped={state.Dropped}");
            foreach (var capture in _captures)
            {
                writer.WriteLine($"[LOADER][TRACE] ownership-event ticks={capture.Timestamp} host_thread={capture.HostThread} thread=0x{capture.GuestThread:X16} caller=0x{capture.Caller:X16} phase={capture.Name} returned={capture.Returned} result=0x{capture.Result:X16} " +
                    string.Join(" ", capture.Registers.Select(pair => $"{pair.Key}=0x{pair.Value:X16}")));
                foreach (var region in capture.Regions)
                    writer.WriteLine($"[LOADER][TRACE] ownership-region ticks={capture.Timestamp} phase={capture.Name} name={region.Name} address=0x{region.Address:X16} requested={region.Requested} readable={region.Readable} truncated={region.Truncated} bytes={Convert.ToHexString(region.Bytes)}");
            }
        }
    }
}
