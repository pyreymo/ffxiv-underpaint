using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal delegate nint NativePassBuilder(
    nint modelRenderer,
    nint materialParameters,
    int vertexCount,
    int startIndex,
    int indexCount
);

internal sealed unsafe class NativeGeometrySubmissionBackend : IDisposable
{
    private const string CreateVertexBufferSignature = "40 55 56 57 41 57 48 83 EC 28";
    private const string InitializeVertexBufferSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 50 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 44 8B 49";
    private const string CreateIndexBufferSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 20 48 8B 05";
    private const string InitializeIndexBufferSignature =
        "40 53 48 83 EC 20 F7 41 40 00 08 00 00 48 8B D9";
    private const string CreateVertexDeclarationSignature =
        "48 8B 49 ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC 40 53 55 57";
    private const string ExpandPassesSignature =
        "44 89 4C 24 ?? 44 89 44 24 ?? 53 56 57 41 54 41 55";
    private const string InitializeShaderSelectionSignature =
        "48 85 D2 0F 84 ?? ?? ?? ?? 48 89 5C 24 ?? 57 48 83 EC 20 48 89 74 24";
    private const string DestroyShaderSelectionSignature =
        "40 53 48 83 EC 20 48 83 79 ?? ?? 48 8B D9 74 ?? 4C 8B 41";
    private const string ApplyMaterialSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 54 41 56 41 57 48 83 EC 20 44 8B 05 ?? ?? ?? ?? 48 8B F2 65 48 8B 04 25 ?? ?? ?? ?? 48 8B D9";
    private const string ResolveShaderSelectionSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 30 48 8B F2 48 8B F9 48 39 4A 08";
    private const string SnapshotCommandSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 56 57 41 57 48 83 EC 30 48 8B 81 58 08 00 00";
    private const uint ImmutableBufferFlags = 0x804;
    private const int Stream0Stride = 8;
    private const int Stream1Stride = 16;
    private const string StandaloneMaterialPath =
        "bgcommon/hou/indoor/general/0517/material/fun_b0_m0517_0a.mtrl";
    private const uint StandaloneMaterialFileType = 0x6D74726C;
    private const uint StandaloneMaterialPathHash = 0x5D6A7B3E;
    private const int ModelObjectParameterSize = 176;
    private const uint SupportedAuxiliaryViewMask = 0x00000003;
    private const int MainRenderViewIndex = 30;
    private const int MainRendezvousSubViewIndex = 11;
    private const int MainTransformSubViewIndex = 12;

    private static readonly byte[] VertexDeclarationElements =
    [
        0,
        0,
        0x1C,
        0,
        1,
        0,
        0x1C,
        2,
        1,
        8,
        0x1C,
        8,
    ];

    [ThreadStatic]
    private static bool submitting;

    [ThreadStatic]
    private static string? rejectedSnapshot;

    private readonly object stateLock = new();
    private readonly Hook<CreateVertexBufferDelegate> createVertexBufferHook;
    private readonly Hook<InitializeBufferDelegate> initializeVertexBufferHook;
    private readonly Hook<CreateIndexBufferDelegate> createIndexBufferHook;
    private readonly Hook<InitializeBufferDelegate> initializeIndexBufferHook;
    private readonly Hook<CreateVertexDeclarationDelegate> createVertexDeclarationHook;
    private readonly Hook<InitializeShaderSelectionDelegate> initializeShaderSelectionHook;
    private readonly Hook<DestroyShaderSelectionDelegate> destroyShaderSelectionHook;
    private readonly Hook<ApplyMaterialDelegate> applyMaterialHook;
    private readonly Hook<ResolveShaderSelectionDelegate> resolveShaderSelectionHook;
    private readonly Hook<SnapshotCommandDelegate> snapshotCommandHook;
    private readonly Hook<NativePassBuilder> expandPassesHook;
    private readonly IPluginLog log;
    private readonly HashSet<NativeGeometry> geometries = [];
    private readonly HashSet<NativeRigidInstance> rigidInstances = [];
    private readonly List<NativeRigidInstance> retiredRigidInstances = [];
    private ConstantBuffer* standaloneObjectConstant;
    private MaterialResourceHandle* standaloneMaterialResource;
    private uint standaloneMaterialConstantId = uint.MaxValue;
    private bool disposed;
    private RendezvousIdentity lastRigidRendezvous;

    private delegate nint CreateVertexBufferDelegate(
        Device* device,
        int byteSize,
        uint flags,
        byte allocationCategory
    );

    private delegate byte InitializeBufferDelegate(nint buffer, void* data);

    private delegate nint CreateIndexBufferDelegate(
        Device* device,
        int byteSize,
        int elementSize,
        uint flags,
        byte allocationCategory
    );

    private delegate nint CreateVertexDeclarationDelegate(
        Device* device,
        byte* elements,
        uint elementCount
    );

    private delegate void InitializeShaderSelectionDelegate(byte* selection, nint shaderPackage);

    private delegate void DestroyShaderSelectionDelegate(byte* selection);

    private delegate void ApplyMaterialDelegate(byte* selection, Material* material);

    private delegate nint ResolveShaderSelectionDelegate(nint shaderPackage, byte* selection);

    private delegate byte SnapshotCommandDelegate(nint context, nint commandState);

