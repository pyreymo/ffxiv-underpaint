using Underpaint.Internal;

namespace Underpaint;

/// <summary>A retained unit equilateral triangle centered at the local origin and facing +Z.</summary>
public sealed class TriangleDrawable : IDisposable
{
    internal DrawableState State { get; }

    internal TriangleDrawable(DrawableState state)
    {
        State = state;
    }

    public void Dispose() => State.Dispose();
}
