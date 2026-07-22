using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

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
    private const uint BufferCreationFlags = 0x804;

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

    internal const int VertexCount = 3;
    internal const int IndexCount = 3;

    // Captured byte-for-byte from the same native two-stream charactertransparency draw.
    // Each record is the binary element accepted by the game's vertex-declaration creator.
    // Format and attribute are game identifiers; their general enum names are not yet known.
    private static readonly VertexElement[] VertexElements =
    [
        new(0, 0, 0x13, 0), // Stream0Vertex.Position, 12 bytes
        new(0, 12, 0x3C, 1), // Stream0Vertex.Attribute1, 4 bytes
        new(0, 16, 0x3C, 7), // Stream0Vertex.Attribute7, 4 bytes
        new(1, 0, 0x1C, 2), // Stream1Vertex.Attribute2, 8 bytes
        new(1, 8, 0x24, 15), // Stream1Vertex.Attribute15, 4 bytes
        new(1, 12, 0x24, 3), // Stream1Vertex.Attribute3, 4 bytes
        new(1, 16, 0x1C, 8), // Stream1Vertex.Attribute8, 8 bytes
    ];

    private nint vertexBuffer;
    private nint indexBuffer;
    private nint vertexDeclaration;
    private nint worldConstant;
    private nint instanceConstant;
    private nint modelConstant;
    private nint materialConstant;

    internal nint VertexBuffer => vertexBuffer;
    internal nint IndexBuffer => indexBuffer;
    internal nint VertexDeclaration => vertexDeclaration;
    internal ConstantBuffer* WorldConstant => (ConstantBuffer*)worldConstant;
    internal ConstantBuffer* InstanceConstant => (ConstantBuffer*)instanceConstant;
    internal ConstantBuffer* ModelConstant => (ConstantBuffer*)modelConstant;
    internal ConstantBuffer* MaterialConstant => (ConstantBuffer*)materialConstant;
    internal static int Stream0Stride => sizeof(Stream0Vertex);
    internal static int Stream1Stride => sizeof(Stream1Vertex);
    internal int Stream1Offset => VertexCount * sizeof(Stream0Vertex);

    internal NativeResources(ISigScanner sigScanner)
    {
        var createVertexBuffer = (delegate* unmanaged<Device*, int, uint, byte, nint>)RequireSignature(
            sigScanner,
            CreateVertexBufferSignature,
            "CreateVertexBuffer"
        );
        var initializeVertexBuffer = (delegate* unmanaged<nint, void*, byte>)RequireSignature(
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

        var stream0Bytes = VertexCount * sizeof(Stream0Vertex);
        var vertexBytes = stream0Bytes + VertexCount * sizeof(Stream1Vertex);
        var vertexData = stackalloc byte[vertexBytes];
        var stream0 = (Stream0Vertex*)vertexData;
        var stream1 = (Stream1Vertex*)(vertexData + stream0Bytes);

        stream0[0] = new Stream0Vertex(new Vector3(-0.5f, 0, 0));
        stream0[1] = new Stream0Vertex(new Vector3(0.5f, 0, 0));
        stream0[2] = new Stream0Vertex(new Vector3(0, 1, 0));
        stream1[0] = new Stream1Vertex(new Vector2(0, 1));
        stream1[1] = new Stream1Vertex(new Vector2(1, 1));
        stream1[2] = new Stream1Vertex(new Vector2(0.5f, 0));
        ushort* indices = stackalloc ushort[IndexCount] { 0, 1, 2 };

        try
        {
            vertexBuffer = createVertexBuffer(device, vertexBytes, BufferCreationFlags, VertexBufferFourthArgument);
            indexBuffer = createIndexBuffer(
                device,
                IndexCount * sizeof(ushort),
                IndexBufferThirdArgument,
                BufferCreationFlags,
                IndexBufferFourthArgument
            );
            fixed (VertexElement* elements = VertexElements)
            {
                vertexDeclaration = createVertexDeclaration(device, (byte*)elements, (uint)VertexElements.Length);
            }

            if (
                vertexBuffer == 0
                || indexBuffer == 0
                || vertexDeclaration == 0
                || initializeVertexBuffer(vertexBuffer, vertexData) == 0
                || initializeIndexBuffer(indexBuffer, indices) == 0
            )
                throw new InvalidOperationException("The game rejected the fixed triangle resources.");

            worldConstant = CreateAndClearConstantBuffer(device, WorldConstantBytes, "world");
            instanceConstant = CreateAndClearConstantBuffer(device, InstanceConstantBytes, "instance");
            modelConstant = CreateAndClearConstantBuffer(device, ModelConstantBytes, "model");
            materialConstant = CreateAndClearConstantBuffer(device, MaterialConstantBytes, "material");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Release(ref materialConstant);
        Release(ref modelConstant);
        Release(ref instanceConstant);
        Release(ref worldConstant);
        Release(ref vertexDeclaration);
        Release(ref indexBuffer);
        Release(ref vertexBuffer);
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

    private static ulong PackHalf4(float x, float y, float z, float w)
    {
        return BitConverter.HalfToUInt16Bits((Half)x)
            | ((ulong)BitConverter.HalfToUInt16Bits((Half)y) << 16)
            | ((ulong)BitConverter.HalfToUInt16Bits((Half)z) << 32)
            | ((ulong)BitConverter.HalfToUInt16Bits((Half)w) << 48);
    }

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

        // Fixed packed defaults required by attributes 1 and 7 in the archived prototype.
        // The capture confirms their field locations, not these values or their shader semantics.
        public readonly uint Attribute1 = 0x000000FF;
        public readonly uint Attribute7 = 0x00000000;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Stream1Vertex
    {
        public Stream1Vertex(Vector2 textureCoordinate)
        {
            Attribute2 = PackHalf4(0, 0, 1, 0);

            // Fixed packed values required by attributes 15 and 3 in the archived prototype.
            // The capture confirms their field locations and shared format 0x24, not these values or their semantics.
            Attribute15 = 0x00800080;
            Attribute3 = 0xFFFFFFFF;
            Attribute8 = PackHalf4(textureCoordinate.X, textureCoordinate.Y, -1, 2);
        }

        public readonly ulong Attribute2;
        public readonly uint Attribute15;
        public readonly uint Attribute3;
        public readonly ulong Attribute8;
    }
}
