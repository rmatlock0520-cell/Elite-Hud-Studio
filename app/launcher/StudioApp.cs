// Elite HUD Studio.exe - opens HUD Studio in its own window.
// Runs the built-in helper (Helper.cs) that reads and writes the HUD color files, and shows HUD Studio in a
// WebView2 window (the web engine built into Windows 10/11). It starts no other programs and does not need
// administrator rights. Built with the C# compiler that ships with Windows (see build.ps1).
// Options (for testing): --game-dir "<folder>"  --port <n>  --test-mode  --helper-only (no window)
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Elite HUD Studio")]
[assembly: AssemblyProduct("Elite HUD Studio")]
[assembly: AssemblyDescription("Elite Dangerous HUD color editor")]
[assembly: AssemblyVersion("1.6.0.0")]
[assembly: AssemblyFileVersion("1.6.0.0")]

namespace EliteHudStudio
{
    static class Program
    {
        internal static string Root, AppDir, DataDir, LogDir, BackupLogDir, LogFile, HelperLog;
        internal static string ArgGameDir;
        internal static int ArgPort = 47810;
        internal static bool ArgTest, ArgHelperOnly;

        [STAThread]
        static int Main(string[] args)
        {
            // let .NET use paths longer than 260 characters (deep backup folders)
            try { AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false); AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false); } catch { }
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--game-dir" && i + 1 < args.Length) ArgGameDir = args[++i];
                else if (args[i] == "--port" && i + 1 < args.Length) int.TryParse(args[++i], out ArgPort);
                else if (args[i] == "--test-mode") ArgTest = true;
                else if (args[i] == "--helper-only") ArgHelperOnly = true;
            }
            Root = AppDomain.CurrentDomain.BaseDirectory;
            AppDir = Path.Combine(Root, "app");
            DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Elite HUD Studio");
            try { Directory.CreateDirectory(DataDir); } catch { }
            // logs and crash reports go in a "Logs" folder next to the app (easy to find and send), or AppData if that isn't writable
            LogDir = Path.Combine(Root, "Logs");
            try { Directory.CreateDirectory(LogDir); File.AppendAllText(Path.Combine(LogDir, ".write-test"), ""); File.Delete(Path.Combine(LogDir, ".write-test")); }
            catch { LogDir = Path.Combine(DataDir, "Logs"); try { Directory.CreateDirectory(LogDir); } catch { } }
            // a second copy of everything in AppData: antivirus folder protection (e.g. Documents) can't block it there
            BackupLogDir = Path.Combine(DataDir, "Logs");
            try { Directory.CreateDirectory(BackupLogDir); } catch { }
            if (string.Equals(Path.GetFullPath(BackupLogDir).TrimEnd('\\'), Path.GetFullPath(LogDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) BackupLogDir = null;
            LogFile = Path.Combine(LogDir, "app-log.txt");
            HelperLog = Path.Combine(LogDir, "helper-log.txt");
            try { FileInfo fi = new FileInfo(LogFile); if (fi.Exists && fi.Length > 1024 * 1024) File.Copy(LogFile, LogFile + ".old", true); if (fi.Exists && fi.Length > 1024 * 1024) File.WriteAllText(LogFile, ""); } catch { }
            // anything unexpected: save a crash report instead of just vanishing
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Crash(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e) { Crash(e.ExceptionObject as Exception); };
            // Microsoft's WebView2 files live in app\webview2
            AppDomain.CurrentDomain.AssemblyResolve += delegate (object s, ResolveEventArgs e)
            {
                string p = Path.Combine(Path.Combine(AppDir, "webview2"), new AssemblyName(e.Name).Name + ".dll");
                return File.Exists(p) ? Assembly.LoadFrom(p) : null;
            };
            if (ArgHelperOnly)
            {
                // no window: just the helper, until something posts /api/shutdown (used for testing)
                try { int hp = Helper.Start(Root, ArgGameDir, ArgPort, ArgTest, true); if (hp == 0) return 1; Helper.Stopped.WaitOne(); return 0; }
                catch (Exception ex) { Log("Helper-only start failed: " + ex.Message); return 1; }
            }
            Status("STARTED", "the app is starting");
            bool first;
            using (Mutex m = new Mutex(true, "Local\\EliteHudStudioApp", out first))
            {
                if (!first) { Status("READY", "Studio was already open"); MessageBox.Show("Elite HUD Studio is already open.", "Elite HUD Studio"); return 0; }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                try { return Studio.Run(); }
                catch (Exception ex) { Crash(ex); return 1; }
            }
        }

        // Logs\launch-status.txt: one line the launcher checks for pass/fail (STARTED, READY, FAILED or CRASHED + details)
        internal static void Status(string state, string detail)
        {
            try
            {
                string clean = Regex.Replace(detail ?? "", "[&|<>^%!\"()\\r\\n]", " ");
                string line = state + " " + clean + Environment.NewLine;
                try { File.WriteAllText(Path.Combine(LogDir, "launch-status.txt"), line, Encoding.ASCII); } catch { }
                if (BackupLogDir != null) try { File.WriteAllText(Path.Combine(BackupLogDir, "launch-status.txt"), line, Encoding.ASCII); } catch { }
            }
            catch { }
        }

        internal static void Log(string msg)
        {
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine;
            try { File.AppendAllText(LogFile, line, Encoding.UTF8); } catch { }
            if (BackupLogDir != null) try { File.AppendAllText(Path.Combine(BackupLogDir, "app-log.txt"), line, Encoding.UTF8); } catch { }
        }

        internal static void Fail(string why)
        {
            Log("Could not start: " + why);
            string report = SaveReport("startup-problem", "Elite HUD Studio couldn't start:\r\n" + why);
            Status("FAILED", why + " - report: " + Path.GetFileName(report));
            MessageBox.Show("Elite HUD Studio couldn't start:\n\n" + why + "\n\nA report was saved here (you can send it for help):\n" + report +
                "\n\nYou can also use the browser version of HUD Studio (the download without an .exe).",
                "Elite HUD Studio", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            ShowInExplorer(report);
        }

        static bool crashed;
        internal static void Crash(Exception ex)
        {
            if (crashed) return; crashed = true;
            string text = "Elite HUD Studio crashed:\r\n" + (ex != null ? ex.ToString() : "(unknown error)");
            Log("CRASH: " + (ex != null ? ex.Message : "unknown"));
            string report = SaveReport("crash", text);
            Status("CRASHED", (ex != null ? ex.Message : "unknown error") + " - report: " + Path.GetFileName(report));
            try
            {
                MessageBox.Show("Sorry, Elite HUD Studio ran into a problem and has to close.\n\nA crash report was saved here (send it for help):\n" + report,
                    "Elite HUD Studio", MessageBoxButtons.OK, MessageBoxIcon.Error);
                ShowInExplorer(report);
            }
            catch { }
            Environment.Exit(1);
        }

        // a text report with what went wrong, this PC's details, and the recent logs
        internal static string SaveReport(string kind, string body)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("ELITE HUD STUDIO - " + kind.ToUpperInvariant().Replace('-', ' ') + " REPORT");
            sb.AppendLine("Time: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            sb.AppendLine("App version: " + Assembly.GetExecutingAssembly().GetName().Version);
            sb.AppendLine();
            sb.AppendLine(body);
            sb.AppendLine();
            sb.AppendLine(SystemInfo());
            sb.AppendLine("--- app log (last lines) ---"); sb.AppendLine(Tail(LogFile, 60));
            sb.AppendLine("--- helper log (last lines) ---"); sb.AppendLine(Tail(HelperLog, 60));
            string path = Path.Combine(LogDir, kind + "-" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
            try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); }
            catch { path = Path.Combine(BackupLogDir ?? Path.GetTempPath(), Path.GetFileName(path)); try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); } catch { } }
            // keep a copy in the AppData logs too, in case the app folder is blocked
            if (BackupLogDir != null && !path.StartsWith(BackupLogDir, StringComparison.OrdinalIgnoreCase))
                try { File.WriteAllText(Path.Combine(BackupLogDir, Path.GetFileName(path)), sb.ToString(), Encoding.UTF8); } catch { }
            return path;
        }

        internal static string SystemInfo()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("--- this PC ---");
            sb.AppendLine("Windows: " + Environment.OSVersion.VersionString + (Environment.Is64BitOperatingSystem ? " (64-bit)" : " (32-bit)"));
            sb.AppendLine(".NET: " + Environment.Version);
            string rt = "not found";
            try { rt = Studio.RuntimeVersion(); } catch { }
            sb.AppendLine("WebView2 runtime: " + rt);
            try { foreach (Screen s in Screen.AllScreens) sb.AppendLine("Screen: " + s.Bounds.Width + "x" + s.Bounds.Height + (s.Primary ? " (main)" : "")); } catch { }
            sb.AppendLine("App folder: " + Root);
            try { foreach (string f in new[] { "HUD Studio.html", "app\\catalog.json", "app\\defaults.json", "app\\webview2\\WebView2Loader.dll", "app\\webview2\\Microsoft.Web.WebView2.Core.dll", "app\\webview2\\Microsoft.Web.WebView2.WinForms.dll" }) sb.AppendLine("  " + f + ": " + (File.Exists(Path.Combine(Root, f)) ? "ok" : "MISSING")); } catch { }
            return sb.ToString();
        }

        internal static string Tail(string path, int lines)
        {
            try
            {
                if (!File.Exists(path)) return "(none)";
                string[] all;
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader sr = new StreamReader(fs, Encoding.UTF8)) all = sr.ReadToEnd().Split('\n');
                int start = Math.Max(0, all.Length - lines);
                return string.Join("\n", all, start, all.Length - start).TrimEnd();
            }
            catch (Exception ex) { return "(could not read: " + ex.Message + ")"; }
        }

        internal static void ShowInExplorer(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }
    }

    // Kept apart from Program so the WebView2 files are only loaded after Program has said where they are.
    static class Studio
    {

        internal static string RuntimeVersion()
        {
            return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        }

        internal static int Run()
        {
            string wv2 = Path.Combine(Program.AppDir, "webview2");
            foreach (string f in new[] { "Microsoft.Web.WebView2.Core.dll", "Microsoft.Web.WebView2.WinForms.dll", "WebView2Loader.dll" })
                if (!File.Exists(Path.Combine(wv2, f))) { Program.Fail(f + " is missing from the app\\webview2 folder. Please unzip the whole package again."); return 1; }
            if (!File.Exists(Path.Combine(Program.Root, "HUD Studio.html"))) { Program.Fail("HUD Studio.html is missing. Please unzip the whole package again."); return 1; }

            Microsoft.Web.WebView2.Core.CoreWebView2Environment.SetLoaderDllFolderPath(wv2);
            string rt = null;
            try { rt = RuntimeVersion(); } catch { }
            if (string.IsNullOrEmpty(rt))
            {
                Program.Fail("This PC is missing the Microsoft Edge WebView2 Runtime (built into Windows 11 and most Windows 10 PCs). It's a free download from Microsoft: https://go.microsoft.com/fwlink/p/?LinkId=2124703");
                return 1;
            }
            Program.Log("Start. WebView2 runtime " + rt);

            // the helper that reads and writes the HUD color files runs inside this app (Helper.cs)
            try { File.AppendAllText(Program.HelperLog, Environment.NewLine + "=== helper started " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===" + Environment.NewLine); } catch { }
            int port;
            try { port = Helper.Start(Program.Root, Program.ArgGameDir, Program.ArgPort, Program.ArgTest, false); }
            catch (Exception ex) { Program.Fail("The helper that reads and writes your HUD color files didn't start: " + ex.Message); return 1; }
            if (port == 0) { Program.Fail("The helper that reads and writes your HUD color files couldn't open a local port (47810-47819 are all in use). Close other copies of HUD Studio and try again."); return 1; }
            string url = "http://localhost:" + port + "/";
            Program.Log("Helper on port " + port + " (built in)");

            using (StudioForm form = new StudioForm(url)) Application.Run(form);

            Helper.Stop();
            Program.Log("Exit");
            return 0;
        }
    }
    class StudioForm : Form
    {
        readonly string url;
        readonly Microsoft.Web.WebView2.WinForms.WebView2 web;
        int dirty;
        bool reported;

        public StudioForm(string url)
        {
            this.url = url;
            Text = "Elite HUD Studio";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            BackColor = Color.FromArgb(11, 15, 23);
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            Size = new Size((int)(wa.Width * 0.88), (int)(wa.Height * 0.88));
            MinimumSize = new Size(Math.Min(1000, (int)(wa.Width * 0.6)), Math.Min(650, (int)(wa.Height * 0.6)));
            StartPosition = FormStartPosition.CenterScreen;
            WindowState = FormWindowState.Maximized;

            web = new Microsoft.Web.WebView2.WinForms.WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.FromArgb(11, 15, 23);
            Microsoft.Web.WebView2.WinForms.CoreWebView2CreationProperties cp = new Microsoft.Web.WebView2.WinForms.CoreWebView2CreationProperties();
            cp.UserDataFolder = Path.Combine(Program.DataDir, "WebView2");
            web.CreationProperties = cp;
            web.CoreWebView2InitializationCompleted += OnReady;
            Controls.Add(web);

            Shown += delegate { web.Source = new Uri(url); };
            Resize += delegate { FitZoom(); };
            FormClosing += delegate (object s, FormClosingEventArgs e)
            {
                if (dirty > 0 && MessageBox.Show(this, "You have unsaved changes that aren't in the game yet.\n\nClose HUD Studio anyway?", "Elite HUD Studio",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) e.Cancel = true;
            };
        }

        void OnReady(object sender, Microsoft.Web.WebView2.Core.CoreWebView2InitializationCompletedEventArgs e)
        {
            if (!e.IsSuccess) { Program.Fail("The window's web engine didn't start: " + (e.InitializationException != null ? e.InitializationException.Message : "unknown")); Close(); return; }
            var cw = web.CoreWebView2;
            cw.Settings.AreDevToolsEnabled = false;
            cw.Settings.IsStatusBarEnabled = false;
            // links to websites open in your normal browser
            cw.NewWindowRequested += delegate (object s, Microsoft.Web.WebView2.Core.CoreWebView2NewWindowRequestedEventArgs a) { a.Handled = true; OpenExternal(a.Uri); };
            cw.NavigationStarting += delegate (object s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationStartingEventArgs a)
            {
                if (!a.Uri.StartsWith(url, StringComparison.OrdinalIgnoreCase) && !a.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) { a.Cancel = true; OpenExternal(a.Uri); }
            };
            cw.WebMessageReceived += OnMessage;
            // tell the launcher Studio is really up (window open and the page loaded)
            cw.NavigationCompleted += delegate (object s, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs a)
            {
                if (reported) return; reported = true;
                if (a.IsSuccess) { Program.Status("READY", "window open, page loaded from " + url); Program.Log("Ready"); }
                else Program.Status("FAILED", "the Studio page did not load - " + a.WebErrorStatus);
            };
            FitZoom();
        }

        static void OpenExternal(string uri)
        {
            if (uri != null && (uri.StartsWith("https://") || uri.StartsWith("http://"))) { try { Process.Start(uri); } catch { } }
        }

        void OnMessage(object sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs a)
        {
            string msg = null;
            try { msg = a.TryGetWebMessageAsString(); } catch { return; }
            if (msg == null) return;
            if (msg.StartsWith("dirty:")) { int.TryParse(msg.Substring(6), out dirty); }
            else if (msg.StartsWith("jserror:")) { Program.Log("Page error: " + msg.Substring(8)); }
            else if (msg.StartsWith("save-report:"))
            {
                // the page's diagnostic report + this PC's details + logs, saved in the Logs folder
                string path = Program.SaveReport("diagnostic", msg.Substring(12));
                web.CoreWebView2.PostWebMessageAsJson("{\"type\":\"report-saved\",\"path\":\"" + path.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"}");
                Program.ShowInExplorer(path);
            }
            else if (msg == "pick-game-folder")
            {
                string p = PickGameFolder();
                string json = "{\"type\":\"game-folder\",\"path\":" + (p == null ? "null" : "\"" + p.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"") + "}";
                web.CoreWebView2.PostWebMessageAsJson(json);
            }
        }

        string PickGameFolder()
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Choose your Elite Dangerous folder (the one that contains \"Products\", or \"elite-dangerous-odyssey-64\" itself).";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) != DialogResult.OK) return null;
                foreach (string sub in new[] { "", "Products\\elite-dangerous-odyssey-64", "elite-dangerous-odyssey-64", "EDLaunch\\Products\\elite-dangerous-odyssey-64" })
                {
                    string c = sub.Length == 0 ? d.SelectedPath : Path.Combine(d.SelectedPath, sub);
                    if (File.Exists(Path.Combine(c, "EliteDangerous64.exe"))) return c;
                }
                return "?" + d.SelectedPath;   // not an Elite folder; the page explains
            }
        }

        // keep the side-by-side layout (preview + settings): zoom out a little when the window is narrow for its scaling
        void FitZoom()
        {
            try
            {
                if (web == null || web.CoreWebView2 == null) return;
                int dpi = DeviceDpi > 0 ? DeviceDpi : 96;
                double css = web.ClientSize.Width / (dpi / 96.0);
                double z = Math.Round(Math.Max(0.6, Math.Min(1.0, css / 1400.0)), 2);
                if (css > 0 && Math.Abs(web.ZoomFactor - z) > 0.01) { web.ZoomFactor = z; Program.Log("Zoom " + z + " (window " + web.ClientSize.Width + " px at " + dpi + " dpi)"); }
            }
            catch (Exception ex) { Program.Log("Zoom: " + ex.Message); }
        }
    }
}
