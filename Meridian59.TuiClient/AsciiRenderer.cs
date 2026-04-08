using System;
using System.Collections.Generic;
using System.Linq;
using Meridian59.Common.Constants;
using Meridian59.Data;
using Meridian59.Data.Models;
using Meridian59.Files.ROO;

namespace Meridian59.TuiClient
{
    public class AsciiRenderer
    {
        private char[,] currentBuffer;
        private char[,] nextBuffer;
        private int lastWidth;
        private int lastHeight;

        // zoomLevel=0 → fits entire room in viewport; positive = zoom in, negative = zoom out.
        // Default=2 → 4x more zoomed in than fit (roughly a couple of stories above the player).
        private int zoomLevel = 2;
        private const int ZOOM_MAX = 7;
        private const int ZOOM_MIN = -2;

        public void ZoomIn()  { if (zoomLevel < ZOOM_MAX) zoomLevel++; }
        public void ZoomOut() { if (zoomLevel > ZOOM_MIN) zoomLevel--; }

        /// <summary>
        /// Forces a full repaint on the next Render call (e.g. after Console.Clear).
        /// </summary>
        public void Invalidate()
        {
            currentBuffer = null;
            nextBuffer    = null;
        }

        public void Render(TuiClient client, int consoleX, int consoleY, int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            // Recreate buffers if size changed or Invalidate() was called.
            if (nextBuffer == null || width != lastWidth || height != lastHeight)
            {
                currentBuffer = new char[width, height];
                nextBuffer    = new char[width, height];
                lastWidth     = width;
                lastHeight    = height;

                for (int x = 0; x < width; x++)
                    for (int y = 0; y < height; y++)
                        currentBuffer[x, y] = '\0';
            }

            // Build next frame — start with all spaces.
            for (int x = 0; x < width; x++)
                for (int y = 0; y < height; y++)
                    nextBuffer[x, y] = ' ';

            var data   = client.Data;
            var avatar = data?.AvatarObject;
            var roo    = data?.RoomInformation?.ResourceRoom;

            // 1. Draw debug info at the top
            string status;
            if (data?.RoomInformation == null) status = "WAITING FOR ROOM...";
            else if (roo == null) status = $"LOADING: {data.RoomInformation.RoomFile}";
            else status = $"ROOM: {roo.Filename} WALLS: {roo.Walls.Count} OBJ: {data.RoomObjects.Count}";
            
            if (avatar == null && data?.AvatarID != 0)
                status += " (AVATAR NULL)";

            for (int i = 0; i < status.Length && i < width; i++)
                nextBuffer[i, 0] = status[i];

            // 2. Determine center point (player or world origin)
            int scale   = GeometryConstants.KOD_FINENESS; // 64 fine units per grid square
            
            // ROO origin (0,0) corresponds to World Grid (64,64) which is KOD (4096,4096)
            // But wait, ConvertToROO is X_kod * 16 - 1024.
            // If X_kod is 64, X_roo is 64*16 - 1024 = 0.
            // Wait, 64 is a BigGrid coord, not KOD.
            // BigGrid 64 = KOD 4096.
            // 4096 * 16 = 65536. 65536 - 1024 = 64512.
            // So X_roo = 64512 is the middle.
            
            // Let's just use the Avatar's coordinates if available.
            // If not, we'll try to find the room's average wall position.
            int centerX = 64512; // Default to world center in ROO units? No, let's use KOD.
            int centerY = 64512;

            if (avatar != null)
            {
                centerX = (int)Math.Round((avatar.CoordinateX * 16f) - 1024f);
                centerY = (int)Math.Round((avatar.CoordinateY * 16f) - 1024f);
            }
            else if (roo != null && roo.Walls.Count > 0)
            {
                // Average wall position
                centerX = (int)roo.Walls.Average(w => (w.P1.X + w.P2.X) / 2f);
                centerY = (int)roo.Walls.Average(w => (w.P1.Y + w.P2.Y) / 2f);
            }

            // Compute "fit entire room" scale, then apply zoomLevel offset.
            // zoomLevel=0 → fit room in box; +1 → 2x in; +2 → 4x in (default); etc.
            float fitRooToGrid = 1024f; // fallback when no room loaded
            if (roo != null && roo.Walls.Count > 0)
            {
                float minX = float.MaxValue, maxX = float.MinValue;
                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var w in roo.Walls)
                {
                    if (w.P1.X < minX) minX = (float)w.P1.X;
                    if (w.P2.X < minX) minX = (float)w.P2.X;
                    if (w.P1.X > maxX) maxX = (float)w.P1.X;
                    if (w.P2.X > maxX) maxX = (float)w.P2.X;
                    if (w.P1.Y < minY) minY = (float)w.P1.Y;
                    if (w.P2.Y < minY) minY = (float)w.P2.Y;
                    if (w.P1.Y > maxY) maxY = (float)w.P1.Y;
                    if (w.P2.Y > maxY) maxY = (float)w.P2.Y;
                }
                float fitX = (maxX - minX) / Math.Max(1, width  - 2);
                float fitY = (maxY - minY) / Math.Max(1, height - 2);
                fitRooToGrid = Math.Max(fitX, fitY);
            }
            float rooToGrid = fitRooToGrid / MathF.Pow(2f, zoomLevel);

