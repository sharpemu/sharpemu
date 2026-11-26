// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Libs.VideoOut;

internal static unsafe partial class VulkanVideoPresenter
{
    private sealed partial class Presenter
    {
        internal DispatchThreadsPipeline? _dispatchThreadsPipeline;

        // Indirect dispatches that count threads keep their GPU-written counts on the GPU (DispatchThreadsPipeline).
        public bool ResolvesIndirectThreadDispatchOnGpu => ResolvesIndirectDispatchOnGpu;
    }

    private sealed partial class RecordingHost
    {
        public bool ResolvesIndirectThreadDispatchOnGpu => _presenter.ResolvesIndirectThreadDispatchOnGpu;

        // On the render thread the arguments register before the stage's resources, so no later acquisition can merge an
        // owner the dispatch binds. A render worker never merges an owner: its arguments are acquired with the dispatch.
        public IIndirectDispatchArguments PrepareIndirectDispatchArguments(ulong argumentsAddress)
        {
            _ = RequirePreparation();
            return _worker ? new SliceDispatchArguments(argumentsAddress) : new PreparedDispatchArguments(this, argumentsAddress);
        }

        public IThreadDispatch PrepareThreadDispatch(IIndirectDispatchArguments arguments, IPreparedBindings bindings, ReadOnlySpan<uint> groupSize,
            ReadOnlySpan<int> physicalAxis)
        {
            if (_worker)
            {
                throw Serial("thread dispatch", resumable: true);
            }

            _ = RequirePreparation();
            var stage = (PreparedStageBindings)bindings;
            var layout = stage.Layout;
            BufferBinding? limits = null;
            if (layout.UsesDispatchThreadLimits)
            {
                if (layout.UsesPushData)
                {
                    throw SubmissionScheduler.Fatal(
                        $"A dispatch with GPU thread counts keeps its thread limits in push data: hash=0x{stage.Program.Hash:X16}.");
                }

                var shaderData = stage.Descriptors.ShaderData;
                limits = new BufferBinding(shaderData.Buffer.Handle, shaderData.Offset + layout.DispatchThreadLimitsDword * sizeof(uint));
            }

            var retained = (PreparedDispatchArguments)arguments;
            var pipeline = _presenter._dispatchThreadsPipeline ??= new DispatchThreadsPipeline(_deviceInfo);
            var resources = pipeline.Prepare(_scheduler, retained.Use(), limits, groupSize, physicalAxis);
            var dispatch = new ThreadDispatch(this, resources);
            try
            {
                dispatch.Record(BeginBatchedGuestCommands());
                return dispatch;
            }
            catch
            {
                dispatch.Dispose();
                throw;
            }
        }

        // A render worker's arguments: TryDispatchIndirect acquires them from the slice's buffer snapshot.
        private sealed class SliceDispatchArguments(ulong address) : IIndirectDispatchArguments
        {
            public ulong Address => address;

            public void Rebind()
            {
            }

            public void Dispose()
            {
            }
        }

        private sealed class PreparedDispatchArguments : IIndirectDispatchArguments
        {
            private readonly RecordingHost _owner;
            private readonly ulong _address;
            private RetainedMeshInput? _input;
            private bool _used;
            private bool _disposed;

            public PreparedDispatchArguments(RecordingHost owner, ulong address)
            {
                _owner = owner;
                _address = address;
                _ = owner._bufferCache.FindBuffer(address, 3u * sizeof(uint));
            }

            public void Rebind()
            {
                if (_used || _disposed) throw SubmissionScheduler.Fatal("Indirect arguments cannot be rebound after GPU use or disposal.");
                var replacement = _owner.RetainMeshInput(_address, 3u * sizeof(uint));
                var previous = _input;
                _input = replacement;
                previous?.Dispose();
            }

            public BufferBinding Use()
            {
                var input = _input ?? throw SubmissionScheduler.Fatal("Indirect arguments were already released.");
                _used = true;
                return input.Binding;
            }

            public void Dispose()
            {
                _disposed = true;
                var input = Interlocked.Exchange(ref _input, null);
                if (input is null) return;
                if (_used) _owner._scheduler.QueueCompletionAction(input.Dispose);
                else input.Dispose();
            }
        }

        private sealed class ThreadDispatch(RecordingHost owner, DispatchThreadsPipeline.PreparedDispatch resources) : IThreadDispatch
        {
            private DispatchThreadsPipeline.PreparedDispatch? _resources = resources;
            private bool _recorded;

            public void Record(Silk.NET.Vulkan.CommandBuffer command)
            {
                _recorded = true;
                _resources!.Record(command);
            }

            public void Dispatch()
            {
                var resources = _resources ?? throw SubmissionScheduler.Fatal("The converted dispatch was already released.");
                using var profileScope = RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.DrawRecording);
                var command = owner.BeginBatchedGuestCommands();
                using (RenderPhaseProfile.MeasureDetail(RenderPhaseProfile.Phase.VulkanCommands))
                {
                    owner._vk.CmdDispatchIndirect(command, resources.GroupsBuffer, 0);
                }

                owner.CountDraw();
                owner.NoteShaderMayWriteMappedMemory();
                Dispose();
            }

            public void Dispose()
            {
                var resources = Interlocked.Exchange(ref _resources, null);
                if (resources is null) return;
                // A later consumer may be in a different submission from the conversion.
                // Cancellation also waits for a recorded conversion before returning its pool lease.
                if (_recorded) owner._scheduler.QueueCompletionAction(resources.Dispose);
                else resources.Dispose();
            }
        }
    }
}