    public NativeGeometrySubmissionBackend(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.log = log;
        createVertexBufferHook = gameInteropProvider.HookFromSignature<CreateVertexBufferDelegate>(
            CreateVertexBufferSignature,
            (_, _, _, _) => 0
        );
        initializeVertexBufferHook =
            gameInteropProvider.HookFromSignature<InitializeBufferDelegate>(
                InitializeVertexBufferSignature,
                (_, _) => 0
            );
        createIndexBufferHook = gameInteropProvider.HookFromSignature<CreateIndexBufferDelegate>(
            CreateIndexBufferSignature,
            (_, _, _, _, _) => 0
        );
        initializeIndexBufferHook = gameInteropProvider.HookFromSignature<InitializeBufferDelegate>(
            InitializeIndexBufferSignature,
            (_, _) => 0
        );
        createVertexDeclarationHook =
            gameInteropProvider.HookFromSignature<CreateVertexDeclarationDelegate>(
                CreateVertexDeclarationSignature,
                (_, _, _) => 0
            );
        initializeShaderSelectionHook =
            gameInteropProvider.HookFromSignature<InitializeShaderSelectionDelegate>(
                InitializeShaderSelectionSignature,
                (_, _) => { }
            );
        destroyShaderSelectionHook =
            gameInteropProvider.HookFromSignature<DestroyShaderSelectionDelegate>(
                DestroyShaderSelectionSignature,
                _ => { }
            );
        applyMaterialHook = gameInteropProvider.HookFromSignature<ApplyMaterialDelegate>(
            ApplyMaterialSignature,
            (_, _) => { }
        );
        resolveShaderSelectionHook =
            gameInteropProvider.HookFromSignature<ResolveShaderSelectionDelegate>(
                ResolveShaderSelectionSignature,
                (_, _) => 0
            );
        snapshotCommandHook = gameInteropProvider.HookFromSignature<SnapshotCommandDelegate>(
            SnapshotCommandSignature,
            SnapshotCommandDetour
        );
        expandPassesHook = gameInteropProvider.HookFromSignature<NativePassBuilder>(
            ExpandPassesSignature,
            ExpandPassesDetour
        );
        snapshotCommandHook.Enable();
        expandPassesHook.Enable();
    }

    public NativeGeometry CreateGeometry(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<ushort> indices
    ) => CreateGeometry(positions, new Vector2[positions.Length], indices);

