using System.Numerics;
using System.Text;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal sealed unsafe class NativeBackend : IDisposable
{
    private const string BuildPassesSignature = "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";
    private const int ExpectedMainView = 30;
    private const int ExpectedMainSubView = 11;
    private const int MainTransformSubView = 12;

    private readonly Hook<BuildPassesDelegate> buildPassesHook;
    private readonly MaterialHelper materialHelper;
    private readonly MaterialLoader material;
    private readonly NativeResources resources;
    private readonly IPluginLog log;
    private int loggedFirstCall;
    private int loggedMainRendezvous;
    private int nativeInitializationAttempted;

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
        buildPassesHook.Enable();
    }

    public void Dispose() => buildPassesHook.Dispose();

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

        if (Interlocked.CompareExchange(ref nativeInitializationAttempted, 1, 0) != 0)
            return result;

        try
        {
            resources.CreateConstants(material.ShaderPackage);
            resources.LoadWhiteTexture();
            var view = GetMainViewMatrix();
            resources.WriteInitialWorld(view);
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
                nint submissionResult;
                try
                {
                    helperResult = materialHelper.Apply((ModelRenderer*)modelRenderer, (byte*)context, ownedMaterialParameters, selection);
                    shaders = MaterialHelper.ResolveActiveShaders((byte*)context, helperResult.ShaderDescriptor);
                    contextState.InstallShaders(shaders, helperResult.ShaderDescriptor);
                    contextState.Install(resources);
                    LogTextureEvidence();
                    submissionResult = buildPassesHook.Original(
                        modelRenderer,
                        (nint)ownedMaterialParameters,
                        NativeResources.VertexCount,
                        0,
                        NativeResources.IndexCount
                    );
                }
                finally
                {
                    contextState.Restore();
                }

                log.Information(
                    "[Underpaint] Submitted one owned triangle through the native pass builder: Result=0x{Result:X}, "
                        + "ActivePass={ActivePass}, OnRenderMaterial=0x{OnRenderMaterial:X}, "
                        + "Output40=0x{Output:X8}, Descriptor=0x{Descriptor:X}, "
                        + "MaterialConstantId={MaterialConstantId}, InstanceConstantId={InstanceConstantId}, "
                        + "ModelConstantId={ModelConstantId}, WorldConstantId={WorldConstantId}, "
                        + "NormalSamplerId={NormalSamplerId}, IndexSamplerId={IndexSamplerId}, "
                        + "TableSamplerId={TableSamplerId}, WhiteTexture=ready.",
                    submissionResult,
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
            finally
            {
                materialHelper.Destroy(selection);
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Underpaint] Native initialization failed; submission is disabled.");
        }

        return result;
    }

    private void LogTextureEvidence()
    {
        var evidence = new StringBuilder("[Underpaint] Texture evidence immediately before native builder. ");
        AppendTextureHandle(evidence, "White", resources.WhiteTextureResource);

        var donor = material.Material;
        if (donor == null)
        {
            evidence.Append(" DonorMaterial=null.");
        }
        else
        {
            evidence.Append($" DonorTextureCount={donor->TextureCount}.");
            for (var index = 0; index < donor->TextureCount; index++)
            {
                var entry = donor->Textures[index];
                evidence.Append($" Donor[{index}]=Id:{entry.Id}/Flags:0x{entry.SamplerFlags:X8};");
                AppendTextureHandle(evidence, $"Donor[{index}]Handle", entry.Texture);
            }
        }

        log.Warning(evidence.ToString());
    }

    private static void AppendTextureHandle(StringBuilder evidence, string name, TextureResourceHandle* handle)
    {
        if (handle == null)
        {
            evidence.Append($" {name}=null.");
            return;
        }

        var bytes = (byte*)handle;
        evidence.Append(
            $" {name}=Handle:0x{(nint)handle:X}/LoadState:{handle->LoadState}/RefCount:{handle->RefCount}"
                + $"/+110:0x{*(nint*)(bytes + 0x110):X}/+118:0x{*(nint*)(bytes + 0x118):X}"
                + $"/+120:0x{*(nint*)(bytes + 0x120):X}/+128:0x{*(nint*)(bytes + 0x128):X}"
                + $"/+130:0x{*(nint*)(bytes + 0x130):X}/+138:0x{*(nint*)(bytes + 0x138):X}"
                + $"/+140:0x{*(nint*)(bytes + 0x140):X}/+148:0x{*(nint*)(bytes + 0x148):X}."
        );
    }

    private static Matrix4x4 GetMainViewMatrix()
    {
        var manager = Manager.Instance();
        var camera = manager == null ? null : manager->Views[ExpectedMainView].SubViews[MainTransformSubView].Camera;
        if (camera == null)
            throw new InvalidOperationException("The native main-view camera is not available.");

        var view = *(Matrix4x4*)&camera->ViewMatrix;
        view.M14 = 0;
        view.M24 = 0;
        view.M34 = 0;
        view.M44 = 1;
        return view;
    }

    private delegate nint BuildPassesDelegate(nint modelRenderer, nint materialParameters, int vertexCount, int startIndex, int indexCount);
}
