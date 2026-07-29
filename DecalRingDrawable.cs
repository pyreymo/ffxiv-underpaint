namespace Underpaint;

/// <summary>A retained native AVFX decal ring authored through VFXEditorCN.</summary>
public sealed class DecalRingDrawable : IDisposable
{
    internal DrawableState State { get; }

    internal DecalRingDrawable(DrawableState state)
    {
        State = state;
    }

    public void Dispose() => State.Dispose();
}
