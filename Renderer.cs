using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns Underpaint's native rendering resources.</summary>
public sealed class Renderer : IDisposable
{
    private readonly NativeResources resources;

    public Renderer(ISigScanner sigScanner)
    {
        resources = new NativeResources(sigScanner);
    }

    public void Dispose() => resources.Dispose();
}
