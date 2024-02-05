using System;
using System.Net;
using System.Security.Cryptography;
using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using KSPArchipelago;

namespace Archipelago
{
    public class APConsole
    {
        public const string GameName = "KSP1";
        private const string connect_usage = "/connect <server address>[:<port>] <slot> [<password>]";
        private const int default_port = 38281;
        private readonly KSPArchipelagoMod archipelagoMod;

        public APConsole(KSPArchipelagoMod mod)
        {
            this.archipelagoMod = mod;
        }

        public void Run()
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

        private void RunConnect(string cmd)
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

            var session = ArchipelagoSessionFactory.CreateSession(host, port);
            LoginResult result = session.TryConnectAndLogin(GameName, slot_name, ItemsHandlingFlags.AllItems, password: pw);
            if (result.Successful)
            {
                archipelagoMod.HandleConnect(session, (LoginSuccessful)result);
            }
            else
            {
                LoginFailure failure = (LoginFailure)result;
                string errorMessage = $"Failed to Connect to {host}:{port} as {slot_name} with pw {pw}:";
                foreach (string error in failure.Errors)
                {
                    errorMessage += $"\n    {error}";
                }
                foreach (ConnectionRefusedError error in failure.ErrorCodes)
                {
                    errorMessage += $"\n    {error}";
                }
                Console.WriteLine(errorMessage);
            }
        }
    }
}