    public NativeGeometry CreateGeometry(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector2> textureCoordinates,
        ReadOnlySpan<ushort> indices
    )
    {
        if (positions.IsEmpty)
            throw new ArgumentException(
                "Native geometry requires at least one vertex.",
                nameof(positions)
            );
        if (positions.Length > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(positions));
        if (textureCoordinates.Length != positions.Length)
            throw new ArgumentException(
                "Native geometry requires one texture coordinate per vertex.",
                nameof(textureCoordinates)
            );
        if (indices.IsEmpty || indices.Length % 3 != 0)
            throw new ArgumentException(
                "Native geometry indices must contain complete triangles.",
                nameof(indices)
            );
        foreach (var index in indices)
        {
            if (index >= positions.Length)
                throw new ArgumentOutOfRangeException(
                    nameof(indices),
                    "An index is outside the vertex range."
                );
        }

        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var geometry = CreateGeometryCore(positions, textureCoordinates, indices);
            geometries.Add(geometry);
            return geometry;
        }
    }

    public NativeRigidInstance CreateRigidInstance(
        NativeGeometry geometry,
        Matrix4x4 currentWorldView
    ) => CreateRigidInstance(geometry, currentWorldView, null);

    public NativeRigidInstance CreateWorldRigidInstance(NativeGeometry geometry, Matrix4x4 world) =>
        CreateRigidInstance(geometry, Matrix4x4.Identity, world);

    private NativeRigidInstance CreateRigidInstance(
        NativeGeometry geometry,
        Matrix4x4 currentWorldView,
        Matrix4x4? fixedWorld
    )
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (geometry.Owner != this || geometry.IsDisposed)
                throw new ObjectDisposedException(nameof(geometry));
            var device = Device.Instance();
            var worldConstant = device == null ? null : device->CreateConstantBuffer(128, 2, 0);
            if (worldConstant == null)
                throw new InvalidOperationException(
                    "The game rejected a rigid-instance world constant buffer."
                );
            var instance = new NativeRigidInstance(
                this,
                geometry,
                worldConstant,
                currentWorldView,
                fixedWorld
            );
            rigidInstances.Add(instance);
            return instance;
        }
    }

    private void SubmitCore(
        nint modelRenderer,
        nint materialParameters,
        NativeGeometry geometry,
        NativePassBuilder submit,
        uint constantId,
        ConstantBuffer* constant,
        NativeContextStateScope? existingState = null
    )
    {
        var threadLocals = ThreadLocals.ThreadLocalInstance();
        var context = threadLocals == null ? null : threadLocals->GraphicsKernelContext;
        if (context == null)
            throw new InvalidOperationException(
                "The current render thread has no graphics context."
            );

        var contextBytes = (byte*)context;
        var state = existingState ?? new NativeContextStateScope(contextBytes);
        var ownsState = existingState == null;

        submitting = true;
        rejectedSnapshot = null;
        try
        {
            state.InstallGeometry(geometry);
            state.SetConstant(constantId, constant);
            submit(modelRenderer, materialParameters, geometry.VertexCount, 0, geometry.IndexCount);
            if (rejectedSnapshot is { } rejection)
                throw new InvalidOperationException(rejection);
        }
        finally
        {
            rejectedSnapshot = null;
            submitting = false;
            if (ownsState)
                state.Dispose();
        }
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
                return;
            disposed = true;
        }

        expandPassesHook.Disable();
        ReleaseNativeResource(ref standaloneObjectConstant);
        ReleaseStandaloneMaterial();
        lock (stateLock)
        {
            foreach (var instance in rigidInstances.Concat(retiredRigidInstances).ToArray())
                instance.DisposeCore();
            rigidInstances.Clear();
            retiredRigidInstances.Clear();
            foreach (var geometry in geometries.ToArray())
                geometry.DisposeCore();
            geometries.Clear();
        }

        createVertexDeclarationHook.Dispose();
        snapshotCommandHook.Dispose();
        resolveShaderSelectionHook.Dispose();
        applyMaterialHook.Dispose();
        destroyShaderSelectionHook.Dispose();
        initializeShaderSelectionHook.Dispose();
        initializeIndexBufferHook.Dispose();
        createIndexBufferHook.Dispose();
        initializeVertexBufferHook.Dispose();
        createVertexBufferHook.Dispose();
        expandPassesHook.Dispose();
    }

    internal void Release(NativeGeometry geometry)
    {
        lock (stateLock)
        {
            if (!geometries.Remove(geometry))
                return;
            geometry.DisposeCore();
        }
    }

    internal void Remove(NativeRigidInstance instance)
    {
        lock (stateLock)
        {
            if (!rigidInstances.Remove(instance))
                return;
            instance.MarkRemoved();
            // Native commands retain these resources beyond builder return. Keep the allocation
            // alive until backend teardown until a confirmed render-frame completion fence exists.
            retiredRigidInstances.Add(instance);
        }
    }

    private NativeGeometry CreateGeometryCore(
        ReadOnlySpan<Vector3> positions,
        ReadOnlySpan<Vector2> textureCoordinates,
        ReadOnlySpan<ushort> indices
    )
    {
        var device = Device.Instance();
        if (device == null)
            throw new InvalidOperationException("The native graphics device is not available.");

        var stream0Bytes = checked(positions.Length * Stream0Stride);
        var vertexBytes = checked(stream0Bytes + positions.Length * Stream1Stride);
        var indexBytes = checked(indices.Length * sizeof(ushort));
        var vertexData = new byte[vertexBytes];
        fixed (byte* vertexDataPointer = vertexData)
        {
            var stream0 = (NativeStream0Vertex*)vertexDataPointer;
            var stream1 = (NativeStream1Vertex*)(vertexDataPointer + stream0Bytes);
            for (var index = 0; index < positions.Length; index++)
            {
                stream0[index] = new NativeStream0Vertex(positions[index]);
                stream1[index] = new NativeStream1Vertex(textureCoordinates[index]);
            }
        }

        nint vertexBuffer = 0;
        nint indexBuffer = 0;
        nint vertexDeclaration = 0;
        try
        {
            vertexBuffer = createVertexBufferHook.Original(
                device,
                vertexBytes,
                ImmutableBufferFlags,
                0
            );
            indexBuffer = createIndexBufferHook.Original(
                device,
                indexBytes,
                1,
                ImmutableBufferFlags,
                0
            );
            fixed (byte* declaration = VertexDeclarationElements)
            {
                vertexDeclaration = createVertexDeclarationHook.Original(
                    device,
                    declaration,
                    (uint)(VertexDeclarationElements.Length / 4)
                );
            }

            fixed (byte* vertexDataPointer = vertexData)
            fixed (ushort* indexDataPointer = indices)
            {
                if (
                    vertexBuffer == 0
                    || indexBuffer == 0
                    || vertexDeclaration == 0
                    || initializeVertexBufferHook.Original(vertexBuffer, vertexDataPointer) == 0
                    || initializeIndexBufferHook.Original(indexBuffer, indexDataPointer) == 0
                )
                {
                    throw new InvalidOperationException(
                        "The game rejected a native geometry resource."
                    );
                }
            }

            return new NativeGeometry(
                this,
                vertexBuffer,
                indexBuffer,
                vertexDeclaration,
                positions.Length,
                indices.Length,
                stream0Bytes
            );
        }
        catch
        {
            ReleaseNativeResource(ref vertexDeclaration);
            ReleaseNativeResource(ref indexBuffer);
            ReleaseNativeResource(ref vertexBuffer);
            throw;
        }
    }

    private nint ExpandPassesDetour(
        nint modelRenderer,
        nint materialParameters,
        int vertexCount,
        int startIndex,
        int indexCount
    )
    {
        var result = expandPassesHook.Original(
            modelRenderer,
            materialParameters,
            vertexCount,
            startIndex,
            indexCount
        );
        if (submitting || materialParameters == 0)
            return result;

        lock (stateLock)
        {
            if (rigidInstances.Count == 0)
                return result;
        }

        var threadLocals = ThreadLocals.ThreadLocalInstance();
        var context = threadLocals == null ? null : threadLocals->GraphicsKernelContext;
        var view = context == null ? -1 : context->ViewIndex;
        var subView = context == null ? -1 : context->CurrentSubViewIndex;
        var modelParams = *(nint*)materialParameters;
        if (
            context == null
            || view != MainRenderViewIndex
            || subView != MainRendezvousSubViewIndex
            || modelParams == 0
        )
            return result;

        SubmitRigidInstancesAtRendezvous(
            modelRenderer,
            materialParameters,
            expandPassesHook.Original,
            GetConstantId(modelRenderer, 1),
            (nint)context,
            view,
            subView
        );

        return result;
    }

    private byte SnapshotCommandDetour(nint context, nint commandState)
    {
        if (!submitting || context == 0)
            return snapshotCommandHook.Original(context, commandState);

        var contextBytes = (byte*)context;
        var descriptor = *(nint*)(contextBytes + 0x8B8);
        if (descriptor != 0)
        {
            if (TryGetActivePassShaders(contextBytes, descriptor, out _, out _))
                return snapshotCommandHook.Original(context, commandState);

            rejectedSnapshot ??=
                $"Native BG command snapshot rejected before submission: ActivePass={contextBytes[0x0B] & 0x0F} "
                + $"Descriptor=0x{descriptor:X} has no complete VS/PS pair.";
            return 0;
        }

        var vertexShader = *(nint*)(contextBytes + 0x878);
        var pixelShader = *(nint*)(contextBytes + 0x880);
        if (vertexShader != 0 && pixelShader != 0)
            return snapshotCommandHook.Original(context, commandState);

        rejectedSnapshot ??=
            $"Native BG command snapshot rejected before submission: ActivePass={contextBytes[0x0B] & 0x0F} "
            + $"Descriptor=null CurrentVS=0x{vertexShader:X} CurrentPS=0x{pixelShader:X}.";
        return 0;
    }

    private void SubmitRigidInstancesAtRendezvous(
        nint modelRenderer,
        nint materialParameters,
        NativePassBuilder submit,
        uint worldConstantId,
        nint context,
        int view,
        int subView
    )
    {
        NativeRigidInstance[] instances;
        uint frame;
        lock (stateLock)
        {
            if (rigidInstances.Count == 0)
                return;
            var framework = Framework.Instance();
            frame = framework == null ? 0 : framework->FrameCounter;
            var identity = new RendezvousIdentity(frame, context, view, subView);
            if (identity == lastRigidRendezvous)
                return;
            lastRigidRendezvous = identity;
            instances = rigidInstances.ToArray();
        }

        foreach (var instance in instances)
        {
            try
            {
                Matrix4x4? renderWorldView = null;
                if (instance.FixedWorld is { } world)
                {
                    if (!TryGetActiveView(out var viewMatrix))
                        continue;
                    renderWorldView = world * viewMatrix;
                }
                if (!instance.PrepareWorld(frame, renderWorldView))
                    continue;
                instance.RecordSubmission("before", frame, context, view, subView);
                SubmitOwnedWorld(
                    modelRenderer,
                    materialParameters,
                    instance.Geometry,
                    submit,
                    worldConstantId,
                    instance.WorldConstant
                );
                instance.RecordSubmission("after", frame, context, view, subView);
                instance.MarkSubmitted(frame);
            }
            catch (Exception exception)
            {
                if (instance.MarkFailed(exception.Message))
                    log.Error(exception, "[Underpaint] Native rigid submission stopped.");
            }
        }
    }

    private static bool TryGetActiveView(out Matrix4x4 view)
    {
        view = default;
        var manager = Manager.Instance();
        var camera =
            manager == null
                ? null
                : manager->Views[MainRenderViewIndex].SubViews[MainTransformSubViewIndex].Camera;
        if (camera == null)
            return false;
        view = *(Matrix4x4*)&camera->ViewMatrix;
        // The native affine multiply reads only the 3x4 payload and supplies the
        // homogeneous column itself. These four storage slots are not initialized.
        view.M14 = 0;
        view.M24 = 0;
        view.M34 = 0;
        view.M44 = 1;
        return true;
    }

    private static uint GetConstantId(nint modelRenderer, int wellKnownIndex) =>
        modelRenderer == 0 ? uint.MaxValue : *(uint*)(modelRenderer + 8 + wellKnownIndex * 4);

    private static nint GetContextConstant(byte* context, uint id) =>
        context == null || id == uint.MaxValue ? 0 : *(nint*)(context + 0x940 + id * 8);

    private static void SetContextConstant(byte* context, uint id, nint value)
    {
        if (context != null && id != uint.MaxValue)
            *(nint*)(context + 0x940 + id * 8) = value;
    }

    private void SubmitOwnedWorld(
        nint modelRenderer,
        nint materialParameters,
        NativeGeometry geometry,
        NativePassBuilder submit,
        uint worldConstantId,
        ConstantBuffer* worldConstant
    )
    {
        var modelParams = *(nint*)materialParameters;
        if (modelParams == 0)
            throw new InvalidOperationException(
                "The native material parameters have no model input."
            );

        EnsureStandaloneMaterial();
        var targetMaterial = standaloneMaterialResource->Material;
        var targetShaderPackageResource = standaloneMaterialResource->ShaderPackageResourceHandle;
        var targetShaderPackage =
            targetShaderPackageResource == null ? null : targetShaderPackageResource->ShaderPackage;
        if (targetMaterial == null || targetShaderPackage == null)
            throw new InvalidOperationException("The owned native material is not ready.");
        EnsureStandaloneConstants();
        WriteDefaultObjectConstant(standaloneObjectConstant);
        if (worldConstant == null)
            throw new InvalidOperationException("The native instance has no world constant.");

        var ownedModel = stackalloc byte[0x180];
        var copiedModelParams = stackalloc byte[0x20];
        var copiedMaterialParams = stackalloc byte[0x48];
        var copiedShaderSelection = stackalloc byte[0x28];
        NativeMemory.Clear(ownedModel, 0x180);
        NativeMemory.Clear(copiedModelParams, 0x20);
        Buffer.MemoryCopy((void*)materialParameters, copiedMaterialParams, 0x48, 0x48);
        *(nint*)(copiedMaterialParams + 0x08) = 0;
        NativeMemory.Clear(copiedMaterialParams + 0x10, 0x28);
        *(uint*)(copiedMaterialParams + 0x3C) = 0;
        *(uint*)(copiedMaterialParams + 0x40) = 0;
        *(uint*)(copiedMaterialParams + 0x44) = SupportedAuxiliaryViewMask;
        NativeMemory.Clear(copiedShaderSelection, 0x28);
        var sourceShaderSelection = *(nint*)(materialParameters + 0x30);
        if (sourceShaderSelection == 0 || *(nint*)sourceShaderSelection == 0)
            throw new InvalidOperationException(
                "The render rendezvous has no shader-selection descriptor."
            );
        // The first field is the selection descriptor. Params2+0x30 points to the
        // source selection object, not to that descriptor directly.
        *(nint*)copiedShaderSelection = *(nint*)sourceShaderSelection;
        initializeShaderSelectionHook.Original(copiedShaderSelection, (nint)targetShaderPackage);
        try
        {
            if (
                *(nint*)(copiedShaderSelection + 0x08) == 0
                || *(nint*)(copiedShaderSelection + 0x10) == 0
            )
                throw new InvalidOperationException(
                    "The native shader-selection constructor failed."
                );
            *(nint*)copiedModelParams = (nint)ownedModel;
            *(nint*)(copiedModelParams + 0x10) = (nint)standaloneObjectConstant;
            *(nint*)copiedMaterialParams = (nint)copiedModelParams;
            *(nint*)(copiedMaterialParams + 0x30) = (nint)copiedShaderSelection;
            CopyCanonicalSceneKeys(modelRenderer, copiedShaderSelection, out _, out _);

            var context = ThreadLocals.ThreadLocalInstance()->GraphicsKernelContext;
            if (context == null)
                throw new InvalidOperationException(
                    "The current render thread has no graphics context."
                );
            var contextBytes = (byte*)context;
            using var contextState = new NativeContextStateScope(contextBytes, targetMaterial);
            ((ModelRenderer*)modelRenderer)->OnRenderMaterial(
                (ModelRenderer.OnRenderMaterialParams2*)copiedMaterialParams,
                targetMaterial,
                0
            );
            if (*(nint*)copiedShaderSelection == 0)
                throw new InvalidOperationException(
                    "The owned material did not select a native shader descriptor."
                );
            applyMaterialHook.Original(copiedShaderSelection, targetMaterial);
            *(uint*)(copiedShaderSelection + 0x20) = *(uint*)(modelRenderer + 0x1B0);
            *(uint*)(copiedShaderSelection + 0x24) = *(uint*)(modelRenderer + 0x1B4);
            var shaderDescriptor = resolveShaderSelectionHook.Original(
                (nint)targetShaderPackage,
                copiedShaderSelection
            );
            if (
                !TryGetActivePassShaders(
                    contextBytes,
                    shaderDescriptor,
                    out var vertexShader,
                    out var pixelShader
                )
            )
                throw new InvalidOperationException(
                    BuildShaderSelectionProbe(contextBytes, copiedShaderSelection, shaderDescriptor)
                );
            var ownedPassFlags = *(uint*)(copiedMaterialParams + 0x40);
            if (
                (ownedPassFlags & 0x201) != 0
                && !TryGetPassShaders(shaderDescriptor, 6, out _, out _)
            )
                throw new InvalidOperationException(
                    $"The owned material is incompatible with the ModelRenderer pass builder: "
                        + $"Flags=0x{ownedPassFlags:X8} require pass 6, but the selected shader descriptor does not provide it."
                );
            contextState.InstallShaders(vertexShader, pixelShader, shaderDescriptor);
            standaloneMaterialConstantId = FindBoundConstantId(
                contextBytes,
                targetMaterial->MaterialParameterCBuffer
            );
            if (standaloneMaterialConstantId == uint.MaxValue)
                throw new InvalidOperationException(
                    "The material helper did not bind the owned material constant."
                );
            SubmitCore(
                modelRenderer,
                (nint)copiedMaterialParams,
                geometry,
                submit,
                worldConstantId,
                worldConstant,
                contextState
            );
        }
        finally
        {
            destroyShaderSelectionHook.Original(copiedShaderSelection);
        }
    }

    private void EnsureStandaloneMaterial()
    {
        if (standaloneMaterialResource != null)
            return;
        var resourceManager = ResourceManager.Instance();
        if (resourceManager == null)
            throw new InvalidOperationException("The native resource manager is not available.");
        var category = ResourceCategory.BgCommon;
        var fileType = StandaloneMaterialFileType;
        var pathHash = StandaloneMaterialPathHash;
        var resource = (MaterialResourceHandle*)
            resourceManager->GetResourceSync(
                &category,
                &fileType,
                &pathHash,
                StandaloneMaterialPath,
                null,
                null,
                0
            );
        if (
            resource == null
            || resource->Material == null
            || resource->ShaderPackageResourceHandle == null
        )
            throw new InvalidOperationException(
                "The standalone native material could not be loaded."
            );
        standaloneMaterialResource = resource;
    }

    private static void CopyCanonicalSceneKeys(
        nint modelRenderer,
        byte* target,
        out int rendererKeyCount,
        out int subViewKeyCount
    )
    {
        var targetMetadata = *(nint*)(target + 0x08);
        var targetValues = *(uint**)(target + 0x10);
        var targetCount = *(uint*)(targetMetadata + 0xEC);
        var targetKeys = *(uint**)(targetMetadata + 0x130);
        if (targetCount > 256 || targetKeys == null || targetValues == null)
            throw new InvalidOperationException("The owned shader scene-key table is invalid.");

        rendererKeyCount = 0;
        subViewKeyCount = 0;
        var rendererKeys = (byte*)modelRenderer + 0x68;
        var subViewKeys = (byte*)modelRenderer + 0x1A8;
        for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
        {
            var targetKey = targetKeys[targetIndex];
            if (TryGetCanonicalKey(rendererKeys, 20, targetKey, out var value))
            {
                targetValues[targetIndex] = value;
                rendererKeyCount++;
            }
            else if (TryGetCanonicalKey(subViewKeys, 5, targetKey, out value))
            {
                targetValues[targetIndex] = value;
                subViewKeyCount++;
            }
        }
    }

    private static bool TryGetCanonicalKey(byte* keys, int count, uint targetKey, out uint value)
    {
        for (var index = 0; index < count; index++)
        {
            var entry = keys + index * 0x10;
            if (*(uint*)(entry + 0x08) != targetKey)
                continue;
            value = *(uint*)(entry + 0x0C);
            return true;
        }
        value = 0;
        return false;
    }

    private static uint FindBoundConstantId(byte* context, ConstantBuffer* constant)
    {
        if (context == null || constant == null)
            return uint.MaxValue;
        const uint constantSlotCount = (0x1140 - 0x940) / sizeof(ulong);
        for (uint id = 0; id < constantSlotCount; id++)
        {
            if (GetContextConstant(context, id) == (nint)constant)
                return id;
        }
        return uint.MaxValue;
    }

    private static string BuildShaderSelectionProbe(
        byte* context,
        byte* selection,
        nint descriptorAddress
    )
    {
        var package = *(nint*)(selection + 0x08);
        var sceneValues = *(nint*)(selection + 0x10);
        var materialValues = *(nint*)(selection + 0x18);
        var activePass = context == null ? -1 : context[0x0B] & 0x0F;
        if (descriptorAddress == 0)
        {
            return $"Native BG selection probe stopped before submission: Package=0x{package:X} "
                + $"SceneValues=0x{sceneValues:X} MaterialValues=0x{materialValues:X} "
                + $"SubView=0x{*(uint*)(selection + 0x20):X8}/0x{*(uint*)(selection + 0x24):X8} "
                + $"ActivePass={activePass} Descriptor=null.";
        }

        var descriptor = (byte*)descriptorAddress;
        var shaderTable = *(byte**)descriptor;
        if (shaderTable == null)
            return $"Native BG selection probe stopped before submission: Descriptor=0x{descriptorAddress:X} has no shader table.";

        var mappings = *(int**)(shaderTable + 0x170);
        var entries = new List<string>(16);
        for (var pass = 0; pass < 16; pass++)
        {
            var mappedPass = mappings == null ? pass : mappings[pass];
            if ((uint)mappedPass >= 16)
            {
                entries.Add($"{pass}:mapped-{mappedPass}");
                continue;
            }

            var slot = *(sbyte*)(descriptor + 0x08 + mappedPass);
            var slotCount = descriptor[0x20];
            if (slot < 0 || slot >= slotCount)
            {
                entries.Add($"{pass}:none");
                continue;
            }

            var entry = descriptor + 0x28 + slot * 8;
            var vertexIndex = *(ushort*)entry;
            var pixelIndex = *(ushort*)(entry + 2);
            var vertexStart = *(nint*)(shaderTable + 0x18);
            var vertexEnd = *(nint*)(shaderTable + 0x20);
            var pixelStart = *(nint*)(shaderTable + 0x38);
            var pixelEnd = *(nint*)(shaderTable + 0x40);
            var vertexCount =
                vertexStart != 0 && vertexEnd >= vertexStart
                    ? (vertexEnd - vertexStart) / sizeof(nint)
                    : 0;
            var pixelCount =
                pixelStart != 0 && pixelEnd >= pixelStart
                    ? (pixelEnd - pixelStart) / sizeof(nint)
                    : 0;
            var vertex = vertexIndex < vertexCount ? *(nint*)(vertexStart + vertexIndex * 8) : 0;
            var pixel = pixelIndex < pixelCount ? *(nint*)(pixelStart + pixelIndex * 8) : 0;
            entries.Add(
                $"{pass}:slot{slot}/VS{vertexIndex}=0x{vertex:X}/PS{pixelIndex}=0x{pixel:X}"
            );
        }

        return $"Native BG selection probe stopped before submission: Package=0x{package:X} "
            + $"Descriptor=0x{descriptorAddress:X} ActivePass={activePass} Passes=[{string.Join(',', entries)}].";
    }

    private static bool TryGetActivePassShaders(
        byte* context,
        nint descriptorAddress,
        out nint vertexShader,
        out nint pixelShader
    )
    {
        if (context == null || descriptorAddress == 0)
        {
            vertexShader = 0;
            pixelShader = 0;
            return false;
        }

        return TryGetPassShaders(
            descriptorAddress,
            context[0x0B] & 0x0F,
            out vertexShader,
            out pixelShader
        );
    }

    private static bool TryGetPassShaders(
        nint descriptorAddress,
        int pass,
        out nint vertexShader,
        out nint pixelShader
    )
    {
        vertexShader = 0;
        pixelShader = 0;
        if (descriptorAddress == 0 || (uint)pass >= 16)
            return false;

        var descriptor = (byte*)descriptorAddress;
        var shaderTable = *(byte**)descriptor;
        if (shaderTable == null)
            return false;

        var mappings = *(int**)(shaderTable + 0x170);
        var mappedPass = mappings == null ? pass : mappings[pass];
        if ((uint)mappedPass >= 16)
            return false;

        var slot = *(sbyte*)(descriptor + 0x08 + mappedPass);
        var slotCount = descriptor[0x20];
        if (slot < 0 || slot >= slotCount)
            return false;

        var entry = descriptor + 0x28 + slot * 8;
        var vertexIndex = *(ushort*)entry;
        var pixelIndex = *(ushort*)(entry + 2);
        var vertexStart = *(nint*)(shaderTable + 0x18);
        var vertexEnd = *(nint*)(shaderTable + 0x20);
        var pixelStart = *(nint*)(shaderTable + 0x38);
        var pixelEnd = *(nint*)(shaderTable + 0x40);
        var vertexCount =
            vertexStart != 0 && vertexEnd >= vertexStart
                ? (vertexEnd - vertexStart) / sizeof(nint)
                : 0;
        var pixelCount =
            pixelStart != 0 && pixelEnd >= pixelStart ? (pixelEnd - pixelStart) / sizeof(nint) : 0;
        if (vertexIndex >= vertexCount || pixelIndex >= pixelCount)
            return false;

        vertexShader = *(nint*)(vertexStart + vertexIndex * sizeof(nint));
        pixelShader = *(nint*)(pixelStart + pixelIndex * sizeof(nint));
        return vertexShader != 0 && pixelShader != 0;
    }

    private static nint[] SaveConstants(byte* context)
    {
        const int constantSlotCount = (0x1140 - 0x940) / sizeof(ulong);
        var values = new nint[constantSlotCount];
        for (uint id = 0; id < values.Length; id++)
            values[id] = GetContextConstant(context, id);
        return values;
    }

    private static void RestoreConstants(byte* context, nint[] values)
    {
        for (uint id = 0; id < values.Length; id++)
            SetContextConstant(context, id, values[id]);
    }

    private static MaterialTextureState[] SaveMaterialTextures(byte* context, Material* material)
    {
        var states = new List<MaterialTextureState>(material->TextureCount);
        for (var index = 0; index < material->TextureCount; index++)
        {
            var id = material->Textures[index].Id;
            if (id == uint.MaxValue)
                continue;
            var slot = context + 0x1140 + id * 24;
            states.Add(
                new MaterialTextureState(id, *(nint*)slot, *(nint*)(slot + 8), *(uint*)(slot + 16))
            );
        }
        return states.ToArray();
    }

    private static void RestoreMaterialTextures(byte* context, MaterialTextureState[] states)
    {
        foreach (var state in states)
        {
            var slot = context + 0x1140 + state.Id * 24;
            *(nint*)slot = state.Resource;
            *(nint*)(slot + 8) = state.Sampler;
            *(uint*)(slot + 16) = state.Flags;
        }
    }

    private void ReleaseStandaloneMaterial()
    {
        var resource = standaloneMaterialResource;
        standaloneMaterialResource = null;
        standaloneMaterialConstantId = uint.MaxValue;
        if (resource != null)
            resource->DecRef();
    }

    private void EnsureStandaloneConstants()
    {
        var device = Device.Instance();
        if (device == null)
            throw new InvalidOperationException("The native graphics device is not available.");

        if (standaloneObjectConstant == null)
            standaloneObjectConstant = device->CreateConstantBuffer(ModelObjectParameterSize, 2, 0);
        if (standaloneObjectConstant == null)
            throw new InvalidOperationException("The game rejected a standalone constant buffer.");
    }

    private static void WriteDefaultObjectConstant(ConstantBuffer* constant)
    {
        var target = constant->LoadSourcePointer(0, ModelObjectParameterSize);
        if (target == null)
            throw new InvalidOperationException(
                "The game did not expose instance-constant storage."
            );

        NativeMemory.Clear(target, ModelObjectParameterSize);
        var values = (Vector4*)target;
        values[0] = Vector4.One;
        values[1] = Vector4.One;
        values[2] = Vector4.One;
        values[3] = Vector4.One;
        values[4] = new Vector4(0, 2, 0, 1);
        values[10] = new Vector4(0, 1, 0, 0);
    }

    internal static void WriteWorldConstant(
        ConstantBuffer* constant,
        Matrix4x4 currentWorldView,
        Matrix4x4 previousWorldView
    )
    {
        var target = constant->LoadSourcePointer(0, 128);
        if (target == null)
            throw new InvalidOperationException("The game did not expose world-constant storage.");

        *(Matrix4x4*)target = Matrix4x4.Transpose(currentWorldView);
        *(Matrix4x4*)((byte*)target + 64) = Matrix4x4.Transpose(previousWorldView);
    }

    private static ulong PackStreamBinding(int byteOffset, int stride) =>
        ((ulong)(uint)byteOffset << 8) | (byte)stride;

    internal static void ReleaseNativeResource(ref nint resource)
    {
        var value = resource;
        resource = 0;
        if (value == 0)
            return;
        var release = (delegate* unmanaged<nint, void>)(*(nint*)(*(nint*)value + 0x18));
        release(value);
    }

    internal static void ReleaseNativeResource(ref ConstantBuffer* resource)
    {
        var value = (nint)resource;
        resource = null;
        ReleaseNativeResource(ref value);
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct NativeStream0Vertex(Vector3 position)
    {
        public readonly Half X = (Half)position.X;
        public readonly Half Y = (Half)position.Y;
        public readonly Half Z = (Half)position.Z;
        public readonly Half W = (Half)1;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct NativeStream1Vertex
    {
        public NativeStream1Vertex(Vector2 textureCoordinate)
        {
            NormalX = (Half)0;
            NormalY = (Half)0;
            NormalZ = (Half)1;
            NormalW = (Half)0;
            TextureX = (Half)textureCoordinate.X;
            TextureY = (Half)textureCoordinate.Y;
            TextureZ = (Half)0;
            TextureW = (Half)1;
        }

        public readonly Half NormalX;
        public readonly Half NormalY;
        public readonly Half NormalZ;
        public readonly Half NormalW;
        public readonly Half TextureX;
        public readonly Half TextureY;
        public readonly Half TextureZ;
        public readonly Half TextureW;
    }

    private sealed class NativeContextStateScope : IDisposable
    {
        private readonly byte* context;
        private readonly nint vertexShader;
        private readonly nint pixelShader;
        private readonly nint indexBuffer;
        private readonly nint vertexDeclaration;
        private readonly nint shaderSelection;
        private readonly ulong[] streams = new ulong[4];
        private readonly nint[] constants;
        private readonly MaterialTextureState[] textures;
        private readonly uint rasterizerState;
        private bool disposed;

        public NativeContextStateScope(byte* context, Material* material = null)
        {
            this.context = context;
            vertexShader = *(nint*)(context + 0x878);
            pixelShader = *(nint*)(context + 0x880);
            indexBuffer = *(nint*)(context + 0x888);
            vertexDeclaration = *(nint*)(context + 0x890);
            shaderSelection = *(nint*)(context + 0x8B8);
            for (var index = 0; index < streams.Length; index++)
                streams[index] = *(ulong*)(context + 0x8C0 + index * 8);
            constants = SaveConstants(context);
            textures = material == null ? [] : SaveMaterialTextures(context, material);
            rasterizerState = *(uint*)(context + 0x874);
        }

        public void InstallGeometry(NativeGeometry geometry)
        {
            *(nint*)(context + 0x888) = geometry.IndexBuffer;
            *(nint*)(context + 0x890) = geometry.VertexDeclaration;
            *(nint*)(context + 0x8C0) = geometry.VertexBuffer;
            *(ulong*)(context + 0x8C8) = PackStreamBinding(0, Stream0Stride);
            *(nint*)(context + 0x8D0) = geometry.VertexBuffer;
            *(ulong*)(context + 0x8D8) = PackStreamBinding(geometry.Stream1Offset, Stream1Stride);
        }

        public void InstallShaders(nint vertex, nint pixel, nint selection)
        {
            *(nint*)(context + 0x878) = vertex;
            *(nint*)(context + 0x880) = pixel;
            *(nint*)(context + 0x8B8) = selection;
        }

        public void SetConstant(uint id, ConstantBuffer* constant) =>
            SetContextConstant(context, id, (nint)constant);

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            *(uint*)(context + 0x874) = rasterizerState;
            *(nint*)(context + 0x878) = vertexShader;
            *(nint*)(context + 0x880) = pixelShader;
            *(nint*)(context + 0x888) = indexBuffer;
            *(nint*)(context + 0x890) = vertexDeclaration;
            *(nint*)(context + 0x8B8) = shaderSelection;
            for (var index = 0; index < streams.Length; index++)
                *(ulong*)(context + 0x8C0 + index * 8) = streams[index];
            RestoreConstants(context, constants);
            RestoreMaterialTextures(context, textures);
        }
    }

    private readonly record struct MaterialTextureState(
        uint Id,
        nint Resource,
        nint Sampler,
        uint Flags
    );

    private readonly record struct RendezvousIdentity(
        uint Frame,
        nint Context,
        int View,
        int SubView
    );
}

