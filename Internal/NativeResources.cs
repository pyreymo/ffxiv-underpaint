using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;
using FFXIVClientStructs.FFXIV.Client.System.Resource;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;

namespace Underpaint.Internal;

internal sealed unsafe class NativeResources : IDisposable
{
    private const string CreateVertexBufferSignature = "40 55 56 57 41 57 48 83 EC 28";
    private const string InitializeVertexBufferSignature =
        "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 50 48 8B 05 ?? ?? ?? ?? 48 33 C4 48 89 44 24 ?? 44 8B 49";
    private const string CreateIndexBufferSignature = "48 89 5C 24 ?? 48 89 6C 24 ?? 48 89 7C 24 ?? 41 56 48 83 EC 20 48 8B 05";
    private const string InitializeIndexBufferSignature = "40 53 48 83 EC 20 F7 41 40 00 08 00 00 48 8B D9";
    private const string CreateVertexDeclarationSignature = "48 8B 49 ?? E9 ?? ?? ?? ?? CC CC CC CC CC CC CC 40 53 55 57";

    // Captured from a native rigid character-material draw using the two-stream,
    // non-skinned charactertransparency vertex path.
    // The individual flag bits have not been identified.
    private const uint StaticBufferCreationFlags = 0x804;

    // Raw third argument passed by the captured native index-buffer creation.
    // Its exact engine meaning and official enum name have not been identified.
    private const int IndexBufferThirdArgument = 1;

    // Raw fourth arguments passed by the captured native buffer creations.
    // Their exact engine meaning has not been identified.
    private const byte VertexBufferFourthArgument = 7;
    private const byte IndexBufferFourthArgument = 0;

    // The archived native-submission prototype verified that buffers created with
    // this value expose writable storage through LoadSourcePointer and can be used
    // by native draw commands. The individual flag bits have not been identified.
    private const uint WritableConstantBufferFlags = 0x2;
    private const uint ConstantBufferLastArgument = 0;

    // World stores current and previous 4x4 matrices in the archived native path.
    private const int WorldConstantBytes = 128;

    // Captured from charactertransparency.shpk package constants:
    // CRC 0x20A30B34 has 11 float4 registers; CRC 0x4E0A5472 has one.
    private const int InstanceConstantBytes = 11 * 16;
    private const int ModelConstantBytes = 16;

    // Captured from a natural charactertransparency material constant buffer.
    private const int MaterialConstantBytes = 416;

    private const string WhiteTexturePath = "chara/common/texture/white.tex";

    // ResourceType.Tex in the game's resource-loading ABI.
    // A transposed value returned a different handle type and crashed when +0x128 was used as Texture*.
    private const uint TextureFileType = 0x00746578;

    // Lumina.Misc.Crc32.Get(WhiteTexturePath).
    private const uint WhiteTexturePathHash = 0x84815A1A;

    // Captured byte-for-byte from the same native two-stream charactertransparency draw.
    // Each record is the binary element accepted by the game's vertex-declaration creator.
    // Format and attribute are game identifiers; their general enum names are not yet known.
    private static readonly VertexElement[] VertexElements =
    [
        new(0, 0, 0x13, 0), // Stream0Vertex.Position, 12 bytes
        new(0, 12, 0x3C, 1), // Stream0Vertex.BlendWeight, 4 bytes
        new(0, 16, 0x3C, 7), // Stream0Vertex.BlendIndices, 4 bytes
        new(1, 0, 0x1C, 2), // Stream1Vertex.Normal, 8 bytes
        new(1, 8, 0x24, 15), // Stream1Vertex.Binormal, 4 bytes
        new(1, 12, 0x24, 3), // Stream1Vertex.Color0, 4 bytes
        new(1, 16, 0x1C, 8), // Stream1Vertex.TexCoord0, 8 bytes
    ];

    private readonly delegate* unmanaged<Device*, int, uint, byte, nint> createVertexBuffer;
    private readonly delegate* unmanaged<nint, void*, byte> initializeVertexBuffer;
    private readonly Dictionary<ulong, NativePrimitiveResources> primitives = [];
    private NativeMesh triangleMesh;
    private NativeMesh quadMesh;
    private nint vertexDeclaration;
    private nint instanceConstant;
    private nint modelConstant;
    private nint materialConstant;
    private TextureResourceHandle* whiteTextureResource;

