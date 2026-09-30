// Custom PS5 (GNF) texture deswizzler.
// Reverse engineered empirically by comparing swizzled PS5 assets against their
// unswizzled Switch equivalents (same game, shipped on both platforms).

// A lot of this was made possible thanks to Claude Code.
// Restructured for clarity and maintainability by haolink.

using System;
using System.Drawing;

namespace AnimeStudio
{
    public class Texture2DPS5Deswizzler
    {
        // PS5 textures are swizzled in 4 KB micro-tiles. Within a tile, each element's
        // index bits are interleaved into local (x, y) offsets according to a fixed
        // per-element-size pattern: 'x' sends the bit to the x-offset, 'y' to the
        // y-offset, '-' is unused by this element size (absorbed into the tile index).
        private static readonly char[] PatternBpe4 = { '-', '-', 'x', 'x', 'y', 'y', 'y', 'x', 'y', 'x', 'y', 'x' };   // RGBA32/RGB24
        private static readonly char[] PatternOther = { '-', '-', '-', 'x', 'y', 'y', 'x', 'x', 'y', 'x', 'y', 'x' };  // DXT1/BC4/DXT5/BC5/BC7

        // Precomputed local (x, y) offset of every element within a 4 KB tile, for one
        // element size. Built once from the pattern above; the decode hot path only
        // ever indexes into LocalX/LocalY, the same way the Switch deswizzler indexes
        // into GOB_X_POSES/GOB_Y_POSES.
        private readonly struct TileLayout
        {
            public readonly int BitsPerTile;
            public readonly int ElementsPerTile;
            public readonly int TileWidth;
            public readonly int TileHeight;
            public readonly int[] LocalX;
            public readonly int[] LocalY;

            public TileLayout(int bytesPerElement, char[] pattern)
            {
                int shift = System.Numerics.BitOperations.Log2((uint)bytesPerElement);
                BitsPerTile = 12 - shift; // element-index bits that fit in one 4 KB tile
                ElementsPerTile = 1 << BitsPerTile;

                int tileWidth = 1, tileHeight = 1;
                for (int bit = 0; bit < BitsPerTile; bit++)
                {
                    if (pattern[bit + shift] == 'x') tileWidth <<= 1; else tileHeight <<= 1;
                }
                TileWidth = tileWidth;
                TileHeight = tileHeight;

                LocalX = new int[ElementsPerTile];
                LocalY = new int[ElementsPerTile];
                for (int element = 0; element < ElementsPerTile; element++)
                {
                    int x = 0, y = 0, xBit = 0, yBit = 0;
                    for (int bit = 0; bit < BitsPerTile; bit++)
                    {
                        int value = (element >> bit) & 1;
                        if (pattern[bit + shift] == 'x') x |= value << xBit++; else y |= value << yBit++;
                    }
                    LocalX[element] = x;
                    LocalY[element] = y;
                }
            }
        }

        private static readonly TileLayout LayoutBpe4 = new TileLayout(4, PatternBpe4);
        private static readonly TileLayout LayoutBpe8 = new TileLayout(8, PatternOther);
        private static readonly TileLayout LayoutBpe16 = new TileLayout(16, PatternOther);

        private static TileLayout GetTileLayout(int bytesPerElement)
        {
            switch (bytesPerElement)
            {
                case 4: return LayoutBpe4;
                case 8: return LayoutBpe8;
                case 16: return LayoutBpe16;
                default: throw new ArgumentOutOfRangeException(nameof(bytesPerElement));
            }
        }

        private static int CeilDivide(int a, int b)
        {
            return (a + b - 1) / b;
        }

        // Bytes per addressable element: 1 pixel for uncompressed formats, 1 compressed
        // block for block-compressed formats. Returns false for formats not known to be
        // swizzled on PS5.
        internal static bool TryGetBytesPerElement(TextureFormat m_TextureFormat, out int bytesPerElement)
        {
            switch (m_TextureFormat)
            {
                case TextureFormat.DXT1:
                case TextureFormat.BC4:
                    bytesPerElement = 8;
                    return true;
                case TextureFormat.DXT5:
                case TextureFormat.BC5:
                case TextureFormat.BC7:
                    bytesPerElement = 16;
                    return true;
                case TextureFormat.RGBA32:
                case TextureFormat.RGB24:
                    bytesPerElement = 4;
                    return true;
                default:
                    bytesPerElement = 0;
                    return false;
            }
        }

        // Uncompressed formats address individual pixels (1x1); block-compressed formats
        // address 4x4 pixel blocks.
        private static Size GetElementBlockSize(int bytesPerElement)
        {
            return bytesPerElement <= 4 ? new Size(1, 1) : new Size(4, 4);
        }

        internal static void Unswizzle(ReadOnlySpan<byte> data, int width, int height, int bytesPerElement, Span<byte> newData)
        {
            var elementBlockSize = GetElementBlockSize(bytesPerElement);
            int blockCountX = CeilDivide(width, elementBlockSize.Width);
            int blockCountY = CeilDivide(height, elementBlockSize.Height);

            var layout = GetTileLayout(bytesPerElement);
            int tilesPerRow = CeilDivide(blockCountX, layout.TileWidth);
            int elementCount = data.Length / bytesPerElement;

            for (int i = 0; i < elementCount; i++)
            {
                int tileIndex = i >> layout.BitsPerTile;
                int elementInTile = i & (layout.ElementsPerTile - 1);
                int blockX = (tileIndex % tilesPerRow) * layout.TileWidth + layout.LocalX[elementInTile];
                int blockY = (tileIndex / tilesPerRow) * layout.TileHeight + layout.LocalY[elementInTile];
                if (blockX >= blockCountX || blockY >= blockCountY) 
                {
                    continue;
                }
                data.Slice(i * bytesPerElement, bytesPerElement)
                    .CopyTo(newData.Slice((blockY * blockCountX + blockX) * bytesPerElement, bytesPerElement));
            }
        }
    }
}