internal sealed unsafe class NativeRigidInstance : IDisposable
{
    private readonly object stateLock = new();
    private Matrix4x4 currentWorldView;
    private Matrix4x4 previousWorldView;
    private uint preparedFrame = uint.MaxValue;
    private bool resetHistory = true;
    private bool removed;
    private bool hasSubmitted;
    private long submissionCount;
    private string? failure;
    private List<NativeRigidSubmissionSnapshot>? submissionCapture;
    private int submissionCaptureLimit;

    internal NativeGeometrySubmissionBackend Owner { get; }
    internal NativeGeometry Geometry { get; }
    internal ConstantBuffer* WorldConstant { get; private set; }
    internal Matrix4x4? FixedWorld { get; }
    internal bool HasSubmitted
    {
        get
        {
            lock (stateLock)
                return hasSubmitted;
        }
    }
    internal long SubmissionCount
    {
        get
        {
            lock (stateLock)
                return submissionCount;
        }
    }
    internal string? Failure
    {
        get
        {
            lock (stateLock)
                return failure;
        }
    }

    internal NativeRigidInstance(
        NativeGeometrySubmissionBackend owner,
        NativeGeometry geometry,
        ConstantBuffer* worldConstant,
        Matrix4x4 currentWorldView,
        Matrix4x4? fixedWorld
    )
    {
        Owner = owner;
        Geometry = geometry;
        WorldConstant = worldConstant;
        FixedWorld = fixedWorld;
        this.currentWorldView = currentWorldView;
        previousWorldView = currentWorldView;
    }

