#if !UNDERPAINT_DISABLE_AVFX
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Scene;
using FfxivQuaternion = FFXIVClientStructs.FFXIV.Common.Math.Quaternion;
using FfxivVector3 = FFXIVClientStructs.FFXIV.Common.Math.Vector3;
using FfxivVector4 = FFXIVClientStructs.FFXIV.Common.Math.Vector4;

namespace Underpaint.Internal;

internal sealed unsafe class AvfxBackend : IDisposable
{
    private const string ShellPath = "vfx/common/eff/underpaint_shell.avfx";
    private const string PoolName = "Client.System.Scheduler.Instance.VfxObject";
    private const string RunCallSignature = "E8 ?? ?? ?? ?? B0 02 EB 02";
    private const string RemoveSignature =
        "40 53 48 83 EC 20 48 8B D9 48 8B 89 ?? ?? ?? ?? 48 85 C9 74 28 33 D2 E8 ?? ?? ?? ?? 48 8B 8B ?? ?? ?? ?? 48 85 C9";
    private const string DepthProducerSignature = "48 89 4C 24 ?? 53 41 56 48 81 EC ?? ?? ?? ?? 48 8B 99 ?? ?? ?? ?? 45 8B D8 4C 63 F2";
    private const string ModelBuilderSignature = "4C 8B DC 55 53 56 57 49 8D 6B A1 48 81 EC E8 00 00 00 49 8B 00";
    private const string CreateVertexWrapperSignature =
        "48 89 74 24 ?? 57 48 83 EC 30 48 8B 0D ?? ?? ?? ?? 41 0F B6 C0 BE 01 00 00 00 84 C0 41 B8 04 08 00 00";
    private const string CreateIndexWrapperSignature =
        "48 89 74 24 ?? 57 48 83 EC 30 48 8B 0D ?? ?? ?? ?? 45 84 C0 BE 01 00 00 00 C7 44 24 ?? 0A 00 00 00";
    private const string InitializeVertexBufferSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 50 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 44 8B 49";
    private const string InitializeIndexBufferSignature = "40 53 48 83 EC 20 F7 41 40 00 08 00 00 48 8B D9";
    private const int ExpectedDrawLayer = 2;

    private readonly object sync = new();
    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private readonly StaticVfxRunDelegate run;
    private readonly Hook<StaticVfxRemoveDelegate> removeHook;
    private readonly Hook<DepthProducerDelegate> depthProducerHook;
    private readonly Hook<ModelBuilderDelegate> modelBuilderHook;
    private readonly CreateBufferWrapperDelegate createVertexWrapper;
    private readonly CreateBufferWrapperDelegate createIndexWrapper;
    private readonly InitializeBufferDelegate initializeVertexBuffer;
    private readonly InitializeBufferDelegate initializeIndexBuffer;
    private readonly Dictionary<MeshKind, nint> models = [];
    private readonly Dictionary<ulong, Host> hosts = [];
    private readonly Dictionary<nint, Host> hostsByVfx = [];
    private Dictionary<nint, Host> documentSnapshot = [];
    private Hook<DocumentRenderDelegate>? documentRenderHook;
    private nint apricotCore;
    private bool disposed;

    [ThreadStatic]
    private static Host? renderingHost;

    internal AvfxBackend(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;
        run = Marshal.GetDelegateForFunctionPointer<StaticVfxRunDelegate>(sigScanner.ScanText(RunCallSignature));
        createVertexWrapper = GetDelegate<CreateBufferWrapperDelegate>(sigScanner, CreateVertexWrapperSignature);
        createIndexWrapper = GetDelegate<CreateBufferWrapperDelegate>(sigScanner, CreateIndexWrapperSignature);
        initializeVertexBuffer = GetDelegate<InitializeBufferDelegate>(sigScanner, InitializeVertexBufferSignature);
        initializeIndexBuffer = GetDelegate<InitializeBufferDelegate>(sigScanner, InitializeIndexBufferSignature);
        removeHook = gameInteropProvider.HookFromSignature<StaticVfxRemoveDelegate>(RemoveSignature, RemoveDetour);
        depthProducerHook = gameInteropProvider.HookFromSignature<DepthProducerDelegate>(DepthProducerSignature, DepthProducerDetour);
        modelBuilderHook = gameInteropProvider.HookFromSignature<ModelBuilderDelegate>(ModelBuilderSignature, ModelBuilderDetour);

        try
        {
            foreach (var definition in MeshDefinition.All)
                models.Add(definition.Kind, (nint)CreateOwnedModel(definition));
            removeHook.Enable();
            depthProducerHook.Enable();
            modelBuilderHook.Enable();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal void SubmitFrame(ReadOnlySpan<FrameCommand> commands)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var visibleIds = new HashSet<ulong>();
            foreach (var command in commands)
            {
                visibleIds.Add(command.DrawableId);
                if (!hosts.TryGetValue(command.DrawableId, out var host))
                {
                    host = CreateHost(command.DrawableId, command.Mesh);
                    hosts.Add(command.DrawableId, host);
                    hostsByVfx.Add(host.VfxAddress, host);
                }
                if (host.Mesh != command.Mesh)
                    throw new InvalidOperationException("A drawable changed mesh type.");

                SetHostState((VfxObject*)host.VfxAddress, command.SortingCenter, command.Color, command.Alpha);
                Volatile.Write(ref host.Payload, new Payload(models[command.Mesh], command.CurrentTransform));
            }

            foreach (var host in hosts.Values)
            {
                if (!visibleIds.Contains(host.DrawableId))
                    Volatile.Write(ref host.Payload, null);
            }

            AttachDocuments();
        }
    }