    internal nint VertexDeclaration => vertexDeclaration;
    internal ConstantBuffer* InstanceConstant => (ConstantBuffer*)instanceConstant;
    internal ConstantBuffer* ModelConstant => (ConstantBuffer*)modelConstant;
    internal ConstantBuffer* MaterialConstant => (ConstantBuffer*)materialConstant;
    internal Texture* WhiteTexture => whiteTextureResource == null ? null : whiteTextureResource->Texture;
    internal static int Stream0Stride => sizeof(Stream0Vertex);
    internal static int Stream1Stride => sizeof(Stream1Vertex);

    internal NativeResources(ISigScanner sigScanner)
    {
        createVertexBuffer = (delegate* unmanaged<Device*, int, uint, byte, nint>)RequireSignature(
            sigScanner,
            CreateVertexBufferSignature,
            "CreateVertexBuffer"
        );
        initializeVertexBuffer = (delegate* unmanaged<nint, void*, byte>)RequireSignature(
            sigScanner,
            InitializeVertexBufferSignature,
            "InitializeVertexBuffer"
        );
        var createIndexBuffer = (delegate* unmanaged<Device*, int, int, uint, byte, nint>)RequireSignature(
            sigScanner,
            CreateIndexBufferSignature,
            "CreateIndexBuffer"
        );
        var initializeIndexBuffer = (delegate* unmanaged<nint, void*, byte>)RequireSignature(
            sigScanner,
            InitializeIndexBufferSignature,
            "InitializeIndexBuffer"
        );
        var createVertexDeclaration = (delegate* unmanaged<Device*, byte*, uint, nint>)RequireSignature(
            sigScanner,
            CreateVertexDeclarationSignature,
            "CreateVertexDeclaration"
        );

        var device = Device.Instance();
        if (device == null)
            throw new InvalidOperationException("The native graphics device is not available.");

        ReadOnlySpan<Stream0Vertex> triangleVertices =
        [
            new(new Vector3(-0.5f, 0, 0)),
            new(new Vector3(0.5f, 0, 0)),
            new(new Vector3(0, 1, 0)),
        ];
        ReadOnlySpan<ushort> triangleIndices = [0, 1, 2];
        ReadOnlySpan<Stream0Vertex> quadVertices =
        [
            new(new Vector3(-0.5f, 0, 0)),
            new(new Vector3(0.5f, 0, 0)),
            new(new Vector3(0.5f, 1, 0)),
            new(new Vector3(-0.5f, 1, 0)),
        ];
        ReadOnlySpan<ushort> quadIndices = [0, 1, 2, 0, 2, 3];

        try
        {
            triangleMesh = CreateMesh(device, triangleVertices, triangleIndices, createIndexBuffer, initializeIndexBuffer);
            quadMesh = CreateMesh(device, quadVertices, quadIndices, createIndexBuffer, initializeIndexBuffer);
            fixed (VertexElement* elements = VertexElements)
            {
                vertexDeclaration = createVertexDeclaration(device, (byte*)elements, (uint)VertexElements.Length);
            }

            if (vertexDeclaration == 0)
                throw new InvalidOperationException("The game rejected the fixed vertex declaration.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        var loadedWhiteTexture = whiteTextureResource;
        whiteTextureResource = null;
        if (loadedWhiteTexture != null)
            loadedWhiteTexture->DecRef();

        foreach (var primitive in primitives.Values)
        {
            var stream1 = primitive.Stream1Buffer;
            var world = primitive.WorldConstant;
            var instance = primitive.InstanceConstant;
            Release(ref instance);
            Release(ref world);
            Release(ref stream1);
        }
        primitives.Clear();

        Release(ref materialConstant);
        Release(ref modelConstant);
        Release(ref instanceConstant);
        Release(ref vertexDeclaration);
        ReleaseMesh(ref quadMesh);
        ReleaseMesh(ref triangleMesh);
    }

    internal NativeMesh GetMesh(PrimitiveType type) =>
        type switch
        {
            PrimitiveType.Triangle => triangleMesh,
            PrimitiveType.Quad => quadMesh,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    internal NativePrimitiveResources WritePrimitive(
        PrimitiveType type,
        ulong id,
        Matrix4x4 currentWorldView,
        Matrix4x4 previousWorldView,
        Vector3 color,
        float alpha,
        float ditherFade
    )
    {
        if (primitives.TryGetValue(id, out var primitive) && primitive.Type != type)
        {
            ReleasePrimitive(ref primitive);
            primitives.Remove(id);
        }

        var packedAlpha = PackNormalizedByte(alpha);
        if (!primitives.TryGetValue(id, out primitive))
        {
            primitive = CreatePrimitiveResources(type, GetMesh(type).VertexCount, packedAlpha);
            primitives.Add(id, primitive);
        }
        else if (primitive.Alpha != packedAlpha)
        {
            var previousStream1 = primitive.Stream1Buffer;
            var stream1 = CreateStream1Buffer(type, GetMesh(type).VertexCount, packedAlpha);
            primitive = primitive with { Stream1Buffer = stream1, Alpha = packedAlpha };
            primitives[id] = primitive;
            Release(ref previousStream1);
        }

        WriteWorldConstant((ConstantBuffer*)primitive.WorldConstant, currentWorldView, previousWorldView);
        WriteInstanceConstant((ConstantBuffer*)primitive.InstanceConstant, new Vector4(color.X, color.Y, color.Z, ditherFade));
        return primitive;
    }

    internal void LoadWhiteTexture()
    {
        if (whiteTextureResource != null)
            return;

        var resourceManager = ResourceManager.Instance();
        if (resourceManager == null)
            throw new InvalidOperationException("The native resource manager is not available.");

        var category = ResourceCategory.Chara;
        var fileType = TextureFileType;
        var pathHash = WhiteTexturePathHash;
        var loaded = (TextureResourceHandle*)
            resourceManager->GetResourceSync(&category, &fileType, &pathHash, WhiteTexturePath, null, null, 0);
        if (loaded == null)
            throw new InvalidOperationException("The fixed white texture could not be loaded.");

        if (loaded->Texture == null)
        {
            loaded->DecRef();
            throw new InvalidOperationException("The fixed white texture is not ready.");
        }

        whiteTextureResource = loaded;
    }

    internal void CreateConstants()
    {
        if (instanceConstant != 0)
            return;

        var device = Device.Instance();
        if (device == null)
            throw new InvalidOperationException("The native graphics device is not available.");

        try
        {
            instanceConstant = CreateAndClearConstantBuffer(device, InstanceConstantBytes, "instance");
            modelConstant = CreateAndClearConstantBuffer(device, ModelConstantBytes, "model");
            materialConstant = CreateAndClearConstantBuffer(device, MaterialConstantBytes, "material");
        }
        catch
        {
            Release(ref materialConstant);
            Release(ref modelConstant);
            Release(ref instanceConstant);
            throw;
        }
    }

    internal void WriteSharedConstants(ShaderPackage* shaderPackage)
    {
        WriteInstanceConstant(InstanceConstant, Vector4.One);
        WriteModelConstant();
        WriteMaterialConstant(shaderPackage);
    }

    private static void WriteWorldConstant(ConstantBuffer* worldConstant, Matrix4x4 currentWorldView, Matrix4x4 previousWorldView)
    {
        var data = worldConstant->LoadSourcePointer(0, WorldConstantBytes);
        if (data == null)
            throw new InvalidOperationException("The world constant buffer has no writable storage.");

        *(Matrix4x4*)data = Matrix4x4.Transpose(currentWorldView);
        *(Matrix4x4*)((byte*)data + sizeof(Matrix4x4)) = Matrix4x4.Transpose(previousWorldView);
    }

    private NativePrimitiveResources CreatePrimitiveResources(PrimitiveType type, int vertexCount, byte alpha)
    {
        var device = Device.Instance();
        if (device == null)
            throw new InvalidOperationException("The native graphics device is not available.");

        nint stream1 = 0;
        nint world = 0;
        nint instance = 0;
        try
        {
            stream1 = CreateStream1Buffer(type, vertexCount, alpha);
            world = CreateAndClearConstantBuffer(device, WorldConstantBytes, $"{type} world");
            instance = CreateAndClearConstantBuffer(device, InstanceConstantBytes, $"{type} instance");
            return new NativePrimitiveResources(type, stream1, world, instance, alpha);
        }
        catch
        {
            Release(ref instance);
            Release(ref world);
            Release(ref stream1);
            throw;
        }
    }

    private nint CreateStream1Buffer(PrimitiveType type, int vertexCount, byte alpha)
    {
        var device = Device.Instance();
        if (device == null)
            throw new InvalidOperationException("The native graphics device is not available.");

        var vertices = stackalloc Stream1Vertex[vertexCount];
        WriteStream1(type, new Span<Stream1Vertex>(vertices, vertexCount), alpha / 255f);
        var stream1 = createVertexBuffer(
            device,
            vertexCount * sizeof(Stream1Vertex),
            StaticBufferCreationFlags,
            VertexBufferFourthArgument
        );
        if (stream1 != 0 && initializeVertexBuffer(stream1, vertices) != 0)
            return stream1;

        Release(ref stream1);
        throw new InvalidOperationException($"The game rejected the {type} attribute buffer.");
    }

    private NativeMesh CreateMesh(
        Device* device,
        ReadOnlySpan<Stream0Vertex> vertices,
        ReadOnlySpan<ushort> indices,
        delegate* unmanaged<Device*, int, int, uint, byte, nint> createIndexBuffer,
        delegate* unmanaged<nint, void*, byte> initializeIndexBuffer
    )
    {
        nint stream0 = 0;
        nint indexBuffer = 0;
        try
        {
            stream0 = createVertexBuffer(
                device,
                vertices.Length * sizeof(Stream0Vertex),
                StaticBufferCreationFlags,
                VertexBufferFourthArgument
            );
            indexBuffer = createIndexBuffer(
                device,
                indices.Length * sizeof(ushort),
                IndexBufferThirdArgument,
                StaticBufferCreationFlags,
                IndexBufferFourthArgument
            );

            fixed (Stream0Vertex* vertexData = vertices)
            fixed (ushort* indexData = indices)
            {
                if (
                    stream0 == 0
                    || indexBuffer == 0
                    || initializeVertexBuffer(stream0, vertexData) == 0
                    || initializeIndexBuffer(indexBuffer, indexData) == 0
                )
                    throw new InvalidOperationException("The game rejected fixed primitive geometry.");
            }

            return new NativeMesh(stream0, indexBuffer, vertices.Length, indices.Length);
        }
        catch
        {
            Release(ref indexBuffer);
            Release(ref stream0);
            throw;
        }
    }

    private static int GetVertexCount(PrimitiveType type) =>
        type switch
        {
            PrimitiveType.Triangle => 3,
            PrimitiveType.Quad => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };

    private static void WriteStream1(PrimitiveType type, Span<Stream1Vertex> vertices, float alpha)
    {
        // UV-isolation probe: collapse triangle and quad sampling to one material location.
        var probeUv = new Vector2(0.5f, 0.5f);
        switch (type)
        {
            case PrimitiveType.Triangle:
                vertices[0] = new Stream1Vertex(probeUv, alpha);
                vertices[1] = new Stream1Vertex(probeUv, alpha);
                vertices[2] = new Stream1Vertex(probeUv, alpha);
                break;
            case PrimitiveType.Quad:
                vertices[0] = new Stream1Vertex(probeUv, alpha);
                vertices[1] = new Stream1Vertex(probeUv, alpha);
                vertices[2] = new Stream1Vertex(probeUv, alpha);
                vertices[3] = new Stream1Vertex(probeUv, alpha);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(type));
        }
    }

    private static nint CreateAndClearConstantBuffer(Device* device, int byteSize, string name)
    {
        var buffer = device->CreateConstantBuffer(byteSize, WritableConstantBufferFlags, ConstantBufferLastArgument);
        if (buffer == null)
            throw new InvalidOperationException($"The game rejected the {name} constant buffer.");

        var resource = (nint)buffer;
        try
        {
            var data = buffer->LoadSourcePointer(0, byteSize);
            if (data == null)
                throw new InvalidOperationException($"The {name} constant buffer has no writable storage.");
            NativeMemory.Clear(data, (nuint)byteSize);
            return resource;
        }
        catch
        {
            Release(ref resource);
            throw;
        }
    }

    private void WriteModelConstant()
    {
        var data = ModelConstant->LoadSourcePointer(0, ModelConstantBytes);
        if (data == null)
            throw new InvalidOperationException("The model constant buffer has no writable storage.");

        *(Vector4*)data = new Vector4(1, 0, 0, 0);
    }

    private static void WriteInstanceConstant(ConstantBuffer* instanceConstant, Vector4 color)
    {
        var data = instanceConstant->LoadSourcePointer(0, InstanceConstantBytes);
        if (data == null)
            throw new InvalidOperationException("The instance constant buffer has no writable storage.");

        var registers = new Span<Vector4>(data, InstanceConstantBytes / sizeof(Vector4));
        registers.Clear();

        // Fixed A/B tests confirmed that register 0 controls output RGB and dither fade
        // for the selected charactertransparency variant. Its general engine name is unknown.
        registers[0] = color;
        registers[1] = Vector4.One;
        registers[2] = Vector4.One;
        registers[3] = Vector4.One;
        registers[4] = new Vector4(0, 2, 0, 1);
        registers[10] = new Vector4(0, 1, 0, 0);
    }

    private void WriteMaterialConstant(ShaderPackage* shaderPackage)
    {
        if (shaderPackage == null || shaderPackage->MaterialConstantBufferSize != MaterialConstantBytes)
            throw new InvalidOperationException("The fixed shader package has an unexpected material constant size.");

        var defaults = shaderPackage->MaterialElementDefaultsSpan;
        if (defaults.Length != MaterialConstantBytes)
            throw new InvalidOperationException("The fixed shader package has no complete material defaults.");

        var data = MaterialConstant->LoadSourcePointer(0, MaterialConstantBytes);
        if (data == null)
            throw new InvalidOperationException("The material constant buffer has no writable storage.");
        defaults.CopyTo(new Span<byte>(data, MaterialConstantBytes));
    }

    private static nint RequireSignature(ISigScanner sigScanner, string signature, string name)
    {
        if (!sigScanner.TryScanText(signature, out var address) || address == 0)
            throw new InvalidOperationException($"Required native function {name} was not found.");
        return address;
    }

    private static void Release(ref nint resource)
    {
        var value = resource;
        resource = 0;
        if (value == 0)
            return;

        var release = (delegate* unmanaged<nint, void>)(*(nint*)(*(nint*)value + 0x18));
        release(value);
    }

    private static void ReleaseMesh(ref NativeMesh mesh)
    {
        var indexBuffer = mesh.IndexBuffer;
        var stream0 = mesh.Stream0Buffer;
        mesh = default;
        Release(ref indexBuffer);
        Release(ref stream0);
    }

    private static void ReleasePrimitive(ref NativePrimitiveResources primitive)
    {
        var instance = primitive.InstanceConstant;
        var world = primitive.WorldConstant;
        var stream1 = primitive.Stream1Buffer;
        primitive = default;
        Release(ref instance);
        Release(ref world);
        Release(ref stream1);
    }

    private static ulong PackHalf4(float x, float y, float z, float w)
    {
        return BitConverter.HalfToUInt16Bits((Half)x)
            | ((ulong)BitConverter.HalfToUInt16Bits((Half)y) << 16)
            | ((ulong)BitConverter.HalfToUInt16Bits((Half)z) << 32)
            | ((ulong)BitConverter.HalfToUInt16Bits((Half)w) << 48);
    }

    private static uint PackNormalizedByte4(float x, float y, float z, float w)
    {
        return PackNormalizedByte(x)
            | ((uint)PackNormalizedByte(y) << 8)
            | ((uint)PackNormalizedByte(z) << 16)
            | ((uint)PackNormalizedByte(w) << 24);
    }

    private static byte PackNormalizedByte(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct VertexElement(byte stream, byte offset, byte format, byte attribute)
    {
        public readonly byte Stream = stream;
        public readonly byte Offset = offset;
        public readonly byte Format = format;
        public readonly byte Attribute = attribute;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Stream0Vertex(Vector3 position)
    {
        public readonly Vector3 Position = position;

        public readonly uint BlendWeight = 0x000000FF;
        public readonly uint BlendIndices = 0x00000000;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Stream1Vertex
    {
        public Stream1Vertex(Vector2 textureCoordinate, float alpha = 1)
        {
            Normal = PackHalf4(0, 0, 1, 0);

            // The captured declaration identifies the input as Binormal, but the official
            // name and channel encoding of format 0x24 have not been identified.
            Binormal = 0x00800080;
            Color0 = PackNormalizedByte4(1, 1, 1, alpha);
            TexCoord0 = PackHalf4(textureCoordinate.X, textureCoordinate.Y, -1, 2);
        }

        public readonly ulong Normal;
        public readonly uint Binormal;
        public readonly uint Color0;
        public readonly ulong TexCoord0;
    }
}

internal readonly record struct NativeMesh(nint Stream0Buffer, nint IndexBuffer, int VertexCount, int IndexCount);

internal readonly record struct NativePrimitiveResources(
    PrimitiveType Type,
    nint Stream1Buffer,
    nint WorldConstant,
    nint InstanceConstant,
    byte Alpha
);
