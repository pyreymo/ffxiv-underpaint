using System.Globalization;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Underpaint.Internal;

// Temporary one-shot probe. Delete after the fixed SHPK's color/alpha inputs
// have been identified and recorded in RESEARCH.md.
internal static unsafe class MaterialElementProbe
{
    internal static string Describe(ShaderPackage* shaderPackage)
    {
        if (shaderPackage == null)
            return "unavailable";

        var defaults = shaderPackage->MaterialElementDefaultsSpan;
        var descriptions = new List<string>(shaderPackage->MaterialElementCount);
        foreach (var element in shaderPackage->MaterialElementsSpan)
        {
            var values = new List<string>(element.Size / sizeof(float));
            if (element.Offset + element.Size <= defaults.Length && element.Size % sizeof(float) == 0)
            {
                for (var byteOffset = 0; byteOffset < element.Size; byteOffset += sizeof(float))
                {
                    var bits = BitConverter.ToInt32(defaults.Slice(element.Offset + byteOffset, sizeof(float)));
                    values.Add(BitConverter.Int32BitsToSingle(bits).ToString("R", CultureInfo.InvariantCulture));
                }
            }

            descriptions.Add($"crc:0x{element.CRC:X8}/offset:{element.Offset}/size:{element.Size}/float:[{string.Join(',', values)}]");
        }

        return string.Join(';', descriptions);
    }
}
