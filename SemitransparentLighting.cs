namespace Underpaint;

/// <summary>Runtime lighting approximation used by the semitransparent composite stage.</summary>
public readonly record struct SemitransparentLighting(float Ambient, float Diffuse, float Specular)
{
    public static SemitransparentLighting Default => new(0.25f, 4f, 4f);

    internal SemitransparentLighting Clamped() =>
        new(Math.Clamp(Ambient, 0f, 2f), Math.Clamp(Diffuse, 0f, 8f), Math.Clamp(Specular, 0f, 8f));
}
