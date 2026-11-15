// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.ShaderCompiler.Vulkan;

public static partial class Gen5SpirvTranslator
{
    // SHARPEMU_SHARED_DEVICE_LOADS (on by default; =0 turns it off): a compact program reads each device dword through
    // one DontInline function (page lookup, fault report, fallback load) instead of emitting that sequence at every
    // load. Inline, the sequences can make the driver's compiler take a minute or more for one large outlined call
    // body, which compiles in seconds this way, and they lengthen the program's GPU submissions. Tests may set it.
    public static bool SharedDeviceLoads { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_SHARED_DEVICE_LOADS"), "0", StringComparison.Ordinal);

    // SHARPEMU_SHARED_DEVICE_RANGES (on by default; =0 turns it off): with the shared function, a range of 2 to 16
    // consecutive device dwords (scalar loads, scalar buffer loads, buffer elements) goes through one DontInline
    // function per range length instead of one call per dword. The driver's compile time grows faster than linearly
    // with the call sites in a module, so a record of many outlined bodies can take minutes and gigabytes to compile;
    // one call per range leaves about a third of the call sites. Tests may set it.
    public static bool SharedDeviceRanges { get; set; } =
        !string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_SHARED_DEVICE_RANGES"), "0", StringComparison.Ordinal);

    private sealed partial class CompilationContext
    {
        private uint _deviceDwordFunction;

        // Whether this program's compact device dword loads go through the shared function.
        private bool UsesSharedDeviceLoads =>
            SharedDeviceLoads && _compactMemoryAccess && _pageTable != 0 && _deviceAddressFaultPage != 0 &&
            !_request.TraceDeviceAddressFaults;

        // One dword through the page table, in the shared function: allowed gates the load (a refused dword reads zero),
        // report gates the report of a missing page, as ResolveCompactDeviceAddress's reportFault does.
        private uint CallDeviceDword(uint address64, uint allowed, uint report)
        {
            if (_deviceDwordFunction == 0)
            {
                _deviceDwordFunction = _module.AllocateId();
            }

            return _module.AddInstruction(SpirvOp.FunctionCall, _uintType, _deviceDwordFunction, address64, allowed, report);
        }

        // Each range length's function holds the range's inline form (LoadDeviceDwordRange: the first and last dwords' page
        // lookups and fault reports, then each dword's load), which reads and reports as the calls per dword do.
        private readonly Dictionary<int, (uint Function, uint ArrayType)> _deviceRangeFunctions = [];
        private bool _emittingDeviceRange;

        private bool UsesSharedDeviceRanges => SharedDeviceRanges && UsesSharedDeviceLoads && !_emittingDeviceRange;

        private uint[] CallDeviceDwordRange(uint address64, IReadOnlyList<uint> allowed)
        {
            var count = allowed.Count;
            if (!_deviceRangeFunctions.TryGetValue(count, out var range))
            {
                range = (_module.AllocateId(), _module.TypeArrayDistinct(_uintType, (uint)count));
                _deviceRangeFunctions[count] = range;
            }

            var result = _module.AddInstruction(SpirvOp.FunctionCall, range.ArrayType, [range.Function, address64, .. allowed]);
            var values = new uint[count];
            for (var dword = 0; dword < count; dword++)
            {
                values[dword] = _module.AddInstruction(SpirvOp.CompositeExtract, _uintType, result, (uint)dword);
            }

            return values;
        }

        // After every function that may call them.
        private void EmitDeviceRangeFunctions()
        {
            const uint dontInline = 2;
            foreach (var (count, range) in _deviceRangeFunctions.OrderBy(entry => entry.Key))
            {
                var functionType = _module.TypeFunction(range.ArrayType, [_ulongType, .. Enumerable.Repeat(_boolType, count)]);
                _module.BeginFunction(range.ArrayType, functionType, dontInline, range.Function);
                _module.AddName(range.Function, $"loadDeviceDwords{count}");
                var address = _module.AddFunctionParameter(_ulongType);
                var allowed = new uint[count];
                for (var dword = 0; dword < count; dword++)
                {
                    allowed[dword] = _module.AddFunctionParameter(_boolType);
                }

                _module.AddLabel();
                InitializeDeviceAddressFallback();
                _emittingDeviceRange = true;
                try
                {
                    var values = LoadDeviceDwordRange(address, allowed);
                    _module.AddStatement(SpirvOp.ReturnValue, _module.AddInstruction(SpirvOp.CompositeConstruct, range.ArrayType, values));
                }
                finally
                {
                    _emittingDeviceRange = false;
                }

                _module.EndFunction();
            }
        }

        // After every function that may call it.
        private void EmitDeviceDwordFunction()
        {
            if (_deviceDwordFunction == 0)
            {
                return;
            }

            const uint dontInline = 2;
            var functionType = _module.TypeFunction(_uintType, _ulongType, _boolType, _boolType);
            _module.BeginFunction(_uintType, functionType, dontInline, _deviceDwordFunction);
            _module.AddName(_deviceDwordFunction, "loadDeviceDword");
            var address = _module.AddFunctionParameter(_ulongType);
            var allowed = _module.AddFunctionParameter(_boolType);
            var report = _module.AddFunctionParameter(_boolType);
            _module.AddLabel();
            InitializeDeviceAddressFallback();
            var (pointer, valid) = ResolveCompactDeviceAddress(address, report);
            _module.AddStatement(SpirvOp.ReturnValue, LoadMappedDeviceDword(pointer, LogicalAnd(allowed, valid)));
            _module.EndFunction();
        }
    }
}
