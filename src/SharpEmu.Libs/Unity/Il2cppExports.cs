// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Unity;

/// <summary>
/// IL2CPP runtime API stubs. Unity games with IL2CPP backend look up
/// these functions by name via il2cpp_api_lookup_symbol, not by NID.
/// We provide a resolver that returns fake function pointers.
/// </summary>
public static class Il2cppExports
{
    private static bool _initialized;
    private static ulong _domain = 0x1000;
    private static ulong _nextFakePtr = 0x10000;

    // il2cpp_api_register_symbol — called by game to register a symbol
    [SysAbiExport(
        Nid = "cJ2Y4E-t258",
        ExportName = "il2cpp_api_register_symbol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libil2cpp")]
    public static int Il2cppRegisterSymbol(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] = 0;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    // The game also calls il2cpp_api_lookup_symbol which is handled
    // by the existing HLE infrastructure. We need to make sure that
    // lookup returns a valid function pointer for each IL2CPP API name.
    //
    // Since we can't register these as SysAbiExports (NID validation
    // fails for made-up NIDs), we handle them via the bootstrap bridge
    // or dlsym mechanism. For now, the game will get NULL for unknown
    // IL2CPP functions, which causes it to abort.
    //
    // A proper fix would be to:
    // 1. Emit native x86-64 stubs that return 0 (success)
    // 2. Register each stub at a unique address
    // 3. Return the stub address when il2cpp_api_lookup_symbol is called

    // For now, let's log what the game is looking for:
    public static ulong ResolveIl2cppSymbol(string name)
    {
        Console.Error.WriteLine($"[HLE][IL2CPP] Resolving: {name}");

        // Return a unique fake pointer for each symbol.
        // The game will call this pointer, which will hit an unresolved
        // import stub and return 0 (success).
        var ptr = _nextFakePtr;
        _nextFakePtr += 0x100;
        return ptr;
    }
}
