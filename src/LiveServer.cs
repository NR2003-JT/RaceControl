using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RaceControl
{
    // A read-only live page: http://<this pc>:8768/ shows the race as Race Control sees it (phase, pits, first time
    // by, the line, the cone, the running order, the log), refreshed every second; /board is the choose cone board
    // as an OBS browser source. Nothing can be changed from the page. Local only unless "on the network" is on.
    public class LiveServer
    {
        public const int DefaultPort = 8768;
        public const string FirewallRule = "NR2003 Race Control live page";

        public volatile string StateJson = "{}";
        public volatile byte[] BoardPng;            // the board as it is now (null = none showing)
        public DateTime BoardWantedAt = DateTime.MinValue; // a /board.png request came in: keep rendering for a while
        public readonly int Port;
        public readonly bool Network;

        TcpListener listener;
        Thread thread;
        volatile bool stop;

        public LiveServer(int port, bool network) { Port = port; Network = network; }

        public void Start()
        {
            listener = new TcpListener(Network ? IPAddress.Any : IPAddress.Loopback, Port);
            listener.Start();
            thread = new Thread(Loop) { IsBackground = true, Name = "live page" };
            thread.Start();
        }

        public void Stop()
        {
            stop = true;
            try { listener.Stop(); } catch { }
        }

        void Loop()
        {
            while (!stop)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); } catch { if (stop) return; continue; }
                ThreadPool.QueueUserWorkItem(_ => Serve(c));
            }
        }

        void Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    c.ReceiveTimeout = c.SendTimeout = 5000;
                    NetworkStream s = c.GetStream();
                    var reader = new StreamReader(s, Encoding.ASCII);
                    string first = reader.ReadLine();
                    if (first == null) return;
                    string line;
                    while (!string.IsNullOrEmpty(line = reader.ReadLine())) { } // headers: not needed
                    string[] parts = first.Split(' ');
                    string path = parts.Length > 1 ? parts[1] : "/";
                    int q = path.IndexOf('?');
                    if (q >= 0) path = path.Substring(0, q);
                    if (parts[0] != "GET" && parts[0] != "HEAD") { Send(s, "405 Method Not Allowed", "text/plain", Encoding.ASCII.GetBytes("read only")); return; }
                    switch (path)
                    {
                        case "/":
                        case "/index.html": Send(s, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LivePage.Html)); break;
                        case "/board": Send(s, "200 OK", "text/html; charset=utf-8", Encoding.UTF8.GetBytes(LivePage.BoardHtml)); break;
                        case "/state.json": Send(s, "200 OK", "application/json; charset=utf-8", Encoding.UTF8.GetBytes(StateJson)); break;
                        case "/board.png":
                            BoardWantedAt = DateTime.Now;
                            Send(s, "200 OK", "image/png", BoardPng ?? Blank); break;
                        default: Send(s, "404 Not Found", "text/plain", Encoding.ASCII.GetBytes("not found")); break;
                    }
                }
            }
            catch { }
        }

        static void Send(Stream s, string status, string type, byte[] body)
        {
            string head = "HTTP/1.1 " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                "\r\nCache-Control: no-store\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n";
            byte[] h = Encoding.ASCII.GetBytes(head);
            s.Write(h, 0, h.Length);
            s.Write(body, 0, body.Length);
            s.Flush();
        }

        // a 1x1 transparent PNG: no board showing
        static readonly byte[] Blank = MakeBlank();

        static byte[] MakeBlank()
        {
            using (var b = new System.Drawing.Bitmap(1, 1, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
            using (var ms = new MemoryStream())
            {
                b.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // The addresses another computer on the network can use.
        public static string[] Addresses()
        {
            try
            {
                var list = new System.Collections.Generic.List<string>();
                foreach (IPAddress a in Dns.GetHostAddresses(Dns.GetHostName()))
                    if (a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a)) list.Add(a.ToString());
                return list.ToArray();
            }
            catch { return new string[0]; }
        }

        // Inbound rule for the port on private networks only (Race Control runs as administrator).
        public static string SetFirewall(int port, bool allow)
        {
            string r = Netsh("advfirewall firewall delete rule name=\"" + FirewallRule + "\"");
            if (!allow) return null;
            r = Netsh("advfirewall firewall add rule name=\"" + FirewallRule + "\" dir=in action=allow protocol=TCP localport=" + port + " profile=private");
            return r;
        }

        static string Netsh(string args)
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo("netsh", args) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
                string o = p.StandardOutput.ReadToEnd();
                p.WaitForExit(5000);
                return p.ExitCode == 0 ? null : o.Trim();
            }
            catch (Exception e) { return e.Message; }
        }
    }
}
