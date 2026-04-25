using System;
using System.IO;
using Meridian59.Files.ROO;

namespace RoomInfoDumper
{
    class Program
    {
        static unsafe void Main(string[] args)
        {
            if (args.Length == 0)
            {
                Console.WriteLine("Usage: RoomInfoDumper <roo_file>");
                return;
            }

            string filePath = args[0];
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"File not found: {filePath}");
                return;
            }

            byte[] buffer = File.ReadAllBytes(filePath);
            fixed (byte* pBuffer = buffer)
            {
                byte* ptr = pBuffer;
                RooFile roo = new RooFile();
                roo.ReadFrom(ref ptr);

                Console.WriteLine($"Room: {filePath}");
                Console.WriteLine($"RoomSizeX: {roo.RoomSizeX}");
                Console.WriteLine($"RoomSizeY: {roo.RoomSizeY}");

                var box = roo.GetBoundingBox2D(true);
                Console.WriteLine($"BoundingBox2D Min: ({box.Min.X}, {box.Min.Y}) Max: ({box.Max.X}, {box.Max.Y})");
                
                if (roo.Walls.Count > 0)
                {
                    Console.WriteLine($"Wall[0] P1: ({roo.Walls[0].P1.X}, {roo.Walls[0].P1.Y}) P2: ({roo.Walls[0].P2.X}, {roo.Walls[0].P2.Y})");
                }
            }
        }
    }
}