            // 3. Draw all walls from the ROO file
            if (roo != null)
            {
                foreach (var wall in roo.Walls)
                {
                    // Relative to center in Grid units
                    int x0 = (int)Math.Round((wall.P1.X - centerX) / rooToGrid) + width / 2;
                    int y0 = (int)Math.Round((wall.P1.Y - centerY) / rooToGrid) + height / 2;
                    int x1 = (int)Math.Round((wall.P2.X - centerX) / rooToGrid) + width / 2;
                    int y1 = (int)Math.Round((wall.P2.Y - centerY) / rooToGrid) + height / 2;

                    // Draw solid boundaries with '#', portals with '.'
                    char symbol = (wall.LeftSectorNum == 0 || wall.RightSectorNum == 0) ? '#' : '.';
                    DrawLine(nextBuffer, x0, y0, x1, y1, symbol);
                }
            }

            // 4. Place room objects relative to center
            if (data != null)
            {
                foreach (var obj in data.RoomObjects)
                {
                    // Object coords are in KOD units. Convert to ROO first to use same logic.
                    float objRooX = obj.CoordinateX * 16f - 1024f;
                    float objRooY = obj.CoordinateY * 16f - 1024f;
                    
                    int relX = (int)Math.Round((objRooX - centerX) / rooToGrid) + width / 2;
                    int relY = (int)Math.Round((objRooY - centerY) / rooToGrid) + height / 2;

                    if (relX >= 0 && relX < width && relY >= 1 && relY < height)
                        nextBuffer[relX, relY] = GetCharForObject(obj);
                }
            }

            // 5. Draw Avatar if available
            if (avatar != null)
                nextBuffer[width / 2, height / 2] = '@';
            else
                nextBuffer[width / 2, height / 2] = '+'; // Center crosshair if no avatar

            // Diff-paint: only write cells that changed since last frame.
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    if (nextBuffer[x, y] != currentBuffer[x, y])
                    {
                        Console.SetCursorPosition(consoleX + x, consoleY + y);
                        Console.Write(nextBuffer[x, y]);
                        currentBuffer[x, y] = nextBuffer[x, y];
                    }
                }
            }
        }

        private void DrawLine(char[,] buffer, int x0, int y0, int x1, int y1, char symbol)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            int width = buffer.GetLength(0);
            int height = buffer.GetLength(1);

            while (true)
            {
                // Skip y=0 because it has our status line
                if (x0 >= 0 && x0 < width && y0 >= 1 && y0 < height)
                {
                    // Don't overwrite objects or the player symbol
                    if (buffer[x0, y0] == ' ' || buffer[x0, y0] == '.')
                        buffer[x0, y0] = symbol;
                }

                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy)
                {
                    err -= dy;
                    x0 += sx;
                }
                if (e2 < dx)
                {
                    err += dx;
                    y0 += sy;
                }
            }
        }

        private char GetCharForObject(RoomObject obj)
        {
            if (obj.IsAvatar)         return '@';
            if (obj.Flags.IsPlayer)   return 'P';
            if (obj.Flags.IsAttackable) return 'M';
            if (obj.Flags.IsGettable) return 'i';
            return '*';
        }
    }
}
