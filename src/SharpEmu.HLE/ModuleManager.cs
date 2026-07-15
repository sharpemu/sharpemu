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
    private readonly HashSet<(Assembly Assembly, Generation Generation)> _scannedAssemblies = new();
    private bool _isFrozen;

    // Parity testing between the generated registry and the reflection scan.
    internal IReadOnlyDictionary<string, ExportedFunction> ExportsForTesting => _exportTable;

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
                registeredCount++;
            }

            return registeredCount;
        }
    }

    // Retained for parity testing against the generated registry; the runtime registers
    // through RegisterExports since the compile-time table replaced this scan.
    public int RegisterFromAssembly(Assembly assembly, Generation generation, ISymbolCatalog? symbolCatalog = null)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (_registrationGate)
        {
            if (_isFrozen)
            {
                throw new InvalidOperationException("Module registration is frozen.");
            }

            // Deduplicated: one assembly is reached through many types.
            if (!_scannedAssemblies.Add((assembly, generation)))
            {
                return 0;
            }

            var registeredCount = 0;
            var instances = new Dictionary<Type, object>();

            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    var exportAttribute = method.GetCustomAttribute<SysAbiExportAttribute>(inherit: false);
                    if (exportAttribute is null)
                    {
                        continue;
                    }

                    var exportInfo = ResolveExportInfo(exportAttribute, method, generation, symbolCatalog);
                    if (exportInfo is null)
                    {
                        continue;
                    }

                    var handler = CreateHandler(type, method, instances);
                    if (!_dispatchTable.TryAdd(exportInfo.Value.Nid, handler))
                    {
                        Console.Error.WriteLine($"[HLE] Duplicate NID '{exportInfo.Value.Nid}' ({exportInfo.Value.ExportName}) — already registered, skipping.");
                        continue;
                    }

                    _exportTable[exportInfo.Value.Nid] = new ExportedFunction(
                        exportInfo.Value.LibraryName,
                        exportInfo.Value.Nid,
                        exportInfo.Value.ExportName,
                        exportInfo.Value.Target,
                        (SysAbiFunction)handler);
                    _exportNameTable.TryAdd(exportInfo.Value.ExportName, _exportTable[exportInfo.Value.Nid]);

                    registeredCount++;
                }
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
            assemblies = _scannedAssemblies.Select(entry => entry.Assembly).Distinct().ToArray();
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

    private static Delegate CreateHandler(Type ownerType, MethodInfo method, IDictionary<Type, object> instances)
    {
        ValidateSignature(method);

        object? target = null;
        if (!method.IsStatic)
        {
            if (!instances.TryGetValue(ownerType, out target))
            {
                target = Activator.CreateInstance(ownerType)
                    ?? throw new InvalidOperationException($"Cannot instantiate module type: {ownerType.FullName}");
                instances.Add(ownerType, target);
            }
        }

        var parameterCount = method.GetParameters().Length;
        if (parameterCount == 0)
        {
            var noArg = method.IsStatic
                ? (Func<int>)method.CreateDelegate(typeof(Func<int>))
                : (Func<int>)method.CreateDelegate(typeof(Func<int>), target!);

            SysAbiFunction adapter = _ => noArg();
            return adapter;
        }

        return method.IsStatic
            ? method.CreateDelegate(typeof(SysAbiFunction))
            : method.CreateDelegate(typeof(SysAbiFunction), target!);
    }

    private static void ValidateSignature(MethodInfo method)
    {
        if (method.ReturnType != typeof(int))
        {
            throw new InvalidOperationException(
                $"Method {method.DeclaringType?.FullName}.{method.Name} must return int.");
        }

        var parameters = method.GetParameters();
        if (parameters.Length == 0)
        {
            return;
        }

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(CpuContext))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Method {method.DeclaringType?.FullName}.{method.Name} must accept no arguments or one {nameof(CpuContext)} argument.");
    }

    private static ExportInfo? ResolveExportInfo(
        SysAbiExportAttribute exportAttribute,
        MethodInfo method,
        Generation generation,
        ISymbolCatalog? symbolCatalog)
    {
        var target = exportAttribute.Target == Generation.None
            ? generation
            : exportAttribute.Target;
        if ((target & generation) == 0)
        {
            return null;
        }

        var nid = exportAttribute.Nid;
        var exportName = exportAttribute.ExportName;

        if (string.IsNullOrWhiteSpace(nid) && !string.IsNullOrWhiteSpace(exportName) && symbolCatalog?.TryGetByExportName(exportName, out var byName) == true)
        {
            nid = byName.Nid;
        }

        if (!string.IsNullOrWhiteSpace(nid) && symbolCatalog?.TryGetByNid(nid, out var byNid) == true)
        {
            exportName = string.IsNullOrWhiteSpace(exportName) ? byNid.ExportName : exportName;
            target = exportAttribute.Target == Generation.None ? byNid.Target : target;
        }

        if (string.IsNullOrWhiteSpace(nid))
        {
            throw new InvalidOperationException(
                $"Method {method.DeclaringType?.FullName}.{method.Name} must define a NID or match one in symbols catalog.");
        }

        if (string.IsNullOrWhiteSpace(exportName))
        {
            exportName = method.Name;
        }

        if ((target & generation) == 0)
        {
            return null;
        }

        var libraryName = string.IsNullOrWhiteSpace(exportAttribute.LibraryName) ? "libKernel" : exportAttribute.LibraryName;
        return new ExportInfo(nid, exportName, libraryName, target);
    }

    private readonly record struct ExportInfo(string Nid, string ExportName, string LibraryName, Generation Target);
}
