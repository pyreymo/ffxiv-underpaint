using Underpaint.Internal;

namespace Underpaint;

/// <summary>A retained rectangle centered at the local origin and facing +Z.</summary>
public sealed class RectangleDrawable : IDisposable
{
    internal DrawableState State { get; }

    public float Width { get; private set; }
    public float Height { get; private set; }

    internal RectangleDrawable(DrawableState state, float width, float height)
    {
        State = state;
        (Width, Height) = ValidateDimensions(width, height);
    }

    public void Resize(float width, float height)
    {
        ObjectDisposedException.ThrowIf(State.IsDisposed, this);
        (Width, Height) = ValidateDimensions(width, height);
    }

    public void Dispose() => State.Dispose();

    internal static (float Width, float Height) ValidateDimensions(float width, float height)
    {
        if (!float.IsFinite(width) || width <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Rectangle width must be finite and positive.");
        if (!float.IsFinite(height) || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(height), "Rectangle height must be finite and positive.");
        return (width, height);
    }
}
