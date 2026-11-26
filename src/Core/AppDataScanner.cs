using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Danilovl.WinCleaner
{
    enum FolderKind { System, Driver, Running, Installed, Orphan }

    class AppFolder
    {
        public string Path;
        public string Name;
        public string Where;
        public long Bytes;
        public long Files;
        public DateTime Newest;
        public FolderKind Kind;
        public string Match;     // с чем совпало / какой процесс
        public bool Checked;

        public string KindText
        {
            get
            {
                switch (Kind)
                {
                    case FolderKind.System: return L.T("Системная — не трогать");
                    case FolderKind.Driver: return L.T("Драйверы/оборудование — лучше не трогать");
                    case FolderKind.Running: return L.T("Используется сейчас: ") + Match;
                    case FolderKind.Installed: return L.T("Установлено: ") + Match;
                    default: return L.T("Не найдена среди установленных — возможно, остаток");
                }
            }
        }
    }

    /// <summary>Размеры папок программ в AppData / ProgramData и поиск остатков удалённых программ.</summary>
    static class AppDataScanner
    {
        static readonly string[] SystemNames =
        {
            "Microsoft", "Packages", "Temp", "Programs", "Comms", "ConnectedDevicesPlatform", "D3DSCache",
            "PlaceholderTileLogoFolder", "Publishers", "VirtualStore", "CrashDumps", "Application Data", "History",
            "Temporary Internet Files", "Microsoft_Corporation", "MicrosoftEdge", "PeerDistRepub", "Package Cache",
            "ssh", "USOPrivate", "USOShared", "WindowsHolographicDevices", "Windows App Certification Kit", "Desktop",
            "Documents", "Start Menu", "Templates", "Favorites", "Microsoft OneDrive", "regid.1991-06.com.microsoft",
            "SoftwareDistribution", "Windows Defender", "Windows Defender Advanced Threat Protection", "Microsoft Help",
            "Comms", "Microsoft SDKs", "Windows Kits", "WindowsApps", "SquirrelMachineInstalls", "Mozilla-1de4eec8-1241-4177-a864-e594e8d1fb38"
        };

        static readonly string[] DriverVendors =
        {
            "nvidia", "nvidiacorporation", "intel", "amd", "ati", "realtek", "realtekhd", "dell", "hp", "hewlettpackard",
            "lenovo", "asus", "msi", "acer", "synaptics", "elantech", "conexant", "dts", "waves", "dolby", "qualcomm",
            "broadcom", "mediatek", "killer", "rivet", "nahimic"
        };

        static string Norm(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) sb.Append(c);
            return sb.ToString();
        }

        class Known { public string Norm; public string Display; }

        static List<Known> InstalledIndex()
        {
            var list = new List<Known>();
            Action<string, string> add = (text, display) =>
            {
                string n = Norm(text);
                if (n.Length >= 3) list.Add(new Known { Norm = n, Display = display });
            };

            string[] keys = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (var hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
            {
                foreach (string key in keys)
                {
                    try
                    {
                        using (var k = hive.OpenSubKey(key))
                        {
                            if (k == null) continue;
                            foreach (string sub in k.GetSubKeyNames())
                            {
                                using (var p = k.OpenSubKey(sub))
                                {
                                    if (p == null) continue;
                                    string name = p.GetValue("DisplayName") as string;
                                    if (string.IsNullOrEmpty(name)) continue;
                                    add(name, name);
                                    add(p.GetValue("Publisher") as string, name);
                                    string loc = p.GetValue("InstallLocation") as string;
                                    if (!string.IsNullOrEmpty(loc))
                                    {
                                        var parts = loc.TrimEnd('\\', '"').TrimStart('"').Split('\\');
                                        foreach (string part in parts.Skip(1)) add(part, name);
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }

            // папки установленных программ и ярлыки меню «Пуск»
            var dirs = new[]
            {
                Environment.GetEnvironmentVariable("ProgramFiles"), Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"),
                Environment.GetFolderPath(Environment.SpecialFolder.Programs),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms)
            };
            foreach (string d in dirs)
            {
                if (string.IsNullOrEmpty(d) || !Directory.Exists(d)) continue;
                try
                {
                    foreach (string sub in Directory.GetDirectories(d)) add(Path.GetFileName(sub), Path.GetFileName(sub));
                    foreach (string lnk in Directory.GetFiles(d, "*.lnk")) add(Path.GetFileNameWithoutExtension(lnk), Path.GetFileNameWithoutExtension(lnk));
                }
                catch { }
            }

            // приложения Store
            try
            {
                string pk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
                foreach (string sub in Directory.GetDirectories(pk))
                {
                    string n = Path.GetFileName(sub);
                    int us = n.IndexOf('_'); if (us > 0) n = n.Substring(0, us);
                    foreach (string part in n.Split('.')) add(part, n);
                }
            }
            catch { }
            return list;
        }

        static string FindMatch(string folder, List<Known> known)
        {
            string n = Norm(folder);
            if (n.Length < 3) return null;
            foreach (var k in known)
                if (k.Norm == n) return k.Display;
            foreach (var k in known)
                if ((n.Length >= 4 && k.Norm.Contains(n)) || (k.Norm.Length >= 4 && n.Contains(k.Norm))) return k.Display;
            return null;
        }

        static List<KeyValuePair<string, string>> RunningExePaths()
        {
            var result = new List<KeyValuePair<string, string>>();
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (!string.IsNullOrEmpty(path)) result.Add(new KeyValuePair<string, string>(path, p.ProcessName));
                }
                catch { }
                finally { p.Dispose(); }
            }
            return result;
        }

        static void Classify(AppFolder f, List<Known> known, List<KeyValuePair<string, string>> running)
        {
            if (SystemNames.Any(s => string.Equals(s, f.Name, StringComparison.OrdinalIgnoreCase)) ||
                f.Name.StartsWith("regid.", StringComparison.OrdinalIgnoreCase) ||
                f.Name.StartsWith("{"))
            { f.Kind = FolderKind.System; return; }

            string prefix = f.Path.TrimEnd('\\') + "\\";
            foreach (var r in running)
                if (r.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) { f.Kind = FolderKind.Running; f.Match = r.Value; return; }

            string n = Norm(f.Name);
            if (DriverVendors.Contains(n)) { f.Kind = FolderKind.Driver; return; }

            string m = FindMatch(f.Name, known);
            if (m != null) { f.Kind = FolderKind.Installed; f.Match = m; return; }
            f.Kind = FolderKind.Orphan;
        }

        /// <summary>Заполняет result по мере подсчёта; при остановке в нём остаётся то, что успели посчитать.</summary>
        public static void Scan(CancellationToken ct, List<AppFolder> result, Action<string> status, Action<int> progress, Action<string> log)
        {
            status(L.T("Собираю список установленных программ…"));
            var known = InstalledIndex();
            var running = RunningExePaths();
            log(L.T("Известных программ/издателей: ") + known.Count + L.T(", запущенных процессов: ") + running.Count);

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var roots = new[]
            {
                new KeyValuePair<string, string>(local, "Local"),
                new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Roaming"),
                new KeyValuePair<string, string>(Path.Combine(Path.GetDirectoryName(local), "LocalLow"), "LocalLow"),
                new KeyValuePair<string, string>(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ProgramData")
            };

            var folders = new List<AppFolder>();
            foreach (var r in roots)
            {
                if (!Directory.Exists(r.Key)) continue;
                foreach (var e in FileWalker.ListDir(r.Key, "*"))
                {
                    if (!e.IsDir || e.IsLink) continue;
                    folders.Add(new AppFolder { Path = e.Path, Name = e.Name, Where = r.Value });
                }
            }

            // размеры считаются в несколько потоков; при остановке показываем то, что успели
            var sw = Stopwatch.StartNew();
            var stats = folders.Select(f => new KeyValuePair<string, WalkStats>(f.Path, new WalkStats())).ToList();
            progress(-1);
            try
            {
                FileWalker.MeasureMany(stats, ct, n => { if (n % 1000 == 0) status(L.T("Просмотрено папок: ") + Util.FormatCount(n)); });
            }
            finally
            {
                // при остановке показываем то, что успели посчитать
                for (int i = 0; i < folders.Count; i++)
                {
                    var f = folders[i];
                    var st = stats[i].Value;
                    f.Bytes = st.Bytes;
                    f.Files = st.Files;
                    try { f.Newest = st.NewestWrite > 0 ? FileWalker.ToLocalTime(st.NewestWrite) : Directory.GetLastWriteTime(f.Path); }
                    catch { f.Newest = DateTime.MinValue; }
                    Classify(f, known, running);
                    result.Add(f);
                }
            }
            log(string.Format(L.T("Папок: {0}, из них возможных остатков: {1} ({2}) — за {3:N1} с"), result.Count,
                result.Count(f => f.Kind == FolderKind.Orphan), Util.FormatSize(result.Where(f => f.Kind == FolderKind.Orphan).Sum(f => f.Bytes)),
                sw.Elapsed.TotalSeconds));
        }
    }
}
