using System;

namespace DodgeServer
{
    class Program
    {
        static void Main(string[] args)
        {
            int port = 5055;
            Console.Title = "Dodge Authoritative Server : " + port;
            var server = new GameServer("0.0.0.0", port);
            server.Start();
            Console.WriteLine("Server started on " + port + ". Press ENTER to stop.");
            Console.ReadLine();
            server.Stop();
        }
    }
}
