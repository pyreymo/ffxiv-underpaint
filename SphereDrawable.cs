using Underpaint.Internal;

namespace Underpaint;

/// <summary>A retained unit-diameter, 1,024-face faceted sphere.</summary>
public sealed class SphereDrawable : IDisposable
{
    internal DrawableState State { get; }

    public float Radius { get; private set; }

    internal SphereDrawable(DrawableState state, float radius)
    {
        State = state;
        Radius = ValidateRadius(radius);
    }

    public void Resize(float radius)
    {
        ObjectDisposedException.ThrowIf(State.IsDisposed, this);
        Radius = ValidateRadius(radius);
    }

    public void Dispose() => State.Dispose();

    internal static float ValidateRadius(float radius)
    {
        if (!float.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius), "Sphere radius must be finite and positive.");
        return radius;
    }
}
