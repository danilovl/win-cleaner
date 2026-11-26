using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Danilovl.WinCleaner
{
    class InstalledApp
    {
        public string Name, Publisher, Version, Location, UninstallCommand;
        public long Bytes;              // 0 — размер неизвестен
        public bool SizeIsEstimate;     // размер — оценка установщика из реестра, а не реальный размер папки
        public DateTime Installed;

        // откуда запись и актуальна ли она
        public string RegRoot, RegPath; // "HKLM"/"HKCU" и раздел Uninstall\…
        public bool Stale;              // программы на самом деле нет, запись осталась
        public string StaleReason;      // чего именно не хватает
    }

    /// <summary>
    /// Установленные программы (раздел Uninstall реестра). Реестр — только заявление «программа установлена»:
    /// каждая запись проверяется по факту (есть ли деинсталлятор, папка, регистрация пакета MSI),
    /// а размер считается по реальной папке программы, когда она известна.
    /// </summary>
    static class InstalledApps
    {
        [DllImport("msi.dll", CharSet = CharSet.Unicode)]
        static extern int MsiQueryProductState(string product);

        static readonly Regex Guid = new Regex(@"\{[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}\}");

        /// <summary>Читает записи реестра и проверяет их актуальность (размеры папок не считает — это быстро).</summary>
        public static List<InstalledApp> ReadEntries()
        {
            var result = new List<InstalledApp>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string[] keys = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (string root in new[] { "HKLM", "HKCU" })
                foreach (string key in keys)
                {
                    try
                    {
                        using (var k = RegistryCleaner.RootKey(root).OpenSubKey(key))
                        {
                            if (k == null) continue;
                            foreach (string sub in k.GetSubKeyNames())
                                using (var p = k.OpenSubKey(sub))
                                {
                                    if (p == null) continue;
                                    string name = p.GetValue("DisplayName") as string;
                                    string cmd = p.GetValue("UninstallString") as string;
                                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(cmd)) continue;
                                    // системные компоненты и обновления других программ в списке не нужны
                                    if (Convert.ToInt32(p.GetValue("SystemComponent") ?? 0) == 1) continue;
                                    if (!string.IsNullOrEmpty(p.GetValue("ParentKeyName") as string)) continue;
                                    string version = p.GetValue("DisplayVersion") as string ?? "";
                                    if (!seen.Add(name + "|" + version)) continue;

                                    var app = new InstalledApp
                                    {
                                        Name = name.Trim(), Version = version, UninstallCommand = cmd.Trim(),
                                        Publisher = p.GetValue("Publisher") as string ?? "",
                                        Location = (p.GetValue("InstallLocation") as string ?? "").Trim().Trim('"').TrimEnd('\\'),
                                        RegRoot = root, RegPath = key + "\\" + sub
                                    };
                                    try { app.Bytes = Convert.ToInt64(p.GetValue("EstimatedSize") ?? 0) * 1024; } catch { }
                                    app.SizeIsEstimate = app.Bytes > 0;
                                    DateTime d;
                                    if (DateTime.TryParseExact(p.GetValue("InstallDate") as string ?? "", "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                                        app.Installed = d;
                                    Check(app, sub, Convert.ToInt32(p.GetValue("WindowsInstaller") ?? 0) == 1);
                                    result.Add(app);
                                }
                        }
                    }
                    catch { }
                }
            return result;
        }

        /// <summary>Проверяет, существует ли программа на самом деле.</summary>
        static void Check(InstalledApp app, string subKey, bool windowsInstaller)
        {
            string cmd = app.UninstallCommand;
            bool msi = windowsInstaller || cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase);
            if (msi)
            {
                // пакет MSI: спрашиваем у установщика Windows, зарегистрирован ли продукт
                var m = Guid.Match(cmd);
                string product = m.Success ? m.Value : (Guid.IsMatch(subKey) ? Guid.Match(subKey).Value : null);
                if (product == null) return;
                int state;
                try { state = MsiQueryProductState(product); } catch { return; }
                if (state == -1 || state == 2)      // INSTALLSTATE_UNKNOWN, INSTALLSTATE_ABSENT
                {
                    app.Stale = true;
                    app.StaleReason = L.T("пакет MSI не зарегистрирован в системе");
                }
                return;
            }

            string exe = RegistryCleaner.ExePath(cmd);

            // программа «установлена» на диск, которого сейчас нет: она недоступна, а размер из реестра ничего не занимает
            string probe = exe ?? (app.Location.Length > 2 ? app.Location : null);
            if (probe != null && RegistryCleaner.DriveAbsent(probe))
            {
                app.Stale = true;
                app.StaleReason = string.Format(L.T("диск {0} не подключён (если такого диска больше нет, запись можно убрать)"), probe.Substring(0, 2).ToUpperInvariant());
                return;
            }
            if (exe != null)
            {
                // деинсталлятор на месте — программа считается установленной, даже если папка в реестре записана неверно
                if (RegistryCleaner.FileMissing(exe))
                {
                    app.Stale = true;
                    app.StaleReason = L.T("нет деинсталлятора: ") + exe;
                }
                return;
            }
            if (RegistryCleaner.FolderMissing(app.Location))
            {
                app.Stale = true;
                app.StaleReason = L.T("нет папки программы: ") + app.Location;
            }
        }

        /// <summary>Полный список: записи реестра + реальные размеры папок установки.</summary>
        public static List<InstalledApp> List(CancellationToken ct, Action<string> status)
        {
            var result = ReadEntries();
            foreach (var a in result.Where(a => a.Stale)) a.Bytes = 0;      // размер несуществующей программы из реестра — просто мусор

            // где папка установки известна и существует — считаем реальный размер; оценка установщика бывает сильно неверной
            // одна и та же папка у нескольких программ — общая: её размер нельзя приписать ни одной из них
            var sharedByApps = new HashSet<string>(result.Where(a => a.Location.Length > 0).GroupBy(a => a.Location, StringComparer.OrdinalIgnoreCase)
                                                         .Where(g => g.Count() > 1).Select(g => g.Key), StringComparer.OrdinalIgnoreCase);
            var toMeasure = result.Where(a => !a.Stale && a.Location.Length > 3 && Directory.Exists(a.Location) && !sharedByApps.Contains(a.Location) &&
                                              !Targets.IsDangerous(a.Location, null) && !IsSharedFolder(a.Location)).ToList();
            if (toMeasure.Count > 0)
            {
                var stats = toMeasure.Select(a => new KeyValuePair<string, WalkStats>(a.Location, new WalkStats())).ToList();
                try
                {
                    FileWalker.MeasureMany(stats, ct, n => { if (status != null && n % 1000 == 0) status(L.T("Считаю размеры. Просмотрено папок: ") + Util.FormatCount(n)); });
                }
                finally
                {
                    for (int i = 0; i < toMeasure.Count; i++) { toMeasure[i].Bytes = stats[i].Value.Bytes; toMeasure[i].SizeIsEstimate = false; }
                }
            }
            return result;
        }

        /// <summary>
        /// Некоторые программы указывают как «папку установки» общую папку (Program Files, System32, корень профиля).
        /// Её размер — не размер программы, поэтому такие папки не измеряем.
        /// </summary>
        static bool IsSharedFolder(string path)
        {
            string p = path.TrimEnd('\\');
            foreach (var f in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.System, Environment.SpecialFolder.SystemX86,
                                      Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.CommonProgramFiles,
                                      Environment.SpecialFolder.CommonProgramFilesX86, Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop,
                                      Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
                                      Environment.SpecialFolder.CommonApplicationData })
            {
                string special = Environment.GetFolderPath(f).TrimEnd('\\');
                if (special.Length > 0 && string.Equals(p, special, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Запускает штатный деинсталлятор программы (его окно появится отдельно).</summary>
        public static void Uninstall(InstalledApp app)
        {
            string cmd = app.UninstallCommand;
            // MSI: в реестре часто записана команда «изменить» (/I) — для удаления нужен /X
            if (cmd.StartsWith("MsiExec", StringComparison.OrdinalIgnoreCase)) cmd = cmd.Replace("/I{", "/X{").Replace("/i{", "/X{");
            Process.Start(new ProcessStartInfo("cmd.exe", "/c \"" + cmd + "\"") { UseShellExecute = false, CreateNoWindow = true });
        }

        /// <summary>Устаревшая запись как «проблема реестра» — чтобы удалить её с резервной копией.</summary>
        public static RegIssue AsIssue(InstalledApp app)
        {
            return new RegIssue { Kind = L.T("Удалённые программы (остались в списке установленных)"), Name = app.Name, Missing = app.StaleReason, Root = app.RegRoot, KeyPath = app.RegPath };
        }
    }
}
