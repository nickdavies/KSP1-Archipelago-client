using System;
using System.Net;
using System.Security.Cryptography;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;

namespace Archipelago
{
    public class ArchipelagoWrapper
    {
        public const string GameName = "KSP1";
        private ArchipelagoSession session;

        ArchipelagoWrapper()
        {
            session = null;
        }

        public void Login(string ip, UInt16 port, string slot_name, string pw = null)
        {
            var new_session = ArchipelagoSessionFactory.CreateSession("localhost", 38281);
            new_session.TryConnectAndLogin(GameName, slot_name, ItemsHandlingFlags.AllItems);

        }

    }

    public static class APConsole
    {
        private const string connect_usage = "/connect <server address>[:<port>] <slot> [<password>]";
        private const int default_port = 38281;

        public static void Run()
        {
            while (true)
            {
                string cmd = Console.ReadLine();
                Console.WriteLine(cmd);
                try
                {
                    if (cmd.StartsWith("/connect"))
                    {
                        RunConnect(cmd);
                    }
                }
                catch
                {
                    Console.WriteLine("Failed to handle command: ");
                }

            }
        }

        private static void RunConnect(string cmd)
        {
            string[] parts = cmd.Split(null, 4);
            if (parts.Length < 3)
            {
                Console.WriteLine("Invalid /connect command usage: " + connect_usage);
                return;
            }

            // We explicitly add TCP here because it then won't default to any port
            // and we can determine if one was provided or not for the user.
            Uri host_port = new Uri("tcp://" + parts[1]);
            string host = host_port.Host;
            int port;
            if (host_port.IsDefaultPort)
            {
                Console.WriteLine("No port provided assuming " + default_port);
                port = default_port;
            }
            else
            {
                port = host_port.Port;
            }

            string slot_name = parts[2];
            string pw = null;
            if (parts.Length == 4)
            {
                pw = parts[3];
            }
            Console.WriteLine("Connect: host=" + host + " port=" + port + " pw=" + pw);
        }
    }
}
