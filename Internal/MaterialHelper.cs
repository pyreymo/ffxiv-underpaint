using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.Interop;

namespace Underpaint.Internal;

internal sealed unsafe class MaterialHelper
{
    private const string InitializeShaderSelectionSignature = "48 85 D2 0F 84 ?? ?? ?? ?? 48 89 5C 24 ?? 57 48 83 EC 20 48 89 74 24";
    private const string DestroyShaderSelectionSignature = "40 53 48 83 EC 20 48 83 79 ?? ?? 48 8B D9 74 ?? 4C 8B 41";
    private const string ApplyMaterialSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 54 41 56 41 57 48 83 EC 20 44 8B 05 ?? ?? ?? ?? 48 8B F2 65 48 8B 04 25 ?? ?? ?? ?? 48 8B D9";
    private const string ResolveShaderSelectionSignature =
        "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 74 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 30 48 8B F2 48 8B F9 48 39 4A 08";

    // A natural charactertransparency call changed only these three context slots.
    private const uint MaterialConstantId = 25;
    private const uint ModelConstantCrc = 0x4E0A5472;
    private const ushort ModelConstantRegisters = 1;
    private const uint InstanceParameterCrc = 0x20A30B34;
    private const ushort InstanceParameterRegisters = 11;

    // charactertransparency material inputs:
    // normal RG = tangent-space normal, B = opacity;
    // index R = colorset pair, G = even/odd blend;
    // the color table is generated from the mtrl colorset.
    private const uint NormalMapSamplerCrc = 0x0C5EC1F1;
    private const ushort MaterialSamplerClass = ShaderPackage.SamplerSlotMaterial;
    private const uint IndexMapSamplerCrc = 0x565F8FD8;
    private const uint ColorTableSamplerCrc = 0x2005679F;
    private const ushort ColorTableSamplerClass = 1;

    // The archived native submission verified 0x01000000 as the main-view
    // request gate in OnRenderMaterialParams2+0x38.
    private const uint MainViewRequestMask = 0x01000000;

    private readonly MaterialLoader material;
    private readonly delegate* unmanaged<ShaderSelection*, ShaderPackage*, void> initializeShaderSelection;
    private readonly delegate* unmanaged<ShaderSelection*, void> destroyShaderSelection;
    private readonly delegate* unmanaged<ShaderSelection*, Material*, void> applyMaterial;
    private readonly delegate* unmanaged<ShaderPackage*, ShaderSelection*, nint> resolveShaderSelection;

    internal MaterialHelper(ISigScanner sigScanner, MaterialLoader material)
    {
        this.material = material;
        initializeShaderSelection = (delegate* unmanaged<ShaderSelection*, ShaderPackage*, void>)RequireSignature(
            sigScanner,
            InitializeShaderSelectionSignature,
            "InitializeShaderSelection"
        );
        destroyShaderSelection = (delegate* unmanaged<ShaderSelection*, void>)RequireSignature(
            sigScanner,
            DestroyShaderSelectionSignature,
            "DestroyShaderSelection"
        );
        applyMaterial = (delegate* unmanaged<ShaderSelection*, Material*, void>)RequireSignature(
            sigScanner,
            ApplyMaterialSignature,
            "ApplyMaterial"
        );
        resolveShaderSelection = (delegate* unmanaged<ShaderPackage*, ShaderSelection*, nint>)RequireSignature(
            sigScanner,
            ResolveShaderSelectionSignature,
            "ResolveShaderSelection"
        );
    }

    internal MaterialBindingIds ValidateResources(
        ConstantBuffer* instanceParameters,
        ConstantBuffer* modelConstant,
        ConstantBuffer* materialConstant
    )
    {
        var shaderPackage = material.ShaderPackage;
        if (material.Material == null || shaderPackage == null)
            throw new InvalidOperationException("The fixed donor material is not ready.");

        var modelConstantEntry = FindConstant(shaderPackage, ModelConstantCrc);
        if (modelConstantEntry.Size != ModelConstantRegisters)
            throw new InvalidOperationException("The fixed shader package has an unexpected model constant size.");
        if (modelConstant == null || modelConstant->ByteSize != modelConstantEntry.Size * 16)
            throw new InvalidOperationException("The owned model constant does not match the fixed shader package.");

        var instanceParameterEntry = FindConstant(shaderPackage, InstanceParameterCrc);
        if (instanceParameterEntry.Size != InstanceParameterRegisters)
            throw new InvalidOperationException("The fixed shader package has an unexpected instance-parameter size.");
        if (instanceParameters == null || instanceParameters->ByteSize != instanceParameterEntry.Size * 16)
            throw new InvalidOperationException("The owned instance parameters do not match the fixed shader package.");
        if (materialConstant == null || materialConstant->ByteSize != shaderPackage->MaterialConstantBufferSize)
            throw new InvalidOperationException("The owned material constant does not match the fixed shader package.");

        var normalMapSampler = FindSampler(shaderPackage, NormalMapSamplerCrc, MaterialSamplerClass);
        var indexMapSampler = FindSampler(shaderPackage, IndexMapSamplerCrc, MaterialSamplerClass);
        var colorTableSampler = FindSampler(shaderPackage, ColorTableSamplerCrc, ColorTableSamplerClass);

        return new MaterialBindingIds(
            MaterialConstantId,
            instanceParameterEntry.Id,
            modelConstantEntry.Id,
            normalMapSampler.Id,
            indexMapSampler.Id,
            colorTableSampler.Id
        );
    }

