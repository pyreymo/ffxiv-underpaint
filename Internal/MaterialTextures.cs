using FFXIVClientStructs.FFXIV.Client.Graphics.Kernel;

namespace Underpaint.Internal;

internal sealed unsafe class MaterialTextures : IDisposable
{
    private const int ImageSize = 32;
    private const int ColorTableWidth = 8;
    private const int ColorTableHeight = 32;
    private const int ColorTableRowHalfCount = ColorTableWidth * 4;
    private const byte MipLevels = 1;
    private const uint CreateTextureLastArgument = 7;
    private const TextureFlags TextureCreationFlags = TextureFlags.TextureNoSwizzle | TextureFlags.Immutable | TextureFlags.Managed;

    // Logical RGBA values encoded for B8G8R8A8_UNORM.
    private const uint NeutralNormal = 0xFF8080FF; // (0.5, 0.5, 1, 1)
    private const uint NeutralIndex = 0xFF000000; // (0, 0, 0, 1)

    internal Texture* Normal { get; private set; }
    internal Texture* Index { get; private set; }
    internal Texture* ColorTable { get; private set; }

    internal void EnsureCreated()
    {
        if (Normal != null)
            return;

        Texture* normal = null;
        Texture* index = null;
        Texture* colorTable = null;
        try
        {
            Span<uint> pixels = stackalloc uint[ImageSize * ImageSize];
            pixels.Fill(NeutralNormal);
            normal = CreateTexture(ImageSize, ImageSize, TextureFormat.B8G8R8A8_UNORM, pixels, "normal");

            pixels.Fill(NeutralIndex);
            index = CreateTexture(ImageSize, ImageSize, TextureFormat.B8G8R8A8_UNORM, pixels, "index");

            Span<Half> table = stackalloc Half[ColorTableWidth * ColorTableHeight * 4];
            WriteDefaultColorTable(table);
            colorTable = CreateTexture(ColorTableWidth, ColorTableHeight, TextureFormat.R16G16B16A16_FLOAT, table, "color table");

            Normal = normal;
            Index = index;
            ColorTable = colorTable;
        }
        catch
        {
            Release(ref colorTable);
            Release(ref index);
            Release(ref normal);
            throw;
        }
    }

    public void Dispose()
    {
        var colorTable = ColorTable;
        var index = Index;
        var normal = Normal;
        ColorTable = null;
        Index = null;
        Normal = null;
        Release(ref colorTable);
        Release(ref index);
        Release(ref normal);
    }

    private static Texture* CreateTexture<T>(int width, int height, TextureFormat format, Span<T> contents, string name)
        where T : unmanaged
    {
        var texture = Texture.CreateTexture2D(width, height, MipLevels, format, TextureCreationFlags, CreateTextureLastArgument);
        if (texture == null)
            throw new InvalidOperationException($"The game rejected the generated {name} texture.");

        fixed (T* data = contents)
        {
            if (texture->InitializeContents(data))
                return texture;
        }

        texture->DecRef();
        throw new InvalidOperationException($"The game rejected the generated {name} texture contents.");
    }

    private static void WriteDefaultColorTable(Span<Half> table)
    {
        Span<Half> row = stackalloc Half[ColorTableRowHalfCount];
        row.Clear();

        row[0] = row[1] = row[2] = Half.One; // Diffuse RGB
        row[3] = Half.One;
        row[4] = row[5] = row[6] = Half.One; // Specular RGB
        row[8] = row[9] = row[10] = Half.Zero; // Emissive RGB
        row[11] = Half.One;
        row[12] = (Half)0.1f; // Sheen rate
        row[13] = (Half)0.2f; // Sheen tint rate
        row[14] = (Half)5.0f; // Sheen aperture
        row[16] = (Half)0.5f; // Roughness
        row[25] = (Half)(0.5f / 64.0f); // Encoded tile index 0
        row[26] = Half.One; // Tile alpha
        row[28] = row[31] = (Half)16.0f; // Default tile transform

        for (var rowIndex = 0; rowIndex < ColorTableHeight; rowIndex++)
            row.CopyTo(table.Slice(rowIndex * ColorTableRowHalfCount, ColorTableRowHalfCount));
    }

    private static void Release(ref Texture* texture)
    {
        var resource = texture;
        texture = null;
        if (resource != null)
            resource->DecRef();
    }
}
