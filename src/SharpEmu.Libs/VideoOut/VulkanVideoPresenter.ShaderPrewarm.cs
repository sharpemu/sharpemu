// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using SharpEmu.ShaderCompiler.Vulkan;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private const int MaxLoggedPrewarmFailures = 8;
        private const int PrewarmProgressBatch = 256;

        private sealed record ShaderPrewarmCompletion(string StampPath, string CoverageStamp);
        private ShaderPrewarmCompletion? _shaderPrewarmCompletion;
        private byte[] _loadedPrewarmCache = [];

        private ShaderPrewarmList? _shaderPrewarmList;
        private ConcurrentDictionary<string, byte>? _prewarmedShaderIdentities;
        private ConcurrentDictionary<string, byte>? _runtimeComputeIdentities;
        private Thread[] _shaderPrewarmThreads = [];
        private volatile bool _shaderPrewarmStopping;
        private volatile bool _shaderPrewarmFinished;
        private int _shaderPrewarmCompiled;
        private int _shaderPrewarmFailed;
        private int _shaderPrewarmRuntimeHits;
        private readonly ConcurrentQueue<ulong> _shaderPrewarmCompleted = new();

        ShaderPrewarmList? IShaderPipelineHost.ShaderPrewarm => Volatile.Read(ref _shaderPrewarmList);

        private void StartShaderPrewarm()
        {
            if (!StartShaderPrewarmCore())
            {
                SetShaderPrewarmState(false);
            }
        }

        private bool StartShaderPrewarmCore()
        {
            if (_shaderPrewarmList is not null ||
                _pipelineCacheShardDirectory is not null ||
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_PREWARM") == "0")
            {
                _loadedPrewarmCache = [];
                return false;
            }

            var directory = Path.GetDirectoryName(VulkanPipelineCacheStorage.ResolvePath(
                VideoOutExports.GetApplicationTitleId(),
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")));
            if (string.IsNullOrEmpty(directory) || ShaderPrewarmList.Open(directory) is not { } list)
            {
                _loadedPrewarmCache = [];
                return false;
            }

            _prewarmedShaderIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            _runtimeComputeIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            Volatile.Write(ref _shaderPrewarmList, list);
            var compiler = GuestGpu.Current;
            var stamp = list.CoverageStamp(ShaderPrewarmStamp(compiler));
            if (list.LoadedComputeCount == 0)
            {
                _loadedPrewarmCache = [];
                _shaderPrewarmFinished = true;
                return false;
            }

            if (CanSkipShaderPrewarm(list, stamp, _loadedPrewarmCache))
            {
                Volatile.Write(ref _shaderPrewarmCompletion, new(list.StampPath, stamp));
                _loadedPrewarmCache = [];
                _shaderPrewarmFinished = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Shader prewarm skipped: compute={list.LoadedComputeCount} reason=cache-and-recording-match");
                return false;
            }

            _loadedPrewarmCache = [];
            // Progress alone does not prove that the saved cache contains those pipelines.
            list.ClearProgress();
            var pending = list.LoadedComputes();
            Interlocked.Add(ref _shaderPrewarmFailed, list.LoadedComputeCount - pending.Count);
            var started = Stopwatch.GetTimestamp();
            if (pending.Count == 0)
            {
                FinishShaderPrewarm(list, stamp, started);
                return false;
            }

            var work = new ConcurrentQueue<(ComputePrewarmRecord Record, ShaderCodeCapture Code)>(pending);
            var workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            var remaining = workerCount;
            var host = (IShaderPipelineHost)this;
            var sharedInt64Atomics = host.SharedInt64AtomicsEnabled;
            var execGuardElision = host.ExecGuardElisionEnabled;
            Volatile.Write(ref _shaderPrewarmTotal, pending.Count);
            Volatile.Write(ref _shaderPrewarmProgress, 0);
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm started: compute={pending.Count} resumed=0 workers={workerCount}");
            SetShaderPrewarmState(true);
            _shaderPrewarmThreads = new Thread[workerCount];
            for (var index = 0; index < workerCount; index++)
            {
                _shaderPrewarmThreads[index] = new Thread(() =>
                {
                    while (!_shaderPrewarmStopping && work.TryDequeue(out var item))
                    {
                        if (PrewarmComputePipeline(item.Record, item.Code, compiler, sharedInt64Atomics, execGuardElision))
                        {
                            _shaderPrewarmCompleted.Enqueue(ShaderPrewarmList.Identity(item.Record));
                        }
                        if (Interlocked.Increment(ref _shaderPrewarmProgress) % PrewarmProgressBatch == 0)
                        {
                            FlushShaderPrewarmProgress(list, stamp);
                        }
                    }

                    FlushShaderPrewarmProgress(list, stamp);
                    if (Interlocked.Decrement(ref remaining) == 0)
                    {
                        FinishShaderPrewarm(list, stamp, started);
                    }
                })
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal,
                    Name = $"SharpEmu shader prewarm {index}",
                };
                _shaderPrewarmThreads[index].Start();
            }

            return true;
        }

        private void FlushShaderPrewarmProgress(ShaderPrewarmList list, string stamp)
        {
            var batch = new List<ulong>();
            while (_shaderPrewarmCompleted.TryDequeue(out var identity))
            {
                batch.Add(identity);
            }

            list.AppendProgress(stamp, batch);
        }

        private static bool CanSkipShaderPrewarm(ShaderPrewarmList list, string stamp, byte[] cache) =>
            cache.Length != 0 && list.IsStampCurrent(ShaderPrewarmList.CacheStamp(stamp, cache));

        private static string ShaderPrewarmStampPart(Type type) =>
            type.Assembly.ManifestModule.ModuleVersionId.ToString("N");

        private string ShaderPrewarmStamp(IGuestGpuBackend compiler) =>
            BuildShaderPrewarmStamp(compiler.GetType(), DriverCacheSignature());

        private string BuildShaderPrewarmStamp(Type compilerType, string driverSignature) =>
            string.Join(
                '|',
                "success-only-v2",
                ShaderPrewarmStampPart(typeof(ShaderProgramCache)),
                ShaderPrewarmStampPart(typeof(Gen5ShaderTranslator)),
                ShaderPrewarmStampPart(typeof(Gen5SpirvTranslator)),
                ShaderPrewarmStampPart(compilerType),
                compilerType.FullName,
                ((IShaderPipelineHost)this).SharedInt64AtomicsEnabled,
                ((IShaderPipelineHost)this).ExecGuardElisionEnabled,
                _supportsShaderSignedZeroInfNanPreserveFloat32,
                SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.TraceEnabled,
                Volatile.Read(ref _nativeSubgroupSize),
                _canRequireComputeSubgroup32,
                _canRequireComputeSubgroup64,
                _maxComputeWorkgroupSubgroups,
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_HALF_MASKS"),
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_LANE_LOCAL"),
                Environment.GetEnvironmentVariable("SHARPEMU_WAVE64_PAIRED"),
                Environment.GetEnvironmentVariable("SHARPEMU_SHADER_MAX_STEPS"),
                Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_CF"),
                Environment.GetEnvironmentVariable("SHARPEMU_STRUCTURED_FORWARD_BLOCKS"),
                Environment.GetEnvironmentVariable("SHARPEMU_EXEC_GUARD_ELISION"),
                Environment.GetEnvironmentVariable("SHARPEMU_EXEC_SELECT_ELIMINATION"),
                driverSignature.TrimEnd('\n'));

        private bool PrewarmComputePipeline(
            ComputePrewarmRecord record,
            ShaderCodeCapture code,
            IGuestGpuBackend compiler,
            bool sharedInt64Atomics,
            bool execGuardElision)
        {
            if (!ShaderProgramCache.TryCompilePrewarm(
                    record, code, compiler, sharedInt64Atomics, execGuardElision,
                    _supportsShaderSignedZeroInfNanPreserveFloat32, out var compiled, out var layout, out var error,
                    ((IShaderPipelineHost)this).ComputeWave64SubgroupNative,
                    ((IShaderPipelineHost)this).ComputeRunsOn32LaneSubgroups(record.Info)))
            {
                NoteShaderPrewarmFailure(record, error);
                return false;
            }

            var payload = compiled!.Payload;
            var bindings = new List<DescriptorSetLayoutBinding>();
            DescriptorSetLayout setLayout = default;
            PipelineLayout pipelineLayout = default;
            ShaderModule module = default;
            try
            {
                CollectLayoutBindings(bindings, layout!, ShaderStage.Compute);
                setLayout = CreateDescriptorSetLayout(bindings, out _, out _);
                pipelineLayout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit);
                module = CreateShaderModule(payload);
                var pipeline = CompileComputePipeline(_vk, _device, _pipelineCache, module, pipelineLayout, ComputeRequiredSubgroupSize(record.Info));
                _vk.DestroyPipeline(_device, pipeline, null);
                _prewarmedShaderIdentities![VulkanPipelineCacheStorage.CompiledShaderIdentity(payload)] = 0;
                Interlocked.Increment(ref _shaderPrewarmCompiled);
                return true;
            }
            catch (Exception exception)
            {
                NoteShaderPrewarmFailure(record, exception.Message);
                return false;
            }
            finally
            {
                if (module.Handle != 0)
                {
                    _vk.DestroyShaderModule(_device, module, null);
                }

                if (pipelineLayout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, pipelineLayout, null);
                }

                if (setLayout.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, setLayout, null);
                }
            }
        }

        private void NoteShaderPrewarmFailure(ComputePrewarmRecord record, string error)
        {
            if (Interlocked.Increment(ref _shaderPrewarmFailed) <= MaxLoggedPrewarmFailures)
            {
                Console.Error.WriteLine(
                    $"[LOADER][WARN] Shader prewarm failed: cs=0x{record.Hash:X16} shader=0x{record.Address:X16} error={error}");
            }
        }

        private void FinishShaderPrewarm(ShaderPrewarmList list, string stamp, long started)
        {
            var runtime = _runtimeComputeIdentities!;
            var prewarmed = _prewarmedShaderIdentities!;
            var matched = runtime.Keys.Count(prewarmed.ContainsKey);
            if (_shaderPrewarmStopping)
            {
                SetShaderPrewarmState(false);
                return;
            }

            if (Volatile.Read(ref _shaderPrewarmFailed) == 0)
            {
                Volatile.Write(ref _shaderPrewarmCompletion, new(list.StampPath, stamp));
            }
            _shaderPrewarmFinished = true;
            SetShaderPrewarmState(false);
            _pipelineCacheDirty = true;
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm done: compiled={Volatile.Read(ref _shaderPrewarmCompiled)} " +
                $"failed={Volatile.Read(ref _shaderPrewarmFailed)} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0} " +
                $"runtime_compute_modules={runtime.Count} matched={matched}");
        }

        private void NoteRuntimeComputeModule(string identity)
        {
            if (_runtimeComputeIdentities is not { } runtime || _prewarmedShaderIdentities is not { } prewarmed)
            {
                return;
            }

            runtime[identity] = 0;
            if (!_shaderPrewarmFinished || !prewarmed.ContainsKey(identity))
            {
                return;
            }

            var hits = Interlocked.Increment(ref _shaderPrewarmRuntimeHits);
            if (hits is 1 or 10 or 100 or 1000)
            {
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Shader prewarm hits: {hits} of runtime_compute_modules={runtime.Count}");
            }
        }

        private void StopShaderPrewarm()
        {
            _shaderPrewarmStopping = true;
            SetShaderPrewarmState(false);
            foreach (var thread in _shaderPrewarmThreads)
            {
                thread.Join(TimeSpan.FromSeconds(10));
            }

            _shaderPrewarmThreads = [];
            if (Interlocked.Exchange(ref _shaderPrewarmList, null) is { } list)
            {
                list.Dispose();
            }
        }
    }
}