    internal void RetireDrawable(ulong drawableId)
    {
        Host? host;
        lock (sync)
        {
            if (disposed || !hosts.Remove(drawableId, out host))
                return;
            hostsByVfx.Remove(host.VfxAddress);
            Volatile.Write(ref host.Payload, null);
            host.DocumentAddress = 0;
            PublishDocumentSnapshot();
        }
        removeHook.Original((VfxObject*)host.VfxAddress);
    }

    public void Dispose()
    {
        Host[] activeHosts;
        lock (sync)
        {
            if (disposed)
                return;
            disposed = true;
            activeHosts = [.. hosts.Values];
            foreach (var host in activeHosts)
                Volatile.Write(ref host.Payload, null);
            hosts.Clear();
            hostsByVfx.Clear();
            Volatile.Write(ref documentSnapshot, []);
        }

        documentRenderHook?.Disable();
        modelBuilderHook.Disable();
        depthProducerHook.Disable();
        removeHook.Disable();
        foreach (var host in activeHosts)
            removeHook.Original((VfxObject*)host.VfxAddress);

        documentRenderHook?.Dispose();
        modelBuilderHook.Dispose();
        depthProducerHook.Dispose();
        removeHook.Dispose();
        foreach (var model in models.Values)
            ReleaseOwnedModel((OwnedModelRecord*)model);
        models.Clear();
    }

    private Host CreateHost(ulong drawableId, MeshKind mesh)
    {
        var vfx = VfxObject.Create(ShellPath, PoolName);
        if (vfx == null)
            throw new InvalidOperationException("VfxObject.Create returned null for an Underpaint drawable.");
        run(vfx, 0f, uint.MaxValue);
        return new Host(drawableId, mesh, (nint)vfx);
    }

    private void AttachDocuments()
    {
        var core = Volatile.Read(ref apricotCore);
        if (core == 0)
            return;
        var state = *(byte**)(core + 0x1498);
        if (state == null)
            return;

        var changed = false;
        foreach (var host in hosts.Values)
        {
            if (host.DocumentAddress != 0)
                continue;
            var vfx = (VfxObject*)host.VfxAddress;
            var resourceInstance = vfx->VfxResourceInstance;
            if (resourceInstance == null)
                continue;
            var handle = *(ulong*)((byte*)resourceInstance + 0x60);
            var generation = (uint)handle;
            var slot = (uint)(handle >> 32);
            if (handle == 0 || slot >= 2048)
                continue;
            var slotRecord = state + 0x2000 + slot * 0x88;
            if (
                *(nint*)(slotRecord + 0x48) != (nint)resourceInstance
                || *(uint*)(slotRecord + 0x60) != generation
                || *(uint*)(slotRecord + 0x64) != slot
            )
                continue;
            var document = *(nint*)(slotRecord + 0x30);
            var resource = *(byte**)(slotRecord + 0x38);
            if (document == 0 || resource == null || ((*(uint*)(resource + 0x5C) >> 10) & 0x1F) != ExpectedDrawLayer)
                continue;
            var renderTarget = *(nint*)(*(nint*)document + 0x128);
            if (renderTarget == 0)
                continue;
            documentRenderHook ??= gameInteropProvider.HookFromAddress<DocumentRenderDelegate>(renderTarget, DocumentRenderDetour);
            documentRenderHook.Enable();
            host.DocumentAddress = document;
            changed = true;
        }
        if (changed)
            PublishDocumentSnapshot();
    }