    internal void Initialize(
        ModelRenderer* renderer,
        Model* model,
        ModelRenderer.OnRenderModelParams* modelParameters,
        ModelRenderer.OnRenderMaterialParams2* materialParameters,
        ShaderSelection* selection,
        ConstantBuffer* instanceParameters
    )
    {
        var shaderPackage = material.ShaderPackage;
        if (shaderPackage == null)
            throw new InvalidOperationException("The fixed shader package is not ready.");

        NativeMemory.Clear(model, (nuint)sizeof(Model));
        NativeMemory.Clear(modelParameters, (nuint)sizeof(ModelRenderer.OnRenderModelParams));
        NativeMemory.Clear(materialParameters, (nuint)sizeof(ModelRenderer.OnRenderMaterialParams2));
        NativeMemory.Clear(selection, (nuint)sizeof(ShaderSelection));

        initializeShaderSelection(selection, shaderPackage);
        try
        {
            if (selection->Package == null || selection->SceneValues == null)
                throw new InvalidOperationException("The native shader-selection initializer returned incomplete state.");

            modelParameters->Model = model;
            // Runtime mapping and the archived native builder path identify
            // OnRenderModelParams+0x10 as the 176-byte instance-parameter input.
            *(ConstantBuffer**)((byte*)modelParameters + 0x10) = instanceParameters;
            materialParameters->Inner = modelParameters;
            *(ShaderSelection**)((byte*)materialParameters + 0x30) = selection;
            *(uint*)((byte*)materialParameters + 0x38) = MainViewRequestMask;
            CopySceneValues(renderer, shaderPackage, selection);

            var subViewKey = renderer->SubViewKeys[0];
            selection->SubViewKey = subViewKey.KeyCRC;
            selection->SubViewValue = subViewKey.ValueCRC;
        }
        catch
        {
            destroyShaderSelection(selection);
            throw;
        }
    }

    internal MaterialHelperResult Apply(
        ModelRenderer* renderer,
        byte* context,
        ModelRenderer.OnRenderMaterialParams2* materialParameters,
        ShaderSelection* selection
    )
    {
        var targetMaterial = material.Material;
        var shaderPackage = material.ShaderPackage;
        if (targetMaterial == null || shaderPackage == null)
            throw new InvalidOperationException("The fixed donor material is not ready.");

        var onRenderMaterialResult = renderer->OnRenderMaterial(materialParameters, targetMaterial, 0);
        applyMaterial(selection, targetMaterial);
        if (*(ConstantBuffer**)(context + 0x940 + MaterialConstantId * sizeof(ulong)) != targetMaterial->MaterialParameterCBuffer)
            throw new InvalidOperationException("ApplyMaterial did not install the fixed material constant.");

        var shaderDescriptor = resolveShaderSelection(shaderPackage, selection);
        if (selection->MaterialValues == null || shaderDescriptor == 0)
            throw new InvalidOperationException("The fixed material did not resolve a shader selection.");

        return new MaterialHelperResult((nint)onRenderMaterialResult, *(uint*)((byte*)materialParameters + 0x40), shaderDescriptor);
    }

    internal void Destroy(ShaderSelection* selection) => destroyShaderSelection(selection);

    internal static bool TryResolveActiveShaders(byte* context, nint shaderDescriptor, out ShaderPair shaders)
    {
        var pass = context[0x0B] & 0x0F;
        if (!TryGetPassShaders(shaderDescriptor, pass, out var vertexShader, out var pixelShader))
        {
            shaders = default;
            return false;
        }

        shaders = new ShaderPair(pass, vertexShader, pixelShader);
        return true;
    }

