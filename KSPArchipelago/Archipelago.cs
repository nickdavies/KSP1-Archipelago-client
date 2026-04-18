using System;
using System.Threading;

using Archipelago.MultiClient.Net;
using Archipelago.MultiClient.Net.Enums;
using KSPArchipelago;
using UnityEngine;

namespace Archipelago
{
    public class APConsole
    {
        // Must match KSP1World.game in the Python world.
        public const string GameName = "Kerbal Space Program 1";

        private const string ConnectUsage = "/connect <address>[:<port>] <slot> [<password>]";
        private const int DefaultPort = 38281;

        private readonly KSPArchipelagoMod mod;

        // Stored for reconnection.
        private string lastHost;
        private int lastPort;
        private string lastSlot;
        private string lastPassword;
        private ArchipelagoSession currentSession;

        // Backoff delays in seconds: 5, 10, 20, 30, 30, ...
        private static readonly int[] BackoffDelays = { 5, 10, 20, 30 };
        private int backoffIndex = 0;
        private bool reconnecting = false;

        public APConsole(KSPArchipelagoMod mod)
        {
            this.mod = mod;
        }

        /// <summary>Log to both Unity (Player.log) and the debug console.</summary>
        private static void Log(string msg)
        {
            Debug.Log(msg);
            Console.WriteLine(msg);
        }

        public void Run()
        {
            while (true)
            {
                try
                {
                    string cmd = Console.ReadLine();
                    if (string.IsNullOrEmpty(cmd)) continue;
                    Console.WriteLine(cmd);
                    if (cmd.StartsWith("/connect"))
                        RunConnect(cmd);
                    else if (cmd == "/disconnect")
                        Disconnect();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Command error: {ex.Message}");
                }
            }
        }

        // Called directly by ArchipelagoUI to avoid re-parsing a command string.
        public void RunConnectDirect(string host, int port, string slot, string password)
        {
            Connect(host, port, slot, password);
        }

        private void RunConnect(string cmd)
        {
            string[] parts = cmd.Split(null, 4);
            if (parts.Length < 3)
            {
                Console.WriteLine("Usage: " + ConnectUsage);
                return;
            }

            Uri hostPort = new Uri("tcp://" + parts[1]);
            string host = hostPort.Host;
            int port = hostPort.IsDefaultPort ? DefaultPort : hostPort.Port;
            string slot = parts[2];
            string password = parts.Length == 4 ? parts[3] : null;

            Connect(host, port, slot, password);
        }

        private void Connect(string host, int port, string slot, string password)
        {
            Log($"[KSP-AP] Connecting: {host}:{port} slot={slot}");
            try
            {
                var session = ArchipelagoSessionFactory.CreateSession(host, port);
                LoginResult result = session.TryConnectAndLogin(
                    GameName, slot, ItemsHandlingFlags.AllItems, password: password);

                if (result.Successful)
                {
                    // Store params for reconnection.
                    lastHost = host;
                    lastPort = port;
                    lastSlot = slot;
                    lastPassword = password;
                    currentSession = session;
                    backoffIndex = 0;
                    reconnecting = false;

                    // Hook disconnection.
                    session.Socket.SocketClosed += OnSocketClosed;

                    Log("[KSP-AP] Connected successfully.");
                    mod.HandleConnect(session, (LoginSuccessful)result, slot);
                }
                else
                {
                    LoginFailure failure = (LoginFailure)result;
                    string msg = $"[KSP-AP] Connection failed to {host}:{port} as {slot}:";
                    foreach (string error in failure.Errors)
                        msg += $"\n  {error}";
                    foreach (ConnectionRefusedError code in failure.ErrorCodes)
                        msg += $"\n  {code}";
                    Log(msg);
                }
            }
            catch (Exception ex)
            {
                Log($"[KSP-AP] Connection error: {ex}");
            }
        }

        private void Disconnect()
        {
            reconnecting = false;
            currentSession = null;
            mod.HandleDisconnect();
            Log("[KSP-AP] Disconnected.");
        }

        private void OnSocketClosed(string reason)
        {
            Log($"[KSP-AP] Server disconnected: {reason}");
            mod.HandleDisconnect();
            if (!reconnecting && lastHost != null)
                ScheduleReconnect();
        }

        private void ScheduleReconnect()
        {
            reconnecting = true;
            int delay = BackoffDelays[Math.Min(backoffIndex, BackoffDelays.Length - 1)];
            backoffIndex++;
            Log($"[KSP-AP] Reconnecting in {delay}s...");
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Thread.Sleep(delay * 1000);
                if (reconnecting)
                    AttemptReconnect();
            });
        }

        private void AttemptReconnect()
        {
            Log($"[KSP-AP] Attempting reconnect ({backoffIndex}/{BackoffDelays.Length})...");
            Connect(lastHost, lastPort, lastSlot, lastPassword);
            if (!mod.IsConnected)
                ScheduleReconnect(); // keep trying
        }
    }
}
