using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Underpaint.Internal;

// Temporary change-only probe for correlating visible color flips with the
// natural render call used as Underpaint's submission rendezvous.
internal sealed unsafe class NativeSubmissionProbe(IPluginLog log)
{
    private const int VertexShaderOffset = 0x878;
    private const int PixelShaderOffset = 0x880;
    private const int ShaderDescriptorOffset = 0x8B8;
    private const int ConstantOffset = 0x940;
    private const uint SystemConstantId = 24;
    private const uint SceneConstantId = 30;

    private ProbeState last;
    private bool hasLast;
    private nint lockedCarrierModel;

    internal ProbeInput CaptureInput(byte* context, nint materialParameters)
    {
        var inner = *(nint*)materialParameters;
        var model = inner == 0 ? 0 : *(nint*)inner;
        return new ProbeInput(
            (nint)context,
            model,
            context[0x0B] & 0x0F,
            *(nint*)(context + VertexShaderOffset),
            *(nint*)(context + PixelShaderOffset),
            *(nint*)(context + ShaderDescriptorOffset),
            GetConstant(context, SystemConstantId),
            GetConstant(context, SceneConstantId)
        );
    }

    internal bool AcceptCarrier(ProbeInput input)
    {
        var carrier = Volatile.Read(ref lockedCarrierModel);
        if (carrier == 0)
        {
            carrier = Interlocked.CompareExchange(ref lockedCarrierModel, input.CarrierModel, 0);
            if (carrier == 0)
            {
                carrier = input.CarrierModel;
                log.Information("[Underpaint] Color probe locked carrier model 0x{CarrierModel:X}.", carrier);
            }
        }

        return input.CarrierModel == carrier;
    }

    internal void Observe(
        int frame,
        ProbeInput input,
        MaterialHelper.ShaderSelection* selection,
        ShaderPackage* shaderPackage,
        MaterialHelperResult helper,
        ShaderPair shaders
    )
    {
        var state = new ProbeState(
            input,
            helper.OnRenderMaterial,
            helper.Output,
            helper.ShaderDescriptor,
            shaders.Pass,
            shaders.Vertex,
            shaders.Pixel,
            Hash(selection->SceneValues, shaderPackage->SceneKeyCount),
            Hash(selection->MaterialValues, shaderPackage->MaterialKeyCount),
            selection->SubViewKey,
            selection->SubViewValue
        );
        if (hasLast && state == last)
            return;

        last = state;
        hasLast = true;
        log.Information(
            "[Underpaint] Color probe changed: Frame={Frame}, CarrierModel=0x{CarrierModel:X}, "
                + "Context=0x{Context:X}, "
                + "NaturalPass={NaturalPass}, NaturalVS=0x{NaturalVS:X}, NaturalPS=0x{NaturalPS:X}, "
                + "NaturalDescriptor=0x{NaturalDescriptor:X}, SystemCB=0x{SystemConstant:X}, SceneCB=0x{SceneConstant:X}, "
                + "OnRenderMaterial=0x{OnRenderMaterial:X}, Output40=0x{Output:X8}, "
                + "OwnedPass={OwnedPass}, OwnedVS=0x{OwnedVS:X}, OwnedPS=0x{OwnedPS:X}, "
                + "OwnedDescriptor=0x{OwnedDescriptor:X}, SceneHash=0x{SceneHash:X8}, "
                + "MaterialHash=0x{MaterialHash:X8}, SubViewKey=0x{SubViewKey:X8}, SubViewValue=0x{SubViewValue:X8}.",
            frame,
            input.CarrierModel,
            input.Context,
            input.NaturalPass,
            input.NaturalVertexShader,
            input.NaturalPixelShader,
            input.NaturalDescriptor,
            input.SystemConstant,
            input.SceneConstant,
            helper.OnRenderMaterial,
            helper.Output,
            shaders.Pass,
            shaders.Vertex,
            shaders.Pixel,
            helper.ShaderDescriptor,
            state.SceneHash,
            state.MaterialHash,
            state.SubViewKey,
            state.SubViewValue
        );
    }

    private static nint GetConstant(byte* context, uint id) => *(nint*)(context + ConstantOffset + id * sizeof(nint));

    private static uint Hash(uint* values, int count)
    {
        if (values == null)
            return 0;

        var hash = 2166136261u;
        for (var index = 0; index < count; index++)
            hash = (hash ^ values[index]) * 16777619;
        return hash;
    }

    internal readonly record struct ProbeInput(
        nint Context,
        nint CarrierModel,
        int NaturalPass,
        nint NaturalVertexShader,
        nint NaturalPixelShader,
        nint NaturalDescriptor,
        nint SystemConstant,
        nint SceneConstant
    );

    private readonly record struct ProbeState(
        ProbeInput Input,
        nint OnRenderMaterial,
        uint Output,
        nint OwnedDescriptor,
        int OwnedPass,
        nint OwnedVertexShader,
        nint OwnedPixelShader,
        uint SceneHash,
        uint MaterialHash,
        uint SubViewKey,
        uint SubViewValue
    );
}
