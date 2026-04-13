using System;
using System.Collections.Generic;
using System.Linq;
using Meridian59.Common.Constants;
using Meridian59.Data;
using Meridian59.Data.Models;
using Meridian59.Files.ROO;

namespace Meridian59.TuiClient
{
    public struct VideoCell
    {
        public char Char;
        public float Intensity; // 0.0 to 1.0
    }

    public class VideoBuffer
    {
        public VideoCell[,] Cells;
        public int Width;
        public int Height;

        public VideoBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            Cells = new VideoCell[width, height];
            Clear();
        }

        public void Clear()
        {
            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    Cells[x, y].Char = ' ';
                    Cells[x, y].Intensity = 1.0f;
                }
            }
        }

        public void Set(int x, int y, char c, float intensity = 1.0f)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                Cells[x, y].Char = c;
                Cells[x, y].Intensity = intensity;
            }
        }
    }

    public enum ViewOrientation
    {
        NorthUp,
        SouthUp,
        FollowRotation
    }

    public class AsciiRenderer
    {
        private VideoBuffer currentBuffer;
        private VideoBuffer nextBuffer;
        private int lastWidth;
        private int lastHeight;

        // zoomLevel=0 → fits entire room in viewport; positive = zoom in, negative = zoom out.
        // Default=2 → 4x more zoomed in than fit (roughly a couple of stories above the player).
        private int zoomLevel = 2;
        private const int ZOOM_MAX = 7;
        private const int ZOOM_MIN = -2;

        // Transformation State
        private float cosTheta = 1.0f;
        private float sinTheta = 0.0f;
        private float rooToGrid = 1.0f;
        private int centerX = 64512;
        private int centerY = 64512;
        private int viewWidth = 0;
        private int viewHeight = 0;

        private uint lastRoomId = 0;
        private float cachedFitRooToGrid = 1024f;

        public ViewOrientation Orientation { get; set; } = ViewOrientation.NorthUp;

        public void CycleOrientation()
        {
            if (Orientation == ViewOrientation.NorthUp) Orientation = ViewOrientation.SouthUp;
            else if (Orientation == ViewOrientation.SouthUp) Orientation = ViewOrientation.FollowRotation;
            else Orientation = ViewOrientation.NorthUp;
        }

        public void ZoomIn()  { if (zoomLevel < ZOOM_MAX) zoomLevel++; }
        public void ZoomOut() { if (zoomLevel > ZOOM_MIN) zoomLevel--; }

        /// <summary>
        /// Forces a full repaint on the next Render call (e.g. after Console.Clear).
        /// </summary>
        public void Invalidate()
        {
            currentBuffer = null;
            nextBuffer    = null;
            lastRoomId    = 0;
        }

        private void WorldToView(float worldX, float worldY, out int viewX, out int viewY)
        {
            // 1. Translate
            float tx = worldX - centerX;
            float ty = worldY - centerY;

            // 2. Rotate
            float rx = tx * cosTheta - ty * sinTheta;
            float ry = tx * sinTheta + ty * cosTheta;

            // 3. Scale and Center in viewport
            viewX = (int)Math.Round(rx / rooToGrid) + viewWidth / 2;
            viewY = (int)Math.Round(ry / rooToGrid) + viewHeight / 2;
        }

        public void Render(TuiClient client, int consoleX, int consoleY, int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            try
            {
                // Recreate buffers if size changed or Invalidate() was called.
                if (nextBuffer == null || width != lastWidth || height != lastHeight)
                {
                    currentBuffer = new VideoBuffer(width, height);
                    nextBuffer    = new VideoBuffer(width, height);
                    lastWidth     = width;
                    lastHeight    = height;

                    for (int x = 0; x < width; x++)
                        for (int y = 0; y < height; y++)
                            currentBuffer.Cells[x, y].Char = '\0';
                }

                viewWidth = width;
                viewHeight = height;

                // Build next frame
                nextBuffer.Clear();

                var data   = client.Data;
                var avatar = data?.AvatarObject;
                var roo    = data?.RoomInformation?.ResourceRoom;

                // 1. Draw status at the top
                string status;
                if (data?.RoomInformation == null) status = "WAITING FOR ROOM...";
                else if (roo == null) status = $"LOADING: {data.RoomInformation.RoomFile}";
                else status = $"ROOM: {roo.Filename} WALLS: {roo.Walls.Count} OBJ: {data.RoomObjects.Count}";
                
                for (int i = 0; i < status.Length && i < width; i++)
                    nextBuffer.Set(i, 0, status[i]);

                // 2. Determine center point and orientation
                centerX = 64512;
                centerY = 64512;
                float avatarAngle = 0;

                if (avatar != null)
                {
                    centerX = (int)Math.Round((avatar.CoordinateX * 16f) - 1024f);
                    centerY = (int)Math.Round((avatar.CoordinateY * 16f) - 1024f);
                    // Convert Meridian angle (0-4095) to radians (0 to 2PI)
                    // 0 is East, 1024 is South, 2048 is West, 3072 is North
                    avatarAngle = (float)(avatar.AngleUnits * 2.0 * Math.PI / 4096.0);
                }

                // Orientation setup
                float rotationAngle = 0; 
                if (Orientation == ViewOrientation.FollowRotation && avatar != null)
                {
                    // To have player facing "Up" (-Y in terminal, North in M59), 
                    // we need to rotate the world by (-avatarAngle + PI/2)
                    rotationAngle = -avatarAngle + (float)(Math.PI * 1.5); // Adjusting for M59 3072 being North
                }
                else if (Orientation == ViewOrientation.SouthUp)
                {
                    rotationAngle = (float)Math.PI; // Upside down
                }

                cosTheta = MathF.Cos(rotationAngle);
                sinTheta = MathF.Sin(rotationAngle);

                // Compute scale (cached)
                if (roo != null && roo.Walls.Count > 0)
                {
                    if (client.Data.RoomInformation.RoomID != lastRoomId)
                    {
                        float minX = float.MaxValue, maxX = float.MinValue;
                        float minY = float.MaxValue, maxY = float.MinValue;
                        foreach (var w in roo.Walls)
                        {
                            minX = Math.Min(minX, Math.Min((float)w.P1.X, (float)w.P2.X));
                            maxX = Math.Max(maxX, Math.Max((float)w.P1.X, (float)w.P2.X));
                            minY = Math.Min(minY, Math.Min((float)w.P1.Y, (float)w.P2.Y));
                            maxY = Math.Max(maxY, Math.Max((float)w.P1.Y, (float)w.P2.Y));
                        }
                        cachedFitRooToGrid = Math.Max((maxX - minX) / Math.Max(1, width - 2), (maxY - minY) / Math.Max(1, height - 2));
                        lastRoomId = client.Data.RoomInformation.RoomID;
                    }
                }
                else
                {
                    cachedFitRooToGrid = 1024f;
                    lastRoomId = 0;
                }
                rooToGrid = cachedFitRooToGrid / MathF.Pow(2f, zoomLevel);

                // 3. Draw all walls
                if (roo != null)
                {
                    foreach (var wall in roo.Walls)
                    {
                        WorldToView((float)wall.P1.X, (float)wall.P1.Y, out int x0, out int y0);
                        WorldToView((float)wall.P2.X, (float)wall.P2.Y, out int x1, out int y1);

                        char symbol = '#';
                        bool isPortal = wall.LeftSectorNum != 0 && wall.RightSectorNum != 0;
                        bool isPassable = (wall.LeftSide != null && wall.LeftSide.Flags.IsPassable) || 
                                          (wall.RightSide != null && wall.RightSide.Flags.IsPassable);

                        if (isPortal)
                        {
                            symbol = '.'; // Portal
                            if (!isPassable)
                                symbol = 'D'; // Closed door
                            else if (wall.LeftSide?.Flags.IsHasAnimated == true || wall.RightSide?.Flags.IsHasAnimated == true)
                                symbol = 'd'; // Open/Animated door
                        }
                        else if (isPassable)
                        {
                            symbol = 'X'; // Exit / Boundary Transition
                        }
                        
                        DrawLine(nextBuffer, x0, y0, x1, y1, symbol);
                    }

                    // Draw room boundaries
                    if (roo.Things.Count >= 2)
                    {
                        var box = roo.GetBoundingBox2DFromThings();
                        WorldToView((float)box.Min.X, (float)box.Min.Y, out int bx0, out int by0);
                        WorldToView((float)box.Max.X, (float)box.Max.Y, out int bx1, out int by1);

                        // Draw boundary box with 'B' at corners
                        nextBuffer.Set(bx0, by0, 'B');
                        nextBuffer.Set(bx1, by0, 'B');
                        nextBuffer.Set(bx0, by1, 'B');
                        nextBuffer.Set(bx1, by1, 'B');
                    }
                }

                // 4. Place objects
                if (data != null)
                {
                    foreach (var obj in data.RoomObjects.ToList())
                    {
                        float objRooX = obj.CoordinateX * 16f - 1024f;
                        float objRooY = obj.CoordinateY * 16f - 1024f;
                        WorldToView(objRooX, objRooY, out int relX, out int relY);

                        if (relX >= 0 && relX < width && relY >= 1 && relY < height)
                            nextBuffer.Set(relX, relY, GetCharForObject(obj));
                    }
                }

                // 5. Draw Avatar
                nextBuffer.Set(width / 2, height / 2, avatar != null ? '@' : '+');

                // 6. Apply Post-processing (Lighting and Vision Cone)
                ApplyLighting(nextBuffer, width / 2, height / 2, avatarAngle, rotationAngle);

                // 7. Diff-paint to console
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        var next = nextBuffer.Cells[x, y];
                        var curr = currentBuffer.Cells[x, y];

                        // Character transformation based on intensity
                        char displayChar = TransformChar(next.Char, next.Intensity);

                        if (displayChar != curr.Char)
                        {
                            Console.SetCursorPosition(consoleX + x, consoleY + y);
                            Console.Write(displayChar);
                            currentBuffer.Cells[x, y].Char = displayChar;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                client.Log("ERROR", $"Renderer failure: {ex.Message}");
            }
        }

        private void ApplyLighting(VideoBuffer buffer, int centerX, int centerY, float avatarAngle, float rotationAngle)
        {
            float maxDist = Math.Min(buffer.Width, buffer.Height) * 0.8f;

            for (int y = 1; y < buffer.Height; y++)
            {
                for (int x = 0; x < buffer.Width; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    
                    // Base lighting based on distance (linear falloff)
                    float intensity = Math.Clamp(1.0f - (dist / maxDist), 0.1f, 1.0f);

                    // Vision Cone (approx 90 degrees)
                    if (dist > 1.0f)
                    {
                        float angle = MathF.Atan2(-dy, dx);
                        
                        // We need the difference between the screen pixel angle and the avatar's view angle
                        // but both need to be in the same coordinate space (the screen space).
                        // rotationAngle rotates the world to the screen.
                        // avatarAngle is in world space (CW, 0=East).
                        
                        // In screen space, 'Up' is -Y (angle PI/2).
                        // If FollowPlayerRotation is on, the player always faces 'Up' in screen space.
                        // If FollowPlayerRotation is off, rotationAngle is 0, so screen space = world space.
                        
                        float targetScreenAngle = (float)Math.PI / 2.0f; // Default 'Up' in screen space
                        if (rotationAngle == 0)
                        {
                            // North is 3072 in M59 (CW), which is Math.PI * 1.5 in standard (CCW) if 0 is East.
                            // However, Atan2(-dy, dx) with -dy means standard cartesian where +Y is up.
                            // Let's use the avatarAngle converted to screen space.
                            // M59 Angle to Rad (CCW, 0=East):
                            float ccwAvatarAngle = -avatarAngle; 
                            targetScreenAngle = ccwAvatarAngle;
                        }

                        float diff = MathF.Abs(NormalizeAngle(angle - targetScreenAngle));
                        if (diff > MathF.PI / 4.0f) // 45 degrees either side
                        {
                            intensity *= 0.4f; // Dim areas outside vision cone
                        }
                    }

                    buffer.Cells[x, y].Intensity = intensity;
                }
            }
        }

        private float NormalizeAngle(float angle)
        {
            while (angle > MathF.PI) angle -= 2 * MathF.PI;
            while (angle < -MathF.PI) angle += 2 * MathF.PI;
            return angle;
        }

        private char TransformChar(char c, float intensity)
        {
            if (c == ' ' || c == '\0') return ' ';
            
            // If intensity is low, use "dimmer" versions of characters
            if (intensity < 0.3f) return '.';
            if (intensity < 0.6f)
            {
                if (c == '#') return '+';
                if (c == '@') return 'o';
                return ':';
            }
            return c;
        }

        private void DrawLine(VideoBuffer buffer, int x0, int y0, int x1, int y1, char symbol)
        {
            int dx = Math.Abs(x1 - x0);
            int dy = Math.Abs(y1 - y0);
            int sx = x0 < x1 ? 1 : -1;
            int sy = y0 < y1 ? 1 : -1;
            int err = dx - dy;

            while (true)
            {
                // Skip y=0 because it has our status line
                if (x0 >= 0 && x0 < buffer.Width && y0 >= 1 && y0 < buffer.Height)
                {
                    // Don't overwrite objects or the player symbol
                    char c = buffer.Cells[x0, y0].Char;
                    if (c == ' ' || c == '.' || c == '\0')
                        buffer.Set(x0, y0, symbol);
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
            
            // Check for common interactive scenery
            if (obj.Name != null)
            {
                if (obj.Name.Contains("door", StringComparison.OrdinalIgnoreCase)) return 'D';
                if (obj.Name.Contains("chest", StringComparison.OrdinalIgnoreCase)) return 'C';
                if (obj.Name.Contains("sign", StringComparison.OrdinalIgnoreCase)) return 'S';
            }

            return '*';
        }
    }
}
