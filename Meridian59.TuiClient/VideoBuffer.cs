using System;

namespace Meridian59.TuiClient
{
    public struct VideoCell
    {
        public char Char;
        public float Intensity;
        public ConsoleColor Color;
        public ConsoleColor BgColor;
    }

    public class VideoBuffer
    {
        public int Width { get; private set; }
        public int Height { get; private set; }
        public VideoCell[,] Cells { get; private set; }

        public VideoBuffer(int width, int height)
        {
            Width = width;
            Height = height;
            Cells = new VideoCell[width, height];
            Clear();
        }

        public void Clear()
        {
            for (int x = 0; x < Width; x++)
            {
                for (int y = 0; y < Height; y++)
                {
                    Cells[x, y].Char = ' ';
                    Cells[x, y].Intensity = 0.0f;
                    Cells[x, y].Color = ConsoleColor.Gray;
                    Cells[x, y].BgColor = ConsoleColor.Black;
                }
            }
        }

        public void Set(int x, int y, char c, float intensity = 1.0f,
                        ConsoleColor color = ConsoleColor.Gray,
                        ConsoleColor bgColor = ConsoleColor.Black)
        {
            if (x >= 0 && x < Width && y >= 0 && y < Height)
            {
                Cells[x, y].Char = c;
                Cells[x, y].Intensity = intensity;
                Cells[x, y].Color = color;
                Cells[x, y].BgColor = bgColor;
            }
        }
    }
}
