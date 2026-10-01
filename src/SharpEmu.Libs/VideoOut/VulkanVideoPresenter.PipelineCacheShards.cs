// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Diagnostics;
using SharpEmu.Libs.Gpu.Pipelines;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private sealed class DriverCacheShard
        {
            public required Lazy<PipelineCache> Cache;
            public required string Path;
            public bool Dirty;
        }

        private string? _pipelineCacheShardDirectory;
        private readonly Dictionary<string, DriverCacheShard> _pipelineCacheShards = new();
        private static readonly bool LegacyShaderGroups =
            Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_LEGACY_SHARDS") == "1";

        private string ComputeCacheKey(ulong guestHash, ulong module) =>
            !LegacyShaderGroups && _shaderModuleCacheIdentities.TryGetValue(module, out var identity)
                ? $"c2-{identity}" : $"c-{guestHash:X16}";

        private string GraphicsCacheKey(ulong vertexHash, ulong pixelHash, ulong vertexModule, ulong pixelModule)
        {
            if (!LegacyShaderGroups && _shaderModuleCacheIdentities.TryGetValue(vertexModule, out var vertex) &&
                (pixelModule == 0 || _shaderModuleCacheIdentities.ContainsKey(pixelModule)))
                return $"g2-{vertex}-{(pixelModule == 0 ? "none" : _shaderModuleCacheIdentities[pixelModule])}";
            return $"g-{vertexHash:X16}-{pixelHash:X16}";
        }

        // MoltenVK recompiles cached MSL libraries during vkCreatePipelineCache.
        // Loading only requested translated modules prevents a mature cache from
        // compiling every previously visited scene before the first frame.
        // Vulkan still validates the complete shader/layout key inside each blob;
        // content hashes only select a storage bucket, never a pipeline to reuse.
        private PipelineCache GetGuestPipelineCache(string key)
            => ResolveGuestPipelineCache(GetGuestPipelineCacheSource(key));

        // The dictionary belongs to the render thread. Variants share one
        // native cache initialization.
        private Lazy<PipelineCache>? GetGuestPipelineCacheSource(string key)
        {
            if (_pipelineCacheShardDirectory is null) return null;
            if (_pipelineCacheShards.TryGetValue(key, out var existing))
            {
                existing.Dirty = true;
                return existing.Cache;
            }

            var path = Path.Combine(_pipelineCacheShardDirectory, key + ".bin");
            var source = new Lazy<PipelineCache>(() => LoadGuestPipelineCache(key, path));
            _pipelineCacheShards.Add(key, new DriverCacheShard { Cache = source, Path = path, Dirty = true });
            return source;
        }

        private PipelineCache ResolveGuestPipelineCache(Lazy<PipelineCache>? source)
        {
            var cache = source?.Value ?? default;
            return cache.Handle != 0 ? cache : _pipelineCache;
        }

        private PipelineCache LoadGuestPipelineCache(string key, string path)
        {
            byte[] data = [];
            try
            {
                if (File.Exists(path) &&
                    !PipelineCacheSignature.TryUnwrap(DriverCacheSignature(), File.ReadAllBytes(path), out data))
                    Console.Error.WriteLine($"[LOADER][INFO] Vulkan cache shard invalidated: path={path}");
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan cache shard read failed: {exception.Message}");
            }

            var started = Stopwatch.GetTimestamp();
            var result = TryCreatePipelineCache(data, out var cache);
            if (result != Result.Success && data.Length != 0)
                result = TryCreatePipelineCache([], out cache);
            if (result != Result.Success)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Vulkan cache shard unavailable: key={key} result={result}");
                return default;
            }

            if (data.Length != 0)
                Console.Error.WriteLine($"[LOADER][INFO] Vulkan cache shard loaded: key={key} bytes={data.Length} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}");
            return cache;
        }

        private void SaveGuestPipelineCaches()
        {
            foreach (var shard in _pipelineCacheShards.Values)
            {
                if (shard.Dirty && shard.Cache.IsValueCreated && shard.Cache.Value.Handle != 0 &&
                    SaveDriverPipelineCache(shard.Cache.Value, shard.Path))
                    shard.Dirty = false;
            }
        }

        private void DestroyGuestPipelineCaches()
        {
            foreach (var shard in _pipelineCacheShards.Values)
            {
                if (shard.Cache.IsValueCreated && shard.Cache.Value.Handle != 0)
                    _vk.DestroyPipelineCache(_device, shard.Cache.Value, null);
            }
            _pipelineCacheShards.Clear();
        }
    }
}
