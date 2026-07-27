using System.Runtime.InteropServices;

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
    private readonly uint instanceParameterId;
    private readonly uint modelConstantId;
    private readonly uint normalMapSamplerId;
    private readonly uint indexMapSamplerId;
    private readonly uint maskMapSamplerId;
    private readonly uint colorTableSamplerId;
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
    private readonly nint instanceParameters;
    private readonly nint modelConstant;
    private readonly SamplerState normalMapSampler;
    private readonly SamplerState indexMapSampler;
    private readonly SamplerState maskMapSampler;
    private readonly SamplerState colorTableSampler;

    internal NativeContextState(byte* context, uint worldConstantId, MaterialBindingIds material)
    {
        this.context = context;
        this.worldConstantId = worldConstantId;
        materialConstantId = material.MaterialConstantId;
        instanceParameterId = material.InstanceParameterId;
        modelConstantId = material.ModelConstantId;
        normalMapSamplerId = material.NormalMapSamplerId;
        indexMapSamplerId = material.IndexMapSamplerId;
        maskMapSamplerId = material.MaskMapSamplerId;
        colorTableSamplerId = material.ColorTableSamplerId;

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
        instanceParameters = GetConstant(instanceParameterId);
        modelConstant = GetConstant(modelConstantId);
        normalMapSampler = GetSampler(normalMapSamplerId);
        indexMapSampler = GetSampler(indexMapSamplerId);
        maskMapSampler = GetSampler(maskMapSamplerId);
        colorTableSampler = GetSampler(colorTableSamplerId);
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
        SetConstant(instanceParameterId, primitive.InstanceParameters);
        SetConstant(modelConstantId, (nint)resources.ModelConstant);
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
        SetConstant(instanceParameterId, instanceParameters);
        SetConstant(modelConstantId, modelConstant);
        SetSampler(normalMapSamplerId, normalMapSampler);
        SetSampler(indexMapSamplerId, indexMapSampler);
        SetSampler(maskMapSamplerId, maskMapSampler);
        SetSampler(colorTableSamplerId, colorTableSampler);
    }

    private readonly StreamState GetStream(int index) => *(StreamState*)(context + StreamOffset + index * StreamSize);

    private readonly void SetStream(int index, StreamState value) => *(StreamState*)(context + StreamOffset + index * StreamSize) = value;

    private readonly nint GetConstant(uint id) => *(nint*)(context + ConstantOffset + id * sizeof(nint));

    private readonly void SetConstant(uint id, nint value) => *(nint*)(context + ConstantOffset + id * sizeof(nint)) = value;

    private readonly SamplerState GetSampler(uint id) => *(SamplerState*)(context + SamplerOffset + id * SamplerSize);

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
