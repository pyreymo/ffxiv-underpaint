using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace Underpaint.Internal;

internal sealed unsafe class MaterialLoader : IDisposable
{
    internal const string DonorMaterialPath =
        "chara/equipment/e0378/material/v0002/mt_c0101e0378_top_a.mtrl";
    internal const string ShaderPackageName = "charactertransparency.shpk";

    private const uint MaterialFileType = 0x6D74726C;
    private const uint DonorMaterialPathHash = 0x56D3AB97;

    private MaterialResourceHandle* resource;

    internal Material* Material => resource == null ? null : resource->Material;
    internal ShaderPackage* ShaderPackage =>
        resource == null || resource->ShaderPackageResourceHandle == null
            ? null
            : resource->ShaderPackageResourceHandle->ShaderPackage;

    internal MaterialLoader()
    {
        var resourceManager = ResourceManager.Instance();
        if (resourceManager == null)
            throw new InvalidOperationException("The native resource manager is not available.");

        var category = ResourceCategory.Chara;
        var fileType = MaterialFileType;
        var pathHash = DonorMaterialPathHash;
        var loaded = (MaterialResourceHandle*)
            resourceManager->GetResourceSync(
                &category,
                &fileType,
                &pathHash,
                DonorMaterialPath,
                null,
                null,
                0
            );

        if (loaded == null)
            throw new InvalidOperationException("The fixed donor material could not be loaded.");

        try
        {
            if (
                loaded->Material == null
                || loaded->ShaderPackageResourceHandle == null
                || loaded->ShaderPackageResourceHandle->ShaderPackage == null
            )
                throw new InvalidOperationException("The fixed donor material is not ready.");

            if (!loaded->ShpkName.AsSpan().SequenceEqual("charactertransparency.shpk"u8))
                throw new InvalidOperationException(
                    $"The fixed donor material uses '{loaded->ShpkName}', not '{ShaderPackageName}'."
                );

            resource = loaded;
        }
        catch
        {
            loaded->DecRef();
            throw;
        }
    }

    public void Dispose()
    {
        var loaded = resource;
        resource = null;
        if (loaded != null)
            loaded->DecRef();
    }
}
