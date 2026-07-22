#if DEBUG
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Underpaint.Internal;

internal static unsafe class SelectedResourceProbe
{
    internal static string Format(ShaderPackage* shaderPackage, nint shaderDescriptor)
    {
        var families = new Dictionary<(nint Vertex, nint Pixel), List<int>>();
        for (var pass = 0; pass < 16; pass++)
        {
            if (!TryGetPassShaders(shaderDescriptor, pass, out var vertex, out var pixel))
                continue;

            if (!families.TryGetValue(((nint)vertex, (nint)pixel), out var passes))
            {
                passes = [];
                families.Add(((nint)vertex, (nint)pixel), passes);
            }

            passes.Add(pass);
        }

        return string.Join(
            ';',
            families.Select(family =>
                $"passes:{string.Join('/', family.Value)}"
                + $"/vsc:[{FormatConstants(shaderPackage, family.Key.Vertex)}]"
                + $"/psc:[{FormatConstants(shaderPackage, family.Key.Pixel)}]"
                + $"/vss:[{FormatSamplers(shaderPackage, family.Key.Vertex)}]"
                + $"/pss:[{FormatSamplers(shaderPackage, family.Key.Pixel)}]"
            )
        );
    }

    private static string FormatConstants(ShaderPackage* shaderPackage, nint shaderAddress)
    {
        var shader = (PVShader*)shaderAddress;
        return string.Join(
            ',',
            shader
                ->ConstantBuffersSpan.ToArray()
                .Select(entry =>
                {
                    var resource = FindResource(shaderPackage->ConstantsSpan, entry.Id);
                    return $"CB{entry.Slot}=id:{entry.Id}/crc:0x{resource.CRC:X8}/size:{entry.Size * 16}";
                })
        );
    }

    private static string FormatSamplers(ShaderPackage* shaderPackage, nint shaderAddress)
    {
        var shader = (PVShader*)shaderAddress;
        return string.Join(
            ',',
            shader
                ->SamplersSpan.ToArray()
                .Select(entry =>
                {
                    var resource = FindResource(shaderPackage->SamplersSpan, entry.Id);
                    return $"S{entry.Slot}=id:{entry.Id}/crc:0x{resource.CRC:X8}/class:{resource.Slot}";
                })
        );
    }

    private static ShaderPackage.ConstantSamplerUnknown FindResource(Span<ShaderPackage.ConstantSamplerUnknown> resources, uint id)
    {
        foreach (var resource in resources)
        {
            if (resource.Id == id)
                return resource;
        }

        return new ShaderPackage.ConstantSamplerUnknown { Id = id };
    }

    private static bool TryGetPassShaders(nint shaderDescriptor, int pass, out PVShader* vertexShader, out PVShader* pixelShader)
    {
        vertexShader = null;
        pixelShader = null;
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

        vertexShader = *(PVShader**)(vertexStart + vertexIndex * sizeof(nint));
        pixelShader = *(PVShader**)(pixelStart + pixelIndex * sizeof(nint));
        return vertexShader != null && pixelShader != null;
    }
}
#endif
