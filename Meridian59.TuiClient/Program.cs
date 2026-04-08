using System;
using System.Threading;
using Meridian59.Bot;

namespace Meridian59.TuiClient
{
    class Program
    {
        static void Main(string[] args)
        {
            Console.Title = "Meridian 59 TUI Client";

            try
            {
                using (var client = new TuiClient())
                {
                    client.IsService = false;
                    client.Start(false);

                    while (client.IsRunning)
                    {
                        client.Tick();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FATAL ERROR: " + ex.Message);
                Console.WriteLine(ex.StackTrace);
                Console.ReadKey();
            }
        }
    }
}