    public void UpdateWorldView(Matrix4x4 worldView, bool resetTemporalHistory = false)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(removed, this);
            currentWorldView = worldView;
            resetHistory |= resetTemporalHistory;
        }
    }

    internal void BeginSubmissionCapture(int limit)
    {
        lock (stateLock)
        {
            submissionCapture = [];
            submissionCaptureLimit = Math.Max(0, limit);
        }
    }

    internal IReadOnlyList<NativeRigidSubmissionSnapshot> TakeSubmissionCapture()
    {
        lock (stateLock)
        {
            var result = submissionCapture?.ToArray() ?? [];
            submissionCapture = null;
            submissionCaptureLimit = 0;
            return result;
        }
    }

    internal void RecordSubmission(string phase, uint frame, nint context, int view, int subView)
    {
        lock (stateLock)
        {
            if (
                submissionCapture == null
                || submissionCapture.Count >= submissionCaptureLimit
                || WorldConstant == null
            )
                return;

            var source = (byte*)WorldConstant->UnsafeSourcePointer;
            submissionCapture.Add(
                new NativeRigidSubmissionSnapshot(
                    phase,
                    submissionCount + 1,
                    frame,
                    context,
                    view,
                    subView,
                    Environment.CurrentManagedThreadId,
                    (nint)WorldConstant,
                    (nint)source,
                    WorldConstant->Flags,
                    source == null ? null : HashBytes(source, 128),
                    source == null ? null : HashBytes(source, 64),
                    source == null ? null : HashBytes(source + 64, 64),
                    currentWorldView,
                    previousWorldView
                )
            );
        }
    }

    private static ulong HashBytes(byte* bytes, int length)
    {
        var hash = 14695981039346656037UL;
        for (var index = 0; index < length; index++)
        {
            hash ^= bytes[index];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    internal bool PrepareWorld(uint frame, Matrix4x4? renderWorldView)
    {
        lock (stateLock)
        {
            if (
                removed
                || failure != null
                || Geometry.IsDisposed
                || WorldConstant == null
                || preparedFrame == frame
            )
                return false;
            if (renderWorldView is { } value)
                currentWorldView = value;
            if (resetHistory)
                previousWorldView = currentWorldView;
            NativeGeometrySubmissionBackend.WriteWorldConstant(
                WorldConstant,
                currentWorldView,
                previousWorldView
            );
            preparedFrame = frame;
            return true;
        }
    }

    internal bool MarkFailed(string message)
    {
        lock (stateLock)
        {
            if (failure != null)
                return false;
            failure = message;
            return true;
        }
    }

    internal void MarkSubmitted(uint frame)
    {
        lock (stateLock)
        {
            if (preparedFrame != frame)
                return;
            previousWorldView = currentWorldView;
            resetHistory = false;
            hasSubmitted = true;
            submissionCount++;
        }
    }

    internal void MarkRemoved()
    {
        lock (stateLock)
            removed = true;
    }

    internal void DisposeCore()
    {
        MarkRemoved();
        var constant = WorldConstant;
        WorldConstant = null;
        NativeGeometrySubmissionBackend.ReleaseNativeResource(ref constant);
    }

    public void Dispose() => Owner.Remove(this);
}

internal sealed unsafe class NativeGeometry : IDisposable
{
    internal NativeGeometrySubmissionBackend Owner { get; }
    internal nint VertexBuffer;
    internal nint IndexBuffer;
    internal nint VertexDeclaration;
    internal int VertexCount { get; }
    internal int IndexCount { get; }
    internal int Stream1Offset { get; }
    internal nint VertexBufferResource => VertexBuffer == 0 ? 0 : *(nint*)(VertexBuffer + 0x40);
    internal nint IndexBufferResource => IndexBuffer == 0 ? 0 : *(nint*)(IndexBuffer + 0x48);
    internal bool IsDisposed => VertexBuffer == 0;

    internal NativeGeometry(
        NativeGeometrySubmissionBackend owner,
        nint vertexBuffer,
        nint indexBuffer,
        nint vertexDeclaration,
        int vertexCount,
        int indexCount,
        int stream1Offset
    )
    {
        Owner = owner;
        VertexBuffer = vertexBuffer;
        IndexBuffer = indexBuffer;
        VertexDeclaration = vertexDeclaration;
        VertexCount = vertexCount;
        IndexCount = indexCount;
        Stream1Offset = stream1Offset;
    }

    public void Dispose() => Owner.Release(this);

    internal void DisposeCore()
    {
        NativeGeometrySubmissionBackend.ReleaseNativeResource(ref VertexDeclaration);
        NativeGeometrySubmissionBackend.ReleaseNativeResource(ref IndexBuffer);
        NativeGeometrySubmissionBackend.ReleaseNativeResource(ref VertexBuffer);
    }
}
