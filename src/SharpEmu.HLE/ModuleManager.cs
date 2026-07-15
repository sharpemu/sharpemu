// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpEmu.HLE;

public sealed class ModuleManager : IModuleManager
{
    private readonly ConcurrentDictionary<string, Delegate> _dispatchTable = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExportedFunction> _exportTable = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ExportedFunction> _exportNameTable = new(StringComparer.Ordinal);
    private readonly object _registrationGate = new();
    private readonly HashSet<Assembly> _warmupAssemblies = new();
    private bool _isFrozen;

    public int RegisterExports(IReadOnlyList<ExportedFunction> exports)
    {
        ArgumentNullException.ThrowIfNull(exports);

        lock (_registrationGate)
        {
            if (_isFrozen)
            {
                throw new InvalidOperationException("Module registration is frozen.");
            }

            var registeredCount = 0;
            foreach (var export in exports)
            {
                if (!_dispatchTable.TryAdd(export.Nid, export.Function))
                {
                    Console.Error.WriteLine($"[HLE] Duplicate NID '{export.Nid}' ({export.Name}) — already registered, skipping.");
                    continue;
                }

                _exportTable[export.Nid] = export;
                _exportNameTable.TryAdd(export.Name, export);
                // The warm sweep in Freeze() covers every assembly that contributed a
                // handler (generated thunks resolve to their home assembly too).
                _warmupAssemblies.Add(export.Function.Method.Module.Assembly);
                registeredCount++;
            }

            return registeredCount;
        }
    }

    public void Freeze()
    {
        lock (_registrationGate)
        {
            _isFrozen = true;
        }

        WarmHleTypeInitializers();
    }

    // A .cctor or first JIT running on a guest thread's hijacked stack fail-fasts the CLR.
    // Run every HLE type's initializer and JIT its methods here first, on a host thread.
    private void WarmHleTypeInitializers()
    {
        Assembly[] assemblies;
        lock (_registrationGate)
        {
            assemblies = new Assembly[_warmupAssemblies.Count];
            _warmupAssemblies.CopyTo(assemblies);
        }

        assemblies = WithGuestReachableDependencies(assemblies);
        var bclWarmed = WarmFrameworkTypeInitializers();

        var warmed = 0;
        var failed = 0;
        var jitted = 0;
        var jitFailed = 0;
        foreach (var assembly in assemblies)
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }

            const BindingFlags allMembers = BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

            foreach (var type in types)
            {
                if (type is null || type.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    warmed++;
                }
                catch
                {
                    // A throw here beats a guest-thread fail-fast later; swallow and continue.
                    failed++;
                }

                // Force-JIT (not execute) every method so no guest thread compiles one first.
                MethodBase[] members;
                try
                {
                    members = type.GetConstructors(allMembers)
                        .Concat<MethodBase>(type.GetMethods(allMembers))
                        .ToArray();
                }
                catch
                {
                    continue;
                }

                foreach (var member in members)
                {
                    if (member.ContainsGenericParameters || member.IsAbstract || member.MethodImplementationFlags.HasFlag(MethodImplAttributes.InternalCall))
                    {
                        continue;
                    }

                    try
                    {
                        RuntimeHelpers.PrepareMethod(member.MethodHandle);
                        jitted++;
                    }
                    catch
                    {
                        jitFailed++;
                    }
                }
            }
        }

        Console.Error.WriteLine(
            $"[HLE] Warmed {warmed} type initializers ({failed} threw) + JIT-compiled {jitted} methods ({jitFailed} skipped) across {assemblies.Length} HLE assemblies, plus {bclWarmed} framework type initializers.");
    }

    // Framework .cctors too (but not JIT — the BCL is too large).
    private static int WarmFrameworkTypeInitializers()
    {
        var warmed = 0;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name;
            if (name is null || !IsFrameworkAssembly(name))
            {
                continue;
            }

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).ToArray()!;
            }
            catch
            {
                continue;
            }

            foreach (var type in types)
            {
                if (type is null || type.ContainsGenericParameters)
                {
                    continue;
                }

                try
                {
                    RuntimeHelpers.RunClassConstructor(type.TypeHandle);
                    warmed++;
                }
                catch
                {
                }
            }
        }

        return warmed;
    }

    private static bool IsFrameworkAssembly(string assemblyName) =>
        assemblyName.StartsWith("System", StringComparison.Ordinal) ||
        string.Equals(assemblyName, "netstandard", StringComparison.Ordinal);

    // Warm the interop assemblies guest threads reach (e.g. Silk.NET via the flip path).
    private static Assembly[] WithGuestReachableDependencies(Assembly[] scanned)
    {
        var result = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var assembly in scanned)
        {
            result[assembly.FullName ?? assembly.GetName().Name ?? string.Empty] = assembly;
        }

        foreach (var assembly in scanned)
        {
            AssemblyName[] references;
            try
            {
                references = assembly.GetReferencedAssemblies();
            }
            catch
            {
                continue;
            }

            foreach (var reference in references)
            {
                var name = reference.Name;
                if (name is null || !IsGuestReachableInterop(name))
                {
                    continue;
                }

                try
                {
                    var loaded = Assembly.Load(reference);
                    result[loaded.FullName ?? name] = loaded;
                }
                catch
                {
                }
            }
        }

        return result.Values.ToArray();
    }

    private static bool IsGuestReachableInterop(string assemblyName) =>
        assemblyName.StartsWith("Silk.NET", StringComparison.Ordinal) ||
        assemblyName.StartsWith("SharpEmu", StringComparison.Ordinal);

    public bool TryGetFunction(string nid, out Delegate function)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        return _dispatchTable.TryGetValue(nid, out function!);
    }

    public bool TryGetExport(string nid, out ExportedFunction export)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        return _exportTable.TryGetValue(nid, out export!);
    }

    public bool TryGetExportByName(string exportName, out ExportedFunction export)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportName);
        return _exportNameTable.TryGetValue(exportName, out export!);
    }

    public OrbisGen2Result Dispatch(string nid, CpuContext context)
    {
        TryDispatch(nid, context, out var result);
        return result;
    }

    public bool TryDispatch(string nid, CpuContext context, out OrbisGen2Result result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nid);
        ArgumentNullException.ThrowIfNull(context);

        if (!_dispatchTable.TryGetValue(nid, out var function) || !_exportTable.TryGetValue(nid, out var export))
        {
            Console.Error.WriteLine($"[HLE] NID '{nid}' not found in dispatch table.");
            context[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND);
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND;
            return false;
        }

        if ((export.Target & context.TargetGeneration) == 0)
        {
            Console.Error.WriteLine($"[HLE] NID '{nid}' ({export.Name}) found but not implemented for generation {context.TargetGeneration} (targets: {export.Target}).");
            context[CpuRegister.Rax] = unchecked((ulong)(int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED);
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_IMPLEMENTED;
            return false;
        }


        context.ClearRaxWriteFlag();
        int ret = ((SysAbiFunction)function).Invoke(context);

        if (!context.WasRaxWritten)
        {
            context[CpuRegister.Rax] = unchecked((ulong)ret);
        }

        result = (OrbisGen2Result)ret;
        return true;
    }

}
