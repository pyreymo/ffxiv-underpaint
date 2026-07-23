using System.Numerics;
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

    /// <summary>Submits one triangle for the next native render frame.</summary>
    /// <param name="id">Stable identity supplied by the caller.</param>
    /// <param name="currentTransform">Current world transform.</param>
    /// <param name="previousTransform">Previous frame's world transform.</param>
    /// <param name="color">Linear RGB color.</param>
    /// <param name="alpha">Smooth vertex alpha, where one is fully opaque within the semitransparent path.</param>
    /// <param name="ditherFade">
    /// Value written to InstanceConstant[0].w. The fixed shader variant uses it for dither coverage;
    /// its general engine meaning is not yet known.
    /// </param>
    public void SubmitTriangle(
        ulong id,
        Matrix4x4 currentTransform,
        Matrix4x4 previousTransform,
        Vector3 color,
        float alpha,
        float ditherFade
    ) => backend.SubmitTriangle(new TriangleSubmission(id, currentTransform, previousTransform, new Vector4(color, alpha), ditherFade));

    public void Dispose()
    {
        backend.Dispose();
        material.Dispose();
        resources.Dispose();
    }
}
