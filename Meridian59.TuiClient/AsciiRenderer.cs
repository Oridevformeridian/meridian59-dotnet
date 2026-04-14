using System;
using System.Collections.Generic;
using System.Linq;
using Meridian59.Common.Constants;
using Meridian59.Data;
using Meridian59.Data.Models;
using Meridian59.Files.ROO;

namespace Meridian59.TuiClient
{
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
        private int zoomLevel = 2;
        private const int ZOOM_MAX = 7;
        private const int ZOOM_MIN = -2;

        private float rooToGrid = 1.0f;
        private int centerX = 64512;
        private int centerY = 64512;
        private int viewWidth = 0;
        private int viewHeight = 0;
        private float avatarAngle = 0;

        private uint lastRoomId = 0;
        private float cachedFitRooToGrid = 1024f;

        public ViewOrientation Orientation { get; set; } = ViewOrientation.SouthUp;

        public void CycleOrientation()
        {
            if (Orientation == ViewOrientation.SouthUp) Orientation = ViewOrientation.NorthUp;
            else if (Orientation == ViewOrientation.NorthUp) Orientation = ViewOrientation.FollowRotation;
            else Orientation = ViewOrientation.SouthUp;
        }

        public void ZoomIn()  { if (zoomLevel < ZOOM_MAX) zoomLevel++; }
        public void ZoomOut() { if (zoomLevel > ZOOM_MIN) zoomLevel--; }

        public void RotateMovement(int dx, int dy, out int rdx, out int rdy)
        {
            if (Orientation == ViewOrientation.NorthUp)
            {
                rdx = dx;
                rdy = dy;
            }
            else if (Orientation == ViewOrientation.SouthUp)
            {
                rdx = -dx;
                rdy = -dy;
            }
            else // FollowRotation
            {
                // To convert View-space movement to World-space, rotate by -rotationAngle
                float rotationAngle = (float)(Math.PI * 1.5) - avatarAngle; 
                float cos = MathF.Cos(-rotationAngle);
                float sin = MathF.Sin(-rotationAngle);
                rdx = (int)Math.Round(dx * cos - dy * sin);
                rdy = (int)Math.Round(dx * sin + dy * cos);
            }
        }

        public void Invalidate()
        {
            currentBuffer = null;
            nextBuffer    = null;
            lastRoomId    = 0;
        }

        /// <summary>
        /// Projects world coordinates into a rotated space based on current orientation, 
        /// WITHOUT applying viewport translation or scaling.
        /// </summary>
        public void WorldToRotatedOnly(float worldX, float worldY, out float rx, out float ry)
        {
            if (Orientation == ViewOrientation.SouthUp)
            {
                // South (+Y) at Top, East (+X) at Left. 180-degree rotation.
                rx = -worldX;
                ry = -worldY;
            }
            else if (Orientation == ViewOrientation.NorthUp)
            {
                // North (-Y) at Top. Identity.
                rx = worldX;
                ry = worldY;
            }
            else // FollowRotation
            {
                // Rotate such that avatarAngle (CW, 0=East) points "Up" (-Y)
                float rotationAngle = (float)(Math.PI * 1.5) - avatarAngle; 
                float cos = MathF.Cos(rotationAngle);
                float sin = MathF.Sin(rotationAngle);
                rx = worldX * cos - worldY * sin;
                ry = worldX * sin + worldY * cos;
            }
        }

        private void WorldToView(float worldX, float worldY, out int viewX, out int viewY)
        {
            float tx = worldX - centerX;
            float ty = worldY - centerY;

            float rx, ry;
            if (Orientation == ViewOrientation.SouthUp)
            {
                rx = -tx;
                ry = -ty;
            }
            else if (Orientation == ViewOrientation.NorthUp)
            {
                rx = tx;
                ry = ty;
            }
            else // FollowRotation
            {
                float rotationAngle = (float)(Math.PI * 1.5) - avatarAngle; 
                float cos = MathF.Cos(rotationAngle);
                float sin = MathF.Sin(rotationAngle);
                rx = tx * cos - ty * sin;
                ry = tx * sin + ty * cos;
            }

            viewX = (int)Math.Round(rx / rooToGrid) + viewWidth / 2;
            viewY = (int)Math.Round(ry / rooToGrid) + viewHeight / 2;
        }

