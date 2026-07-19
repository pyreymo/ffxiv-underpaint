using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal delegate nint NativePassBuilder(
    nint modelRenderer,
    nint materialParameters,
    int vertexCount,
    int startIndex,
    int indexCount
);

internal readonly record struct NativeGeometrySubmissionResult(
    nint BuilderResult,
    nint Context,
    nint VertexBuffer,
    nint VertexBufferResource,
    nint IndexBuffer,
    nint IndexBufferResource,
    nint VertexDeclaration,
    int VertexCount,
    int IndexCount
);

internal readonly record struct NativeGeometryStandaloneSubmission(
    bool Succeeded,
    string? Failure,
    nint ModelRenderer,
    nint MaterialParameters,
    nint Model,
    int View,
    int SubView,
    nint SourceVertexBuffer,
    nint SourceIndexBuffer,
    nint SourceVertexDeclaration,
    int SourceStream0Stride,
    int SourceStream1Stride,
    int SourceVertexCount,
    int SourceStartIndex,
    int SourceIndexCount,
    nint ModelParams,
    nint RenderModelCallback,
    nint RenderModelCallbackFunction,
    nint ModelField38,
    NativeConstantBufferProbe OnRenderModelConstant,
    uint WorldConstantId,
    NativeConstantBufferProbe WorldConstant,
    uint InstancingConstantId,
    NativeConstantBufferProbe InstancingConstant,
    uint PreviousInstancingConstantId,
    NativeConstantBufferProbe PreviousInstancingConstant,
    NativeConstantBufferProbe OffsetModelConstant,
    NativeConstantBufferProbe OffsetWorldConstant,
    NativeConstantBufferProbe OffsetInstancingConstant,
    NativeConstantBufferProbe OffsetPreviousInstancingConstant,
    nint ShaderSelection,
    nint OffsetShaderSelection,
    uint ModelTypeSceneKey,
    uint DonorModelTypeValue,
    uint OffsetModelTypeValue,
    NativeGeometrySubmissionResult Submission
);

