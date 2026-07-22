using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal sealed unsafe class NativeBackend : IDisposable
{
    private const string BuildPassesSignature = "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";
    private const string PushBackCommandSignature = "48 63 41 ?? 4C 8B DA";
    private const int ExpectedMainView = 30;
    private const int ExpectedMainSubView = 11;

    [ThreadStatic]
    private static List<PushedCommand>? pushedCommandProbe;

    [ThreadStatic]
    private static bool countOwnedDraws;

    [ThreadStatic]
    private static int ownedDrawCount;

    private readonly Hook<BuildPassesDelegate> buildPassesHook;
    private readonly Hook<DrawIndexedDelegate> drawIndexedHook;
    private readonly Hook<ProcessCommandsDelegate> processCommandsHook;
    private readonly Hook<PushBackCommandDelegate> pushBackCommandHook;
    private readonly MaterialHelper materialHelper;
    private readonly MaterialLoader material;
    private readonly NativeResources resources;
    private readonly IPluginLog log;
    private int loggedFirstCall;
    private int loggedMainRendezvous;
    private int loggedFirstSubmission;
    private int remainingPushedCommandProbes = 1;
    private int lastSubmittedFrame = -1;
    private int submissionDisabled;
    private nint[]? pendingCommandAddresses;
    private nint pipelineStatisticsQuery;
    private bool pipelineStatisticsPending;
    private bool pipelineStatisticsSubmitted;

    internal NativeBackend(
        IGameInteropProvider gameInteropProvider,
        ISigScanner sigScanner,
        MaterialLoader material,
        NativeResources resources,
        IPluginLog log
    )
    {
        this.log = log;
        this.material = material;
        this.resources = resources;
        materialHelper = new MaterialHelper(sigScanner, material);
        buildPassesHook = gameInteropProvider.HookFromSignature<BuildPassesDelegate>(BuildPassesSignature, BuildPassesDetour);
        pushBackCommandHook = gameInteropProvider.HookFromSignature<PushBackCommandDelegate>(
            PushBackCommandSignature,
            PushBackCommandDetour
        );
        processCommandsHook = gameInteropProvider.HookFromAddress<ProcessCommandsDelegate>(
            (nint)ImmediateContext.MemberFunctionPointers.ProcessCommands,
            ProcessCommandsDetour
        );
        var device = Device.Instance();
        if (device == null || device->D3D11DeviceContext == null)
            throw new InvalidOperationException("The D3D11 device context is not available.");

        // ID3D11DeviceContext::DrawIndexed is vtable slot 12. IDA confirms that
        // native render command type 6 dispatches through this slot.
        var d3dContext = (nint)device->D3D11DeviceContext;
        var drawIndexedAddress = (*(nint**)d3dContext)[12];
        drawIndexedHook = gameInteropProvider.HookFromAddress<DrawIndexedDelegate>(drawIndexedAddress, DrawIndexedDetour);
        pipelineStatisticsQuery = CreatePipelineStatisticsQuery(d3dContext);
        drawIndexedHook.Enable();
        processCommandsHook.Enable();
        pushBackCommandHook.Enable();
        buildPassesHook.Enable();
    }

    public void Dispose()
    {
        buildPassesHook.Dispose();
        pushBackCommandHook.Dispose();
        processCommandsHook.Dispose();
        drawIndexedHook.Dispose();
        ReleaseComObject(ref pipelineStatisticsQuery);
    }

    private nint BuildPassesDetour(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount)
    {
        var result = buildPassesHook.Original(modelRenderer, materialParameters, vertexCount, startIndex, indexCount);

        var threadLocals = ThreadLocals.ThreadLocalInstance();
        var context = threadLocals == null ? null : threadLocals->GraphicsKernelContext;
        if (context == null)
            return result;

        if (Volatile.Read(ref loggedFirstCall) == 0 && Interlocked.CompareExchange(ref loggedFirstCall, 1, 0) == 0)
        {
            log.Information(
                "[Underpaint] Native pass builder reached on view {View}, subview {SubView}.",
                context->ViewIndex,
                context->CurrentSubViewIndex
            );
        }

        var isMainRendezvous =
            context->ViewIndex == ExpectedMainView
            && context->CurrentSubViewIndex == ExpectedMainSubView
            && modelRenderer != 0
            && materialParameters != 0
            && *(nint*)materialParameters != 0;
        if (!isMainRendezvous)
            return result;

        if (Volatile.Read(ref loggedMainRendezvous) == 0 && Interlocked.CompareExchange(ref loggedMainRendezvous, 1, 0) == 0)
        {
            log.Information(
                "[Underpaint] Native main-view rendezvous verified at view {View}, subview {SubView}.",
                context->ViewIndex,
                context->CurrentSubViewIndex
            );
        }

        if (Volatile.Read(ref submissionDisabled) != 0)
            return result;

        var framework = Framework.Instance();
        if (framework == null)
            return result;

        var frame = unchecked((int)framework->FrameCounter);
        if (Interlocked.Exchange(ref lastSubmittedFrame, frame) == frame)
            return result;

        try
        {
            resources.CreateConstants(material.ShaderPackage);
            resources.LoadWhiteTexture();
            resources.WriteFixedViewSpaceWorld();
            var worldConstantId = ((ModelRenderer*)modelRenderer)->ConstantSamplerIds[(int)ModelRenderer.WellKnownConstant.WorldViewMatrix];
            var bindings = materialHelper.ValidateResources(
                resources.InstanceConstant,
                resources.ModelConstant,
                resources.MaterialConstant,
                resources.WhiteTexture
            );
            var model = stackalloc Model[1];
            var modelParameters = stackalloc ModelRenderer.OnRenderModelParams[1];
            var ownedMaterialParameters = stackalloc ModelRenderer.OnRenderMaterialParams2[1];
            var selection = stackalloc MaterialHelper.ShaderSelection[1];
            materialHelper.Initialize(
                (ModelRenderer*)modelRenderer,
                model,
                modelParameters,
                ownedMaterialParameters,
                selection,
                resources.InstanceConstant
            );
            try
            {
                var contextState = new NativeContextState((byte*)context, worldConstantId, bindings);
                MaterialHelperResult helperResult;
                ShaderPair shaders;
                List<PushedCommand>? pushedCommands = null;
                nint commandBaseBefore;
                ulong commandUsedBefore;
                nint commandBaseAfter;
                ulong commandUsedAfter;
                try
                {
                    helperResult = materialHelper.Apply((ModelRenderer*)modelRenderer, (byte*)context, ownedMaterialParameters, selection);
                    shaders = MaterialHelper.ResolveActiveShaders((byte*)context, helperResult.ShaderDescriptor);
                    contextState.InstallShaders(shaders, helperResult.ShaderDescriptor);
                    contextState.Install(resources);
                    commandBaseBefore = (nint)context->CommandAllocationBase;
                    commandUsedBefore = context->CommandAllocationUsedSize;
                    var capturePushedCommands = Interlocked.Exchange(ref remainingPushedCommandProbes, 0) == 1;
                    pushedCommandProbe = capturePushedCommands ? [] : null;
                    try
                    {
                        buildPassesHook.Original(
                            modelRenderer,
                            (nint)ownedMaterialParameters,
                            NativeResources.VertexCount,
                            NativeResources.IndexStart,
                            NativeResources.IndexCount
                        );
                        pushedCommands = pushedCommandProbe;
                    }
                    finally
                    {
                        pushedCommandProbe = null;
                    }
                    commandBaseAfter = (nint)context->CommandAllocationBase;
                    commandUsedAfter = context->CommandAllocationUsedSize;
                    if (commandBaseAfter == commandBaseBefore && commandUsedAfter <= commandUsedBefore)
                        throw new InvalidOperationException("The native pass builder produced no command data.");
                }
                finally
                {
                    contextState.Restore();
                }

                if (pushedCommands is { } commands)
                {
                    Volatile.Write(ref pendingCommandAddresses, commands.Select(command => command.Address).ToArray());
                    log.Information(
                        "[Underpaint] Owned PushBackCommand probe: Commands={CommandCount}, "
                            + "AllBindingsMatch={AllBindingsMatch}, Items=[{Commands}]",
                        commands.Count,
                        commands.Count > 0
                            && commands.All(command => command.BindingsMatch(resources, shaders, helperResult.ShaderDescriptor)),
                        string.Join(',', commands)
                    );
                }

                if (Interlocked.CompareExchange(ref loggedFirstSubmission, 1, 0) == 0)
                {
                    log.Information(
                        "[Underpaint] Submitted one owned triangle every render frame: Frame={Frame}, "
                            + "CommandArena=0x{CommandBaseBefore:X}+{CommandUsedBefore}->0x{CommandBaseAfter:X}+{CommandUsedAfter}, "
                            + "ActivePass={ActivePass}, OnRenderMaterial=0x{OnRenderMaterial:X}, "
                            + "Output40=0x{Output:X8}, Descriptor=0x{Descriptor:X}, "
                            + "MaterialConstantId={MaterialConstantId}, InstanceConstantId={InstanceConstantId}, "
                            + "ModelConstantId={ModelConstantId}, WorldConstantId={WorldConstantId}, "
                            + "NormalSamplerId={NormalSamplerId}, IndexSamplerId={IndexSamplerId}, "
                            + "TableSamplerId={TableSamplerId}, WhiteTexture=ready.",
                        frame,
                        commandBaseBefore,
                        commandUsedBefore,
                        commandBaseAfter,
                        commandUsedAfter,
                        shaders.Pass,
                        helperResult.OnRenderMaterial,
                        helperResult.Output,
                        helperResult.ShaderDescriptor,
                        bindings.MaterialConstantId,
                        bindings.InstanceConstantId,
                        bindings.ModelConstantId,
                        worldConstantId,
                        bindings.NormalSamplerId,
                        bindings.IndexSamplerId,
                        bindings.TableSamplerId
                    );
                }
            }
            finally
            {
                materialHelper.Destroy(selection);
            }
        }
        catch (Exception exception)
        {
            Volatile.Write(ref submissionDisabled, 1);
            log.Error(exception, "[Underpaint] Native submission failed; later frames are disabled.");
        }

        return result;
    }

    private void PushBackCommandDetour(nint context, nint command)
    {
        if (context != 0 && command != 0 && pushedCommandProbe is { Count: < 16 } commands)
        {
            const int indexBufferOffset = 0x888;
            const int vertexDeclarationOffset = 0x890;
            const int vertexShaderOffset = 0x878;
            const int pixelShaderOffset = 0x880;
            const int shaderDescriptorOffset = 0x8B8;
            const int streamOffset = 0x8C0;
            const int streamSize = 16;

            var contextBytes = (byte*)context;
            commands.Add(
                new PushedCommand(
                    command,
                    *(uint*)command,
                    *(uint*)(command + 0x04),
                    *(uint*)(command + 0x08),
                    *(uint*)(command + 0x0C),
                    *(uint*)(command + 0x10),
                    *(uint*)(command + 0x14),
                    *(uint*)(command + 0x18),
                    *(nint*)(contextBytes + indexBufferOffset),
                    *(nint*)(contextBytes + vertexDeclarationOffset),
                    *(StreamBinding*)(contextBytes + streamOffset),
                    *(StreamBinding*)(contextBytes + streamOffset + streamSize),
                    *(nint*)(contextBytes + vertexShaderOffset),
                    *(nint*)(contextBytes + pixelShaderOffset),
                    *(nint*)(contextBytes + shaderDescriptorOffset)
                )
            );
        }

        pushBackCommandHook.Original(context, command);
    }

    private void ProcessCommandsDetour(
        ImmediateContext* immediateContext,
        RenderCommandBufferGroup* renderCommands,
        uint renderCommandCount
    )
    {
        var pending = Volatile.Read(ref pendingCommandAddresses);
        if (pending is { Length: > 0 } && renderCommands != null)
        {
            var matches = 0;
            for (var index = 0u; index < renderCommandCount; index++)
            {
                var address = (nint)renderCommands[index].Command;
                if (pending.Contains(address))
                    matches++;
            }

            if (matches > 0)
            {
                Volatile.Write(ref pendingCommandAddresses, null);
                ownedDrawCount = 0;
                countOwnedDraws = true;
                try
                {
                    processCommandsHook.Original(immediateContext, renderCommands, renderCommandCount);
                }
                finally
                {
                    countOwnedDraws = false;
                }

                log.Information(
                    "[Underpaint] Owned command execution probe: Matched={Matched}/{Expected}, "
                        + "ProcessCommandCount={ProcessCommandCount}, DrawIndexed(3,256,0)={DrawCount}.",
                    matches,
                    pending.Length,
                    renderCommandCount,
                    ownedDrawCount
                );
                return;
            }
        }

        processCommandsHook.Original(immediateContext, renderCommands, renderCommandCount);
    }

    private void DrawIndexedDetour(nint context, uint indexCount, uint startIndex, int baseVertex)
    {
        TryReadPipelineStatistics(context);

        if (countOwnedDraws && indexCount == 3 && startIndex == NativeResources.IndexStart && baseVertex == 0)
        {
            ownedDrawCount++;

            if (!pipelineStatisticsSubmitted)
            {
                pipelineStatisticsSubmitted = true;
                var contextVTable = *(nint**)context;
                ((delegate* unmanaged<nint, nint, void>)contextVTable[27])(context, pipelineStatisticsQuery);
                drawIndexedHook.Original(context, indexCount, startIndex, baseVertex);
                ((delegate* unmanaged<nint, nint, void>)contextVTable[28])(context, pipelineStatisticsQuery);
                pipelineStatisticsPending = true;
                return;
            }
        }

        drawIndexedHook.Original(context, indexCount, startIndex, baseVertex);
    }

    private void TryReadPipelineStatistics(nint context)
    {
        if (!pipelineStatisticsPending)
            return;

        PipelineStatistics statistics;
        var contextVTable = *(nint**)context;
        var result = ((delegate* unmanaged<nint, nint, PipelineStatistics*, uint, uint, int>)contextVTable[29])(
            context,
            pipelineStatisticsQuery,
            &statistics,
            (uint)sizeof(PipelineStatistics),
            1
        );
        if (result != 0)
            return;

        pipelineStatisticsPending = false;
        log.Information(
            "[Underpaint] Owned draw pipeline statistics: IAVertices={IAVertices}, IAPrimitives={IAPrimitives}, "
                + "VS={VS}, ClipperInvocations={ClipperInvocations}, ClipperPrimitives={ClipperPrimitives}, PS={PS}.",
            statistics.IAVertices,
            statistics.IAPrimitives,
            statistics.VSInvocations,
            statistics.ClipperInvocations,
            statistics.ClipperPrimitives,
            statistics.PSInvocations
        );
    }

    private static nint CreatePipelineStatisticsQuery(nint context)
    {
        var contextVTable = *(nint**)context;
        nint device = 0;
        ((delegate* unmanaged<nint, nint*, void>)contextVTable[3])(context, &device);
        if (device == 0)
            throw new InvalidOperationException("ID3D11DeviceContext::GetDevice returned null.");

        try
        {
            var description = new QueryDescription(4, 0);
            nint query = 0;
            var result = ((delegate* unmanaged<nint, QueryDescription*, nint*, int>)(*(nint**)device)[24])(device, &description, &query);
            if (result < 0 || query == 0)
                throw new InvalidOperationException($"ID3D11Device::CreateQuery failed with HRESULT 0x{result:X8}.");

            return query;
        }
        finally
        {
            ((delegate* unmanaged<nint, uint>)(*(nint**)device)[2])(device);
        }
    }

    private static void ReleaseComObject(ref nint value)
    {
        var current = value;
        value = 0;
        if (current != 0)
            ((delegate* unmanaged<nint, uint>)(*(nint**)current)[2])(current);
    }

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);

    private delegate void PushBackCommandDelegate(nint context, nint command);

    private delegate void ProcessCommandsDelegate(
        ImmediateContext* immediateContext,
        RenderCommandBufferGroup* renderCommands,
        uint renderCommandCount
    );

    private delegate void DrawIndexedDelegate(nint context, uint indexCount, uint startIndex, int baseVertex);

    private readonly record struct StreamBinding(nint Buffer, ulong OffsetAndStride);

    private readonly record struct QueryDescription(uint Query, uint MiscFlags);

    private struct PipelineStatistics
    {
        internal ulong IAVertices;
        internal ulong IAPrimitives;
        internal ulong VSInvocations;
        internal ulong GSInvocations;
        internal ulong GSPrimitives;
        internal ulong ClipperInvocations;
        internal ulong ClipperPrimitives;
        internal ulong PSInvocations;
        internal ulong HSInvocations;
        internal ulong DSInvocations;
        internal ulong CSInvocations;
    }

    private readonly record struct PushedCommand(
        nint Address,
        uint Type,
        uint Value04,
        uint Value08,
        uint Value0C,
        uint Value10,
        uint Value14,
        uint Value18,
        nint IndexBuffer,
        nint VertexDeclaration,
        StreamBinding Stream0,
        StreamBinding Stream1,
        nint VertexShader,
        nint PixelShader,
        nint ShaderDescriptor
    )
    {
        internal bool BindingsMatch(NativeResources resources, ShaderPair shaders, nint descriptor) =>
            IndexBuffer == resources.IndexBuffer
            && VertexDeclaration == resources.VertexDeclaration
            && Stream0 == new StreamBinding(resources.VertexBuffer, PackStreamBinding(0, NativeResources.Stream0Stride))
            && Stream1
                == new StreamBinding(resources.VertexBuffer, PackStreamBinding(resources.Stream1Offset, NativeResources.Stream1Stride))
            && VertexShader == shaders.Vertex
            && PixelShader == shaders.Pixel
            && ShaderDescriptor == descriptor;

        public override string ToString() =>
            $"type={Type}/+04={Value04}/+08={Value08}/+0C={Value0C}/+10={Value10}/+14={Value14}/+18={Value18}/"
            + $"ib=0x{IndexBuffer:X}/decl=0x{VertexDeclaration:X}/s0=0x{Stream0.Buffer:X}:0x{Stream0.OffsetAndStride:X}/"
            + $"s1=0x{Stream1.Buffer:X}:0x{Stream1.OffsetAndStride:X}/vs=0x{VertexShader:X}/ps=0x{PixelShader:X}/"
            + $"descriptor=0x{ShaderDescriptor:X}";

        private static ulong PackStreamBinding(int byteOffset, int stride) => ((ulong)(uint)byteOffset << 8) | (byte)stride;
    }
}
