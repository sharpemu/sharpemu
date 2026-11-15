// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.VideoOut;

using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Resources;
using Silk.NET.Core.Native;
using Silk.NET.Vulkan;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        // The graphics prewarm (SHARPEMU_GRAPHICS_PREWARM=0 off). The pixel programs and the vertex and mesh programs
        // with their pre-rasterization states that earlier sessions recorded are translated again at boot (through the
        // translation cache), and their libraries are compiled the way the session builds them, into the pipeline cache.
        // After a new build or translator change, a program's first draw then links cached libraries instead of compiling
        // them while the command stream waits, which can take seconds per library. Items go in the order sessions first
        // met them, on the prewarm pool's below-normal threads beside the compute records, the more urgent first
        // (PrewarmPool); the side file's progress skips the items this build already prewarmed.
        private static readonly bool GraphicsPrewarmEnabled = Environment.GetEnvironmentVariable("SHARPEMU_GRAPHICS_PREWARM") != "0";

        private readonly ConcurrentQueue<ulong> _graphicsPrewarmCompleted = new();
        // The payloads the prewarm compiled, and the pixel modules the session created while it runs: the prewarm leaves a
        // pixel item of a session module to the session's early pixel library, which compiles the same part. A
        // pre-rasterization item always compiles: a program can take several states, and a part the session compiled already
        // is found in the pipeline cache.
        private ConcurrentDictionary<string, byte>? _prewarmedGraphicsModules;
        private readonly ConcurrentDictionary<string, byte> _sessionPixelModules = new(StringComparer.Ordinal);
        // The prewarm identity of each vertex or mesh module the session recorded (VertexProgramRecorded).
        private readonly ConcurrentDictionary<ulong, ulong> _vertexPrewarmIdentities = new();
        private int _graphicsPrewarmCompiled;
        private int _graphicsPrewarmTaken;
        private int _graphicsPrewarmFailed;
        private int _graphicsPrewarmProgress;
        private int _runtimePixelModules;
        private int _runtimePixelHits;
        private int _runtimeVertexModules;
        private int _runtimeVertexHits;

        private void StartGraphicsPrewarm(ShaderPrewarmList list, string buildStamp, IGuestGpuBackend compiler)
        {
            // Only where the device has pipeline binaries, like the compute prewarm; a capture layer that hides the extension
            // runs without a prewarm.
            if (!GraphicsPrewarmEnabled || !_graphicsPipelineLibraries || _pipelineBinaries is null || _pipelineBinaryDirectory is null)
            {
                return;
            }

            var items = list.LoadedGraphicsPrewarm();
            var missing = list.LoadedPixelCount + list.LoadedPreRasterizationCount - items.Count;
            // A pixel item builds the part early pixel libraries build (PixelProgramCreated); without them, draws compile their
            // pixel parts for their own sample states.
            IReadOnlyList<GraphicsPrewarmItem> pending = _earlyPixelLibraries
                ? items
                : items.Where(static item => item is not PixelPrewarmItem).ToList();
            var recorded = pending.Count;
            if (PrewarmBinarySkip && recorded != 0 && list.ReadProgress(buildStamp, extension: true) is { Count: > 0 } completed)
            {
                pending = pending.Where(item => !completed.Contains(item.Identity)).ToList();
            }

            if (pending.Count == 0)
            {
                if (recorded != 0)
                {
                    Console.Error.WriteLine($"[LOADER][INFO] Graphics prewarm skipped: items={recorded} resumed={recorded}");
                }

                return;
            }

            _prewarmedGraphicsModules = new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);
            var work = new ConcurrentQueue<GraphicsPrewarmItem>(pending);
            var workerCount = Math.Clamp(Environment.ProcessorCount / 4, 1, 4);
            var started = Stopwatch.GetTimestamp();
            Interlocked.Add(ref _shaderPrewarmGraphicsTotal, pending.Count);
            Interlocked.Add(ref _shaderPrewarmWorkers, workerCount);
            Console.Error.WriteLine(
                $"[LOADER][INFO] Graphics prewarm started: pixel={pending.Count(static item => item is PixelPrewarmItem)} " +
                $"pre_rasterization={pending.Count(static item => item is PreRasterizationPrewarmItem)} " +
                $"resumed={recorded - pending.Count} missing={missing} workers={workerCount}");
            PrewarmWorkers.Add(new PrewarmPool.Lane(pending.Count,
                (out float start) =>
                {
                    var any = work.TryPeek(out var next);
                    start = any ? next!.FirstUse : 0;
                    return any;
                },
                _ =>
                {
                    if (!work.TryDequeue(out var item))
                    {
                        return false;
                    }

                    if (Prewarm(item, compiler))
                    {
                        _graphicsPrewarmCompleted.Enqueue(item.Identity);
                    }

                    Interlocked.Increment(ref _shaderPrewarmGraphicsDone);
                    if (Interlocked.Increment(ref _graphicsPrewarmProgress) % PrewarmProgressBatch == 0)
                    {
                        FlushGraphicsPrewarmProgress(list, buildStamp);
                    }

                    return true;
                },
                () =>
                {
                    FlushGraphicsPrewarmProgress(list, buildStamp);
                    Console.Error.WriteLine(
                        $"[LOADER][INFO] Graphics prewarm done: compiled={Volatile.Read(ref _graphicsPrewarmCompiled)} " +
                        $"taken={Volatile.Read(ref _graphicsPrewarmTaken)} failed={Volatile.Read(ref _graphicsPrewarmFailed)} " +
                        $"stopped={_shaderPrewarmStopping} ms={Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
                }),
                workerCount, "SharpEmu graphics prewarm");
        }

        private void FlushGraphicsPrewarmProgress(ShaderPrewarmList list, string stamp)
        {
            var batch = new List<ulong>();
            while (_graphicsPrewarmCompleted.TryDequeue(out var identity))
            {
                batch.Add(identity);
            }

            list.AppendProgress(stamp, batch, extension: true);
        }

        private bool Prewarm(GraphicsPrewarmItem item, IGuestGpuBackend compiler) => item switch
        {
            PixelPrewarmItem pixel => PrewarmPixelLibrary(pixel.Record, pixel.Code, compiler),
            PreRasterizationPrewarmItem part => PrewarmPreRasterizationLibrary(part.Program, part.Code, part.State, compiler),
            _ => false,
        };

        // The translation and the pixel library of one record. The module (the same payload CreateShaderModule gives the
        // session) and the library go once the compile has put the part in the pipeline cache.
        private bool PrewarmPixelLibrary(PixelPrewarmRecord record, ShaderCodeCapture code, IGuestGpuBackend compiler)
        {
            var name = $"ps=0x{record.Hash:X16} shader=0x{record.Address:X16}";
            if (!ShaderProgramCache.TryCompilePixelPrewarm(record, code, compiler, this, out var compiled, out var layout, out var error))
            {
                NoteGraphicsPrewarmFailure(name, error);
                return false;
            }

            ShaderModule module = default;
            var library = default(Pipeline);
            try
            {
                var payload = WithDescriptorSet(compiled!.Payload, 0, PixelDescriptorSet);
                var identity = VulkanPipelineCacheStorage.CompiledShaderIdentity(payload);
                if (LeftToSession(identity))
                {
                    return true;
                }

                module = CreateShaderModule(payload);
                var bindings = new List<DescriptorSetLayoutBinding>();
                CollectLayoutBindings(bindings, layout!, ShaderStage.Pixel);
                library = CreateEarlyPixelLibrary(module.Handle, bindings, $"vs=prewarm {name}");
                return NoteGraphicsPrewarmLibrary(library, identity, name);
            }
            catch (Exception exception)
            {
                NoteGraphicsPrewarmFailure(name, exception.Message);
                return false;
            }
            finally
            {
                if (library.Handle != 0)
                {
                    _vk.DestroyPipeline(_device, library, null);
                }

                if (module.Handle != 0)
                {
                    _vk.DestroyShaderModule(_device, module, null);
                }
            }
        }

        // The translation of a vertex or mesh record and its pre-rasterization library for one recorded state, built as
        // CompilePreRasterizationPart builds the session's, with the task stage of a task-launched mesh program.
        private bool PrewarmPreRasterizationLibrary(VertexPrewarmRecord program, ShaderCodeCapture code, PreRasterizationState state,
            IGuestGpuBackend compiler)
        {
            var name = $"{(program.Mesh ? "ms" : "vs")}=0x{program.Hash:X16} shader=0x{program.Address:X16}";
            if (!ShaderProgramCache.TryCompileVertexPrewarm(program, code, compiler, this, out var compiled, out var layout,
                    out var task, out var error))
            {
                NoteGraphicsPrewarmFailure(name, error);
                return false;
            }

            if (task is not null && !_supportsTaskShader)
            {
                NoteGraphicsPrewarmFailure(name, "the device has no task shaders");
                return false;
            }

            ShaderModule module = default;
            DescriptorSetLayout set = default;
            PipelineLayout pipelineLayout = default;
            var library = default(Pipeline);
            var taskEntries = stackalloc SpecializationMapEntry[TaskSpecializationValues];
            var taskValues = stackalloc uint[TaskSpecializationValues];
            var taskSpecialization = new SpecializationInfo();
            var entryPoint = (byte*)SilkMarshal.StringToPtr("main");
            try
            {
                var payload = compiled!.Payload;
                var identity = VulkanPipelineCacheStorage.CompiledShaderIdentity(payload);
                module = CreateShaderModule(payload);
                var bindings = new List<DescriptorSetLayoutBinding>();
                CollectLayoutBindings(bindings, layout!, program.Mesh ? ShaderStage.Mesh : ShaderStage.Vertex);
                if (bindings.Count != 0)
                {
                    set = CreateDescriptorSetLayout(bindings, allowPush: true, out _, out _);
                }

                pipelineLayout = CreateLibraryPipelineLayout(set, default);
                var taskStage = task is { } launch ? TaskStageInfo(launch, taskEntries, taskValues, &taskSpecialization, entryPoint) : default;
                library = CreatePreRasterizationLibrary(VertexStageInfo(program.Mesh, module, entryPoint), task is null ? null : &taskStage,
                    state, pipelineLayout, $"{name} prewarm");
                return NoteGraphicsPrewarmLibrary(library, identity, name);
            }
            catch (Exception exception)
            {
                NoteGraphicsPrewarmFailure(name, exception.Message);
                return false;
            }
            finally
            {
                if (library.Handle != 0)
                {
                    _vk.DestroyPipeline(_device, library, null);
                }

                if (pipelineLayout.Handle != 0)
                {
                    _vk.DestroyPipelineLayout(_device, pipelineLayout, null);
                }

                if (set.Handle != 0)
                {
                    _vk.DestroyDescriptorSetLayout(_device, set, null);
                }

                if (module.Handle != 0)
                {
                    _vk.DestroyShaderModule(_device, module, null);
                }

                SilkMarshal.Free((nint)entryPoint);
            }
        }

        // True when the session already created this pixel module: its early pixel library compiles the same part.
        private bool LeftToSession(string identity)
        {
            if (!_sessionPixelModules.ContainsKey(identity))
            {
                return false;
            }

            Interlocked.Increment(ref _graphicsPrewarmTaken);
            return true;
        }

        private bool NoteGraphicsPrewarmLibrary(Pipeline library, string identity, string name)
        {
            if (library.Handle == 0)
            {
                NoteGraphicsPrewarmFailure(name, "the library was not created");
                return false;
            }

            _prewarmedGraphicsModules![identity] = 0;
            Interlocked.Increment(ref _graphicsPrewarmCompiled);
            return true;
        }

        private void NoteGraphicsPrewarmFailure(string name, string error)
        {
            if (Interlocked.Increment(ref _graphicsPrewarmFailed) <= MaxLoggedPrewarmFailures)
            {
                Console.Error.WriteLine($"[LOADER][WARN] Graphics prewarm failed: {name} error={error}");
            }
        }

        // A vertex or mesh program the program cache recorded; the states its pre-rasterization parts compile with are
        // recorded under its identity (RecordPreRasterization).
        public void VertexProgramRecorded(ulong module, ulong prewarmIdentity) => _vertexPrewarmIdentities[module] = prewarmIdentity;

        private void RecordPreRasterization(ulong module, PreRasterizationState state)
        {
            if (Volatile.Read(ref _shaderPrewarmList) is { } list && _vertexPrewarmIdentities.TryGetValue(module, out var program))
            {
                list.RecordPreRasterization(program, state);
            }
        }

        // The session's pixel, vertex and mesh modules while a graphics prewarm runs, with a census of those whose payload the
        // prewarm compiled.
        private void NoteRuntimeGraphicsModule(string identity, ShaderStage stage)
        {
            if (_prewarmedGraphicsModules is not { } prewarmed)
            {
                return;
            }

            var hit = prewarmed.ContainsKey(identity);
            if (stage == ShaderStage.Pixel)
            {
                _sessionPixelModules.TryAdd(identity, 0);
                NoteRuntimeHit("Pixel", "pixel", ref _runtimePixelModules, ref _runtimePixelHits, hit);
            }
            else
            {
                NoteRuntimeHit("Vertex", "vertex", ref _runtimeVertexModules, ref _runtimeVertexHits, hit);
            }
        }

        private static void NoteRuntimeHit(string name, string key, ref int modules, ref int hits, bool hit)
        {
            var count = Interlocked.Increment(ref modules);
            var matched = hit ? Interlocked.Increment(ref hits) : Volatile.Read(ref hits);
            if (count is 1 or 10 or 50 or 100 or 200 or 400 or 800)
            {
                Console.Error.WriteLine($"[LOADER][INFO] {name} prewarm hits: {matched} of runtime_{key}_modules={count}");
            }
        }
    }
}
