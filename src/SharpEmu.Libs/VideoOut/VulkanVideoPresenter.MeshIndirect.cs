// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.Libs.Gpu.Vulkan;
using Silk.NET.Vulkan;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        internal delegate* unmanaged<CommandBuffer, Silk.NET.Vulkan.Buffer, ulong, uint, uint, void> _cmdDrawMeshTasksIndirect;
        internal MeshIndirectPipeline? _meshIndirectPipeline;
    }

    private sealed partial class RecordingHost
    {
        private static int _clampWarned;
        private static int _overLimitDraws;

        public bool SupportsIndirectMeshDraw => _presenter._supportsMeshShader && _presenter._cmdDrawMeshTasksIndirect is not null;

        public IIndirectMeshDraw PrepareIndirectMeshDraw(in IndirectMeshDrawRequest request)
        {
            if (_worker)
            {
                throw Serial("indirect mesh draw", resumable: true);
            }

            _ = RequirePreparation();
            if (!SupportsIndirectMeshDraw) throw SubmissionScheduler.Fatal("The Vulkan device has no indirect mesh command.");
            if (request.TaskLaunched)
            {
                return PrepareTaskLaunchedMeshDraw(in request);
            }

            var arguments = RetainMeshInput(request.ArgumentsAddress, request.Automatic ? 16ul : 20ul);
            RetainedMeshInput? indices = null;
            MeshIndirectPipeline.PreparedDraw? resources = null;
            try
            {
                var prefix = 0u;
                if (!request.Automatic)
                {
                    var indexBase = request.IndexAddress & ~3ul;
                    prefix = checked((uint)(request.IndexAddress - indexBase));
                    var size = (prefix + (ulong)request.IndexCapacity * request.IndexElementSize + 3) & ~3ul;
                    indices = RetainMeshInput(indexBase, size);
                }

                var pipeline = _presenter._meshIndirectPipeline ??= new MeshIndirectPipeline(_deviceInfo);
                // An automatic draw reads no indices; its arguments fill the unused binding.
                resources = pipeline.Prepare(_scheduler, arguments.Binding, (indices ?? arguments).Binding, prefix, request);
                return new PreparedIndirectMesh(this, resources, arguments, indices, request.ArgumentsAddress, request.IndexCapacity);
            }
            catch
            {
                resources?.Dispose();
                indices?.Dispose();
                arguments.Dispose();
                throw;
            }
        }

        // The task stage converts the arguments (MeshTaskLaunch): it reads them by device address, or from the push words when
        // they sit in a host ring without one (a CPU-written range staged for this recording, so the CPU bytes are current).
        private IIndirectMeshDraw PrepareTaskLaunchedMeshDraw(in IndirectMeshDrawRequest request)
        {
            if (!request.Automatic || !_presenter._supportsTaskShader)
                throw SubmissionScheduler.Fatal("A task-launched mesh draw needs an automatic draw and task shaders.");
            // The task stage loads the arguments as dwords, as the command processor reads them.
            if ((request.ArgumentsAddress & 3) != 0)
                throw SubmissionScheduler.Fatal($"A task-launched mesh draw's arguments are not dword aligned: 0x{request.ArgumentsAddress:X16}.");
            var arguments = RetainMeshInput(request.ArgumentsAddress, 16);
            try
            {
                var draw = new TaskLaunchedMesh(this, arguments, request.ArgumentsAddress);
                if (arguments.Buffer.HasDeviceAddress)
                {
                    draw.SetAddress(arguments.Buffer.DeviceAddress + arguments.Offset);
                }
                else if (arguments.Buffer.Mapped.Length >= checked((int)arguments.Offset) + 16)
                {
                    draw.SetInline(arguments.Buffer.Mapped.Slice((int)arguments.Offset, 16));
                }
                else
                {
                    throw SubmissionScheduler.Fatal("A task-launched mesh draw's arguments have neither a device address nor host memory.");
                }

                return draw;
            }
            catch
            {
                arguments.Dispose();
                throw;
            }
        }

        // The task stage leaves out a draw over the device group limits and clamps a range past the vertices, as the
        // conversion does. It reports neither: the host never reads a word the GPU may still write, and the draw records
        // inside the rendering, where no barrier to the host can follow it (push words 2-3 stay 0). With meshtask listed
        // the conversion runs and reports them.
        private sealed class TaskLaunchedMesh(RecordingHost owner, RetainedMeshInput arguments, ulong argumentsAddress)
            : IIndirectMeshDraw
        {
            private readonly uint[] _words = new uint[SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.PushWordCount];
            private bool _drawn;
            private bool _disposed;

            // The mesh program reads its parameters from the task payload.
            public ulong ParametersAddress => 0;

            public void SetAddress(ulong address)
            {
                _words[0] = (uint)address;
                _words[1] = (uint)(address >> 32);
            }

            public void SetInline(ReadOnlySpan<byte> arguments)
            {
                for (var index = 0; index < 4; index++)
                    _words[4 + index] = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(arguments[(index * 4)..]);
            }

            // Nothing to record before the pass.
            public void Prepare()
            {
                if (_drawn || _disposed) throw new InvalidOperationException("The task-launched mesh draw is not available.");
            }

            public void Draw()
            {
                using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
                if (_drawn || _disposed || owner._boundGraphicsPipeline is not { } pipeline)
                    throw SubmissionScheduler.Fatal("The task-launched mesh draw has no bound pipeline.");
                if (owner._presenter._cmdDrawMeshTasks is null)
                    throw SubmissionScheduler.Fatal("The Vulkan device has no mesh shader draw command.");
                var command = owner.BeginBatchedGuestCommands();
                owner._gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
                fixed (uint* words = _words)
                {
                    owner._vk.CmdPushConstants(command, pipeline.Layout, ShaderStageFlags.TaskBitExt,
                        SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.PushOffsetBytes,
                        SharpEmu.ShaderCompiler.Vulkan.SpirvMeshTask.PushWordCount * 4u, words);
                }

                owner._presenter._cmdDrawMeshTasks(command, 1, 1, 1);
                _drawn = true;
                // The arguments' buffer stays retained until the GPU has read them.
                owner._scheduler.QueueCompletionAction(arguments.Dispose);
                owner._presenter._occlusionQueries?.TraceDraw();
                if (MeshDrawTrace.Active) MeshDrawTrace.RecordedCommand((ulong)command.Handle);
                owner._gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.MeshDraw,
                    pipeline.Id, 1, 1, 1,
                    owner._profileMeshDrawState with { Width = owner._renderingState.Width, Height = owner._renderingState.Height });
                owner.CountDraw();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (!_drawn) arguments.Dispose();
            }

            public override string ToString() => $"task-launched mesh draw arguments=0x{argumentsAddress:X16}";
        }

        private RetainedMeshInput RetainMeshInput(ulong address, ulong size)
        {
            var (buffer, offset) = _bufferCache.ObtainBuffer(address, size, false);
            return new RetainedMeshInput(buffer, offset);
        }

        private sealed class RetainedMeshInput : IDisposable
        {
            private GpuBuffer? _buffer;
            private readonly IDisposable? _stream;
            public BufferBinding Binding { get; }
            public GpuBuffer Buffer { get; }
            public ulong Offset { get; }

            public RetainedMeshInput(GpuBuffer buffer, ulong offset)
            {
                _buffer = buffer;
                Buffer = buffer;
                Offset = offset;
                Binding = new BufferBinding(buffer.Handle.Handle, offset);
                _stream = (buffer as GpuRingBuffer)?.RetainContents();
                buffer.RetainForeignRead();
            }

            public void Dispose()
            {
                var buffer = Interlocked.Exchange(ref _buffer, null);
                if (buffer is null) return;
                _stream?.Dispose();
                buffer.ReleaseForeignRead();
            }
        }

        private sealed class PreparedIndirectMesh(RecordingHost owner, MeshIndirectPipeline.PreparedDraw resources,
            RetainedMeshInput arguments, RetainedMeshInput? indices, ulong argumentsAddress, uint capacity) : IIndirectMeshDraw
        {
            private bool _recorded;
            private bool _disposed;
            private ulong _tick;
            public ulong ParametersAddress => resources.ParametersAddress;

            public void Prepare()
            {
                if (_recorded || _disposed) throw new InvalidOperationException("The indirect mesh preparation is not available.");
                owner.EndRendering();
                var command = owner.BeginBatchedGuestCommands();
                _tick = owner._scheduler.CurrentTick;
                // Transfer ownership before the first command can reference these resources.
                owner._scheduler.QueueCompletionAction(() =>
                {
                    uint status;
                    try { status = resources.ReadStatus(); }
                    finally { ReleaseResources(); }
                    // The preparation launched no groups for a draw over the device group limits; the CPU path leaves such
                    // a draw out too.
                    if (status == 2)
                    {
                        var left = Interlocked.Increment(ref _overLimitDraws);
                        if ((left & (left - 1)) == 0)
                            Console.Error.WriteLine($"[GPU][WARN] An indirect mesh draw over the device group limits was left out ({left} so far): " +
                                $"arguments=0x{argumentsAddress:X16} capacity={capacity}.");
                    }
                    if (status == 1 && Interlocked.Exchange(ref _clampWarned, 1) == 0)
                        Console.Error.WriteLine($"[GPU][WARN] An indirect mesh draw read past its index range; the extra indices were dropped: " +
                            $"arguments=0x{argumentsAddress:X16} capacity={capacity}.");
                });
                _recorded = true;
                resources.Record(command);
            }

            public void Draw()
            {
                using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
                if (!_recorded || _disposed || _tick != owner._scheduler.CurrentTick)
                    throw SubmissionScheduler.Fatal("The indirect mesh draw lost its recording lifetime.");
                var command = owner.BeginBatchedGuestCommands();
                owner._gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.Preparation);
                owner._presenter._cmdDrawMeshTasksIndirect(command, resources.CommandBuffer, 0, 1, 12);
                owner._presenter._occlusionQueries?.TraceDraw();
                if (MeshDrawTrace.Active) MeshDrawTrace.RecordedCommand((ulong)command.Handle);
                owner._gpuCommandProfile?.WriteMarker(command, VulkanCommandProfile.IntervalKind.MeshDraw,
                    owner._boundGraphicsPipeline?.Id ?? 0, 0, 0, 0,
                    owner._profileMeshDrawState with { Width = owner._renderingState.Width, Height = owner._renderingState.Height });
                owner.CountDraw();
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                if (!_recorded) ReleaseResources();
            }

            private void ReleaseResources()
            {
                resources.Dispose();
                indices?.Dispose();
                arguments.Dispose();
            }
        }
    }
}
