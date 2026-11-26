using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Danilovl.WinCleaner
{
    /// <summary>Устаревшая запись реестра: она ссылается на файл или программу, которых больше нет.</summary>
    class RegIssue
    {
        public string Kind;         // группа («Автозагрузка», «Удалённые программы» …)
        public string Name;         // что это за запись
        public string Missing;      // чего не хватает (путь к несуществующему файлу)
        public string Root;         // "HKLM" или "HKCU"
        public string KeyPath;      // раздел
        public string ValueName;    // null — удаляется весь раздел KeyPath, иначе только этот параметр
        public bool Checked;

        public string FullKey { get { return (Root == "HKLM" ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER") + "\\" + KeyPath; } }
    }

    /// <summary>
    /// Поиск и удаление устаревших записей реестра. Намеренно осторожный: ищутся только записи,
    /// про которые можно ПРОВЕРИТЬ, что они указывают на несуществующий файл на постоянно подключённом диске.
    /// Перед удалением затронутые разделы сохраняются в .reg-файл — двойной щелчок по нему возвращает всё назад.
    /// </summary>
    static class RegistryCleaner
    {
        const string CV = @"SOFTWARE\Microsoft\Windows\CurrentVersion";
        const string CV32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion";

        public static string BackupFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "registry-backup"); }
        }

        public static RegistryKey RootKey(string root) { return root == "HKLM" ? Registry.LocalMachine : Registry.CurrentUser; }

        // ------------------------------------------------------------ проверка путей

        /// <summary>
        /// Путь к программе из командной строки («"C:\x\app.exe" /arg», «C:\x\app.exe /arg»).
        /// null — путь определить нельзя (rundll32, msiexec без пути и т.п.): такую запись не трогаем.
        /// </summary>
        public static string ExePath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;
            string c = Environment.ExpandEnvironmentVariables(command.Trim());
            string path;
            if (c.StartsWith("\""))
            {
                int end = c.IndexOf('"', 1);
                if (end < 0) return null;
                path = c.Substring(1, end - 1);
            }
            else
            {
                int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                path = exe >= 0 ? c.Substring(0, exe + 4) : c;
            }
            path = path.Trim();
            if (path.Length < 4 || path[1] != ':' || path[2] != '\\') return null;      // не полный путь вида C:\…
            if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            return path;
        }

        /// <summary>
        /// true — про этот путь можно уверенно сказать «файла нет»: диск постоянный и доступен.
        /// Для съёмных, сетевых и отключённых дисков отсутствие файла ничего не значит.
        /// </summary>
        static bool CanJudge(string path)
        {
            try
            {
                if (path.IndexOf(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) >= 0) return false;   // приложения Store: доступ ограничен
                var d = new DriveInfo(Path.GetPathRoot(path));
                return d.DriveType == DriveType.Fixed && d.IsReady;
            }
            catch { return false; }
        }

        /// <summary>Файла точно нет. wow32 — запись из 32-разрядной части реестра: для неё System32 означает SysWOW64.</summary>
        public static bool FileMissing(string path, bool wow32 = false)
        {
            if (path == null || !CanJudge(path)) return false;
            if (File.Exists(path)) return false;
            if (wow32)
            {
                string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                string sys = win + @"\System32\";
                if (path.StartsWith(sys, StringComparison.OrdinalIgnoreCase) && File.Exists(win + @"\SysWOW64\" + path.Substring(sys.Length))) return false;
            }
            return true;
        }

        /// <summary>
        /// Диска, на который указывает путь, сейчас нет вообще (буква не занята или диск не готов).
        /// Это либо отключённый внешний диск, либо диск, которого больше не существует, — программа в любом случае недоступна.
        /// </summary>
        public static bool DriveAbsent(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || path.Length < 3 || path[1] != ':') return false;
                var d = new DriveInfo(path.Substring(0, 1));
                return d.DriveType == DriveType.NoRootDirectory || !d.IsReady;
            }
            catch { return false; }
        }

        public static bool FolderMissing(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 4 || path[1] != ':' || !CanJudge(path)) return false;
            return !Directory.Exists(path);
        }

        // ------------------------------------------------------------ поиск

        public static List<RegIssue> Scan(CancellationToken ct, Action<string> status)
        {
            var result = new List<RegIssue>();
            Action<string> say = s => { if (status != null) status(s); };

            // 1. записи об установленных программах, которых на самом деле нет
            say(L.T("Проверяю список установленных программ…"));
            string kUninstall = L.T("Удалённые программы (остались в списке установленных)");
            foreach (var a in InstalledApps.ReadEntries().Where(a => a.Stale))
                result.Add(new RegIssue { Kind = kUninstall, Name = a.Name, Missing = a.StaleReason, Root = a.RegRoot, KeyPath = a.RegPath, Checked = false });
            ct.ThrowIfCancellationRequested();

            // 2. автозагрузка: программа запускается при входе, но её файла нет
            say(L.T("Проверяю автозагрузку…"));
            string kRun = L.T("Автозагрузка (программы нет)");
            foreach (string root in new[] { "HKCU", "HKLM" })
                foreach (string cv in new[] { CV, CV32 })
                    foreach (string run in new[] { "Run", "RunOnce" })
                        Values(root, cv + "\\" + run, (name, value) =>
                        {
                            string exe = ExePath(value as string);
                            if (FileMissing(exe, cv == CV32))
                                result.Add(new RegIssue { Kind = kRun, Name = name, Missing = exe, Root = root, KeyPath = cv + "\\" + run, ValueName = name, Checked = true });
                        });
            ct.ThrowIfCancellationRequested();

            // 3. App Paths: «короткие имена» программ для окна «Выполнить»
            say(L.T("Проверяю пути приложений…"));
            string kApp = L.T("Пути приложений (App Paths)");
            // (раздел общий для 32- и 64-разрядных программ, поэтому читается один раз)
            foreach (string root in new[] { "HKCU", "HKLM" })
                SubKeys(root, CV + @"\App Paths", (name, key) =>
                {
                    string exe = ExePath(key.GetValue(null) as string);
                    if (FileMissing(exe))
                        result.Add(new RegIssue { Kind = kApp, Name = name, Missing = exe, Root = root, KeyPath = CV + @"\App Paths\" + name, Checked = true });
                });
            ct.ThrowIfCancellationRequested();

            // 4. SharedDLLs: счётчики общих библиотек, которых уже нет
            say(L.T("Проверяю общие библиотеки…"));
            string kDll = L.T("Общие библиотеки (SharedDLLs)");
            foreach (string cv in new[] { CV, CV32 })
                Values("HKLM", cv + @"\SharedDLLs", (name, value) =>
                {
                    string file = ExePathPlain(name);
                    if (FileMissing(file, cv == CV32))
                        result.Add(new RegIssue { Kind = kDll, Name = Path.GetFileName(file), Missing = file, Root = "HKLM", KeyPath = cv + @"\SharedDLLs", ValueName = name, Checked = true });
                });
            ct.ThrowIfCancellationRequested();

            // 5. MuiCache: запомненные названия программ для меню «Открыть с помощью»
            say(L.T("Проверяю кэш названий программ…"));
            string kMui = L.T("Кэш названий программ (MuiCache)");
            const string mui = @"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\MuiCache";
            Values("HKCU", mui, (name, value) =>
            {
                int exe = name.IndexOf(".exe.", StringComparison.OrdinalIgnoreCase);
                if (exe < 0) return;
                string file = ExePathPlain(name.Substring(0, exe + 4));
                if (FileMissing(file))
                    result.Add(new RegIssue { Kind = kMui, Name = value as string ?? Path.GetFileName(file), Missing = file, Root = "HKCU", KeyPath = mui, ValueName = name, Checked = true });
            });
            return result;
        }

        /// <summary>Строка — это просто путь к файлу (без кавычек и аргументов).</summary>
        static string ExePathPlain(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = Environment.ExpandEnvironmentVariables(path.Trim());
            if (path.Length < 4 || path[1] != ':' || path[2] != '\\' || path.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            return path;
        }

        static void Values(string root, string path, Action<string, object> each)
        {
            try
            {
                using (var k = RootKey(root).OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (string name in k.GetValueNames())
                    {
                        if (string.IsNullOrEmpty(name)) continue;
                        try { each(name, k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)); } catch { }
                    }
                }
            }
            catch { }
        }

        static void SubKeys(string root, string path, Action<string, RegistryKey> each)
        {
            try
            {
                using (var k = RootKey(root).OpenSubKey(path))
                {
                    if (k == null) return;
                    foreach (string name in k.GetSubKeyNames())
                        try { using (var sub = k.OpenSubKey(name)) if (sub != null) each(name, sub); } catch { }
                }
            }
            catch { }
        }

        // ------------------------------------------------------------ исправление

        /// <summary>
        /// Сохраняет затронутые разделы в .reg-файлы и удаляет записи. Запись, для которой не удалось сделать копию, не удаляется.
        /// Возвращает число удалённых записей; backupDir — папка с копиями этого запуска.
        /// </summary>
        public static int Fix(IList<RegIssue> issues, Action<string> log, out string backupDir)
        {
            backupDir = Path.Combine(BackupFolder, DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss"));
            Directory.CreateDirectory(backupDir);
            var saved = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int n = 0, done = 0;
            foreach (var i in issues)
            {
                // один раздел (например, Run) сохраняем один раз, сколько бы параметров из него ни удалялось
                bool ok;
                if (!saved.TryGetValue(i.FullKey, out ok))
                {
                    string file = Path.Combine(backupDir, string.Format("{0:000}-{1}.reg", ++n, SafeName(i.KeyPath)));
                    List<string> lines;
                    ok = Util.RunCapture("reg.exe", "export \"" + i.FullKey + "\" \"" + file + "\" /y", Util.OemEncoding(), out lines) == 0 && File.Exists(file);
                    saved[i.FullKey] = ok;
                    if (!ok) log("  ! " + L.T("не удалось сохранить копию раздела, пропущено: ") + i.FullKey);
                }
                if (!ok) continue;
                try
                {
                    if (i.ValueName != null)
                    {
                        using (var k = RootKey(i.Root).OpenSubKey(i.KeyPath, true))
                            if (k != null) k.DeleteValue(i.ValueName, false);
                    }
                    else
                    {
                        int cut = i.KeyPath.LastIndexOf('\\');
                        using (var parent = RootKey(i.Root).OpenSubKey(i.KeyPath.Substring(0, cut), true))
                            if (parent != null) parent.DeleteSubKeyTree(i.KeyPath.Substring(cut + 1), false);
                    }
                    done++;
                    log("  - " + i.Kind + ": " + i.Name);
                }
                catch (Exception ex) { log("  ! " + i.Name + ": " + ex.Message); }
            }
            return done;
        }

        static string SafeName(string keyPath)
        {
            string name = keyPath.Substring(keyPath.LastIndexOf('\\') + 1);
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 60 ? name.Substring(0, 60) : name;
        }

        /// <summary>Открывает редактор реестра на нужном разделе.</summary>
        public static void OpenInRegedit(string fullKey)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit"))
                    if (k != null) k.SetValue("LastKey", "Computer\\" + fullKey);
                Process.Start("regedit.exe", "/m");
            }
            catch { }
        }
    }
}