internal readonly record struct NativeConstantBufferProbe(
    nint Buffer,
    int ByteSize,
    nint SourcePointer,
    ulong ContentHash,
    Vector4 Row0,
    Vector4 Row1,
    Vector4 Row2
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
    private const uint ImmutableBufferFlags = 0x804;
    private const int Stream0Stride = 20;
    private const int Stream1Stride = 24;
    private const float StandaloneViewDepth = 5.0f;

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
    private readonly Hook<NativePassBuilder> expandPassesHook;
    private readonly IPluginLog log;
    private readonly HashSet<NativeGeometry> geometries = [];
    private NativeGeometry? armedStandaloneGeometry;
    private NativeGeometryStandaloneSubmission? completedStandaloneSubmission;
    private ConstantBuffer* standaloneModelConstant;
    private ConstantBuffer* standaloneWorldConstant;
    private bool disposed;

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
        expandPassesHook = gameInteropProvider.HookFromSignature<NativePassBuilder>(
            ExpandPassesSignature,
            ExpandPassesDetour
        );
        expandPassesHook.Enable();
    }

    public void ArmStandalone(NativeGeometry geometry)
    {
        lock (stateLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (geometry.Owner != this || geometry.IsDisposed)
                throw new ObjectDisposedException(nameof(geometry));
            armedStandaloneGeometry = geometry;
            completedStandaloneSubmission = null;
        }
    }

    public void CancelStandalone()
    {
        lock (stateLock)
            armedStandaloneGeometry = null;
    }

    public bool TryTakeStandalone(out NativeGeometryStandaloneSubmission submission)
    {
        lock (stateLock)
        {
            if (completedStandaloneSubmission is not { } completed)
            {
                submission = default;
                return false;
            }

            completedStandaloneSubmission = null;
            submission = completed;
            return true;
        }
    }

    public NativeGeometry CreateGeometry(
        ReadOnlySpan<Vector3> positions,
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
            var geometry = CreateGeometryCore(positions, indices);
            geometries.Add(geometry);
            return geometry;
        }
    }

    public NativeGeometrySubmissionResult Submit(
        nint modelRenderer,
        nint materialParameters,
        NativeGeometry geometry,
        NativePassBuilder submit
    )
    {
        ArgumentNullException.ThrowIfNull(submit);
        if (modelRenderer == 0)
            throw new ArgumentNullException(nameof(modelRenderer));
        if (materialParameters == 0)
            throw new ArgumentNullException(nameof(materialParameters));
        if (geometry.Owner != this || geometry.IsDisposed)
            throw new ObjectDisposedException(nameof(geometry));
        if (submitting)
            throw new InvalidOperationException("Native geometry submission is not reentrant.");

        return SubmitCore(modelRenderer, materialParameters, geometry, submit, uint.MaxValue, null);
    }

    private NativeGeometrySubmissionResult SubmitCore(
        nint modelRenderer,
        nint materialParameters,
        NativeGeometry geometry,
        NativePassBuilder submit,
        uint constantId,
        ConstantBuffer* constant
    )
    {
        var threadLocals = ThreadLocals.ThreadLocalInstance();
        var context = threadLocals == null ? null : threadLocals->GraphicsKernelContext;
        if (context == null)
            throw new InvalidOperationException(
                "The current render thread has no graphics context."
            );

        var contextBytes = (byte*)context;
        var savedIndexBuffer = *(nint*)(contextBytes + 0x888);
        var savedVertexDeclaration = *(nint*)(contextBytes + 0x890);
        Span<ulong> savedStreams = stackalloc ulong[4];
        for (var index = 0; index < savedStreams.Length; index++)
            savedStreams[index] = *(ulong*)(contextBytes + 0x8C0 + index * 8);
        var savedConstant = GetContextConstant(contextBytes, constantId);

        nint builderResult;
        submitting = true;
        try
        {
            *(nint*)(contextBytes + 0x888) = geometry.IndexBuffer;
            *(nint*)(contextBytes + 0x890) = geometry.VertexDeclaration;
            *(nint*)(contextBytes + 0x8C0) = geometry.VertexBuffer;
            *(ulong*)(contextBytes + 0x8C8) = PackStreamBinding(0, Stream0Stride);
            *(nint*)(contextBytes + 0x8D0) = geometry.VertexBuffer;
            *(ulong*)(contextBytes + 0x8D8) = PackStreamBinding(
                geometry.Stream1Offset,
                Stream1Stride
            );
            SetContextConstant(contextBytes, constantId, (nint)constant);
            builderResult = submit(
                modelRenderer,
                materialParameters,
                geometry.VertexCount,
                0,
                geometry.IndexCount
            );
        }
        finally
        {
            *(nint*)(contextBytes + 0x888) = savedIndexBuffer;
            *(nint*)(contextBytes + 0x890) = savedVertexDeclaration;
            for (var index = 0; index < savedStreams.Length; index++)
                *(ulong*)(contextBytes + 0x8C0 + index * 8) = savedStreams[index];
            SetContextConstant(contextBytes, constantId, savedConstant);
            submitting = false;
        }

        return new NativeGeometrySubmissionResult(
            builderResult,
            (nint)context,
            geometry.VertexBuffer,
            *(nint*)(geometry.VertexBuffer + 0x40),
            geometry.IndexBuffer,
            *(nint*)(geometry.IndexBuffer + 0x48),
            geometry.VertexDeclaration,
            geometry.VertexCount,
            geometry.IndexCount
        );
    }

    public void Dispose()
    {
        lock (stateLock)
        {
            if (disposed)
                return;
            disposed = true;
            armedStandaloneGeometry = null;
            completedStandaloneSubmission = null;
        }

        expandPassesHook.Disable();
        ReleaseNativeResource(ref standaloneWorldConstant);
        ReleaseNativeResource(ref standaloneModelConstant);
        lock (stateLock)
        {
            foreach (var geometry in geometries.ToArray())
                geometry.DisposeCore();
            geometries.Clear();
        }

        createVertexDeclarationHook.Dispose();
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

    private NativeGeometry CreateGeometryCore(
        ReadOnlySpan<Vector3> positions,
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
                stream1[index] = NativeStream1Vertex.Default;
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
        var threadLocals = ThreadLocals.ThreadLocalInstance();
        var context = threadLocals == null ? null : threadLocals->GraphicsKernelContext;
        var contextBytes = (byte*)context;
        var view = context == null ? -1 : context->ViewIndex;
        var subView = context == null ? -1 : context->CurrentSubViewIndex;
        var sourceIndexBuffer = context == null ? 0 : *(nint*)(contextBytes + 0x888);
        var sourceVertexDeclaration = context == null ? 0 : *(nint*)(contextBytes + 0x890);
        var sourceVertexBuffer = context == null ? 0 : *(nint*)(contextBytes + 0x8C0);
        var sourceStream0Stride = context == null ? 0 : *(byte*)(contextBytes + 0x8C8);
        var sourceStream1Stride = context == null ? 0 : *(byte*)(contextBytes + 0x8D8);
        var modelParams = materialParameters == 0 ? 0 : *(nint*)materialParameters;
        var model = modelParams == 0 ? 0 : *(nint*)modelParams;
        var renderModelCallback = model == 0 ? 0 : *(nint*)(model + 0x48);
        var renderModelCallbackFunction =
            renderModelCallback == 0 ? 0 : *(nint*)renderModelCallback;
        var modelField38 = model == 0 ? 0 : *(nint*)(model + 0x38);
        var onRenderModelConstant = ProbeConstantBuffer(
            modelParams == 0 ? 0 : *(nint*)(modelParams + 0x10)
        );
        var worldConstantId = GetConstantId(modelRenderer, 1);
        var instancingConstantId = GetConstantId(modelRenderer, 11);
        var previousInstancingConstantId = GetConstantId(modelRenderer, 12);
        var worldConstant = ProbeConstantBuffer(GetContextConstant(contextBytes, worldConstantId));
        var instancingConstant = ProbeConstantBuffer(
            GetContextConstant(contextBytes, instancingConstantId)
        );
        var previousInstancingConstant = ProbeConstantBuffer(
            GetContextConstant(contextBytes, previousInstancingConstantId)
        );
        var result = expandPassesHook.Original(
            modelRenderer,
            materialParameters,
            vertexCount,
            startIndex,
            indexCount
        );
        if (submitting || materialParameters == 0)
            return result;

        if (
            context == null
            || view != 30
            || subView != 11
            || sourceStream0Stride != Stream0Stride
            || sourceStream1Stride != Stream1Stride
        )
            return result;

        NativeGeometry? geometry;
        lock (stateLock)
        {
            geometry = armedStandaloneGeometry;
            armedStandaloneGeometry = null;
        }
        if (geometry == null)
            return result;

        try
        {
            var submission = SubmitStandaloneWorld(
                modelRenderer,
                materialParameters,
                geometry,
                expandPassesHook.Original,
                worldConstantId,
                out var shaderSelection,
                out var offsetShaderSelection,
                out var modelTypeSceneKey,
                out var donorModelTypeValue,
                out var offsetModelTypeValue
            );
            var offsetModelConstant = ProbeConstantBuffer((nint)standaloneModelConstant);
            var offsetWorldConstant = ProbeConstantBuffer((nint)standaloneWorldConstant);
            lock (stateLock)
            {
                completedStandaloneSubmission = new NativeGeometryStandaloneSubmission(
                    true,
                    null,
                    modelRenderer,
                    materialParameters,
                    model,
                    view,
                    subView,
                    sourceVertexBuffer,
                    sourceIndexBuffer,
                    sourceVertexDeclaration,
                    sourceStream0Stride,
                    sourceStream1Stride,
                    vertexCount,
                    startIndex,
                    indexCount,
                    modelParams,
                    renderModelCallback,
                    renderModelCallbackFunction,
                    modelField38,
                    onRenderModelConstant,
                    worldConstantId,
                    worldConstant,
                    instancingConstantId,
                    instancingConstant,
                    previousInstancingConstantId,
                    previousInstancingConstant,
                    offsetModelConstant,
                    offsetWorldConstant,
                    default,
                    default,
                    shaderSelection,
                    offsetShaderSelection,
                    modelTypeSceneKey,
                    donorModelTypeValue,
                    offsetModelTypeValue,
                    submission
                );
            }
        }
        catch (Exception exception)
        {
            log.Error(exception, "[Underpaint] Standalone native geometry submission failed.");
            lock (stateLock)
            {
                completedStandaloneSubmission = new NativeGeometryStandaloneSubmission(
                    false,
                    exception.Message,
                    modelRenderer,
                    materialParameters,
                    model,
                    view,
                    subView,
                    sourceVertexBuffer,
                    sourceIndexBuffer,
                    sourceVertexDeclaration,
                    sourceStream0Stride,
                    sourceStream1Stride,
                    vertexCount,
                    startIndex,
                    indexCount,
                    modelParams,
                    renderModelCallback,
                    renderModelCallbackFunction,
                    modelField38,
                    onRenderModelConstant,
                    worldConstantId,
                    worldConstant,
                    instancingConstantId,
                    instancingConstant,
                    previousInstancingConstantId,
                    previousInstancingConstant,
                    default,
                    default,
                    default,
                    default,
                    default,
                    default,
                    default,
                    default,
                    default,
                    default
                );
            }
        }

        return result;
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

    private NativeGeometrySubmissionResult SubmitStandaloneWorld(
        nint modelRenderer,
        nint materialParameters,
        NativeGeometry geometry,
        NativePassBuilder submit,
        uint worldConstantId,
        out nint shaderSelection,
        out nint offsetShaderSelection,
        out uint modelTypeSceneKey,
        out uint donorModelTypeValue,
        out uint offsetModelTypeValue
    )
    {
        var modelParams = *(nint*)materialParameters;
        if (modelParams == 0)
            throw new InvalidOperationException(
                "The native material parameters have no model input."
            );

        var modelConstant = ProbeConstantBuffer(*(nint*)(modelParams + 0x10));
        if (modelConstant.ByteSize != 176 || modelConstant.SourcePointer == 0)
            throw new InvalidOperationException(
                "The native model constant is not a readable 176-byte input."
            );

        shaderSelection = *(nint*)(materialParameters + 0x30);
        if (shaderSelection == 0)
            throw new InvalidOperationException(
                "The native material parameters have no shader selection."
            );
        var shaderMetadata = *(nint*)(shaderSelection + 0x08);
        var shaderValues = *(nint*)(shaderSelection + 0x10);
        if (shaderMetadata == 0 || shaderValues == 0)
            throw new InvalidOperationException("The shader selection is incomplete.");
        var keyCount = *(uint*)(shaderMetadata + 0xEC);
        var shaderKeys = *(nint*)(shaderMetadata + 0x130);
        if (keyCount is 0 or > 256 || shaderKeys == 0)
            throw new InvalidOperationException("The shader selection key table is invalid.");

        var nonSkinnedSceneKey = (byte*)modelRenderer + 0x68;
        var skinnedSceneKey = nonSkinnedSceneKey + 0x10;
        modelTypeSceneKey = *(uint*)(nonSkinnedSceneKey + 0x08);
        offsetModelTypeValue = *(uint*)(nonSkinnedSceneKey + 0x0C);
        if (*(uint*)(skinnedSceneKey + 0x08) != modelTypeSceneKey)
            throw new InvalidOperationException(
                "The model-type scene keys do not share a key CRC."
            );

        var modelTypeKeyIndex = -1;
        for (var index = 0; index < keyCount; index++)
        {
            if (*(uint*)(shaderKeys + index * sizeof(uint)) == modelTypeSceneKey)
            {
                modelTypeKeyIndex = (int)index;
                break;
            }
        }
        if (modelTypeKeyIndex < 0)
            throw new InvalidOperationException(
                "The active shader does not expose the model-type scene key."
            );

        donorModelTypeValue = *(uint*)(shaderValues + modelTypeKeyIndex * sizeof(uint));
        var expectedSkinnedValue = *(uint*)(skinnedSceneKey + 0x0C);
        if (donorModelTypeValue != expectedSkinnedValue)
            throw new InvalidOperationException(
                "The compatible donor is not using the skinned shader variant."
            );

        EnsureStandaloneConstants();
        CopyConstant(modelConstant, standaloneModelConstant);
        WriteStandaloneWorldConstant();

        var copiedModelParams = stackalloc byte[0x20];
        var copiedMaterialParams = stackalloc byte[0x48];
        var copiedShaderSelection = stackalloc byte[0x28];
        var copiedShaderValues = stackalloc uint[(int)keyCount];
        Buffer.MemoryCopy((void*)modelParams, copiedModelParams, 0x20, 0x20);
        Buffer.MemoryCopy((void*)materialParameters, copiedMaterialParams, 0x48, 0x48);
        Buffer.MemoryCopy((void*)shaderSelection, copiedShaderSelection, 0x28, 0x28);
        Buffer.MemoryCopy(
            (void*)shaderValues,
            copiedShaderValues,
            keyCount * sizeof(uint),
            keyCount * sizeof(uint)
        );
        *(nint*)(copiedModelParams + 0x10) = (nint)standaloneModelConstant;
        *(nint*)copiedMaterialParams = (nint)copiedModelParams;
        copiedShaderValues[modelTypeKeyIndex] = offsetModelTypeValue;
        *(nint*)(copiedShaderSelection + 0x10) = (nint)copiedShaderValues;
        *(nint*)(copiedMaterialParams + 0x30) = (nint)copiedShaderSelection;
        offsetShaderSelection = (nint)copiedShaderSelection;

        return SubmitCore(
            modelRenderer,
            (nint)copiedMaterialParams,
            geometry,
            submit,
            worldConstantId,
            standaloneWorldConstant
        );
    }

    private void EnsureStandaloneConstants()
    {
        var device = Device.Instance();
        if (device == null)
            throw new InvalidOperationException("The native graphics device is not available.");

        if (standaloneModelConstant == null)
            standaloneModelConstant = device->CreateConstantBuffer(176, 2, 0);
        if (standaloneWorldConstant == null)
            standaloneWorldConstant = device->CreateConstantBuffer(128, 2, 0);
        if (standaloneModelConstant == null || standaloneWorldConstant == null)
            throw new InvalidOperationException("The game rejected a standalone constant buffer.");
    }

    private void WriteStandaloneWorldConstant()
    {
        var target = standaloneWorldConstant->LoadSourcePointer(0, 128);
        if (target == null)
            throw new InvalidOperationException("The game did not expose world-constant storage.");

        var worldView = Matrix4x4.Transpose(Matrix4x4.CreateTranslation(0, 0, StandaloneViewDepth));
        *(Matrix4x4*)target = worldView;
        *(Matrix4x4*)((byte*)target + 64) = worldView;
    }

    private static void CopyConstant(NativeConstantBufferProbe source, ConstantBuffer* destination)
    {
        var target = destination->LoadSourcePointer(0, source.ByteSize);
        if (target == null)
            throw new InvalidOperationException("The game did not expose constant-buffer storage.");
        Buffer.MemoryCopy((void*)source.SourcePointer, target, source.ByteSize, source.ByteSize);
    }

    private static NativeConstantBufferProbe ProbeConstantBuffer(nint buffer)
    {
        if (buffer == 0)
            return default;
        var byteSize = *(int*)(buffer + 0x20);
        var sourcePointer = *(nint*)(buffer + 0x28);
        var hash = 14695981039346656037UL;
        if (sourcePointer != 0 && byteSize is > 0 and <= 4096)
        {
            var bytes = (byte*)sourcePointer;
            for (var index = 0; index < Math.Min(byteSize, 512); index++)
            {
                hash ^= bytes[index];
                hash *= 1099511628211UL;
            }
        }
        else
        {
            hash = 0;
        }
        var row0 = default(Vector4);
        var row1 = default(Vector4);
        var row2 = default(Vector4);
        if (sourcePointer != 0 && byteSize >= 48)
        {
            row0 = *(Vector4*)sourcePointer;
            row1 = *(Vector4*)(sourcePointer + 16);
            row2 = *(Vector4*)(sourcePointer + 32);
        }
        return new NativeConstantBufferProbe(
            buffer,
            byteSize,
            sourcePointer,
            hash,
            row0,
            row1,
            row2
        );
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

    private static void ReleaseNativeResource(ref ConstantBuffer* resource)
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
        public static readonly NativeStream1Vertex Default = new(
            0x3C003C0000000000,
            uint.MaxValue,
            uint.MaxValue,
            0
        );

        private NativeStream1Vertex(
            ulong attribute2,
            uint attribute15,
            uint attribute3,
            ulong attribute8
        )
        {
            Attribute2 = attribute2;
            Attribute15 = attribute15;
            Attribute3 = attribute3;
            Attribute8 = attribute8;
        }

        public readonly ulong Attribute2;
        public readonly uint Attribute15;
        public readonly uint Attribute3;
        public readonly ulong Attribute8;
    }
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
