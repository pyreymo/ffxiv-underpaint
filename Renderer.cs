using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns Underpaint's retained AVFX rendering resources.</summary>
public sealed class Renderer : IDisposable
{
    private readonly object drawableLock = new();
    private readonly HashSet<DrawableState> drawables = [];
    private readonly AvfxBackend backend;
    private readonly VfxEditorBridge vfxEditor;
    private ulong nextDrawableId;
    private string? decalRingPath;
    private bool disposed;

    public Renderer(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        backend = new AvfxBackend(gameInteropProvider, sigScanner, log);
        vfxEditor = new VfxEditorBridge(log);
    }

    public TriangleDrawable CreateTriangle() => new(CreateDrawable(MeshKind.Triangle));

    public RectangleDrawable CreateRectangle(float width, float height)
    {
        RectangleDrawable.ValidateDimensions(width, height);
        return new RectangleDrawable(CreateDrawable(MeshKind.Rectangle), width, height);
    }

    public SphereDrawable CreateSphere(float radius)
    {
        SphereDrawable.ValidateRadius(radius);
        return new SphereDrawable(CreateDrawable(MeshKind.Sphere), radius);
    }

    public DecalRingDrawable CreateAnimatedDecalRing()
    {
        lock (drawableLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            decalRingPath ??= vfxEditor.CreateAnimatedDecalRing();
            return new DecalRingDrawable(CreateDrawable(decalRingPath));
        }
    }

    public PrimitiveFrame BeginFrame()
    {
        lock (drawableLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new PrimitiveFrame(this);
        }
    }

    public void Dispose()
    {
        lock (drawableLock)
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var drawable in drawables)
                drawable.Invalidate();
            drawables.Clear();
        }
        backend.Dispose();
        vfxEditor.Dispose();
    }

    internal void Publish(List<FrameCommand> commands)
    {
        lock (drawableLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            backend.SubmitFrame([.. commands]);
        }
    }

    internal void Retire(DrawableState drawable)
    {
        lock (drawableLock)
        {
            if (disposed || !drawables.Remove(drawable))
                return;
            backend.RetireDrawable(drawable.Id);
        }
    }

    private DrawableState CreateDrawable(MeshKind mesh)
    {
        lock (drawableLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var drawable = new DrawableState(this, ++nextDrawableId, mesh);
            drawables.Add(drawable);
            return drawable;
        }
    }

    private DrawableState CreateDrawable(string vfxPath)
    {
        var drawable = new DrawableState(this, ++nextDrawableId, vfxPath);
        drawables.Add(drawable);
        return drawable;
    }
}

internal sealed class DrawableState
{
    private int disposed;

    internal Renderer Owner { get; }
    internal ulong Id { get; }
    internal MeshKind? Mesh { get; }
    internal string? VfxPath { get; }
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal DrawableState(Renderer owner, ulong id, MeshKind mesh)
    {
        Owner = owner;
        Id = id;
        Mesh = mesh;
    }

    internal DrawableState(Renderer owner, ulong id, string vfxPath)
    {
        Owner = owner;
        Id = id;
        VfxPath = vfxPath;
    }

    internal void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            Owner.Retire(this);
    }

    internal void Invalidate() => Interlocked.Exchange(ref disposed, 1);
}
