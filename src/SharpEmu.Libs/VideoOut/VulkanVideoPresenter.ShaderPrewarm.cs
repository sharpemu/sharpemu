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
                return false;
            }

            var directory = Path.GetDirectoryName(VulkanPipelineCacheStorage.ResolvePath(
                VideoOutExports.GetApplicationTitleId(),
                Environment.GetEnvironmentVariable("SHARPEMU_VK_PIPELINE_CACHE_PATH")));
            if (string.IsNullOrEmpty(directory) || ShaderPrewarmList.Open(directory) is not { } list)
            {
                return false;
            }

            _prewarmedShaderIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            _runtimeComputeIdentities = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

            Volatile.Write(ref _shaderPrewarmList, list);
            var compiler = GuestGpu.Current;
            var stamp = ShaderPrewarmStamp(compiler);
            if (list.LoadedComputeCount == 0)
            {
                list.WriteStamp(stamp);
                _shaderPrewarmFinished = true;
                return false;
            }

            if (list.IsStampCurrent(stamp))
            {
                _shaderPrewarmFinished = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Shader prewarm skipped: compute={list.LoadedComputeCount} reason=same-build-and-driver");
                return false;
            }

            var completed = list.ReadProgress(stamp);
            var pending = list.LoadedComputes()
                .Where(item => !completed.Contains(ShaderPrewarmList.Identity(item.Record)))
                .ToList();
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
            Volatile.Write(ref _shaderPrewarmTotal, pending.Count);
            Volatile.Write(ref _shaderPrewarmProgress, 0);
            Console.Error.WriteLine(
                $"[LOADER][INFO] Shader prewarm started: compute={pending.Count} resumed={completed.Count} workers={workerCount}");
            SetShaderPrewarmState(true);
            _shaderPrewarmThreads = new Thread[workerCount];
            for (var index = 0; index < workerCount; index++)
            {
                _shaderPrewarmThreads[index] = new Thread(() =>
                {
                    while (!_shaderPrewarmStopping && work.TryDequeue(out var item))
                    {
                        PrewarmComputePipeline(item.Record, item.Code, compiler, host);
                        _shaderPrewarmCompleted.Enqueue(ShaderPrewarmList.Identity(item.Record));
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

        private static string ShaderPrewarmStampPart(Type type) =>
            type.Assembly.ManifestModule.ModuleVersionId.ToString("N");

        private string ShaderPrewarmStamp(IGuestGpuBackend compiler) =>
            string.Join(
                '|',
                ShaderPrewarmStampPart(typeof(Gen5ShaderTranslator)),
                ShaderPrewarmStampPart(typeof(Gen5SpirvTranslator)),
                compiler.GetType().FullName,
                DriverCacheSignature().TrimEnd('\n'));

        private void PrewarmComputePipeline(
            ComputePrewarmRecord record,
            ShaderCodeCapture code,
            IGuestGpuBackend compiler,
            IShaderPipelineHost host)
        {
            if (!ShaderProgramCache.TryCompilePrewarm(
                    record, code, compiler, host, out var compiled, out var layout, out var error))
            {
                NoteShaderPrewarmFailure(record, error);
                return;
            }

            var payload = compiled!.Payload;
            _prewarmedShaderIdentities![VulkanPipelineCacheStorage.CompiledShaderIdentity(payload)] = 0;
            var bindings = new List<DescriptorSetLayoutBinding>();
            CollectLayoutBindings(bindings, layout!, ShaderStage.Compute);
            DescriptorSetLayout setLayout = default;
            PipelineLayout pipelineLayout = default;
            ShaderModule module = default;
            try
            {
                setLayout = CreateDescriptorSetLayout(bindings, out _, out _);
                pipelineLayout = CreatePipelineLayout(setLayout, ShaderStageFlags.ComputeBit);
                module = CreateShaderModule(payload);
                var pipeline = CompileComputePipeline(_vk, _device, _pipelineCache, module, pipelineLayout);
                _vk.DestroyPipeline(_device, pipeline, null);
                Interlocked.Increment(ref _shaderPrewarmCompiled);
            }
            catch (Exception exception)
            {
                NoteShaderPrewarmFailure(record, exception.Message);
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

            list.WriteStamp(stamp);
            list.ClearProgress();
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
