using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Underpaint.Internal;

internal unsafe ref struct NativeContextState
{
    // These offsets were exercised by the archived native-submission prototype.
    // FFCS does not currently expose these Context fields.
    private const int IndexBufferOffset = 0x888;
    private const int VertexDeclarationOffset = 0x890;
    private const int VertexShaderOffset = 0x878;
    private const int PixelShaderOffset = 0x880;
    private const int ShaderDescriptorOffset = 0x8B8;
    private const int StreamOffset = 0x8C0;
    private const int ConstantOffset = 0x940;
    private const int SamplerOffset = 0x1140;
    private const int StreamSize = 16;
    private const int SamplerSize = 24;

    private readonly byte* context;
    private readonly uint worldConstantId;
    private readonly uint materialConstantId;
    private readonly uint instanceConstantId;
    private readonly uint modelConstantId;
    private readonly uint normalSamplerId;
    private readonly uint indexSamplerId;
    private readonly uint tableSamplerId;
    private readonly uint unidentifiedSamplerId;
    private readonly nint indexBuffer;
    private readonly nint vertexDeclaration;
    private readonly nint vertexShader;
    private readonly nint pixelShader;
    private readonly nint shaderDescriptor;
    private readonly StreamState stream0;
    private readonly StreamState stream1;
    private readonly StreamState stream2;
    private readonly StreamState stream3;
    private readonly nint worldConstant;
    private readonly nint materialConstant;
    private readonly nint instanceConstant;
    private readonly nint modelConstant;
    private readonly SamplerState normalSampler;
    private readonly SamplerState indexSampler;
    private readonly SamplerState tableSampler;
    private readonly SamplerState unidentifiedSampler;

    internal NativeContextState(byte* context, uint worldConstantId, MaterialBindingIds material)
    {
        this.context = context;
        this.worldConstantId = worldConstantId;
        materialConstantId = material.MaterialConstantId;
        instanceConstantId = material.InstanceConstantId;
        modelConstantId = material.ModelConstantId;
        normalSamplerId = material.NormalSamplerId;
        indexSamplerId = material.IndexSamplerId;
        tableSamplerId = material.TableSamplerId;
        unidentifiedSamplerId = material.UnidentifiedSamplerId;

        indexBuffer = *(nint*)(context + IndexBufferOffset);
        vertexDeclaration = *(nint*)(context + VertexDeclarationOffset);
        vertexShader = *(nint*)(context + VertexShaderOffset);
        pixelShader = *(nint*)(context + PixelShaderOffset);
        shaderDescriptor = *(nint*)(context + ShaderDescriptorOffset);
        stream0 = GetStream(0);
        stream1 = GetStream(1);
        stream2 = GetStream(2);
        stream3 = GetStream(3);
        worldConstant = GetConstant(worldConstantId);
        materialConstant = GetConstant(materialConstantId);
        instanceConstant = GetConstant(instanceConstantId);
        modelConstant = GetConstant(modelConstantId);
        normalSampler = GetSampler(normalSamplerId);
        indexSampler = GetSampler(indexSamplerId);
        tableSampler = GetSampler(tableSamplerId);
        unidentifiedSampler = GetSampler(unidentifiedSamplerId);
    }

    internal void Install(NativeResources resources, NativeMesh mesh, NativePrimitiveResources primitive)
    {
        *(nint*)(context + IndexBufferOffset) = mesh.IndexBuffer;
        *(nint*)(context + VertexDeclarationOffset) = resources.VertexDeclaration;
        SetStream(0, new StreamState(mesh.Stream0Buffer, PackStreamBinding(0, NativeResources.Stream0Stride)));
        SetStream(1, new StreamState(primitive.Stream1Buffer, PackStreamBinding(0, NativeResources.Stream1Stride)));
        SetStream(2, default);
        SetStream(3, default);

        SetConstant(worldConstantId, primitive.WorldConstant);
        SetConstant(materialConstantId, (nint)resources.MaterialConstant);
        SetConstant(instanceConstantId, primitive.InstanceConstant);
        SetConstant(modelConstantId, (nint)resources.ModelConstant);
        SetSampler(normalSamplerId, resources.WhiteTexture);
        SetSampler(indexSamplerId, resources.WhiteTexture);
        SetSampler(tableSamplerId, resources.WhiteTexture);
        SetSampler(unidentifiedSamplerId, resources.WhiteTexture);
    }

    internal void InstallShaders(ShaderPair shaders, nint descriptor)
    {
        *(nint*)(context + VertexShaderOffset) = shaders.Vertex;
        *(nint*)(context + PixelShaderOffset) = shaders.Pixel;
        *(nint*)(context + ShaderDescriptorOffset) = descriptor;
    }

    internal void Restore()
    {
        *(nint*)(context + IndexBufferOffset) = indexBuffer;
        *(nint*)(context + VertexDeclarationOffset) = vertexDeclaration;
        *(nint*)(context + VertexShaderOffset) = vertexShader;
        *(nint*)(context + PixelShaderOffset) = pixelShader;
        *(nint*)(context + ShaderDescriptorOffset) = shaderDescriptor;
        SetStream(0, stream0);
        SetStream(1, stream1);
        SetStream(2, stream2);
        SetStream(3, stream3);
        SetConstant(worldConstantId, worldConstant);
        SetConstant(materialConstantId, materialConstant);
        SetConstant(instanceConstantId, instanceConstant);
        SetConstant(modelConstantId, modelConstant);
        SetSampler(normalSamplerId, normalSampler);
        SetSampler(indexSamplerId, indexSampler);
        SetSampler(tableSamplerId, tableSampler);
        SetSampler(unidentifiedSamplerId, unidentifiedSampler);
    }

    private readonly StreamState GetStream(int index) => *(StreamState*)(context + StreamOffset + index * StreamSize);

    private readonly void SetStream(int index, StreamState value) => *(StreamState*)(context + StreamOffset + index * StreamSize) = value;

    private readonly nint GetConstant(uint id) => *(nint*)(context + ConstantOffset + id * sizeof(nint));

    private readonly void SetConstant(uint id, nint value) => *(nint*)(context + ConstantOffset + id * sizeof(nint)) = value;

    private readonly SamplerState GetSampler(uint id) => *(SamplerState*)(context + SamplerOffset + id * SamplerSize);

    private readonly void SetSampler(uint id, Texture* texture) => SetSampler(id, new SamplerState(0, (nint)texture, 0));

    private readonly void SetSampler(uint id, SamplerState value) => *(SamplerState*)(context + SamplerOffset + id * SamplerSize) = value;

    private static ulong PackStreamBinding(int byteOffset, int stride) => ((ulong)(uint)byteOffset << 8) | (byte)stride;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct StreamState(nint Buffer, ulong OffsetAndStride);

    [StructLayout(LayoutKind.Explicit, Size = SamplerSize)]
    private readonly record struct SamplerState
    {
        [FieldOffset(0)]
        internal readonly nint Unknown;

        [FieldOffset(8)]
        internal readonly nint Texture;

        [FieldOffset(16)]
        internal readonly uint Flags;

        internal SamplerState(nint unknown, nint texture, uint flags)
        {
            Unknown = unknown;
            Texture = texture;
            Flags = flags;
        }
    }
}
