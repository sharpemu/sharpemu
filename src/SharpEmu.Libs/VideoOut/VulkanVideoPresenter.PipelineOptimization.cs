// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        private readonly object _pipelineOptimizationGate = new();
        private readonly BlockingCollection<(Func<Pipeline> Build, TaskCompletionSource<Pipeline> Completion)> _pipelineOptimizationQueue = new();
        private readonly ConcurrentDictionary<ulong, Task<Pipeline>> _pipelineOptimizations = new();
        private Thread[]? _pipelineOptimizationThreads;
        private volatile bool _stoppingPipelineOptimizations;

        private const int PipelineOptimizationThreads = 2;

        private bool _fastPipelineBuild = Environment.GetEnvironmentVariable("SHARPEMU_PIPELINE_FAST_BUILD") != "0";
        private bool FastPipelineBuildEnabled => _fastPipelineBuild;

        private void QueuePipelineOptimization(Pipeline initial, Func<Pipeline> build)
        {
            lock (_pipelineOptimizationGate)
            {
                if (_stoppingPipelineOptimizations)
                {
                    return;
                }

                var completion = new TaskCompletionSource<Pipeline>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (!_pipelineOptimizations.TryAdd(initial.Handle, completion.Task))
                {
                    return;
                }

                if (_pipelineOptimizationThreads is null)
                {
                    _pipelineOptimizationThreads = new Thread[PipelineOptimizationThreads];
                    for (var index = 0; index < _pipelineOptimizationThreads.Length; index++)
                    {
                        _pipelineOptimizationThreads[index] = new Thread(OptimizePipelines)
                        {
                            Name = $"Vulkan pipeline optimizer {index}",
                            IsBackground = true,
                            Priority = ThreadPriority.BelowNormal,
                        };
                        _pipelineOptimizationThreads[index].Start();
                    }
                }

                _pipelineOptimizationQueue.Add((build, completion));
            }
        }

        private void OptimizePipelines()
        {
            foreach (var work in _pipelineOptimizationQueue.GetConsumingEnumerable())
            {
                if (_stoppingPipelineOptimizations)
                {
                    work.Completion.SetResult(default);
                    continue;
                }

                try
                {
                    work.Completion.SetResult(work.Build());
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[LOADER][WARN] Background pipeline optimization failed: {exception.Message}");
                    work.Completion.SetResult(default);
                }
            }
        }

        private Pipeline ResolveOptimizedPipeline(Pipeline initial)
        {
            if (_pipelineOptimizations.TryGetValue(initial.Handle, out var task) && task.IsCompletedSuccessfully)
            {
                var optimized = task.GetAwaiter().GetResult();
                if (optimized.Handle != 0)
                {
                    return optimized;
                }
            }
            return initial;
        }

        private void StopPipelineOptimization()
        {
            lock (_pipelineOptimizationGate)
            {
                _stoppingPipelineOptimizations = true;
                _pipelineOptimizationQueue.CompleteAdding();
            }
            foreach (var thread in _pipelineOptimizationThreads ?? [])
            {
                thread.Join();
            }

            foreach (var task in _pipelineOptimizations.Values)
            {
                if (task.IsCompletedSuccessfully && task.GetAwaiter().GetResult() is { Handle: not 0 } pipeline)
                {
                    _vk.DestroyPipeline(_device, pipeline, null);
                }
            }
            _pipelineOptimizations.Clear();
        }
    }
}
