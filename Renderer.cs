using Dalamud.Plugin.Services;
using Underpaint.Internal;

namespace Underpaint;

/// <summary>Owns Underpaint's native rendering resources.</summary>
public sealed class Renderer : IDisposable
{
    private readonly NativeResources resources;
    private readonly MaterialLoader material;
    private readonly NativeBackend backend;

    public Renderer(IGameInteropProvider gameInteropProvider, ISigScanner sigScanner, IPluginLog log)
    {
        resources = new NativeResources(sigScanner);
        try
        {
            material = new MaterialLoader();
            try
            {
                backend = new NativeBackend(gameInteropProvider, sigScanner, material, resources, log);
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

    /// <summary>
    /// Publishes the complete triangle set for the next native render frame.
    /// IDs must be unique within the frame.
    /// </summary>
    public void SubmitFrame(ReadOnlySpan<Triangle> triangles) => backend.SubmitFrame(triangles);

    public void Dispose()
    {
        backend.Dispose();
        material.Dispose();
        resources.Dispose();
    }
}