    private void PublishDocumentSnapshot()
    {
        var snapshot = new Dictionary<nint, Host>();
        foreach (var host in hosts.Values)
        {
            if (host.DocumentAddress != 0)
                snapshot[host.DocumentAddress] = host;
        }
        Volatile.Write(ref documentSnapshot, snapshot);
    }

    private nint DepthProducerDetour(nint core, int category, uint workerIndex, uint workerCount, nint producedCount)
    {
        var result = depthProducerHook.Original(core, category, workerIndex, workerCount, producedCount);
        if (category == ExpectedDrawLayer)
            Volatile.Write(ref apricotCore, core);
        return result;
    }

    private nint DocumentRenderDetour(nint document)
    {
        var previous = renderingHost;
        Volatile.Read(ref documentSnapshot).TryGetValue(document, out var host);
        renderingHost = host;
        try
        {
            return documentRenderHook!.Original(document);
        }
        finally
        {
            renderingHost = previous;
        }
    }

    private nint ModelBuilderDetour(nint rendererState, byte useProjection, nint descriptorAddress)
    {
        var host = renderingHost;
        if (host == null)
            return modelBuilderHook.Original(rendererState, useProjection, descriptorAddress);
        var payload = Volatile.Read(ref host.Payload);
        if (payload == null || descriptorAddress == 0)
            return 0;

        var sourceDescriptor = (nint*)descriptorAddress;
        var descriptor = stackalloc nint[11];
        Buffer.MemoryCopy(sourceDescriptor, descriptor, 11 * sizeof(nint), 11 * sizeof(nint));
        var transform = stackalloc float[12];
        WriteTransform(payload.Transform, transform);
        descriptor[0] = payload.Model;
        descriptor[2] = (nint)transform;
        return modelBuilderHook.Original(rendererState, useProjection, (nint)descriptor);
    }

    private nint RemoveDetour(VfxObject* vfx)
    {
        lock (sync)
        {
            if (hostsByVfx.Remove((nint)vfx, out var host))
            {
                hosts.Remove(host.DrawableId);
                Volatile.Write(ref host.Payload, null);
                host.DocumentAddress = 0;
                PublishDocumentSnapshot();
                log.Warning("[Underpaint] The game removed AVFX host for drawable {DrawableId}.", host.DrawableId);
            }
        }
        return removeHook.Original(vfx);
    }

    private OwnedModelRecord* CreateOwnedModel(MeshDefinition definition)
    {
        var model = (OwnedModelRecord*)NativeMemory.AllocZeroed((nuint)sizeof(OwnedModelRecord));
        try
        {
            var vertices = new AvfxVertex[definition.Vertices.Length];
            for (var index = 0; index < vertices.Length; index++)
            {
                var vertex = definition.Vertices[index];
                vertices[index] = new AvfxVertex(vertex.Position, vertex.Normal);
            }
            var indices = definition.Indices;
            fixed (AvfxVertex* vertexData = vertices)
            fixed (ushort* indexData = indices)
            {
                model->VertexWrapper = createVertexWrapper(0, (uint)(vertices.Length * sizeof(AvfxVertex)), 0);
                if (model->VertexWrapper == 0 || !InitializeWrapper(model->VertexWrapper, vertexData, initializeVertexBuffer))
                    throw new InvalidOperationException($"The game rejected the {definition.Kind} AVFX vertex wrapper.");
                model->IndexWrapper = createIndexWrapper(0, (uint)(indices.Length * sizeof(ushort)), 0);
                if (model->IndexWrapper == 0 || !InitializeWrapper(model->IndexWrapper, indexData, initializeIndexBuffer))
                    throw new InvalidOperationException($"The game rejected the {definition.Kind} AVFX index wrapper.");
            }
            model->VertexCount = checked((ushort)vertices.Length);
            model->IndexCount = checked((ushort)indices.Length);
            return model;
        }
        catch
        {
            ReleaseOwnedModel(model);
            throw;
        }
    }

    private static void SetHostState(VfxObject* vfx, Vector3 position, Vector3 color, float alpha)
    {
        vfx->Position = new FfxivVector3 { X = position.X, Y = position.Y, Z = position.Z };
        vfx->Rotation = FfxivQuaternion.Identity;
        vfx->Color = new FfxivVector4 { X = color.X, Y = color.Y, Z = color.Z, W = alpha };
        vfx->UpdateTransforms(true);
    }

