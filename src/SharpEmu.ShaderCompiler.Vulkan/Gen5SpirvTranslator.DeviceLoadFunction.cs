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