        public void Render(TuiClient client, int consoleX, int consoleY, int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            try
            {
                if (nextBuffer == null || width != lastWidth || height != lastHeight)
                {
                    currentBuffer = new VideoBuffer(width, height);
                    nextBuffer    = new VideoBuffer(width, height);
                    lastWidth     = width;
                    lastHeight    = height;
                }

                viewWidth = width;
                viewHeight = height;
                nextBuffer.Clear();

                var data   = client.Data;
                var avatar = data?.AvatarObject;
                var roo    = data?.RoomInformation?.ResourceRoom;

                string status;
                if (data?.RoomInformation == null) status = "WAITING FOR ROOM...";
                else if (roo == null) status = $"LOADING: {data.RoomInformation.RoomFile}";
                else status = $"AREA: {roo.Filename} WALLS: {roo.Walls.Count} OBJ: {data.RoomObjects.Count}";
                
                for (int i = 0; i < status.Length && i < width; i++)
                    nextBuffer.Set(i, 0, status[i]);

                centerX = 64512;
                centerY = 64512;
                avatarAngle = 0;

                if (avatar != null)
                {
                    centerX = (int)Math.Round((avatar.CoordinateX * 16f) - 1024f);
                    centerY = (int)Math.Round((avatar.CoordinateY * 16f) - 1024f);
                    avatarAngle = (float)(avatar.AngleUnits * 2.0 * Math.PI / 4096.0);
                }

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
                            symbol = '.';
                            if (!isPassable) symbol = 'D';
                            else if (wall.LeftSide?.Flags.IsHasAnimated == true || wall.RightSide?.Flags.IsHasAnimated == true) symbol = 'd';
                        }
                        else if (isPassable) symbol = 'X';
                        
                        DrawLine(nextBuffer, x0, y0, x1, y1, symbol);
                    }

                    if (roo.Things.Count >= 2)
                    {
                        var box = roo.GetBoundingBox2DFromThings();
                        WorldToView((float)box.Min.X, (float)box.Min.Y, out int bx0, out int by0);
                        WorldToView((float)box.Max.X, (float)box.Max.Y, out int bx1, out int by1);
                        nextBuffer.Set(bx0, by0, 'B');
                        nextBuffer.Set(bx1, by0, 'B');
                        nextBuffer.Set(bx0, by1, 'B');
                        nextBuffer.Set(bx1, by1, 'B');
                    }
                }

                if (data != null)
                {
                    foreach (var obj in data.RoomObjects.ToList())
                    {
                        WorldToView(obj.CoordinateX * 16f - 1024f, obj.CoordinateY * 16f - 1024f, out int relX, out int relY);
                        if (relX >= 0 && relX < width && relY >= 1 && relY < height)
                            nextBuffer.Set(relX, relY, GetCharForObject(obj));
                    }
                }

                nextBuffer.Set(width / 2, height / 2, avatar != null ? '@' : '+');
                ApplyLighting(nextBuffer, width / 2, height / 2);

                for (int x = 0; x < width; x++)
                    nextBuffer.Cells[x, 0].Intensity = 1.0f;

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        var next = nextBuffer.Cells[x, y];
                        var curr = currentBuffer.Cells[x, y];
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

        private void ApplyLighting(VideoBuffer buffer, int centerX, int centerY)
        {
            float maxDist = Math.Min(buffer.Width, buffer.Height) * 0.8f;

            for (int y = 0; y < buffer.Height; y++)
            {
                for (int x = 0; x < buffer.Width; x++)
                {
                    float dx = x - centerX;
                    float dy = y - centerY;
                    float dist = MathF.Sqrt(dx * dx + dy * dy);
                    float intensity = Math.Clamp(1.0f - (dist / maxDist), 0.1f, 1.0f);

                    if (dist > 1.0f)
                    {
                        float angle = MathF.Atan2(-dy, dx);
                        float targetScreenAngle = (float)Math.PI / 2.0f; 
                        if (Orientation != ViewOrientation.FollowRotation)
                        {
                            // In static modes, we want the vision cone to follow the avatar's world direction
                            // correctly mapped into the static view space.
                            float staticViewRotation = (Orientation == ViewOrientation.SouthUp) ? (float)Math.PI : 0;
                            // World CCW angle: -avatarAngle
                            targetScreenAngle = -avatarAngle + staticViewRotation;
                        }

                        float diff = MathF.Abs(NormalizeAngle(angle - targetScreenAngle));
                        if (diff > MathF.PI / 4.0f) intensity *= 0.4f;
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
                if (x0 >= 0 && x0 < buffer.Width && y0 > 0 && y0 < buffer.Height)
                {
                    buffer.Set(x0, y0, symbol);
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private char GetCharForObject(RoomObject obj)
        {
            if (obj.IsAvatar) return '@';
            if (obj.Flags.IsPlayer) return 'P';
            if (obj.Flags.IsAttackable) return 'M';
            if (obj.Flags.IsGettable) return 'i';
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
