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
            public Lazy<PipelineCache> Cache = null!;
            public required string Path;
            public bool Dirty;
            public nuint SavedBytes;
        }

        private const double SlowShardLoadMilliseconds = 50;

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

        // The dictionary belongs to the render thread. Only the lazy native
        // creation runs on compiler workers; variants share one initialization.
        private Lazy<PipelineCache>? GetGuestPipelineCacheSource(string key)
        {
            if (_pipelineCacheShardDirectory is null) return null;
            if (_pipelineCacheShards.TryGetValue(key, out var existing))
            {
                existing.Dirty = true;
                return existing.Cache;
            }

            var shard = new DriverCacheShard { Path = Path.Combine(_pipelineCacheShardDirectory, key + ".bin"), Dirty = true };
            shard.Cache = new Lazy<PipelineCache>(() => LoadGuestPipelineCache(key, shard));
            _pipelineCacheShards.Add(key, shard);
            return shard.Cache;
        }

        private PipelineCache ResolveGuestPipelineCache(Lazy<PipelineCache>? source)
        {
            var cache = source?.Value ?? default;
            return cache.Handle != 0 ? cache : _pipelineCache;
        }

        private PipelineCache LoadGuestPipelineCache(string key, DriverCacheShard shard)
        {
            var path = shard.Path;
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

            if (data.Length == 0)
                return cache;

            var milliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (QueryPipelineCacheSize(cache) is { } size)
                shard.SavedBytes = size;
            if (milliseconds >= SlowShardLoadMilliseconds)
                Console.Error.WriteLine($"[LOADER][INFO] Vulkan cache shard loaded: key={key} bytes={data.Length} ms={milliseconds:F1}");
            return cache;
        }

        private void SaveGuestPipelineCaches()
        {
            var savedShards = 0;
            nuint savedBytes = 0;
            foreach (var shard in _pipelineCacheShards.Values)
            {
                if (!shard.Dirty || !shard.Cache.IsValueCreated || shard.Cache.Value.Handle == 0)
                    continue;

                var cache = shard.Cache.Value;
                if (QueryPipelineCacheSize(cache) is { } size && size == shard.SavedBytes)
                {
                    shard.Dirty = false;
                    continue;
                }

                if (SaveDriverPipelineCache(cache, shard.Path, MaxPipelineCacheBytes, out var bytes))
                {
                    shard.Dirty = false;
                    shard.SavedBytes = bytes;
                    savedShards++;
                    savedBytes += bytes;
                }
            }

            if (savedShards != 0)
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Vulkan cache shards saved: directory={_pipelineCacheShardDirectory} shards={savedShards} bytes={savedBytes}");
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
