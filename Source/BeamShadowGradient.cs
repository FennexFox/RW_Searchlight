using UnityEngine;

namespace DirectedSearchlights
{
    // Preserve the existing beam shader and sample its gradient at the
    // transmission of each band. Padding keeps bilinear samples in that band.
    internal static class BeamShadowGradient
    {
        private const int TilesPerSide = 4;
        private const int Levels = TilesPerSide * TilesPerSide;
        internal const int FullTransmission = Levels - 1;
        private const int GradientSize = 64;
        private const int TileSize = GradientSize + 2;
        private const int AtlasSize = TilesPerSide * TileSize;

        internal static Texture2D Create(float aimDiameter)
        {
            var alpha = new float[TileSize * TileSize];
            for (int y = 0; y < TileSize; y++)
            for (int x = 0; x < TileSize; x++)
            {
                float u = (Mathf.Clamp(x - 1, 0, GradientSize - 1) + 0.5f) / GradientSize;
                float v = (Mathf.Clamp(y - 1, 0, GradientSize - 1) + 0.5f) / GradientSize;
                alpha[x + y * TileSize] = BeamGrid.GradientAlpha(u, v, aimDiameter);
            }

            var pixels = new Color32[AtlasSize * AtlasSize];
            for (int level = 0; level < Levels; level++)
            {
                GetTileOrigin(level, out int tileX, out int tileY);
                float scale = 255f * level / (Levels - 1);
                for (int y = 0; y < TileSize; y++)
                for (int x = 0; x < TileSize; x++)
                {
                    byte value = (byte)Mathf.RoundToInt(alpha[x + y * TileSize] * scale);
                    pixels[tileX + x + (tileY + y) * AtlasSize] = new Color32(255, 255, 255, value);
                }
            }

            var atlas = new Texture2D(AtlasSize, AtlasSize, TextureFormat.RGBA32, false);
            atlas.wrapMode = TextureWrapMode.Clamp;
            atlas.SetPixels32(pixels);
            atlas.Apply(false, true);
            return atlas;
        }

        internal static int Level(float transmission) => Mathf.RoundToInt(Mathf.Clamp01(transmission) * FullTransmission);

        internal static Vector2 UV(float u, float v, int level)
        {
            GetTileOrigin(level, out int x, out int y);
            return new Vector2((x + 1 + u * GradientSize) / AtlasSize, (y + 1 + v * GradientSize) / AtlasSize);
        }

        private static void GetTileOrigin(int level, out int x, out int y)
        {
            x = (level % TilesPerSide) * TileSize;
            y = (level / TilesPerSide) * TileSize;
        }
    }
}
