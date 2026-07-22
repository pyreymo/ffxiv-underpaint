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

    private const uint ImmutableBufferFlags = 0x804;
    internal const int Stream0Stride = 20;
    internal const int Stream1Stride = 24;
    internal const int VertexCount = 3;
    internal const int IndexCount = 3;

    private static readonly byte[] VertexDeclarationElements =
    [
        0,
        0,
        0x13,
        0,
        0,
        12,
        0x3C,
        1,
        0,
        16,
        0x3C,
        7,
        1,
        0,
        0x1C,
        2,
        1,
        8,
        0x24,
        15,
        1,
        12,
        0x24,
        3,
        1,
        16,
        0x1C,
        8,
    ];

    private nint vertexBuffer;
    private nint indexBuffer;
    private nint vertexDeclaration;

    internal nint VertexBuffer => vertexBuffer;
    internal nint IndexBuffer => indexBuffer;
    internal nint VertexDeclaration => vertexDeclaration;
    internal int Stream1Offset => VertexCount * Stream0Stride;

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

        var stream0Bytes = VertexCount * Stream0Stride;
        var vertexBytes = stream0Bytes + VertexCount * Stream1Stride;
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
            vertexBuffer = createVertexBuffer(device, vertexBytes, ImmutableBufferFlags, 0);
            indexBuffer = createIndexBuffer(device, IndexCount * sizeof(ushort), 1, ImmutableBufferFlags, 0);
            fixed (byte* declaration = VertexDeclarationElements)
            {
                vertexDeclaration = createVertexDeclaration(device, declaration, (uint)(VertexDeclarationElements.Length / 4));
            }

            if (
                vertexBuffer == 0
                || indexBuffer == 0
                || vertexDeclaration == 0
                || initializeVertexBuffer(vertexBuffer, vertexData) == 0
                || initializeIndexBuffer(indexBuffer, indices) == 0
            )
                throw new InvalidOperationException("The game rejected the fixed triangle resources.");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        Release(ref vertexDeclaration);
        Release(ref indexBuffer);
        Release(ref vertexBuffer);
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

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Stream0Vertex(Vector3 position)
    {
        public readonly Vector3 Position = position;
        public readonly uint Attribute1 = 0x000000FF;
        public readonly uint Attribute7 = 0;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct Stream1Vertex
    {
        public Stream1Vertex(Vector2 textureCoordinate)
        {
            Attribute2 = 0x00003C0000000000;
            Attribute15 = 0x00800080;
            Attribute3 = uint.MaxValue;
            Attribute8 =
                BitConverter.HalfToUInt16Bits((Half)textureCoordinate.X)
                | ((ulong)BitConverter.HalfToUInt16Bits((Half)textureCoordinate.Y) << 16)
                | (0xBC00UL << 32)
                | (0x4000UL << 48);
        }

        public readonly ulong Attribute2;
        public readonly uint Attribute15;
        public readonly uint Attribute3;
        public readonly ulong Attribute8;
    }
}
