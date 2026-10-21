using System.Buffers.Binary;

namespace CncRemasterWorkshopFixer;

public sealed record TgaPreviewImage(int Width, int Height, byte[] BgraPixels)
{
    public int Stride => checked(Width * 4);
}

/// <summary>Decodes only the uncompressed 32-bit true-colour TGA subset used by map previews.</summary>
public static class TgaPreviewDecoder
{
    private const int HeaderSize = 18;
    private const int MaximumDimension = 4096;

    public static TgaPreviewImage Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
        {
            throw new InvalidDataException("The TGA header is truncated.");
        }

        var imageIdLength = data[0];
        var colourMapType = data[1];
        var imageType = data[2];
        var width = BinaryPrimitives.ReadUInt16LittleEndian(data[12..14]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(data[14..16]);
        var pixelDepth = data[16];
        var descriptor = data[17];

        if (colourMapType != 0)
        {
            throw new NotSupportedException("Colour-mapped TGA previews are not supported.");
        }
        if (imageType != 2)
        {
            throw new NotSupportedException("Only uncompressed true-colour TGA previews are supported.");
        }
        if (pixelDepth != 32)
        {
            throw new NotSupportedException("Only 32-bit BGRA TGA previews are supported.");
        }
        if ((descriptor & 0xC0) != 0 || (descriptor & 0x0F) is not (0 or 8))
        {
            throw new NotSupportedException("The TGA descriptor layout is not supported.");
        }
        if (width == 0 || height == 0 || width > MaximumDimension || height > MaximumDimension)
        {
            throw new InvalidDataException("The TGA dimensions are invalid or exceed the preview safety limit.");
        }

        var pixelLength = checked((int)width * height * 4);
        var pixelOffset = checked(HeaderSize + imageIdLength);
        if (pixelOffset > data.Length || pixelLength > data.Length - pixelOffset)
        {
            throw new InvalidDataException("The TGA pixel data is truncated.");
        }

        var source = data.Slice(pixelOffset, pixelLength);
        var pixels = new byte[pixelLength];
        // Normalise either TGA origin to top-left without touching the alpha bytes.
        // Real RA previews contain partial alpha, which the thumbnail must preserve.
        var rightToLeft = (descriptor & 0x10) != 0;
        var topToBottom = (descriptor & 0x20) != 0;
        for (var y = 0; y < height; y++)
        {
            var sourceY = topToBottom ? y : height - 1 - y;
            for (var x = 0; x < width; x++)
            {
                var sourceX = rightToLeft ? width - 1 - x : x;
                source.Slice((sourceY * width + sourceX) * 4, 4)
                    .CopyTo(pixels.AsSpan((y * width + x) * 4, 4));
            }
        }

        return new(width, height, pixels);
    }
}
