using System.Numerics;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Collects one complete primitive snapshot for native consumption.</summary>
public sealed class PrimitiveFrame : IDisposable
{
    private readonly Renderer renderer;
    private readonly List<FrameCommand> commands = [];
    private readonly HashSet<ulong> drawnDrawableIds = [];
    private bool published;
    private bool disposed;

    internal PrimitiveFrame(Renderer renderer)
    {
        this.renderer = renderer;
    }

    public void DrawTriangle(TriangleDrawable drawable, Matrix4x4 transform, Vector3 color, float alpha = 1f)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        Add(drawable.State, transform, color, alpha);
    }

    public void DrawRectangle(RectangleDrawable drawable, Matrix4x4 transform, Vector3 color, float alpha = 1f)
    {
        ArgumentNullException.ThrowIfNull(drawable);
        Add(drawable.State, Matrix4x4.CreateScale(drawable.Width, drawable.Height, 1f) * transform, color, alpha);
    }

    public void Publish()
    {
        ThrowIfClosed();
        renderer.Publish(commands);
        published = true;
    }

    public void Dispose() => disposed = true;

    private void Add(DrawableState drawable, Matrix4x4 transform, Vector3 color, float alpha)
    {
        ThrowIfClosed();
        if (!ReferenceEquals(drawable.Owner, renderer))
            throw new ArgumentException("The drawable belongs to a different renderer.", nameof(drawable));
        ObjectDisposedException.ThrowIf(drawable.IsDisposed, drawable);
        if (!drawnDrawableIds.Add(drawable.Id))
            throw new InvalidOperationException("A drawable can only be drawn once per frame.");

        commands.Add(new FrameCommand(drawable.Id, drawable.Mesh, transform, color, Math.Clamp(alpha, 0f, 1f)));
    }

    private void ThrowIfClosed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (published)
            throw new InvalidOperationException("The frame has already been published.");
    }
}
