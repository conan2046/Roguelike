using System;
using UnityEngine;

namespace ProjectX.Migration
{
    public sealed class CocosMapCollisionGrid : MonoBehaviour
    {
        [SerializeField] private TextAsset blockAsset;
        [SerializeField] private Vector2 mapPixelSize;
        [SerializeField] private int gridWidth;
        [SerializeField] private int gridHeight;

        private byte[] blockBytes = Array.Empty<byte>();

        public TextAsset BlockAsset => blockAsset;
        public Vector2 MapPixelSize => mapPixelSize;
        public int GridWidth => gridWidth;
        public int GridHeight => gridHeight;
        public bool IsLoaded => blockBytes.Length > 0 && gridWidth > 0 && gridHeight > 0;

        private void Awake()
        {
            Initialize();
        }

        public void Configure(TextAsset valueBlockAsset, Vector2 valueMapPixelSize)
        {
            blockAsset = valueBlockAsset;
            mapPixelSize = valueMapPixelSize;
            Initialize();
        }

        public void Initialize()
        {
            blockBytes = Array.Empty<byte>();
            gridWidth = 0;
            gridHeight = 0;
            if (blockAsset == null || blockAsset.bytes == null || blockAsset.bytes.Length < 5) return;

            byte[] bytes = blockAsset.bytes;
            gridWidth = bytes[0] | bytes[1] << 8;
            gridHeight = bytes[2] | bytes[3] << 8;
            int requiredBytes = (gridWidth * gridHeight + 7) / 8;
            if (gridWidth <= 0 || gridHeight <= 0 || bytes.Length < 4 + requiredBytes)
            {
                gridWidth = 0;
                gridHeight = 0;
                return;
            }

            blockBytes = new byte[requiredBytes];
            Buffer.BlockCopy(bytes, 4, blockBytes, 0, requiredBytes);
        }

        public bool CanWalkTile(int x, int y)
        {
            if (!IsLoaded || x < 0 || y < 0 || x >= gridWidth || y >= gridHeight) return false;
            int bit = y * gridWidth + x;
            return (blockBytes[bit >> 3] & 1 << (bit & 7)) == 0;
        }

        public bool CanWalkCocosPosition(Vector2 cocosPosition)
        {
            int x = (int)(cocosPosition.x / CocosMapCharacterMotor.CocosTileSize);
            int y = (int)(cocosPosition.y / CocosMapCharacterMotor.CocosTileSize);
            return CanWalkTile(x, y);
        }

        public bool CanWalkVisualPosition(Vector2 visualPosition)
        {
            return CanWalkCocosPosition(VisualToCocos(visualPosition));
        }

        public Vector2 CocosToVisual(Vector2 cocosPosition)
        {
            return new Vector2(cocosPosition.x, mapPixelSize.y - cocosPosition.y);
        }

        public Vector2 VisualToCocos(Vector2 visualPosition)
        {
            return new Vector2(visualPosition.x, mapPixelSize.y - visualPosition.y);
        }

        public Vector2 ResolveMovement(Vector2 currentVisualPosition, Vector2 visualDisplacement)
        {
            Vector2 next = currentVisualPosition;
            Vector2 xCandidate = ClampVisual(new Vector2(next.x + visualDisplacement.x, next.y));
            if (CanWalkVisualPosition(xCandidate)) next.x = xCandidate.x;
            Vector2 yCandidate = ClampVisual(new Vector2(next.x, next.y + visualDisplacement.y));
            if (CanWalkVisualPosition(yCandidate)) next.y = yCandidate.y;
            return next;
        }

        public Vector2 FindNearestWalkableCocosPosition(Vector2 requested)
        {
            if (!IsLoaded) return requested;
            int startX = Mathf.Clamp((int)(requested.x / CocosMapCharacterMotor.CocosTileSize), 0, gridWidth - 1);
            int startY = Mathf.Clamp((int)(requested.y / CocosMapCharacterMotor.CocosTileSize), 0, gridHeight - 1);
            int maxRadius = Mathf.Max(gridWidth, gridHeight);
            for (int radius = 0; radius <= maxRadius; radius++)
            {
                for (int yOffset = -radius; yOffset <= radius; yOffset++)
                {
                    int xOffset = radius - Mathf.Abs(yOffset);
                    if (TryTileCenter(startX - xOffset, startY + yOffset, out Vector2 result)) return result;
                    if (xOffset > 0 && TryTileCenter(startX + xOffset, startY + yOffset, out result)) return result;
                }
            }
            return requested;
        }

        public int CountWalkableTiles()
        {
            int count = 0;
            for (int y = 0; y < gridHeight; y++)
                for (int x = 0; x < gridWidth; x++)
                    if (CanWalkTile(x, y)) count++;
            return count;
        }

        private bool TryTileCenter(int x, int y, out Vector2 result)
        {
            if (CanWalkTile(x, y))
            {
                result = new Vector2((x + 0.5f) * CocosMapCharacterMotor.CocosTileSize, (y + 0.5f) * CocosMapCharacterMotor.CocosTileSize);
                return true;
            }
            result = default;
            return false;
        }

        private Vector2 ClampVisual(Vector2 value)
        {
            const float epsilon = 0.01f;
            return new Vector2(
                Mathf.Clamp(value.x, 0f, Mathf.Max(0f, mapPixelSize.x - epsilon)),
                Mathf.Clamp(value.y, 0f, Mathf.Max(0f, mapPixelSize.y - epsilon)));
        }
    }
}
