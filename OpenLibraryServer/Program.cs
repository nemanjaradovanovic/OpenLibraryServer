using System;

namespace OpenLibraryServer
{
    internal static class Program
    {
        private static WebServer _server;

        private static void Main(string[] args)
        {
            const string prefix = "http://localhost:8080/";
            Console.Title = "OpenLibrary Multithreaded Server (.NET Framework - Classic Threads)";
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Booting server on {prefix}");

            _server = new WebServer(prefix);
            _server.Start(); // ⇐ sinhrono startovanje bez async/await

            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Ready. Open your browser at {prefix}");
            Console.WriteLine("Press Enter to stop...");
            Console.ReadLine();

            _server.Dispose();
        }
    }
}