    private static void WriteTransform(Matrix4x4 value, float* output)
    {
        output[0] = value.M11;
        output[1] = value.M12;
        output[2] = value.M13;
        output[3] = value.M21;
        output[4] = value.M22;
        output[5] = value.M23;
        output[6] = value.M31;
        output[7] = value.M32;
        output[8] = value.M33;
        output[9] = value.M41;
        output[10] = value.M42;
        output[11] = value.M43;
    }

    private static bool InitializeWrapper(nint wrapper, void* data, InitializeBufferDelegate initialize)
    {
        var resource = *(nint*)(wrapper + 0x10);
        return resource != 0 && initialize(resource, data) != 0;
    }

    private static void ReleaseOwnedModel(OwnedModelRecord* model)
    {
        if (model == null)
            return;
        ReleaseWrapper(ref model->VertexWrapper);
        ReleaseWrapper(ref model->IndexWrapper);
        NativeMemory.Free(model);
    }

    private static void ReleaseWrapper(ref nint wrapper)
    {
        var value = wrapper;
        wrapper = 0;
        if (value == 0)
            return;
        var release = (delegate* unmanaged<nint, uint>)(*(nint**)value)[1];
        release(value);
    }

    private static T GetDelegate<T>(ISigScanner scanner, string signature)
        where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(scanner.ScanText(signature));

    private delegate nint StaticVfxRunDelegate(VfxObject* vfx, float a1, uint a2);
    private delegate nint StaticVfxRemoveDelegate(VfxObject* vfx);
    private delegate nint DepthProducerDelegate(nint core, int category, uint workerIndex, uint workerCount, nint producedCount);
    private delegate nint DocumentRenderDelegate(nint document);
    private delegate nint ModelBuilderDelegate(nint rendererState, byte useProjection, nint descriptor);
    private delegate nint CreateBufferWrapperDelegate(nint allocatorState, uint byteSize, byte dynamic);
    private delegate byte InitializeBufferDelegate(nint resource, void* data);

    private sealed class Host(ulong drawableId, MeshKind mesh, nint vfxAddress)
    {
        internal ulong DrawableId { get; } = drawableId;
        internal MeshKind Mesh { get; } = mesh;
        internal nint VfxAddress { get; } = vfxAddress;
        internal nint DocumentAddress;
        internal Payload? Payload;
    }

    private sealed record Payload(nint Model, Matrix4x4 Transform);

    [StructLayout(LayoutKind.Explicit, Size = 0x28)]
    private struct OwnedModelRecord
    {
        [FieldOffset(0x10)] internal nint VertexWrapper;
        [FieldOffset(0x18)] internal nint IndexWrapper;
        [FieldOffset(0x24)] internal ushort VertexCount;
        [FieldOffset(0x26)] internal ushort IndexCount;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct AvfxVertex
    {
        private readonly Half positionX;
        private readonly Half positionY;
        private readonly Half positionZ;
        private readonly Half positionW;
        private readonly uint normal;
        private readonly uint tangent;
        private readonly uint color;
        private readonly Half uv1X;
        private readonly Half uv1Y;
        private readonly Half uv2X;
        private readonly Half uv2Y;
        private readonly Half uv3X;
        private readonly Half uv3Y;
        private readonly Half uv4X;
        private readonly Half uv4Y;

        internal AvfxVertex(Vector3 position, Vector3 normal)
        {
            positionX = (Half)position.X;
            positionY = (Half)position.Y;
            positionZ = (Half)position.Z;
            positionW = (Half)1f;
            this.normal = PackDirection(normal);
            var reference = MathF.Abs(normal.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            tangent = PackDirection(Vector3.Normalize(Vector3.Cross(reference, normal)));
            color = uint.MaxValue;
            uv1X = uv1Y = uv2X = uv2Y = uv3X = uv3Y = uv4X = uv4Y = (Half)0.5f;
        }

        private static uint PackDirection(Vector3 value) =>
            PackComponent(value.X) | ((uint)PackComponent(value.Y) << 8) | ((uint)PackComponent(value.Z) << 16) | 0x7F000000;
        private static byte PackComponent(float value) =>
            (byte)MathF.Round((Math.Clamp(value, -1f, 1f) * 0.5f + 0.5f) * 255f);
    }
}

internal readonly record struct FrameCommand(
    ulong DrawableId,
    MeshKind Mesh,
    Matrix4x4 CurrentTransform,
    Vector3 SortingCenter,
    Vector3 Color,
    float Alpha
);
#endif
