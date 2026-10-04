// The helper that reads and writes the HUD color files, built into Elite HUD Studio.exe.
// It is the same small web server as app\server.ps1 (used by the browser version), written in C# so the app
// never has to start PowerShell. Only this PC can reach it (http://localhost:<port>) and every request from
// the page must carry the random token made at start-up.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using Microsoft.Win32;

namespace EliteHudStudio
{
    using Obj = System.Collections.Generic.Dictionary<string, object>;

    static class Helper
    {
        const string AppVersion = "1.0";
        static readonly string[] ColorFiles = { "Startup-Profile", "Advanced", "SuitHud", "XML-Profile" };
        static readonly UTF8Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        static string Root, AppDir, PageFile, BackupDir, MyThemesDir, SettingsFile, Token, forcedGameDir;
        static bool testMode, sessionBackup, standalone;
        static volatile bool running;
        static int port;
        static HttpListener listener;
        static List<Obj> games = new List<Obj>();
        static Obj game;
        internal static readonly ManualResetEvent Stopped = new ManualResetEvent(false);

        // Starts the helper on the first free port from firstPort. Returns the port, or 0 if none was free.
        internal static int Start(string root, string gameDir, int firstPort, bool test, bool alone)
        {
            Root = root.TrimEnd('\\');
            AppDir = Path.Combine(Root, "app");
            PageFile = Path.Combine(Root, "HUD Studio.html");
            BackupDir = Path.Combine(Root, "Backups");
            MyThemesDir = Path.Combine(Root, "My Themes");
            SettingsFile = Path.Combine(Root, "settings.json");
            forcedGameDir = gameDir; testMode = test; standalone = alone;
            byte[] tb = new byte[16];
            using (var rng = System.Security.Cryptography.RandomNumberGenerator.Create()) rng.GetBytes(tb);
            StringBuilder t = new StringBuilder();
            foreach (byte b in tb) t.Append(b.ToString("x2"));
            Token = t.ToString();
            SelectGame();
            for (int p = firstPort; p < firstPort + 10; p++)
            {
                try
                {
                    HttpListener l = new HttpListener();
                    l.Prefixes.Add("http://localhost:" + p + "/");
                    l.Start();
                    listener = l; port = p;
                    break;
                }
                catch { }
            }
            if (listener == null) return 0;
            running = true;
            Thread th = new Thread(Loop);
            th.IsBackground = true; th.Name = "HUD Studio helper";
            th.Start();
            Log("Helper started on port " + port + (game != null ? " - game folder: " + game["path"] : " - Elite Dangerous was not found automatically"));
            return port;
        }

        internal static string PageToken { get { return Token; } }

        internal static void Stop()
        {
            running = false;
            try { if (listener != null) { listener.Stop(); listener.Close(); } } catch { }
            Log("Helper stopped.");
            Stopped.Set();
        }

        static void Loop()
        {
            while (running)
            {
                HttpListenerContext ctx;
                try { ctx = listener.GetContext(); } catch { break; }
                try { Route(ctx); }
                catch (Exception ex) { Log("Problem: " + ex.Message); try { ctx.Response.Abort(); } catch { } }
            }
        }

