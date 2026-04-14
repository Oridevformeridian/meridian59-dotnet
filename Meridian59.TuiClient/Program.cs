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
            string scriptFile = null;
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg.Equals("--no-autoexec", StringComparison.OrdinalIgnoreCase))
                    noAutoexec = true;
                else if ((arg.Equals("-s", StringComparison.OrdinalIgnoreCase) || arg.Equals("--script", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    scriptFile = args[++i];
                }
            }

            try
            {
                using (var client = new TuiClient())
                {
                    client.NoAutoexec = noAutoexec;
                    client.ScriptFile = scriptFile;
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
