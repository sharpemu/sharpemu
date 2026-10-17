// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Pipelines;

// Process-wide counts of the shader and pipeline caches, printed once at shutdown.
public static class ShaderCacheCounters
{
    private static long _programs;
    private static long _permutations;
    private static long _compiles;
    private static long _graphicsPipelines;
    private static long _computePipelines;
    private static long _heapSets;
    private static long _pushSets;
    private static long _shaderDataFallbacks;
    private static long _deviceAddressPrograms;

    public static long Programs => Volatile.Read(ref _programs);
    public static long Permutations => Volatile.Read(ref _permutations);
    public static long Compiles => Volatile.Read(ref _compiles);
    public static long GraphicsPipelines => Volatile.Read(ref _graphicsPipelines);
    public static long ComputePipelines => Volatile.Read(ref _computePipelines);
    public static long HeapSets => Volatile.Read(ref _heapSets);
    public static long PushSets => Volatile.Read(ref _pushSets);
    public static long ShaderDataFallbacks => Volatile.Read(ref _shaderDataFallbacks);
    public static long DeviceAddressPrograms => Volatile.Read(ref _deviceAddressPrograms);

    public static void CountProgram() => Interlocked.Increment(ref _programs);
    public static void CountPermutation() => Interlocked.Increment(ref _permutations);
    public static void CountCompile() => Interlocked.Increment(ref _compiles);
    public static void CountGraphicsPipeline() => Interlocked.Increment(ref _graphicsPipelines);
    public static void CountComputePipeline() => Interlocked.Increment(ref _computePipelines);
    public static void CountHeapSet() => Interlocked.Increment(ref _heapSets);
    public static void CountPushSet() => Interlocked.Increment(ref _pushSets);
    public static void CountShaderDataFallback() => Interlocked.Increment(ref _shaderDataFallbacks);
    public static void CountDeviceAddressProgram() => Interlocked.Increment(ref _deviceAddressPrograms);

    public static string Summary() =>
        $"shader_cache programs={Programs} permutations={Permutations} compiles={Compiles} graphics_pipelines={GraphicsPipelines} " +
        $"compute_pipelines={ComputePipelines} heap_sets={HeapSets} push_sets={PushSets} shader_data_fallbacks={ShaderDataFallbacks} " +
        $"device_address_programs={DeviceAddressPrograms}";
}
