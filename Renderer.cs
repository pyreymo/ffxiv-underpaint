using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns Underpaint's native rendering resources.</summary>
public sealed class Renderer : IDisposable
{
    private readonly object drawableLock = new();
    private readonly HashSet<DrawableState> drawables = [];
    private readonly NativeResources resources;
    private readonly MaterialLoader material;
    private readonly NativeBackend backend;
#if DEBUG
    private readonly AvfxSortProbe? avfxSortProbe;
#endif
    private ulong nextDrawableId;
    private bool disposed;

    public Renderer(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        resources = new NativeResources(sigScanner);
        try
        {
            material = new MaterialLoader();
            try
            {
                backend = new NativeBackend(gameInteropProvider, sigScanner, material, resources, log);
#if DEBUG
                try
                {
                    avfxSortProbe = new AvfxSortProbe(gameInteropProvider, sigScanner, log);
                }
                catch (Exception exception)
                {
                    log.Warning(exception, "[Underpaint] AVFX sorting probe is unavailable.");
                }
#endif
            }
            catch
            {
                material.Dispose();
                throw;
            }
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    public TriangleDrawable CreateTriangle() => new(CreateDrawable(MeshKind.Triangle));

    public RectangleDrawable CreateRectangle(float width, float height)
    {
        RectangleDrawable.ValidateDimensions(width, height);
        return new RectangleDrawable(CreateDrawable(MeshKind.Rectangle), width, height);
    }

    public PrimitiveFrame BeginFrame()
    {
        lock (drawableLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            return new PrimitiveFrame(this);
        }
    }

#if DEBUG
    public void ArmSortKeyCapture()
    {
        lock (drawableLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            backend.ArmSortKeyCapture();
        }
    }

    public string? SortKeyCaptureStatus => backend.SortKeyCaptureStatus;

    public void ArmAvfxSortProbe(string resourcePath, IReadOnlyList<System.Numerics.Vector3> positions, int expectedDrawLayerType)
    {
        lock (drawableLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            avfxSortProbe?.Arm(resourcePath, positions, expectedDrawLayerType);
        }
    }

    public void UpdateAvfxSortProbe()
    {
        lock (drawableLock)
        {
            if (!disposed)
                avfxSortProbe?.Update();
        }
    }

    public void StopAvfxSortProbe()
    {
        lock (drawableLock)
        {
            if (!disposed)
                avfxSortProbe?.Stop();
        }
    }

    public string AvfxSortProbeStatus
    {
        get
        {
            lock (drawableLock)
                return avfxSortProbe?.Status ?? "Unavailable.";
        }
    }

    public string? TakeAvfxSortProbeReport()
    {
        lock (drawableLock)
            return avfxSortProbe?.TakeReport();
    }
#endif

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

#if DEBUG
        avfxSortProbe?.Dispose();
#endif
        backend.Dispose();
        material.Dispose();
        resources.Dispose();
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
