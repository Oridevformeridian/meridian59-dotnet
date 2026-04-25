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
        private VideoBuffer currentBuffer;
        private VideoBuffer nextBuffer;
        private int lastWidth;
        private int lastHeight;
        private volatile bool invalidatePending = false;

        // zoomLevel=0 → fits entire room in viewport; positive = zoom in, negative = zoom out.
        private int zoomLevel = -2;
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

        public void ZoomIn()  { if (zoomLevel < ZOOM_MAX) zoomLevel++; }
        public void ZoomOut() { if (zoomLevel > ZOOM_MIN) zoomLevel--; }

        public void Invalidate()
        {
            // Set a flag instead of nulling buffers directly — Render() runs on a different
            // thread and nulling here races with the diff-render loop, causing a
            // NullReferenceException that is silently caught, leaving the console with a
            // stale/ghosted image.
            invalidatePending = true;
            lastRoomId = 0;
        }

        private void WorldToView(float worldX, float worldY, out int viewX, out int viewY)
        {
            float rx = worldX - centerX;
            float ry = worldY - centerY;

            viewX = (int)Math.Round(rx / rooToGrid + viewWidth / 2.0f);
            // Scale Y by 0.5 to compensate for terminal character aspect ratio (tall chars)
            viewY = (int)Math.Round((ry / rooToGrid) * 0.5f + viewHeight / 2.0f);
        }

        public string MapStatus { get; private set; } = "";

        public void Render(TuiClient client, int consoleX, int consoleY, int width, int height)
        {
            if (width <= 0 || height <= 0) return;

            try
            {
                if (invalidatePending)
                {
                    invalidatePending = false;
                    currentBuffer = null;
                    nextBuffer    = null;
                }

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
                var ri     = data?.RoomInformation;
                var roo    = ri?.ResourceRoom;

                if (ri == null) MapStatus = "WAITING FOR ROOM...";
                else if (roo == null) MapStatus = $"LOADING: {ri.RoomFile}";
                else
                {
                    string avPos = avatar != null ? $"AV:({avatar.CoordinateX},{avatar.CoordinateY})" : "AV:null";
                    string zoomStr = zoomLevel == 0 ? "AUTO-FIT" : $"x{MathF.Pow(2.0f, zoomLevel - 1):F1}";
                    MapStatus = $"AREA: {roo.Filename} {avPos} ZOOM: {zoomStr}";
                }
                
                float minX = 0, maxX = 0, minY = 0, maxY = 0;
                if (roo != null && roo.Walls.Count > 0)
                {
                    minX = float.MaxValue; maxX = float.MinValue;
                    minY = float.MaxValue; maxY = float.MinValue;
                    foreach (var w in roo.Walls)
                    {
                        minX = Math.Min(minX, Math.Min((float)w.P1.X, (float)w.P2.X));
                        maxX = Math.Max(maxX, Math.Max((float)w.P1.X, (float)w.P2.X));
                        minY = Math.Min(minY, Math.Min((float)w.P1.Y, (float)w.P2.Y));
                        maxY = Math.Max(maxY, Math.Max((float)w.P1.Y, (float)w.P2.Y));
                    }
                    if (ri.RoomID != lastRoomId)
                    {
                        // Fit room to panel, accounting for the 0.5f vertical compression in WorldToView
                        float requiredScaleX = (maxX - minX) / Math.Max(1.0f, width - 2);
                        float requiredScaleY = ((maxY - minY) * 0.5f) / Math.Max(1.0f, height - 2);
                        cachedFitRooToGrid = Math.Max(requiredScaleX, requiredScaleY);
                        lastRoomId = ri.RoomID;
                    }
                }
                else
                {
                    cachedFitRooToGrid = 1024f;
                    lastRoomId = 0;
                }

                // Base scale: 32.0 units per grid cell is a good default zoom
                float baseScale = 32.0f;
                
                if (zoomLevel == 0)
                {
                    // zoomLevel 0 is "Auto-fit"
                    rooToGrid = cachedFitRooToGrid;
                    centerX = (int)Math.Round((maxX + minX) / 2.0);
                    centerY = (int)Math.Round((maxY + minY) / 2.0);
                }
                else
                {
                    // detail zooms (1-7) zoom in from baseScale
                    // zoomed out (-1, -2) zoom out from baseScale
                    rooToGrid = baseScale * MathF.Pow(0.5f, zoomLevel - 1);
                    
                    if (avatar != null)
                    {
                        centerX = avatar.CoordinateX * 16 - 1024;
                        centerY = avatar.CoordinateY * 16 - 1024;
                        avatarAngle = (float)(avatar.AngleUnits * 2.0 * Math.PI / 4096.0);
                    }
                    else
                    {
                        centerX = (int)Math.Round((maxX + minX) / 2.0);
                        centerY = (int)Math.Round((maxY + minY) / 2.0);
                    }
                }

                if (roo != null)
                {
                    var walls = roo.Walls;
                    for (int i = 0; i < walls.Count; i++)
                    {
                        var wall = walls[i];
                        WorldToView((float)wall.P1.X, (float)wall.P1.Y, out int x0, out int y0);
                        WorldToView((float)wall.P2.X, (float)wall.P2.Y, out int x1, out int y1);

                        char symbol = '#';
                        bool isPortal = wall.LeftSectorNum != 0 && wall.RightSectorNum != 0;
                        bool isPassable = (wall.LeftSide != null && wall.LeftSide.Flags.IsPassable) ||
                                          (wall.RightSide != null && wall.RightSide.Flags.IsPassable);

                        ConsoleColor wallColor = ConsoleColor.DarkGray;
                        if (isPortal)
                        {
                            symbol = '.';
                            wallColor = ConsoleColor.DarkCyan;
                            if (!isPassable) { symbol = 'D'; wallColor = ConsoleColor.Yellow; }
                            else if (wall.LeftSide?.Flags.IsHasAnimated == true || wall.RightSide?.Flags.IsHasAnimated == true)
                                { symbol = 'd'; wallColor = ConsoleColor.DarkYellow; }
                        }
                        else if (isPassable) { symbol = 'X'; wallColor = ConsoleColor.Cyan; }

                        DrawLine(nextBuffer, x0, y0, x1, y1, symbol, wallColor);
                    }

                    if (roo.Things.Count >= 2)
                    {
                        var box = roo.GetBoundingBox2DFromThings();
                        WorldToView((float)box.Min.X, (float)box.Min.Y, out int bx0, out int by0);
                        WorldToView((float)box.Max.X, (float)box.Max.Y, out int bx1, out int by1);
                        nextBuffer.Set(bx0, by0, 'B', 1.0f, ConsoleColor.DarkGray);
                        nextBuffer.Set(bx1, by0, 'B', 1.0f, ConsoleColor.DarkGray);
                        nextBuffer.Set(bx0, by1, 'B', 1.0f, ConsoleColor.DarkGray);
                        nextBuffer.Set(bx1, by1, 'B', 1.0f, ConsoleColor.DarkGray);
                    }
                }

                // Apply lighting to walls only — all objects drawn after so they're unaffected
                WorldToView(avatar != null ? avatar.CoordinateX * 16f - 1024f : 0,
                            avatar != null ? avatar.CoordinateY * 16f - 1024f : 0,
                            out int avViewX, out int avViewY);
                ApplyLighting(nextBuffer, avViewX, avViewY);

                if (data != null)
                {
                    uint targetID = (data.TargetObject as RoomObject)?.ID ?? 0;

                    // Non-player objects (NPCs, mobs, items, etc.)
                    foreach (var obj in data.RoomObjects.ToList())
                    {
                        if (obj.Flags.IsPlayer || obj.IsAvatar) continue;

                        WorldToView(obj.CoordinateX * 16f - 1024f, obj.CoordinateY * 16f - 1024f, out int relX, out int relY);
                        if (relX >= 0 && relX < width && relY >= 1 && relY < height)
                        {
                            var (ch, fg, bg) = GetDisplay(obj, obj.ID == targetID && targetID != 0);
                            nextBuffer.Set(relX, relY, ch, 1.0f, fg, bg);
                        }
                    }

                    // Players on top
                    foreach (var obj in data.RoomObjects.ToList())
                    {
                        if (!obj.Flags.IsPlayer || obj.IsAvatar) continue;

                        WorldToView(obj.CoordinateX * 16f - 1024f, obj.CoordinateY * 16f - 1024f, out int relX, out int relY);
                        if (relX >= 0 && relX < width && relY >= 1 && relY < height)
                        {
                            bool isTarget = obj.ID == targetID && targetID != 0;
                            ConsoleColor bg = isTarget ? ConsoleColor.DarkMagenta : ConsoleColor.DarkBlue;
                            nextBuffer.Set(relX, relY, '@', 1.0f, ConsoleColor.White, bg);
                        }
                    }
                }

                if (avatar != null)
                {
                    int dir = ((avatar.AngleUnits + 256) % 4096) / 512;
                    char icon = dir switch {
                        0 => '>', 1 => '\\', 2 => 'v', 3 => '/',
                        4 => '<', 5 => '\'', 6 => '^', 7 => '`',
                        _ => '@'
                    };
                    nextBuffer.Set(avViewX, avViewY, icon, 1.0f, ConsoleColor.White, ConsoleColor.DarkGray);
                }
                else nextBuffer.Set(width / 2, height / 2, '+', 1.0f, ConsoleColor.White);


                ConsoleColor currentColor   = ConsoleColor.Gray;
                ConsoleColor currentBgColor = ConsoleColor.Black;
                Console.ForegroundColor = currentColor;
                Console.BackgroundColor = currentBgColor;

                var runBuf = new System.Text.StringBuilder(width);

                for (int y = 0; y < height; y++)
                {
                    // Batch consecutive changed cells on the same row that share the same color.
                    int runStartX   = -1;
                    ConsoleColor runFg = ConsoleColor.Gray;
                    ConsoleColor runBg = ConsoleColor.Black;
                    runBuf.Clear();

                    for (int x = 0; x < width; x++)
                    {
                        var next = nextBuffer.Cells[x, y];
                        var curr = currentBuffer.Cells[x, y];
                        char displayChar = TransformChar(next.Char, next.Intensity);

                        bool charChanged  = displayChar != curr.Char;
                        bool colorChanged = displayChar != ' ' && next.Color != curr.Color;
                        bool bgChanged    = next.BgColor != curr.BgColor;
                        bool needsWrite   = charChanged || colorChanged || bgChanged;

                        if (needsWrite && runStartX >= 0 &&
                            (next.Color != runFg || next.BgColor != runBg))
                        {
                            // Flush current run — color break
                            if (runFg != currentColor)   { Console.ForegroundColor = runFg; currentColor   = runFg; }
                            if (runBg != currentBgColor) { Console.BackgroundColor = runBg; currentBgColor = runBg; }
                            Console.SetCursorPosition(consoleX + runStartX, consoleY + y);
                            Console.Write(runBuf.ToString());
                            runBuf.Clear();
                            runStartX = -1;
                        }

                        if (needsWrite)
                        {
                            if (runStartX < 0)
                            {
                                runStartX = x;
                                runFg     = next.Color;
                                runBg     = next.BgColor;
                            }
                            runBuf.Append(displayChar);
                            currentBuffer.Cells[x, y].Char    = displayChar;
                            currentBuffer.Cells[x, y].Color   = next.Color;
                            currentBuffer.Cells[x, y].BgColor = next.BgColor;
                        }
                        else if (runStartX >= 0)
                        {
                            // Gap in changed cells — flush run
                            if (runFg != currentColor)   { Console.ForegroundColor = runFg; currentColor   = runFg; }
                            if (runBg != currentBgColor) { Console.BackgroundColor = runBg; currentBgColor = runBg; }
                            Console.SetCursorPosition(consoleX + runStartX, consoleY + y);
                            Console.Write(runBuf.ToString());
                            runBuf.Clear();
                            runStartX = -1;
                        }
                    }

                    // Flush any remaining run at end of row
                    if (runStartX >= 0)
                    {
                        if (runFg != currentColor)   { Console.ForegroundColor = runFg; currentColor   = runFg; }
                        if (runBg != currentBgColor) { Console.BackgroundColor = runBg; currentBgColor = runBg; }
                        Console.SetCursorPosition(consoleX + runStartX, consoleY + y);
                        Console.Write(runBuf.ToString());
                        runBuf.Clear();
                    }
                }
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                client.Log("ERROR", $"Renderer failure: {ex.Message}");
            }
        }

        private void ApplyLighting(VideoBuffer buffer, int originX, int originY)
        {
            float maxDist    = Math.Min(buffer.Width, buffer.Height) * 0.8f;
            float maxDistSq  = maxDist * maxDist;

            for (int y = 0; y < buffer.Height; y++)
            {
                float dy = y - originY;
                float dySq = dy * dy;
                for (int x = 0; x < buffer.Width; x++)
                {
                    float dx    = x - originX;
                    float distSq = dx * dx + dySq;
                    float intensity = Math.Clamp(1.0f - MathF.Sqrt(distSq) / maxDist, 0.1f, 1.0f);

                    if (distSq > 1.0f)
                    {
                        float angle = MathF.Atan2(-dy, dx);
                        float targetScreenAngle = -avatarAngle;
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

        private void DrawLine(VideoBuffer buffer, int x0, int y0, int x1, int y1, char symbol, ConsoleColor color = ConsoleColor.DarkGray)
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
                    buffer.Set(x0, y0, symbol, 1.0f, color);
                }
                if (x0 == x1 && y0 == y1) break;
                int e2 = 2 * err;
                if (e2 > -dy) { err -= dy; x0 += sx; }
                if (e2 < dx) { err += dx; y0 += sy; }
            }
        }

        private (char ch, ConsoleColor fg, ConsoleColor bg) GetDisplay(RoomObject obj, bool isTarget)
        {
            if (obj.ID == 0) return (' ', ConsoleColor.Gray, ConsoleColor.Black);

            // Players: @ on blue tile (handled in render loop; avatar handled separately)
            if (obj.Flags.IsPlayer)
                return ('@', ConsoleColor.White, ConsoleColor.DarkBlue);

            // Hostile mobs/creatures
            if (obj.Flags.IsCreature || obj.Flags.IsAttackable)
            {
                if (isTarget)
                    return ('!', ConsoleColor.White,    ConsoleColor.Red);      // our target
                if (obj.Flags.IsMinimapAggroSelf)
                    return ('!', ConsoleColor.Black,    ConsoleColor.Red);      // targeting us (black ! = black ring in minimap)
                if (obj.Flags.IsMinimapAggroOther)
                    return ('!', ConsoleColor.White,    ConsoleColor.DarkRed);  // targeting another (white ring in minimap)
                return ('*', ConsoleColor.DarkRed, ConsoleColor.Black);         // idle
            }

            // Dialog NPCs
            if (obj.Flags.IsNPC)
                return isTarget
                    ? ('*', ConsoleColor.White,   ConsoleColor.Yellow)   // NPC targeted: yellow box, white *
                    : ('*', ConsoleColor.DarkBlue, ConsoleColor.Yellow);  // NPC idle: yellow box, blue *

            // Items on the ground
            if (obj.Flags.IsGettable) return ('i', ConsoleColor.Green, ConsoleColor.Black);

            // Environmental objects
            if (obj.Name != null)
            {
                if (obj.Name.Contains("door",  StringComparison.OrdinalIgnoreCase)) return ('D', ConsoleColor.DarkYellow, ConsoleColor.Black);
                if (obj.Name.Contains("chest", StringComparison.OrdinalIgnoreCase)) return ('C', ConsoleColor.DarkYellow, ConsoleColor.Black);
                if (obj.Name.Contains("sign",  StringComparison.OrdinalIgnoreCase)) return ('S', ConsoleColor.DarkGray,   ConsoleColor.Black);
            }

            return ('*', ConsoleColor.Magenta, ConsoleColor.Black);
        }
    }
}
