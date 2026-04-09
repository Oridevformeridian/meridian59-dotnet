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

            bool noAutoexec = false;
            foreach (var arg in args)
            {
                if (arg.Equals("--no-autoexec", StringComparison.OrdinalIgnoreCase))
                    noAutoexec = true;
            }

            try
            {
                using (var client = new TuiClient())
                {
                    client.NoAutoexec = noAutoexec;
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
