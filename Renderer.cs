using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns Underpaint's native rendering resources.</summary>
public sealed class Renderer : IDisposable
{
    private readonly NativeResources resources;
    private readonly MaterialLoader material;

    public Renderer(ISigScanner sigScanner)
    {
        resources = new NativeResources(sigScanner);
        try
        {
            material = new MaterialLoader();
        }
        catch
        {
            resources.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        material.Dispose();
        resources.Dispose();
    }
}
