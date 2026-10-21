using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace DropFly;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "DropFly.SingleInstance", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp());
    }
}

// ---------------------------------------------------------------- config

static class Config
{
    static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DropFly");
    static string CodeFile => Path.Combine(Dir, "code.txt");

    public static string Code { get; private set; } = "";
    public static bool IsFirstRun { get; private set; }

    public static string SaveDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "DropFly");

    public static void Load()
    {
        Directory.CreateDirectory(Dir);
        if (File.Exists(CodeFile)) Code = File.ReadAllText(CodeFile).Trim();
        if (Code.Length == 0)
        {
            Code = RandomNumberGenerator.GetInt32(100000, 1000000).ToString();
            File.WriteAllText(CodeFile, Code);
            IsFirstRun = true;
        }
        Directory.CreateDirectory(SaveDir);
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool StartWithWindows
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue("DropFly") != null;
        }
        set
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k == null) return;
            if (value) k.SetValue("DropFly", "\"" + Environment.ProcessPath + "\"");
            else k.DeleteValue("DropFly", false);
        }
    }
}

// ---------------------------------------------------------------- tray app

sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon tray;
    readonly Control ui = new();

    public TrayApp()
    {
        Config.Load();
        ui.CreateControl();
        _ = ui.Handle; // force handle so we can BeginInvoke from server threads

        if (Config.IsFirstRun) Config.StartWithWindows = true;

        var menu = new ContextMenuStrip();
        var codeItem = new ToolStripMenuItem("Pairing code: " + Config.Code) { Enabled = false };
        menu.Items.Add(codeItem);
        menu.Items.Add("Open received files", null, (_, _) =>
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + Config.SaveDir + "\"") { UseShellExecute = true }));
        var startup = new ToolStripMenuItem("Start with Windows") { Checked = Config.StartWithWindows, CheckOnClick = true };
        startup.CheckedChanged += (_, _) => Config.StartWithWindows = startup.Checked;
        menu.Items.Add(startup);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => { tray!.Visible = false; Application.Exit(); });

        tray = new NotifyIcon
        {
            Icon = MakeIcon(),
            Text = "DropFly - ready",
            ContextMenuStrip = menu,
            Visible = true,
        };

        var server = new Server(
            begin: (title, sub) => ui.BeginInvoke(new Action(() => Overlay.Begin(title, sub))),
            progress: f => ui.BeginInvoke(new Action(() => Overlay.Progress(f))),
            finish: (ok, sub, path) => ui.BeginInvoke(new Action(() => Overlay.Finish(ok, sub, path))),
            setClipboard: text =>
            {
                ui.Invoke(new Action(() => { try { Clipboard.SetText(text); } catch { } }));
            });
        server.Start();

        if (Config.IsFirstRun)
        {
            MessageBox.Show(
                "DropFly is running in your system tray.\n\nYour pairing code is:\n\n        " + Config.Code +
                "\n\nEnter this code once in the DropFly app on your phone.\n" +
                "(You can see it again any time by right-clicking the tray icon.)\n\n" +
                "If Windows asks about the firewall, allow it on Private networks.",
                "DropFly", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    static Icon MakeIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var br = new LinearGradientBrush(new Rectangle(0, 0, 32, 32),
                Color.FromArgb(94, 160, 255), Color.FromArgb(140, 90, 255), 45f);
            g.FillEllipse(br, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 16, 23, 16, 9);
            g.DrawLine(pen, 10, 15, 16, 9);
            g.DrawLine(pen, 22, 15, 16, 9);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

// ---------------------------------------------------------------- server

sealed class Server
{
    const int HttpPort = 53317;
    const int DiscoveryPort = 53318;

    readonly Action<string, string> begin;
    readonly Action<double> progress;
    readonly Action<bool, string, string?> finish;
    readonly Action<string> setClipboard;

    public Server(Action<string, string> begin, Action<double> progress,
                  Action<bool, string, string?> finish, Action<string> setClipboard)
    {
        this.begin = begin; this.progress = progress; this.finish = finish; this.setClipboard = setClipboard;
    }

    public void Start()
    {
        new Thread(DiscoveryLoop) { IsBackground = true }.Start();
        new Thread(HttpLoop) { IsBackground = true }.Start();
    }

    // The phone broadcasts "DROPFLY?" on the Wi-Fi; we answer with our port.
    void DiscoveryLoop()
    {
        while (true)
        {
            try
            {
                using var udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                var ep = new IPEndPoint(IPAddress.Any, 0);
                while (true)
                {
                    var data = udp.Receive(ref ep);
                    if (Encoding.UTF8.GetString(data) == "DROPFLY?")
                    {
                        var reply = Encoding.UTF8.GetBytes($"DROPFLY!{HttpPort}|{Environment.MachineName}");
                        udp.Send(reply, reply.Length, ep);
                    }
                }
            }
            catch { Thread.Sleep(1000); }
        }
    }

    void HttpLoop()
    {
        while (true)
        {
            try
            {
                var l = new TcpListener(IPAddress.Any, HttpPort);
                l.Start();
                while (true)
                {
                    var c = l.AcceptTcpClient();
                    Task.Run(() => Handle(c));
                }
            }
            catch { Thread.Sleep(1000); }
        }
    }

    static void Reply(Stream s, int code, string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var head = $"HTTP/1.1 {code} {text}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var h = Encoding.ASCII.GetBytes(head);
        s.Write(h, 0, h.Length);
        s.Write(body, 0, body.Length);
        s.Flush();
    }

    static string? ReadHeaderBlock(Stream s, out byte[] leftover)
    {
        var buf = new byte[16384];
        int len = 0;
        leftover = Array.Empty<byte>();
        while (len < buf.Length)
        {
            int n = s.Read(buf, len, buf.Length - len);
            if (n <= 0) return null;
            len += n;
            for (int i = Math.Max(0, len - n - 3); i + 3 < len; i++)
            {
                if (buf[i] == 13 && buf[i + 1] == 10 && buf[i + 2] == 13 && buf[i + 3] == 10)
                {
                    leftover = buf[(i + 4)..len];
                    return Encoding.ASCII.GetString(buf, 0, i);
                }
            }
        }
        return null;
    }

    void Handle(TcpClient c)
    {
        string? savedPath = null;
        bool started = false;
        try
        {
            using (c)
            {
                c.ReceiveTimeout = 20000;
                c.SendTimeout = 20000;
                using var ns = c.GetStream();

                var head = ReadHeaderBlock(ns, out var leftover);
                if (head == null) return;

                var lines = head.Split("\r\n");
                var first = lines[0].Split(' ');
                if (first.Length < 2) return;
                string method = first[0], target = first[1];
                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    int i = line.IndexOf(':');
                    if (i > 0) headers[line[..i].Trim()] = line[(i + 1)..].Trim();
                }

                if (!headers.TryGetValue("X-Token", out var tok) || tok != Config.Code)
                {
                    Reply(ns, 401, "Unauthorized");
                    return;
                }

                string path = target, query = "";
                int q = target.IndexOf('?');
                if (q >= 0) { path = target[..q]; query = target[(q + 1)..]; }

                long length = headers.TryGetValue("Content-Length", out var cl) && long.TryParse(cl, out var l) ? l : 0;

                if (path == "/ping") { Reply(ns, 200, "OK"); return; }

                if (path == "/text" && method == "POST")
                {
                    if (length > 1_000_000) { Reply(ns, 413, "Too large"); return; }
                    var ms = new MemoryStream();
                    ms.Write(leftover, 0, leftover.Length);
                    var tmp = new byte[8192];
                    while (ms.Length < length)
                    {
                        int n = ns.Read(tmp, 0, (int)Math.Min(tmp.Length, length - ms.Length));
                        if (n <= 0) break;
                        ms.Write(tmp, 0, n);
                    }
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    begin("Text copied", "Ready to paste");
                    setClipboard(text);
                    finish(true, Preview(text), null);
                    Reply(ns, 200, "OK");
                    return;
                }

                if (path == "/upload" && method == "POST")
                {
                    string name = "file";
                    foreach (var part in query.Split('&'))
                        if (part.StartsWith("name=")) name = Uri.UnescapeDataString(part[5..].Replace('+', ' '));
                    savedPath = UniquePath(name);

                    started = true;
                    begin("Receiving", Path.GetFileName(savedPath));

                    long done = 0;
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    long lastReport = 0;
                    using (var fs = File.Create(savedPath))
                    {
                        if (leftover.Length > 0) { fs.Write(leftover, 0, leftover.Length); done += leftover.Length; }
                        var buf = new byte[64 * 1024];
                        while (done < length)
                        {
                            int n = ns.Read(buf, 0, (int)Math.Min(buf.Length, length - done));
                            if (n <= 0) throw new IOException("connection closed");
                            fs.Write(buf, 0, n);
                            done += n;
                            if (sw.ElapsedMilliseconds - lastReport > 40 && length > 0)
                            {
                                lastReport = sw.ElapsedMilliseconds;
                                progress((double)done / length);
                            }
                        }
                    }
                    finish(true, Path.GetFileName(savedPath), savedPath);
                    Reply(ns, 200, "OK");
                    return;
                }

                Reply(ns, 404, "Not found");
            }
        }
        catch
        {
            if (savedPath != null) { try { File.Delete(savedPath); } catch { } }
            if (started) finish(false, "Transfer failed", null);
        }
    }

    static string Preview(string t)
    {
        t = t.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 60 ? t[..60] + "..." : t;
    }

    static string UniquePath(string name)
    {
        name = Path.GetFileName(name);
        foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
        if (string.IsNullOrWhiteSpace(name)) name = "file";
        var p = Path.Combine(Config.SaveDir, name);
        if (!File.Exists(p)) return p;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            p = Path.Combine(Config.SaveDir, $"{stem} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
    }
}

// ---------------------------------------------------------------- overlay card

sealed class Overlay : Form
{
    static Overlay? cur;

    string title = "", sub = "";
    double frac;
    bool done, failed, closing;
    string? openPath;

    readonly System.Windows.Forms.Timer anim = new() { Interval = 8 };
    readonly System.Windows.Forms.Timer hold = new() { Interval = 2600 };
    readonly System.Diagnostics.Stopwatch sw = new();
    bool slidingIn = true;
    int startY, endY;

    const int W = 380, H = 92;

    // ---- static entry points (UI thread)

    public static void Begin(string t, string s)
    {
        if (cur == null || cur.IsDisposed || cur.closing)
        {
            cur = new Overlay();
            cur.Show();
            cur.SlideIn();
        }
        cur.hold.Stop();
        cur.title = t; cur.sub = s; cur.frac = 0; cur.done = false; cur.failed = false; cur.openPath = null;
        cur.Invalidate();
    }

    public static void Progress(double f)
    {
        if (cur == null || cur.IsDisposed) return;
        cur.frac = f;
        cur.Invalidate();
    }

    public static void Finish(bool ok, string s, string? path)
    {
        if (cur == null || cur.IsDisposed) return;
        cur.done = ok; cur.failed = !ok; cur.frac = 1;
        cur.title = ok ? (cur.title == "Text copied" ? "Text copied" : "Received") : "Failed";
        cur.sub = s; cur.openPath = path;
        cur.Invalidate();
        cur.hold.Stop(); cur.hold.Start();
    }

    // ---- form

    Overlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        Size = new Size(W, H);
        BackColor = Color.FromArgb(28, 28, 33);
        DoubleBuffered = true;
        Opacity = 0;
        Cursor = Cursors.Hand;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        endY = wa.Top + 18;
        startY = wa.Top - H - 4;
        Location = new Point(wa.Left + (wa.Width - W) / 2, startY);

        using var gp = RoundedRect(new Rectangle(0, 0, W, H), 26);
        Region = new Region(gp);

        anim.Tick += (_, _) => Tick();
        hold.Tick += (_, _) => { hold.Stop(); SlideOut(); };
        Click += (_, _) =>
        {
            if (openPath != null)
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + openPath + "\"") { UseShellExecute = true });
            SlideOut();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000 | 0x00000080; // WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW
            return cp;
        }
    }

    void SlideIn() { slidingIn = true; sw.Restart(); anim.Start(); }

    void SlideOut()
    {
        if (closing) return;
        closing = true; slidingIn = false; sw.Restart(); anim.Start();
    }

    void Tick()
    {
        double t = sw.Elapsed.TotalSeconds;
        if (slidingIn)
        {
            // damped spring: overshoots slightly then settles (the "swift" bounce)
            double s = 1 - Math.Exp(-7.0 * t) * Math.Cos(12.0 * t);
            Top = (int)(startY + (endY - startY) * s);
            Opacity = Math.Min(1.0, t / 0.10);
            if (t > 0.9) { Top = endY; Opacity = 1; anim.Stop(); }
        }
        else
        {
            double u = Math.Min(1.0, t / 0.28);
            double s = u * u;
            Top = (int)(endY + (startY - endY) * s);
            Opacity = 1 - u;
            if (u >= 1) { anim.Stop(); hold.Stop(); Close(); }
        }
    }

    // ---- painting

    static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        int d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(BackColor);

        var circle = new Rectangle(20, 20, 52, 52);
        var accent = failed ? Color.FromArgb(235, 87, 87) : done ? Color.FromArgb(52, 199, 120) : Color.FromArgb(94, 160, 255);

        if (done || failed)
        {
            using var br = new SolidBrush(accent);
            g.FillEllipse(br, circle);
            using var pen = new Pen(Color.White, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            if (done)
                g.DrawLines(pen, new[] { new Point(33, 47), new Point(43, 57), new Point(60, 36) });
            else
            {
                g.DrawLine(pen, 36, 36, 56, 56);
                g.DrawLine(pen, 56, 36, 36, 56);
            }
        }
        else
        {
            using var back = new Pen(Color.FromArgb(60, 60, 70), 5f);
            g.DrawEllipse(back, circle);
            using var arc = new Pen(accent, 5f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            float sweep = (float)Math.Max(0.04, frac) * 360f;
            g.DrawArc(arc, circle, -90, sweep);
            using var pen = new Pen(Color.White, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(pen, 46, 33, 46, 58);
            g.DrawLine(pen, 37, 49, 46, 58);
            g.DrawLine(pen, 55, 49, 46, 58);
        }

        using var f1 = new Font("Segoe UI Semibold", 12.5f);
        using var f2 = new Font("Segoe UI", 10f);
        TextRenderer.DrawText(g, title, f1, new Rectangle(88, 16, W - 104, 30), Color.White,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(g, sub, f2, new Rectangle(88, 46, W - 104, 28), Color.FromArgb(165, 165, 178),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { anim.Dispose(); hold.Dispose(); }
        base.Dispose(disposing);
    }
}
