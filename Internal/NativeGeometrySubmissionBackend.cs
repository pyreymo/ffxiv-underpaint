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
    private const uint ImmutableBufferFlags = 0x804;
    private const int Stream0Stride = 20;
    private const int Stream1Stride = 24;
    private const string StandaloneMaterialPath =
        "chara/equipment/e0378/material/v0002/mt_c0101e0378_top_a.mtrl";
    private const uint StandaloneMaterialFileType = 0x6D74726C;
    private const uint StandaloneMaterialPathHash = 0x56D3AB97;
    private const uint InstanceParameterCrc = 0x20A30B34;
    private const int InstanceParameterSize = 176;
    private const uint SupportedMainPassMask = 0x01000000;
    private const uint SupportedAuxiliaryPassMask = 0x00C00000;
    private const uint SupportedAuxiliaryViewMask = 0x00000003;
    private const int MainRenderViewIndex = 30;
    private const int MainRendezvousSubViewIndex = 11;
    private const int MainTransformSubViewIndex = 12;

    private static readonly byte[] VertexDeclarationElements =
    [
        0,
        0,
        0x13,
        0,
        0,
        12,
        0x3C,
        1,
        0,
        16,
        0x3C,
        7,
        1,
        0,
        0x1C,
        2,
        1,
        8,
        0x24,
        15,
        1,
        12,
        0x24,
        3,
        1,
        16,
        0x1C,
        8,
    ];

    [ThreadStatic]
    private static bool submitting;

    private readonly object stateLock = new();
    private readonly Hook<CreateVertexBufferDelegate> createVertexBufferHook;
    private readonly Hook<InitializeBufferDelegate> initializeVertexBufferHook;
    private readonly Hook<CreateIndexBufferDelegate> createIndexBufferHook;
    private readonly Hook<InitializeBufferDelegate> initializeIndexBufferHook;
    private readonly Hook<CreateVertexDeclarationDelegate> createVertexDeclarationHook;
    private readonly Hook<InitializeShaderSelectionDelegate> initializeShaderSelectionHook;
    private readonly Hook<DestroyShaderSelectionDelegate> destroyShaderSelectionHook;
    private readonly Hook<ApplyMaterialDelegate> applyMaterialHook;
    private readonly Hook<NativePassBuilder> expandPassesHook;
    private readonly IPluginLog log;
    private readonly HashSet<NativeGeometry> geometries = [];
    private readonly HashSet<NativeRigidInstance> rigidInstances = [];
    private readonly List<NativeRigidInstance> retiredRigidInstances = [];
    private ConstantBuffer* standaloneInstanceConstant;
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
        expandPassesHook = gameInteropProvider.HookFromSignature<NativePassBuilder>(
            ExpandPassesSignature,
            ExpandPassesDetour
        );
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
        try
        {
            state.InstallGeometry(geometry);
            state.SetConstant(constantId, constant);
            submit(modelRenderer, materialParameters, geometry.VertexCount, 0, geometry.IndexCount);
        }
        finally
        {
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
        ReleaseNativeResource(ref standaloneInstanceConstant);
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
        var contextBytes = (byte*)context;
        var view = context == null ? -1 : context->ViewIndex;
        var subView = context == null ? -1 : context->CurrentSubViewIndex;
        var sourceStream0Stride = context == null ? 0 : *(byte*)(contextBytes + 0x8C8);
        var modelParams = *(nint*)materialParameters;
        var sourceInstanceConstant =
            modelParams == 0 ? null : *(ConstantBuffer**)(modelParams + 0x10);
        if (
            context == null
            || view != MainRenderViewIndex
            || subView != MainRendezvousSubViewIndex
            || sourceInstanceConstant == null
            || *(int*)((byte*)sourceInstanceConstant + 0x20) != InstanceParameterSize
            || sourceStream0Stride != Stream0Stride
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

        var nonSkinnedSceneKey = (byte*)modelRenderer + 0x68;
        var skinnedSceneKey = nonSkinnedSceneKey + 0x10;
        var modelTypeSceneKey = *(uint*)(nonSkinnedSceneKey + 0x08);
        var modelTypeValue = *(uint*)(nonSkinnedSceneKey + 0x0C);
        if (*(uint*)(skinnedSceneKey + 0x08) != modelTypeSceneKey)
            throw new InvalidOperationException(
                "The model-type scene keys do not share a key CRC."
            );

        EnsureStandaloneMaterial();
        var targetMaterial = standaloneMaterialResource->Material;
        var targetShaderPackageResource = standaloneMaterialResource->ShaderPackageResourceHandle;
        var targetShaderPackage =
            targetShaderPackageResource == null ? null : targetShaderPackageResource->ShaderPackage;
        if (targetMaterial == null || targetShaderPackage == null)
            throw new InvalidOperationException("The owned native material is not ready.");
        var instanceConstantId = FindShaderConstantId(
            targetShaderPackage,
            InstanceParameterCrc,
            InstanceParameterSize
        );

        EnsureStandaloneConstants();
        WriteDefaultInstanceConstant(standaloneInstanceConstant);
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
        *(uint*)(copiedMaterialParams + 0x38) = SupportedMainPassMask | SupportedAuxiliaryPassMask;
        *(uint*)(copiedMaterialParams + 0x3C) = 0;
        *(uint*)(copiedMaterialParams + 0x40) = 0;
        *(uint*)(copiedMaterialParams + 0x44) = SupportedAuxiliaryViewMask;
        NativeMemory.Clear(copiedShaderSelection, 0x28);
        *(nint*)copiedShaderSelection = *(nint*)(materialParameters + 0x30);
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
            *(nint*)(copiedModelParams + 0x10) = (nint)standaloneInstanceConstant;
            *(nint*)copiedMaterialParams = (nint)copiedModelParams;
            *(nint*)(copiedMaterialParams + 0x30) = (nint)copiedShaderSelection;
            CopyCanonicalSceneKeys(modelRenderer, copiedShaderSelection, out _, out _);
            SetSceneKey(copiedShaderSelection, modelTypeSceneKey, modelTypeValue);

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
            applyMaterialHook.Original(copiedShaderSelection, targetMaterial);
            standaloneMaterialConstantId = FindBoundConstantId(
                contextBytes,
                targetMaterial->MaterialParameterCBuffer
            );
            if (standaloneMaterialConstantId == uint.MaxValue)
                throw new InvalidOperationException(
                    "The material helper did not bind the owned material constant."
                );
            contextState.SetConstant(instanceConstantId, standaloneInstanceConstant);
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
        var category = ResourceCategory.Chara;
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

    private static void SetSceneKey(byte* selection, uint key, uint value)
    {
        var metadata = *(nint*)(selection + 0x08);
        var values = *(uint**)(selection + 0x10);
        var count = *(uint*)(metadata + 0xEC);
        var keys = *(uint**)(metadata + 0x130);
        for (var index = 0; index < count; index++)
        {
            if (keys[index] != key)
                continue;
            values[index] = value;
            return;
        }
        throw new InvalidOperationException("The owned material has no model-type scene key.");
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

    private static uint FindShaderConstantId(ShaderPackage* shaderPackage, uint crc, int byteSize)
    {
        if (shaderPackage == null || shaderPackage->Constants == null)
            throw new InvalidOperationException("The owned shader constant table is unavailable.");
        for (var index = 0; index < shaderPackage->ConstantCount; index++)
        {
            var constant = shaderPackage->Constants[index];
            if (constant.CRC != crc)
                continue;
            if (constant.Size * 16 != byteSize)
                throw new InvalidOperationException(
                    "The owned shader has an unexpected instance-constant size."
                );
            return constant.Id;
        }
        throw new InvalidOperationException("The owned shader has no instance-parameter constant.");
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

        if (standaloneInstanceConstant == null)
            standaloneInstanceConstant = device->CreateConstantBuffer(InstanceParameterSize, 2, 0);
        if (standaloneInstanceConstant == null)
            throw new InvalidOperationException("The game rejected a standalone constant buffer.");
    }

    private static void WriteDefaultInstanceConstant(ConstantBuffer* constant)
    {
        var target = constant->LoadSourcePointer(0, InstanceParameterSize);
        if (target == null)
            throw new InvalidOperationException(
                "The game did not expose instance-constant storage."
            );

        NativeMemory.Clear(target, InstanceParameterSize);
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
        public readonly Vector3 Position = position;
        public readonly uint Attribute1 = uint.MaxValue;
        public readonly uint Attribute7 = 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct NativeStream1Vertex
    {
        public NativeStream1Vertex(Vector2 textureCoordinate)
        {
            Attribute2 = 0x3C003C0000000000;
            Attribute15 = uint.MaxValue;
            Attribute3 = uint.MaxValue;
            Attribute8 =
                BitConverter.HalfToUInt16Bits((Half)textureCoordinate.X)
                | ((ulong)BitConverter.HalfToUInt16Bits((Half)textureCoordinate.Y) << 16)
                | (0x3C00UL << 48);
        }

        public readonly ulong Attribute2;
        public readonly uint Attribute15;
        public readonly uint Attribute3;
        public readonly ulong Attribute8;
    }

    private sealed class NativeContextStateScope : IDisposable
    {
        private readonly byte* context;
        private readonly nint indexBuffer;
        private readonly nint vertexDeclaration;
        private readonly ulong[] streams = new ulong[4];
        private readonly nint[] constants;
        private readonly MaterialTextureState[] textures;
        private readonly uint rasterizerState;
        private bool disposed;

        public NativeContextStateScope(byte* context, Material* material = null)
        {
            this.context = context;
            indexBuffer = *(nint*)(context + 0x888);
            vertexDeclaration = *(nint*)(context + 0x890);
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

        public void SetConstant(uint id, ConstantBuffer* constant) =>
            SetContextConstant(context, id, (nint)constant);

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            *(uint*)(context + 0x874) = rasterizerState;
            *(nint*)(context + 0x888) = indexBuffer;
            *(nint*)(context + 0x890) = vertexDeclaration;
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