        static void Log(string msg)
        {
            try { File.AppendAllText(Program.HelperLog, "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + msg + Environment.NewLine, Encoding.UTF8); } catch { }
        }

        // ------------------------------------------------------------ small helpers
        static JavaScriptSerializer Json() { JavaScriptSerializer j = new JavaScriptSerializer(); j.MaxJsonLength = int.MaxValue; j.RecursionLimit = 64; return j; }
        static string S(object o)
        {
            if (o == null) return "";
            IFormattable f = o as IFormattable;
            return f != null ? f.ToString(null, Inv) : o.ToString();
        }
        static object Get(object o, string key)
        {
            Obj d = o as Obj; object v;
            return d != null && d.TryGetValue(key, out v) ? v : null;
        }
        static bool Truthy(object o)
        {
            if (o == null) return false;
            if (o is bool) return (bool)o;
            string s = o as string;
            if (s != null) return s.Length > 0;
            return true;
        }
        static string[] StrArray(object list)
        {
            List<string> r = new List<string>();
            if (list is string) r.Add((string)list);
            else { IEnumerable e = list as IEnumerable; if (e != null) foreach (object x in e) if (x != null) r.Add(S(x)); }
            return r.ToArray();
        }
        static Obj ParseObj(string json) { try { return Json().DeserializeObject(json) as Obj; } catch { return null; } }
        // Windows' old 260-character path limit: the \\?\ form lets .NET use longer paths (deep backup folders).
        static string LP(string p) { return p != null && p.Length >= 240 && Regex.IsMatch(p, "^[A-Za-z]:\\\\") ? "\\\\?\\" + p : p; }
        static bool SafeName(string n) { return !string.IsNullOrEmpty(n) && n.Length <= 200 && !Regex.IsMatch(n, "[\\\\/:*?\"<>|]") && !n.StartsWith("."); }

        // ------------------------------------------------------------ settings
        static Obj GetSettings()
        {
            if (File.Exists(SettingsFile)) { Obj o = ParseObj(File.ReadAllText(SettingsFile)); if (o != null) return o; }
            return new Obj();
        }
        static void SaveSettings(Obj h) { File.WriteAllText(SettingsFile, Json().Serialize(h), Utf8NoBom); }

        // ------------------------------------------------------------ finding the game
        static List<Obj> FindGameDirs()
        {
            List<string[]> roots = new List<string[]>();
            try
            {
                string steam = Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath", null) as string;
                if (!string.IsNullOrEmpty(steam))
                {
                    steam = steam.Replace('/', '\\');
                    List<string> libs = new List<string>(); libs.Add(steam);
                    string vdf = Path.Combine(steam, "steamapps\\libraryfolders.vdf");
                    if (File.Exists(vdf)) foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\"")) libs.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
                    List<string> seen = new List<string>();
                    foreach (string l in libs)
                    {
                        if (seen.Exists(delegate (string x) { return string.Equals(x, l, StringComparison.OrdinalIgnoreCase); })) continue;
                        seen.Add(l);
                        roots.Add(new[] { Path.Combine(l, "steamapps\\common\\Elite Dangerous"), "Steam" });
                    }
                }
            }
            catch { }
            try
            {
                string epic = "C:\\ProgramData\\Epic\\EpicGamesLauncher\\Data\\Manifests";
                if (Directory.Exists(epic)) foreach (string f in Directory.GetFiles(epic, "*.item"))
                {
                    Obj m = ParseObj(File.ReadAllText(f));
                    if (m != null && S(Get(m, "DisplayName")).IndexOf("Elite Dangerous", StringComparison.OrdinalIgnoreCase) >= 0) roots.Add(new[] { S(Get(m, "InstallLocation")), "Epic" });
                }
            }
            catch { }
            foreach (string v in new[] { Environment.GetEnvironmentVariable("ProgramFiles(x86)"), Environment.GetEnvironmentVariable("ProgramFiles") })
                if (!string.IsNullOrEmpty(v)) roots.Add(new[] { Path.Combine(v, "Frontier"), "Frontier" });
            roots.Add(new[] { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier_Developments"), "Frontier" });

            List<Obj> found = new List<Obj>();
            foreach (string[] r in roots)
            {
                try
                {
                    if (string.IsNullOrEmpty(r[0]) || !Directory.Exists(r[0])) continue;
                    foreach (string sub in new[] { "Products\\elite-dangerous-odyssey-64", "EDLaunch\\Products\\elite-dangerous-odyssey-64", "elite-dangerous-odyssey-64" })
                    {
                        string d = Path.Combine(r[0], sub);
                        if (!File.Exists(Path.Combine(d, "EliteDangerous64.exe"))) continue;
                        if (found.Exists(delegate (Obj x) { return string.Equals(S(x["path"]), d, StringComparison.OrdinalIgnoreCase); })) continue;
                        found.Add(GameEntry(d, r[1]));
                    }
                }
                catch { }
            }
            return found;
        }
        static Obj GameEntry(string path, string store)
        {
            Obj o = new Obj(); o["path"] = path; o["store"] = store; o["edhm"] = File.Exists(Path.Combine(path, "d3dx.ini")); return o;
        }

        static void SelectGame()
        {
            games = FindGameDirs();
            Obj s = GetSettings();
            Obj pick = null;
            string saved = S(Get(s, "gameDir"));
            if (!string.IsNullOrEmpty(forcedGameDir))
            {
                if (!Directory.Exists(forcedGameDir)) throw new Exception("Game folder not found: " + forcedGameDir);
                pick = GameEntry(Path.GetFullPath(forcedGameDir).TrimEnd('\\'), "Custom");
            }
            else if (saved.Length > 0 && File.Exists(Path.Combine(saved, "EliteDangerous64.exe")))
            {
                pick = games.Find(delegate (Obj x) { return S(x["path"]) == saved; });
                if (pick == null) pick = GameEntry(saved, "Custom");
            }
            else
            {
                pick = games.Find(delegate (Obj x) { return (bool)x["edhm"]; });
                if (pick == null && games.Count > 0) pick = games[0];
            }
            game = pick;
        }

        static bool GameRunning() { try { return Process.GetProcessesByName("EliteDangerous64").Length > 0; } catch { return false; } }

        static string GameVersion(string dir)
        {
            string f = Path.Combine(dir, "VersionInfo.txt");
            if (File.Exists(f)) { Obj o = ParseObj(File.ReadAllText(f)); object v = Get(o, "Version"); if (v != null) return S(v); }
            return null;
        }

        static string IniDir()
        {
            if (game == null) return null;
            string d = Path.Combine(S(game["path"]), "EDHM-ini");
            return Directory.Exists(d) ? d : null;
        }

        static Obj ModInfo(string dir)
        {
            Obj files = new Obj();
            Obj info = new Obj();
            info["installed"] = false; info["version"] = null; info["date"] = null; info["forGame"] = null; info["iniDir"] = null; info["linkTarget"] = null; info["files"] = files;
            string d3dx = Path.Combine(dir, "d3dx.ini");
            if (File.Exists(d3dx))
            {
                Match m = Regex.Match(File.ReadAllText(d3dx), "EDHM\\)[^\\r\\n]*?v(\\d+(?:\\.\\d+)+)\\s*\\(([^)]*)\\)(?:\\s*for FDev Update\\s*([\\d.]+))?");
                if (m.Success) { info["version"] = m.Groups[1].Value; info["date"] = m.Groups[2].Value; info["forGame"] = m.Groups[3].Value; }
            }
            string iniDir = Path.Combine(dir, "EDHM-ini");
            if (Directory.Exists(iniDir))
            {
                info["iniDir"] = iniDir;
                foreach (string n in ColorFiles) files[n] = File.Exists(Path.Combine(iniDir, n + ".ini"));
            }
            info["installed"] = File.Exists(d3dx) && File.Exists(Path.Combine(dir, "d3d11.dll")) && info["iniDir"] != null;
            return info;
        }

        // Elite's own built-in colour file. The HUD colours look right when it is left at default.
        static Obj XmlStatus()
        {
            string p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Frontier Developments\\Elite Dangerous\\Options\\Graphics\\GraphicsConfigurationOverride.xml");
            Obj r = new Obj();
            r["path"] = p; r["exists"] = File.Exists(p); r["isDefault"] = true; r["matrix"] = null;
            if ((bool)r["exists"])
            {
                try
                {
                    XmlDocument x = new XmlDocument();
                    x.LoadXml(File.ReadAllText(p));
                    XmlNode d = x.SelectSingleNode("/GraphicsConfig/GUIColour/Default");
                    if (d != null)
                    {
                        List<double[]> rows = new List<double[]>();
                        foreach (string name in new[] { "MatrixRed", "MatrixGreen", "MatrixBlue" })
                        {
                            XmlNode n = d.SelectSingleNode(name);
                            string[] parts = (n != null ? n.InnerText : "").Split(',');
                            double[] row = new double[parts.Length];
                            for (int i = 0; i < parts.Length; i++) row[i] = double.Parse(parts[i].Trim(), Inv);
                            rows.Add(row);
                        }
                        r["matrix"] = rows;
                        for (int i = 0; i < 3; i++) for (int k = 0; k < 3; k++) if (Math.Abs(rows[i][k] - (i == k ? 1 : 0)) > 0.001) r["isDefault"] = false;
                    }
                }
                catch { r["isDefault"] = null; }
            }
            return r;
        }

        // ------------------------------------------------------------ ini writing
        static bool ConstantsSpan(string text, out int start, out int end)
        {
            start = 0; end = 0;
            Match sec = Regex.Match(text, "(?im)^[ \\t]*\\[Constants\\][^\\n]*");
            if (!sec.Success) return false;
            start = sec.Index + sec.Length;
            Match next = Regex.Match(text.Substring(start), "(?m)^[ \\t]*\\[");
            end = next.Success ? start + next.Index : text.Length;
            return true;
        }

        // Changes only the numbers after "key =" inside [Constants]; every comment and line stays as it was.
        static string UpdateIniText(string text, Dictionary<string, string> values, out int count, List<string> missing)
        {
            int start, end;
            count = 0;
            if (!ConstantsSpan(text, out start, out end)) throw new Exception("The file has no [Constants] section.");
            string head = text.Substring(0, start), body = text.Substring(start, end - start), tail = text.Substring(end);
            foreach (KeyValuePair<string, string> kv in values)
            {
                Match m = Regex.Match(body, "(?m)^([ \\t]*" + kv.Key + "[ \\t]*=[ \\t]*)(-?[0-9]*\\.?[0-9]+(?:[eE][-+]?[0-9]+)?)");
                if (m.Success) { Group g = m.Groups[2]; body = body.Substring(0, g.Index) + kv.Value + body.Substring(g.Index + g.Length); count++; }
                else missing.Add(kv.Key);
            }
            return head + body + tail;
        }

        static void WriteTextFile(string path, string text)
        {
            bool bom = false;
            string tmp = LP(path + ".hudstudio-tmp");
            path = LP(path);
            if (File.Exists(path))
            {
                using (FileStream fs = File.OpenRead(path)) { byte[] b = new byte[3]; int n = fs.Read(b, 0, 3); bom = n == 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF; }
            }
            Encoding enc = bom ? new UTF8Encoding(true) : Utf8NoBom;
            File.WriteAllText(tmp, text, enc);
            if (!File.Exists(path)) { File.Move(tmp, path); return; }
            try { File.Replace(tmp, path, null); } catch { File.Copy(tmp, path, true); File.Delete(tmp); }
        }

        // Validates {"Advanced": {"x232": "0.3278"}, ...} coming from the page.
        static Dictionary<string, Dictionary<string, string>> ChangePlan(object changes)
        {
            Dictionary<string, Dictionary<string, string>> plan = new Dictionary<string, Dictionary<string, string>>();
            Obj c = changes as Obj;
            if (c == null) return plan;
            foreach (KeyValuePair<string, object> fp in c)
            {
                if (Array.IndexOf(ColorFiles, fp.Key) < 0) throw new Exception("Unknown settings file: " + fp.Key);
                Dictionary<string, string> kv = new Dictionary<string, string>();
                Obj vals = fp.Value as Obj;
                if (vals != null) foreach (KeyValuePair<string, object> p in vals)
                {
                    if (!Regex.IsMatch(p.Key, "^[xyzw]\\d{1,3}$")) throw new Exception("Bad setting name: " + p.Key);
                    string v = S(p.Value);
                    if (!Regex.IsMatch(v, "^-?\\d{1,4}(\\.\\d{1,6})?$")) throw new Exception("Bad value for " + p.Key + ": " + v);
                    kv[p.Key] = v;
                }
                if (kv.Count > 0) plan[fp.Key] = kv;
            }
            return plan;
        }

        // ------------------------------------------------------------ backups
        static void CopyColorFiles(string fromDir, string toDir)
        {
            Directory.CreateDirectory(LP(toDir));
            foreach (string n in ColorFiles) { string src = LP(Path.Combine(fromDir, n + ".ini")); if (File.Exists(src)) File.Copy(src, LP(Path.Combine(toDir, n + ".ini")), true); }
        }

        static string NewColorBackup(string reason)
        {
            string iniDir = IniDir();
            if (iniDir == null) return null;
            string name = Regex.Replace(DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + " " + reason, "[\\\\/:*?\"<>|]", "-");
            string dir = Path.Combine(BackupDir, name);
            CopyColorFiles(iniDir, dir);
            Obj info = new Obj();
            info["kind"] = "colors"; info["reason"] = reason; info["time"] = DateTime.Now.ToString("s"); info["edhm"] = ModInfo(S(game["path"]))["version"];
            // info.json is written last: a backup without it is incomplete and is never used for a restore
            File.WriteAllText(LP(Path.Combine(dir, "info.json")), Json().Serialize(info), Utf8NoBom);
            Log("Backup made: " + name);
            return name;
        }

        static List<Obj> BackupList()
        {
            List<Obj> items = new List<Obj>();
            if (!Directory.Exists(BackupDir)) return items;
            List<string> dirs = new List<string>(Directory.GetDirectories(BackupDir));
            dirs.Sort(delegate (string a, string b) { return string.Compare(Path.GetFileName(b), Path.GetFileName(a), StringComparison.OrdinalIgnoreCase); });
            foreach (string d in dirs)
            {
                Obj item = new Obj();
                item["name"] = Path.GetFileName(d); item["time"] = Directory.GetCreationTime(d).ToString("s"); item["reason"] = "Incomplete backup"; item["kind"] = "incomplete"; item["edhm"] = null; item["to"] = null;
                string ip = LP(Path.Combine(d, "info.json"));
                Obj info = File.Exists(ip) ? ParseObj(File.ReadAllText(ip)) : null;
                if (info != null)
                {
                    item["time"] = Get(info, "time"); item["reason"] = Get(info, "reason"); item["edhm"] = Get(info, "edhm"); item["kind"] = "colors";
                    if (Truthy(Get(info, "kind"))) item["kind"] = Get(info, "kind");
                    if (Truthy(Get(info, "to"))) item["to"] = Get(info, "to");
                }
                items.Add(item);
            }
            return items;
        }

        static Obj FolderIniTexts(string dir)
        {
            Obj files = new Obj();
            foreach (string n in ColorFiles) { string p = LP(Path.Combine(dir, n + ".ini")); if (File.Exists(p)) files[n] = File.ReadAllText(p); }
            return files;
        }

        // ------------------------------------------------------------ themes
        static Obj ThemeList()
        {
            List<Obj> mine = new List<Obj>();
            if (Directory.Exists(MyThemesDir))
            {
                List<FileInfo> files = new List<FileInfo>(new DirectoryInfo(MyThemesDir).GetFiles("*.json"));
                files.Sort(delegate (FileInfo a, FileInfo b) { return b.LastWriteTime.CompareTo(a.LastWriteTime); });
                foreach (FileInfo f in files)
                {
                    Obj t = ParseObj(File.ReadAllText(f.FullName));
                    if (t == null) continue;
                    Obj e = new Obj();
                    e["id"] = Path.GetFileNameWithoutExtension(f.Name); e["name"] = S(Get(t, "name")); e["author"] = S(Get(t, "author")); e["created"] = S(Get(t, "created")); e["colors"] = StrArray(Get(t, "colors"));
                    mine.Add(e);
                }
            }
            Obj data = new Obj();
            data["ok"] = true; data["dir"] = null; data["edhmui"] = new object[0]; data["mine"] = mine;
            return data;
        }

        static Obj SaveMyTheme(Obj body)
        {
            string name = S(Get(body, "name")).Trim();
            if (name.Length == 0 || name.Length > 80) throw new Exception("Please give the theme a name (up to 80 characters).");
            string safe = Regex.Replace(name, "[\\\\/:*?\"<>|]", "-").Trim().TrimEnd('.');
            if (!SafeName(safe)) throw new Exception("That name cannot be used as a file name.");
            Dictionary<string, Dictionary<string, string>> plan = ChangePlan(Get(body, "values"));
            if (plan.Count == 0) throw new Exception("The theme has no values.");
            Directory.CreateDirectory(MyThemesDir);
            Obj o = new Obj();
            o["app"] = "hud-studio"; o["version"] = 1; o["name"] = name; o["author"] = S(Get(body, "author")); o["created"] = DateTime.Now.ToString("s"); o["colors"] = StrArray(Get(body, "colors")); o["values"] = plan;
            File.WriteAllText(Path.Combine(MyThemesDir, safe + ".json"), Json().Serialize(o), Utf8NoBom);
            Log("Theme saved: " + name);
            Obj r = new Obj(); r["ok"] = true; r["id"] = safe; return r;
        }

        // ------------------------------------------------------------ API
        static Obj Status()
        {
            Obj st = new Obj();
            st["ok"] = true; st["app"] = "hud-studio"; st["appVersion"] = AppVersion; st["testMode"] = testMode;
            st["running"] = GameRunning(); st["game"] = null; st["games"] = games; st["edhm"] = null;
            st["xml"] = XmlStatus(); st["themesDir"] = null; st["backupsDir"] = BackupDir; st["root"] = Root;
            if (game != null)
            {
                string path = S(game["path"]);
                Obj g = new Obj(); g["path"] = path; g["store"] = game["store"]; g["version"] = GameVersion(path);
                st["game"] = g;
                // the player confirmed this folder once; remembered in settings.json so Studio doesn't ask again
                st["gameConfirmed"] = S(Get(GetSettings(), "gameConfirmed")) == path;
                st["edhm"] = ModInfo(path);
            }
            return st;
        }

        // Elite reloads the HUD colours when F11 is pressed in the game: bring Elite to the front and press it.
        static Obj Reload()
        {
            Obj r = new Obj(); r["attempted"] = false; r["sent"] = false; r["error"] = null;
            Process[] ps;
            try { ps = Process.GetProcessesByName("EliteDangerous64"); } catch { return r; }
            if (ps.Length == 0) return r;
            r["attempted"] = true;
            try
            {
                if (ps[0].MainWindowHandle == IntPtr.Zero) throw new Exception("Elite Dangerous is running but its main window handle is not available.");
                Microsoft.VisualBasic.Interaction.AppActivate(ps[0].Id);
                Thread.Sleep(200);
                System.Windows.Forms.SendKeys.SendWait("{F11}");
                r["sent"] = true;
                Log("Sent F11 to Elite Dangerous to reload the HUD colours.");
            }
            catch (Exception ex) { r["error"] = ex.Message; Log("Could not send F11 to Elite Dangerous: " + ex.Message); }
            return r;
        }

        static Obj SaveChanges(Obj body)
        {
            string iniDir = IniDir();
            if (iniDir == null) throw new Exception("HUD color files were not found in the selected game folder.");
            Dictionary<string, Dictionary<string, string>> plan = ChangePlan(Get(body, "changes"));
            Obj res = new Obj();
            if (plan.Count == 0) { res["ok"] = true; res["written"] = 0; res["missing"] = new string[0]; res["backup"] = null; return res; }
            string backup = null;
            if (Truthy(Get(body, "backup")) || !sessionBackup) { backup = NewColorBackup("Before save"); sessionBackup = true; }
            int written = 0;
            List<string> missing = new List<string>();
            foreach (KeyValuePair<string, Dictionary<string, string>> f in plan)
            {
                string path = Path.Combine(iniDir, f.Key + ".ini");
                if (!File.Exists(LP(path))) { foreach (string k in f.Value.Keys) missing.Add(f.Key + ":" + k); continue; }
                int count; List<string> miss = new List<string>();
                string text = UpdateIniText(File.ReadAllText(LP(path)), f.Value, out count, miss);
                if (count > 0) WriteTextFile(path, text);
                written += count;
                foreach (string k in miss) missing.Add(f.Key + ":" + k);
            }
            bool isRunning = GameRunning();
            Obj reload = new Obj(); reload["attempted"] = false; reload["sent"] = false; reload["error"] = null;
            if (written > 0 && isRunning) reload = Reload();
            Log("Saved " + written + " setting(s) to the HUD colour files" + ((bool)reload["sent"] ? " - F11 reload sent" : isRunning ? " - F11 reload could not be sent automatically" : ""));
            res["ok"] = true; res["written"] = written; res["missing"] = missing; res["backup"] = backup; res["running"] = isRunning; res["reload"] = reload;
            return res;
        }

        static Obj OpenFolder(string what)
        {
            string p = null;
            if (what == "tool") p = Root;
            else if (what == "backups") { p = BackupDir; Directory.CreateDirectory(p); }
            else if (what == "mythemes") { p = MyThemesDir; Directory.CreateDirectory(p); }
            else if (what == "game") { if (game != null) p = S(game["path"]); }
            else if (what == "edhm") p = IniDir();
            if (p == null || !Directory.Exists(p)) throw new Exception("That folder was not found.");
            Process.Start("explorer.exe", "\"" + p + "\"");
            return Ok();
        }

        static Obj Ok() { Obj o = new Obj(); o["ok"] = true; return o; }

        static Obj ReadBody(HttpListenerContext ctx)
        {
            string b;
            using (StreamReader r = new StreamReader(ctx.Request.InputStream, Encoding.UTF8)) b = r.ReadToEnd();
            if (string.IsNullOrWhiteSpace(b)) return new Obj();
            Obj o = Json().DeserializeObject(b) as Obj;
            return o ?? new Obj();
        }

        static object Api(HttpListenerContext ctx, string method, string path)
        {
            string route = method + " " + path;
            if (route == "GET /api/status") return Status();
            if (route == "GET /api/ini")
            {
                string iniDir = IniDir();
                Obj r = Ok(); r["files"] = iniDir == null ? new Obj() : FolderIniTexts(iniDir); return r;
            }
            if (route == "POST /api/save") return SaveChanges(ReadBody(ctx));
            if (route == "POST /api/reload-edhm") return Reload();
            if (route == "GET /api/backups") { Obj r = Ok(); r["dir"] = BackupDir; r["items"] = BackupList(); return r; }
            if (route == "POST /api/backup")
            {
                Obj b = ReadBody(ctx);
                string reason = "Manual backup";
                if (Truthy(Get(b, "reason"))) reason = Regex.Replace(S(Get(b, "reason")), "[^\\w \\-]", "");
                string n = NewColorBackup(reason);
                if (n == null) throw new Exception("No HUD color files were found, so there is nothing to back up.");
                Obj r = Ok(); r["name"] = n; return r;
            }
            if (route == "GET /api/backup")
            {
                string name = ctx.Request.QueryString["name"];
                if (!SafeName(name) || !Directory.Exists(LP(Path.Combine(BackupDir, name)))) throw new Exception("Backup not found.");
                Obj r = Ok(); r["name"] = name; r["files"] = FolderIniTexts(Path.Combine(BackupDir, name)); return r;
            }
            if (route == "GET /api/themes") return ThemeList();
            if (route == "GET /api/theme")
            {
                string src = ctx.Request.QueryString["src"], id = ctx.Request.QueryString["id"];
                if (!SafeName(id) || src != "mine") throw new Exception("Theme not found.");
                string p = Path.Combine(MyThemesDir, id + ".json");
                if (!File.Exists(p)) throw new Exception("Theme not found.");
                Obj t = ParseObj(File.ReadAllText(p));
                if (t == null) throw new Exception("That theme file could not be read.");
                Obj r = Ok(); r["id"] = id; r["name"] = S(Get(t, "name")); r["author"] = S(Get(t, "author")); r["values"] = Get(t, "values"); return r;
            }
            if (route == "POST /api/mythemes") return SaveMyTheme(ReadBody(ctx));
            if (route == "POST /api/mythemes/delete")
            {
                string id = S(Get(ReadBody(ctx), "id"));
                string p = Path.Combine(MyThemesDir, id + ".json");
                if (!SafeName(id) || !File.Exists(p)) throw new Exception("Theme not found.");
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(p, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                return Ok();
            }
            if (route == "POST /api/open") return OpenFolder(S(Get(ReadBody(ctx), "what")));
            if (route == "POST /api/select-game")
            {
                string p = S(Get(ReadBody(ctx), "path"));
                if (p.Length == 0 || !File.Exists(Path.Combine(p, "EliteDangerous64.exe"))) throw new Exception("That folder does not contain EliteDangerous64.exe.");
                Obj s = GetSettings(); s["gameDir"] = p; s["gameConfirmed"] = p; SaveSettings(s);
                SelectGame();
                return Status();
            }
            if (route == "POST /api/confirm-game")
            {
                if (game == null) throw new Exception("No game folder to confirm.");
                Obj s = GetSettings(); s["gameConfirmed"] = S(game["path"]); SaveSettings(s);
                return Status();
            }
            if (route == "POST /api/heartbeat") return Ok();
            if (route == "POST /api/shutdown") { if (standalone) running = false; return Ok(); }
            return null;
        }

        // ------------------------------------------------------------ HTTP plumbing
        static void SendBytes(HttpListenerContext ctx, int code, string type, byte[] bytes)
        {
            HttpListenerResponse res = ctx.Response;
            res.StatusCode = code;
            res.ContentType = type;
            res.Headers["Cache-Control"] = "no-store";
            res.Headers["X-Content-Type-Options"] = "nosniff";
            res.ContentLength64 = bytes.Length;
            res.OutputStream.Write(bytes, 0, bytes.Length);
            res.OutputStream.Close();
        }
        static void SendJson(HttpListenerContext ctx, object obj, int code) { SendBytes(ctx, code, "application/json; charset=utf-8", Utf8NoBom.GetBytes(Json().Serialize(obj))); }
        static void SendText(HttpListenerContext ctx, int code, string text) { SendBytes(ctx, code, "text/plain; charset=utf-8", Utf8NoBom.GetBytes(text)); }
        static Obj Err(string msg) { Obj o = new Obj(); o["ok"] = false; o["error"] = msg; return o; }

        static void SendStatic(HttpListenerContext ctx, string urlPath)
        {
            string rel = Uri.UnescapeDataString(urlPath.Substring(5)).Replace('/', '\\');
            if (rel.Contains("..") || rel.Contains(":") || rel.StartsWith("\\")) { SendText(ctx, 400, "Bad path"); return; }
            string full = Path.GetFullPath(Path.Combine(AppDir, rel));
            if (!full.StartsWith(AppDir + "\\", StringComparison.OrdinalIgnoreCase)) { SendText(ctx, 403, "Forbidden"); return; }
            string type = null;
            switch (Path.GetExtension(full).ToLowerInvariant())
            {
                case ".json": type = "application/json; charset=utf-8"; break;
                case ".js": type = "text/javascript; charset=utf-8"; break;
                case ".css": type = "text/css; charset=utf-8"; break;
                case ".png": type = "image/png"; break;
                case ".jpg": type = "image/jpeg"; break;
                case ".svg": type = "image/svg+xml"; break;
                case ".ico": type = "image/x-icon"; break;
            }
            if (type == null || !File.Exists(full)) { SendText(ctx, 404, "Not found"); return; }
            SendBytes(ctx, 200, type, File.ReadAllBytes(full));
        }

        static void Route(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            string host = req.Headers["Host"] ?? "";
            if (host != "localhost:" + port && host != "127.0.0.1:" + port) { SendText(ctx, 403, "Forbidden"); return; }
            string path = req.Url.AbsolutePath, method = req.HttpMethod;
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                if (!File.Exists(PageFile)) { SendText(ctx, 404, "HUD Studio.html is missing from the HUD Studio folder."); return; }
                SendBytes(ctx, 200, "text/html; charset=utf-8", Utf8NoBom.GetBytes(File.ReadAllText(PageFile).Replace("__HUD_TOKEN__", Token)));
                return;
            }
            if (method == "GET" && path.StartsWith("/app/")) { SendStatic(ctx, path); return; }
            if (path == "/api/ping") { Obj p = new Obj(); p["app"] = "hud-studio"; p["version"] = AppVersion; SendJson(ctx, p, 200); return; }
            if (path.StartsWith("/api/"))
            {
                if ((req.Headers["X-HUD-Token"] ?? "") != Token) { SendJson(ctx, Err("HUD Studio was restarted. Please reload this page."), 401); return; }
                if (method == "POST" && !(req.ContentType ?? "").StartsWith("application/json")) { SendJson(ctx, Err("Bad request."), 400); return; }
                try
                {
                    object result = Api(ctx, method, path);
                    if (result == null) SendJson(ctx, Err("Unknown request."), 404); else SendJson(ctx, result, 200);
                }
                catch (Exception ex)
                {
                    string msg = ex.Message;
                    if (msg.IndexOf("denied", StringComparison.OrdinalIgnoreCase) >= 0) msg += " Windows blocked HUD Studio from changing this file. If Elite is open, close it and try again. If it still fails, your antivirus may be protecting the folder, or the game folder may need administrator rights: right-click \"Elite HUD Studio.exe\" and choose \"Run as administrator\".";
                    else if (msg.IndexOf("being used by another process", StringComparison.OrdinalIgnoreCase) >= 0) msg += " Another program (usually Elite) has this file open. Close it and try again.";
                    Log("Problem: " + msg);
                    SendJson(ctx, Err(msg), 500);
                }
                if (!running) Stop();
                return;
            }
            SendText(ctx, 404, "Not found");
        }
    }
}
