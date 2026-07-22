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
    private const uint ApplyMaterialSamplerId = 6;
    private const uint OnRenderMaterialSamplerId = 62;
    private const uint ModelConstantCrc = 0x4E0A5472;
    private const ushort ModelConstantRegisters = 1;

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

    internal MaterialHelperResult Validate(ModelRenderer* renderer, byte* context, ConstantBuffer* modelConstant)
    {
        var targetMaterial = material.Material;
        var shaderPackage = material.ShaderPackage;
        if (targetMaterial == null || shaderPackage == null)
            throw new InvalidOperationException("The fixed donor material is not ready.");

        var modelConstantEntry = FindConstant(shaderPackage, ModelConstantCrc);
        if (modelConstantEntry.Size != ModelConstantRegisters)
            throw new InvalidOperationException("The fixed shader package has an unexpected model constant size.");
        if (modelConstant == null || modelConstant->ByteSize != modelConstantEntry.Size * 16)
            throw new InvalidOperationException("The owned model constant does not match the fixed shader package.");

        var model = stackalloc Model[1];
        var modelParameters = stackalloc ModelRenderer.OnRenderModelParams[1];
        var materialParameters = stackalloc ModelRenderer.OnRenderMaterialParams2[1];
        var selection = stackalloc ShaderSelection[1];
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
            materialParameters->Inner = modelParameters;
            *(ShaderSelection**)((byte*)materialParameters + 0x30) = selection;
            CopySceneValues(renderer, shaderPackage, selection);

            var subViewKey = renderer->SubViewKeys[0];
            selection->SubViewKey = subViewKey.KeyCRC;
            selection->SubViewValue = subViewKey.ValueCRC;

            var savedState = new ContextState(context);
            try
            {
                var onRenderMaterialResult = renderer->OnRenderMaterial(materialParameters, targetMaterial, 0);
                applyMaterial(selection, targetMaterial);
                var shaderDescriptor = resolveShaderSelection(shaderPackage, selection);
                if (selection->MaterialValues == null || shaderDescriptor == 0)
                    throw new InvalidOperationException("The fixed material did not resolve a shader selection.");

                return new MaterialHelperResult(
                    (nint)onRenderMaterialResult,
                    *(uint*)((byte*)materialParameters + 0x40),
                    shaderDescriptor,
                    modelConstantEntry.Id
                );
            }
            finally
            {
                savedState.Restore(context);
            }
        }
        finally
        {
            destroyShaderSelection(selection);
        }
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

    private static nint RequireSignature(ISigScanner sigScanner, string signature, string name)
    {
        if (!sigScanner.TryScanText(signature, out var address) || address == 0)
            throw new InvalidOperationException($"Required native function {name} was not found.");
        return address;
    }

    [StructLayout(LayoutKind.Explicit, Size = 0x28)]
    private struct ShaderSelection
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

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private readonly struct SamplerState
    {
        [FieldOffset(0)]
        private readonly nint unknown;

        [FieldOffset(8)]
        private readonly nint texture;

        [FieldOffset(16)]
        private readonly uint flags;
    }

    private readonly struct ContextState(byte* context)
    {
        private readonly ulong materialConstant = *(ulong*)(context + 0x940 + MaterialConstantId * sizeof(ulong));
        private readonly SamplerState applyMaterialSampler = *(SamplerState*)(context + 0x1140 + ApplyMaterialSamplerId * 24);
        private readonly SamplerState onRenderMaterialSampler = *(SamplerState*)(context + 0x1140 + OnRenderMaterialSamplerId * 24);

        internal void Restore(byte* context)
        {
            *(ulong*)(context + 0x940 + MaterialConstantId * sizeof(ulong)) = materialConstant;
            *(SamplerState*)(context + 0x1140 + ApplyMaterialSamplerId * 24) = applyMaterialSampler;
            *(SamplerState*)(context + 0x1140 + OnRenderMaterialSamplerId * 24) = onRenderMaterialSampler;
        }
    }
}

internal readonly record struct MaterialHelperResult(nint OnRenderMaterial, uint Output, nint ShaderDescriptor, uint ModelConstantId);
