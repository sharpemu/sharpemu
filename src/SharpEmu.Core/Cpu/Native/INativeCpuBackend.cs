// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Core.Loader;

namespace SharpEmu.Core.Cpu.Native;

public interface INativeCpuBackend
{
    string BackendName { get; }

    string? LastError { get; }

    bool TryExecute(
        CpuContext context,
        ulong entryPoint,
        Generation generation,
        IReadOnlyDictionary<ulong, string> importStubs,
        IReadOnlyDictionary<string, ImportedSymbolMetadata> importMetadata,
        IReadOnlyDictionary<string, ulong> runtimeSymbols,
        CpuExecutionOptions executionOptions,
        out OrbisGen2Result result);
}
