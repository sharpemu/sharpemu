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

        public bool SupportsIndirectMeshDraw => _presenter._supportsMeshShader && _presenter._cmdDrawMeshTasksIndirect is not null;

        public IIndirectMeshDraw PrepareIndirectMeshDraw(in IndirectMeshDrawRequest request)
        {
            if (_worker)
            {
                throw Serial("indirect mesh draw", resumable: true);
            }

            _ = RequirePreparation();
            if (!SupportsIndirectMeshDraw) throw SubmissionScheduler.Fatal("The Vulkan device has no indirect mesh command.");
            var indexBase = request.IndexAddress & ~3ul;
            var prefix = checked((uint)(request.IndexAddress - indexBase));
            var size = (prefix + (ulong)request.IndexCapacity * request.IndexElementSize + 3) & ~3ul;
            var arguments = RetainMeshInput(request.ArgumentsAddress, 20);
            RetainedMeshInput? indices = null;
            MeshIndirectPipeline.PreparedDraw? resources = null;
            try
            {
                indices = RetainMeshInput(indexBase, size);
                var pipeline = _presenter._meshIndirectPipeline ??= new MeshIndirectPipeline(_deviceInfo);
                resources = pipeline.Prepare(_scheduler, arguments.Binding, indices.Binding, prefix, request);
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

            public RetainedMeshInput(GpuBuffer buffer, ulong offset)
            {
                _buffer = buffer;
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
            RetainedMeshInput arguments, RetainedMeshInput indices, ulong argumentsAddress, uint capacity) : IIndirectMeshDraw
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
                    // The CPU path also rejects draws over the device group limits.
                    if (status == 2)
                        throw SubmissionScheduler.Fatal("The indirect mesh draw exceeds device group limits.");
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
                indices.Dispose();
                arguments.Dispose();
            }
        }
    }
}
