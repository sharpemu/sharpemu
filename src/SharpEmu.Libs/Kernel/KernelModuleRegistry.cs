// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq;
using System.Collections.Generic;
using System.IO;

namespace SharpEmu.Libs.Kernel;

public static class KernelModuleRegistry
{
    public enum ModuleStartState
    {
        NotStarted,
        Starting,
        Started,
    }

    private static readonly object _gate = new();
    private static readonly Dictionary<int, ModuleEntry> _modulesByHandle = new();
    private static readonly Dictionary<int, Dictionary<string, ulong>> _symbolsByHandle = new();
    private static readonly Dictionary<string, int> _handleByPath = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, int> _handleByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<int, int> _sysmoduleHandleById = new();
    private static int _nextHandle = 1;

    private static readonly Dictionary<int, string[]> KnownSysmoduleNames = new()
    {
        [0x0006] = new[] { "libSceFiber.sprx", "libSceFiber.prx" },
        [0x0021] = new[] { "libSceRudp.sprx", "libSceRudp.prx" },
        [0x0095] = new[] { "libSceIme.sprx", "libSceIme.prx" },
        [0x0096] = new[] { "libSceImeDialog.sprx", "libSceImeDialog.prx" },
        [0x00A9] = new[] { "libSceMouse.sprx", "libSceMouse.prx" },
    };

    public readonly record struct ModuleEntry(
        int Handle,
        string Name,
        string Path,
        ulong BaseAddress,
        ulong EndAddress,
        ulong EntryPoint,
        ulong InitEntryPoint,
        ulong EhFrameHeaderAddress,
        ulong EhFrameAddress,
        ulong EhFrameSize,
        ModuleStartState StartState,
        bool IsMain,
        bool IsSystemModule);

    public static void Reset()
    {
        lock (_gate)
        {
            _modulesByHandle.Clear();
            _symbolsByHandle.Clear();
            _handleByPath.Clear();
            _handleByName.Clear();
            _sysmoduleHandleById.Clear();
            _nextHandle = 1;
        }
    }

    public static int RegisterModule(
        string? modulePath,
        ulong baseAddress,
        ulong size,
        ulong entryPoint,
        ulong initEntryPoint,
        ulong ehFrameHeaderAddress,
        ulong ehFrameAddress,
        ulong ehFrameSize,
        bool isMain,
        bool isSystemModule = false)
    {
        var normalizedPath = NormalizePath(modulePath);
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(normalizedPath) &&
                _handleByPath.TryGetValue(normalizedPath, out var existingHandle) &&
                _modulesByHandle.TryGetValue(existingHandle, out var existing))
            {
                var updated = existing with
                {
                    BaseAddress = baseAddress,
                    EndAddress = ComputeEnd(baseAddress, size),
                    EntryPoint = entryPoint,
                    InitEntryPoint = initEntryPoint,
                    EhFrameHeaderAddress = ehFrameHeaderAddress,
                    EhFrameAddress = ehFrameAddress,
                    EhFrameSize = ehFrameSize,
                    IsMain = existing.IsMain || isMain,
                    IsSystemModule = existing.IsSystemModule || isSystemModule,
                };
                _modulesByHandle[existingHandle] = updated;
                _handleByName[updated.Name] = existingHandle;
                return existingHandle;
            }

            var handle = _nextHandle++;
            var name = ResolveName(normalizedPath, handle);
            var entry = new ModuleEntry(
                Handle: handle,
                Name: name,
                Path: normalizedPath,
                BaseAddress: baseAddress,
                EndAddress: ComputeEnd(baseAddress, size),
                EntryPoint: entryPoint,
                InitEntryPoint: initEntryPoint,
                EhFrameHeaderAddress: ehFrameHeaderAddress,
                EhFrameAddress: ehFrameAddress,
                EhFrameSize: ehFrameSize,
                StartState: ModuleStartState.NotStarted,
                IsMain: isMain,
                IsSystemModule: isSystemModule);
            _modulesByHandle[handle] = entry;
            if (!string.IsNullOrWhiteSpace(normalizedPath))
            {
                _handleByPath[normalizedPath] = handle;
            }

