using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;

namespace QuiVrRevival
{
    // Called from the patched game code in place of the original, hard-coded server address.
    //
    // Settings live in <game>/QuiVrRevival/config.ini:
    //   server=http://127.0.0.1:4040/parse/
    //   autostart=true      start <game>/QuiVrRevival/QuiVrRevival.exe if nothing listens on the server's port
    public static class Hook
    {
        const string DefaultUrl = "http://127.0.0.1:4040/parse/";
        const int StartupTimeoutMs = 20000;

        static string url;
        static string modDir;

        public static string ServerUrl()
        {
            if (url != null) return url;
            url = DefaultUrl;
            try
            {
                string managed = Path.GetDirectoryName(typeof(Hook).Assembly.Location);
                string gameDir = Path.GetDirectoryName(Path.GetDirectoryName(managed));
                modDir = Path.Combine(gameDir, "QuiVrRevival");

                Dictionary<string, string> cfg = ReadConfig(Path.Combine(modDir, "config.ini"));
                string value;
                if (cfg.TryGetValue("server", out value) && value.Length > 0)
                    url = value.EndsWith("/") ? value : value + "/";
                Log("server " + url);

                if (cfg.TryGetValue("autostart", out value) && value.Trim().ToLowerInvariant() == "true")
                    EnsureLocalServer(new Uri(url));
            }
            catch (Exception e)
            {
                Log("error: " + e);
            }
            return url;
        }

        static void EnsureLocalServer(Uri uri)
        {
            int port = uri.Port;
            if (IsListening(port))
            {
                Log("server already running on port " + port);
                return;
            }
            string exe = Path.Combine(modDir, "QuiVrRevival.exe");
            if (!File.Exists(exe))
            {
                Log("autostart: " + exe + " not found");
                return;
            }
            var psi = new ProcessStartInfo(exe,
                "serve --port " + port + " --host 127.0.0.1 --parent-pid " + Process.GetCurrentProcess().Id)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = modDir,
            };
            Process.Start(psi);
            Log("autostart: started " + exe);

            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < StartupTimeoutMs)
            {
                if (IsListening(port))
                {
                    Log("autostart: server ready after " + watch.ElapsedMilliseconds + " ms");
                    return;
                }
                Thread.Sleep(200);
            }
            Log("autostart: server did not start within " + StartupTimeoutMs + " ms");
        }

        static bool IsListening(int port)
        {
            try
            {
                using (var client = new TcpClient())
                {
                    IAsyncResult ar = client.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(300)) return false;
                    client.EndConnect(ar);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        static Dictionary<string, string> ReadConfig(string path)
        {
            var cfg = new Dictionary<string, string>();
            if (!File.Exists(path)) return cfg;
            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq > 0) cfg[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
            }
            return cfg;
        }

        static void Log(string msg)
        {
            try
            {
                if (modDir == null) return;
                File.AppendAllText(Path.Combine(modDir, "hook.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + msg + Environment.NewLine);
            }
            catch
            {
            }
        }
    }
}