    private static bool TryGetPassShaders(nint shaderDescriptor, int pass, out nint vertexShader, out nint pixelShader)
    {
        vertexShader = 0;
        pixelShader = 0;
        if (shaderDescriptor == 0 || (uint)pass >= 16)
            return false;

        var descriptor = (byte*)shaderDescriptor;
        var shaderTable = *(byte**)descriptor;
        if (shaderTable == null)
            return false;

        var mappings = *(int**)(shaderTable + 0x170);
        var mappedPass = mappings == null ? pass : mappings[pass];
        if ((uint)mappedPass >= 16)
            return false;

        var slot = *(sbyte*)(descriptor + 0x08 + mappedPass);
        var slotCount = descriptor[0x20];
        if (slot < 0 || slot >= slotCount)
            return false;

        var entry = descriptor + 0x28 + slot * 8;
        var vertexIndex = *(ushort*)entry;
        var pixelIndex = *(ushort*)(entry + 2);
        var vertexStart = *(nint*)(shaderTable + 0x18);
        var vertexEnd = *(nint*)(shaderTable + 0x20);
        var pixelStart = *(nint*)(shaderTable + 0x38);
        var pixelEnd = *(nint*)(shaderTable + 0x40);
        var vertexCount = vertexStart != 0 && vertexEnd >= vertexStart ? (vertexEnd - vertexStart) / sizeof(nint) : 0;
        var pixelCount = pixelStart != 0 && pixelEnd >= pixelStart ? (pixelEnd - pixelStart) / sizeof(nint) : 0;
        if (vertexIndex >= vertexCount || pixelIndex >= pixelCount)
            return false;

        vertexShader = *(nint*)(vertexStart + vertexIndex * sizeof(nint));
        pixelShader = *(nint*)(pixelStart + pixelIndex * sizeof(nint));
        return vertexShader != 0 && pixelShader != 0;
    }

    private static void CopySceneValues(ModelRenderer* renderer, ShaderPackage* shaderPackage, ShaderSelection* selection)
    {
        var rendererKeys = renderer->SceneKeys;
        var subViewKeys = renderer->SubViewKeys;
        var targetKeys = shaderPackage->SceneKeysSpan;
        for (var targetIndex = 0; targetIndex < targetKeys.Length; targetIndex++)
        {
            if (
                TryFindValue(rendererKeys, targetKeys[targetIndex], out var value)
                || TryFindValue(subViewKeys, targetKeys[targetIndex], out value)
            )
            {
                selection->SceneValues[targetIndex] = value;
            }
        }

        if (rendererKeys[0].KeyCRC != rendererKeys[1].KeyCRC)
            throw new InvalidOperationException("The renderer's non-skinned and skinned model keys do not match.");
        SetSceneValue(shaderPackage, selection, rendererKeys[0].KeyCRC, rendererKeys[0].ValueCRC);
    }

    private static bool TryFindValue(Span<ShaderSceneKey> keys, uint targetKey, out uint value)
    {
        foreach (var key in keys)
        {
            if (key.KeyCRC != targetKey)
                continue;
            value = key.ValueCRC;
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryFindValue(Span<ShaderSubViewKey> keys, uint targetKey, out uint value)
    {
        foreach (var key in keys)
        {
            if (key.KeyCRC != targetKey)
                continue;
            value = key.ValueCRC;
            return true;
        }
        value = 0;
        return false;
    }

    private static void SetSceneValue(ShaderPackage* shaderPackage, ShaderSelection* selection, uint key, uint value)
    {
        var keys = shaderPackage->SceneKeysSpan;
        for (var index = 0; index < keys.Length; index++)
        {
            if (keys[index] != key)
                continue;
            selection->SceneValues[index] = value;
            return;
        }
        throw new InvalidOperationException("The fixed shader package has no model-type scene key.");
    }

    private static ShaderPackage.ConstantSamplerUnknown FindConstant(ShaderPackage* shaderPackage, uint crc)
    {
        foreach (var constant in shaderPackage->ConstantsSpan)
        {
            if (constant.CRC == crc)
                return constant;
        }

        throw new InvalidOperationException($"The fixed shader package has no constant CRC 0x{crc:X8}.");
    }

    private static ShaderPackage.ConstantSamplerUnknown FindSampler(ShaderPackage* shaderPackage, uint crc, ushort samplerClass)
    {
        foreach (var sampler in shaderPackage->SamplersSpan)
        {
            if (sampler.CRC == crc && sampler.Slot == samplerClass)
                return sampler;
        }

        throw new InvalidOperationException($"The fixed shader package has no sampler CRC 0x{crc:X8} in class {samplerClass}.");
    }

    private static nint RequireSignature(ISigScanner sigScanner, string signature, string name)
    {
        if (!sigScanner.TryScanText(signature, out var address) || address == 0)
            throw new InvalidOperationException($"Required native function {name} was not found.");
        return address;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x28)]
    internal struct ShaderSelection
    {
        [FieldOffset(0x08)]
        public ShaderPackage* Package;

        [FieldOffset(0x10)]
        public uint* SceneValues;

        [FieldOffset(0x18)]
        public uint* MaterialValues;

        [FieldOffset(0x20)]
        public uint SubViewKey;

        [FieldOffset(0x24)]
        public uint SubViewValue;
    }
}

internal readonly record struct MaterialBindingIds(
    uint MaterialConstantId,
    uint InstanceParameterId,
    uint ModelConstantId,
    uint NormalMapSamplerId,
    uint IndexMapSamplerId,
    uint ColorTableSamplerId
);

internal readonly record struct MaterialHelperResult(nint OnRenderMaterial, uint Output, nint ShaderDescriptor);

internal readonly record struct ShaderPair(int Pass, nint Vertex, nint Pixel);
