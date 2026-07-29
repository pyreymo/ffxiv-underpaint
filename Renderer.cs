using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns Underpaint's retained AVFX rendering resources.</summary>
public sealed class Renderer : IDisposable
{
    private readonly object drawableLock = new();
    private readonly HashSet<DrawableState> drawables = [];
    private readonly AvfxBackend backend;
    private ulong nextDrawableId;
    private bool disposed;

    public Renderer(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        backend = new AvfxBackend(gameInteropProvider, sigScanner, log);
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
}

internal sealed class DrawableState(Renderer owner, ulong id, MeshKind mesh)
{
    private int disposed;

    internal Renderer Owner { get; } = owner;
    internal ulong Id { get; } = id;
    internal MeshKind Mesh { get; } = mesh;
    internal bool IsDisposed => Volatile.Read(ref disposed) != 0;

    internal void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            Owner.Retire(this);
    }

    internal void Invalidate() => Interlocked.Exchange(ref disposed, 1);
}