            _handleByName[name] = handle;
            return handle;
        }
    }

    public static int RegisterSyntheticModule(string moduleName, bool isSystemModule)
    {
        lock (_gate)
        {
            if (TryResolveHandleByNameLocked(moduleName, out var existingHandle))
            {
                var existing = _modulesByHandle[existingHandle];
                _modulesByHandle[existingHandle] = existing with { IsSystemModule = existing.IsSystemModule || isSystemModule };
                return existingHandle;
            }

            var handle = _nextHandle++;
            var fallbackName = string.IsNullOrWhiteSpace(moduleName)
                ? $"module_{handle:X4}.sprx"
                : moduleName;
            var entry = new ModuleEntry(
                Handle: handle,
                Name: fallbackName,
                Path: string.Empty,
                BaseAddress: 0,
                EndAddress: 0,
                EntryPoint: 0,
                InitEntryPoint: 0,
                EhFrameHeaderAddress: 0,
                EhFrameAddress: 0,
                EhFrameSize: 0,
                StartState: ModuleStartState.Started,
                IsMain: false,
                IsSystemModule: isSystemModule);
            _modulesByHandle[handle] = entry;
            _handleByName[fallbackName] = handle;
            return handle;
        }
    }

    public static int MarkSysmoduleLoaded(int sysmoduleId)
    {
        lock (_gate)
        {
            if (_sysmoduleHandleById.TryGetValue(sysmoduleId, out var existingHandle) &&
                _modulesByHandle.ContainsKey(existingHandle))
            {
                return existingHandle;
            }

            var handle = TryResolveKnownSysmoduleHandleLocked(sysmoduleId, out var knownHandle)
                ? knownHandle
                : RegisterSyntheticModule($"sysmodule_0x{sysmoduleId:X4}.sprx", isSystemModule: true);
            _sysmoduleHandleById[sysmoduleId] = handle;
            return handle;
        }
    }

    public static void MarkSysmoduleUnloaded(int sysmoduleId)
    {
        lock (_gate)
        {
            _sysmoduleHandleById.Remove(sysmoduleId);
        }
    }

    public static bool IsSysmoduleLoaded(int sysmoduleId)
    {
        lock (_gate)
        {
            return _sysmoduleHandleById.ContainsKey(sysmoduleId);
        }
    }

    public static bool TryGetModuleByAddress(ulong address, out ModuleEntry module)
    {
        lock (_gate)
        {
            ModuleEntry? best = null;
            foreach (var entry in _modulesByHandle.Values)
            {
                if (entry.BaseAddress == 0 || entry.EndAddress <= entry.BaseAddress)
                {
                    continue;
                }

                if (address < entry.BaseAddress || address >= entry.EndAddress)
                {
                    continue;
                }

                if (best is null || (entry.EndAddress - entry.BaseAddress) < (best.Value.EndAddress - best.Value.BaseAddress))
                {
                    best = entry;
                }
            }

            if (best is null)
            {
                module = default;
                return false;
            }

            module = best.Value;
            return true;
        }
    }

    public static bool TryGetModuleByHandle(int handle, out ModuleEntry module)
    {
        lock (_gate)
        {
            return _modulesByHandle.TryGetValue(handle, out module);
        }
    }

    /// <summary>
    /// Atomically claims a module initializer. A module can be observed while
    /// it is starting (for recursive loader calls), but its DT_INIT routine is
    /// executed at most once after a successful start.
    /// </summary>
    public static bool TryBeginModuleStart(int handle, out ModuleEntry module)
    {
        lock (_gate)
        {
            if (!_modulesByHandle.TryGetValue(handle, out module))
            {
                return false;
            }

            if (module.StartState != ModuleStartState.NotStarted)
            {
                return false;
            }

            if (module.InitEntryPoint < 0x10000)
            {
                module = module with { StartState = ModuleStartState.Started };
                _modulesByHandle[handle] = module;
                return false;
            }

            module = module with { StartState = ModuleStartState.Starting };
            _modulesByHandle[handle] = module;
            return true;
        }
    }

    public static void CompleteModuleStart(int handle, bool succeeded)
    {
        lock (_gate)
        {
            if (!_modulesByHandle.TryGetValue(handle, out var module) ||
                module.StartState != ModuleStartState.Starting)
            {
                return;
            }

            _modulesByHandle[handle] = module with
            {
                StartState = succeeded ? ModuleStartState.Started : ModuleStartState.NotStarted,
            };
        }
    }

    public static void RegisterModuleSymbols(int handle, IReadOnlyDictionary<string, ulong> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        lock (_gate)
        {
            if (!_modulesByHandle.ContainsKey(handle))
            {
                return;
            }

            if (!_symbolsByHandle.TryGetValue(handle, out var destination))
            {
                destination = new Dictionary<string, ulong>(StringComparer.Ordinal);
                _symbolsByHandle[handle] = destination;
            }

            foreach (var (name, address) in symbols)
            {
                if (!string.IsNullOrWhiteSpace(name) && address >= 0x10000)
                {
                    destination.TryAdd(name, address);
                }
            }
        }
    }

    public static bool TryResolveModuleSymbol(int handle, string symbolName, out ulong address)
    {
        address = 0;
        if (string.IsNullOrWhiteSpace(symbolName))
        {
            return false;
        }

        lock (_gate)
        {
            return _symbolsByHandle.TryGetValue(handle, out var symbols) &&
                   symbols.TryGetValue(symbolName, out address) &&
                   address >= 0x10000;
        }
    }

    public static bool TryFindByPathOrName(string? modulePathOrName, out ModuleEntry module)
    {
        module = default;
        if (string.IsNullOrWhiteSpace(modulePathOrName))
        {
            return false;
        }

        var normalizedPath = NormalizePath(modulePathOrName);
        var fileName = ResolveName(normalizedPath, 0);
        lock (_gate)
        {
            if (!string.IsNullOrWhiteSpace(normalizedPath) &&
                _handleByPath.TryGetValue(normalizedPath, out var handleByPath) &&
                _modulesByHandle.TryGetValue(handleByPath, out module))
            {
                return true;
            }

            if (TryResolveHandleByNameLocked(fileName, out var handleByName) &&
                _modulesByHandle.TryGetValue(handleByName, out module))
            {
                return true;
            }
        }

        return false;
    }

    public static int[] GetModuleHandles(bool includeSystemModules)
    {
        lock (_gate)
        {
            return _modulesByHandle.Values
                .Where(entry => includeSystemModules || !entry.IsSystemModule)
                .OrderBy(entry => entry.Handle)
                .Select(entry => entry.Handle)
                .ToArray();
        }
    }

    public static bool TryGetFirstModule(out ModuleEntry module)
    {
        lock (_gate)
        {
            if (_modulesByHandle.Count == 0)
            {
                module = default;
                return false;
            }

            var first = _modulesByHandle.Values.OrderBy(entry => entry.Handle).First();
            module = first;
            return true;
        }
    }

    private static bool TryResolveKnownSysmoduleHandleLocked(int sysmoduleId, out int handle)
    {
        handle = 0;
        if (!KnownSysmoduleNames.TryGetValue(sysmoduleId, out var candidates))
        {
            return false;
        }

        foreach (var candidate in candidates)
        {
            if (TryResolveHandleByNameLocked(candidate, out handle))
            {
                var existing = _modulesByHandle[handle];
                _modulesByHandle[handle] = existing with { IsSystemModule = true };
                return true;
            }
        }

        return false;
    }

    private static bool TryResolveHandleByNameLocked(string moduleName, out int handle)
    {
        handle = 0;
        if (string.IsNullOrWhiteSpace(moduleName))
        {
            return false;
        }

        if (_handleByName.TryGetValue(moduleName, out handle))
        {
            return true;
        }

        var altName = moduleName.EndsWith(".sprx", StringComparison.OrdinalIgnoreCase)
            ? moduleName[..^5] + ".prx"
            : (moduleName.EndsWith(".prx", StringComparison.OrdinalIgnoreCase)
                ? moduleName[..^4] + ".sprx"
                : string.Empty);
        return !string.IsNullOrWhiteSpace(altName) && _handleByName.TryGetValue(altName, out handle);
    }

    private static ulong ComputeEnd(ulong baseAddress, ulong size)
    {
        if (size == 0)
        {
            return baseAddress;
        }

        return unchecked(baseAddress + size);
    }

    private static string NormalizePath(string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(modulePath).Replace('\\', '/');
        }
        catch
        {
            return modulePath.Replace('\\', '/');
        }
    }

    private static string ResolveName(string normalizedPath, int handleHint)
    {
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var fileName = Path.GetFileName(normalizedPath);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
        }

        return handleHint <= 0
            ? "module.sprx"
            : $"module_{handleHint:X4}.sprx";
    }
}
