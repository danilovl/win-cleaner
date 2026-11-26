using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace Danilovl.WinCleaner
{
    /// <summary>Текущее состояние файла гибернации и файла подкачки + изменение их настроек.</summary>
    class SystemFilesInfo
    {
        public const string MemKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

        public long RamBytes;

        // гибернация
        public long HibSize;          // -1 — файла нет
        public bool HibOn, HibReduced, FastStartup;
        public string HibText;        // готовая строка «Сейчас: …»

        // подкачка
        public string PageText;       // готовое описание (несколько строк)
        public int PageMode;          // 0 — авто, 1 — фиксированный, 2 — без подкачки
        public string CurDrive;       // диск из настройки или системный
        public int CurInit, CurMax;   // МБ (0 — не задано)
        public int RecInit = 4096, RecMax = 8192;
        public string RecommendText;
        public List<string> Drives = new List<string>();

        static long SystemFileSize(string root, string name)
        {
            foreach (var e in FileWalker.ListDir(root, name))
                return e.Size;
            return -1;
        }

        static object RegValue(string key, string name)
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(key))
                    return k == null ? null : k.GetValue(name);
            }
            catch { return null; }
        }

        static int ParseInt(string s) { int v; return int.TryParse(s, out v) ? v : 0; }
        static int RoundUp(int v, int step) { return (v + step - 1) / step * step; }

        public static SystemFilesInfo Read()
        {
            var r = new SystemFilesInfo();
            r.RamBytes = Wmi.TotalRamBytes();
            string sysDrive = Environment.GetEnvironmentVariable("SYSTEMDRIVE") ?? "C:";
            string sysRoot = sysDrive + "\\";

            // ---- гибернация
            r.HibSize = SystemFileSize(sysRoot, "hiberfil.sys");
            object he = RegValue(@"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled");
            r.HibOn = he != null ? Convert.ToInt32(he) != 0 : r.HibSize > 0;
            object ht = RegValue(@"SYSTEM\CurrentControlSet\Control\Power", "HiberFileType");
            r.HibReduced = ht != null && Convert.ToInt32(ht) == 1;
            if (ht == null && r.HibSize > 0 && r.RamBytes > 0) r.HibReduced = r.HibSize < r.RamBytes * 0.3;   // запасной способ: по размеру
            object fs = RegValue(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled");
            r.FastStartup = fs != null && Convert.ToInt32(fs) != 0;

            r.HibText = string.Format(
                L.T("Сейчас: {0}{1}.   Быстрый запуск: {2}.   Оперативная память: {3}"),
                r.HibSize > 0 ? sysRoot + "hiberfil.sys = " + Util.FormatSize(r.HibSize) : L.T("файла нет"),
                r.HibOn ? (r.HibReduced ? L.T(" (сокращённый)") : L.T(" (полный)")) : L.T(", гибернация отключена"),
                r.FastStartup ? L.T("включён") : L.T("выключен"),
                Util.FormatSize(r.RamBytes));

            // ---- подкачка
            var sb = new StringBuilder(L.T("Сейчас: "));
            var fileParts = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string root = d.Name.TrimEnd('\\');
                    r.Drives.Add(root.ToUpperInvariant());
                    long sz = SystemFileSize(d.Name, "pagefile.sys");
                    if (sz > 0) fileParts.Add(root + "\\pagefile.sys = " + Util.FormatSize(sz));
                }
                catch { }
            }
            sb.Append(fileParts.Count > 0 ? string.Join(", ", fileParts) : L.T("файла подкачки нет"));

            string[] entries = (RegValue(MemKey, "PagingFiles") as string[] ?? new string[0])
                .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
            sb.AppendLine();
            sb.Append(L.T("Настройка: "));
            string curDrive = null;
            if (entries.Length == 0) { sb.Append(L.T("без файла подкачки")); r.PageMode = 2; }
            else if (entries.Any(x => x.StartsWith("?:"))) { sb.Append(L.T("автоматически на всех дисках")); r.PageMode = 0; }
            else
            {
                var descr = new List<string>();
                foreach (string e in entries)
                {
                    var parts = e.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string path = parts[0];
                    if (path.Length < 2) continue;      // мусор в реестре — пропускаем, а не падаем
                    int i = parts.Length > 1 ? ParseInt(parts[1]) : 0, m = parts.Length > 2 ? ParseInt(parts[2]) : 0;
                    descr.Add(i == 0 && m == 0 ? path + L.T(" — размер выбирает система") : string.Format(L.T("{0} — {1}–{2} МБ"), path, i, m));
                    if (curDrive == null) { curDrive = path.Substring(0, 2); r.CurInit = i; r.CurMax = m; }
                }
                sb.Append(descr.Count > 0 ? string.Join("; ", descr) : L.T("не распознана"));
                r.PageMode = r.CurInit == 0 && r.CurMax == 0 ? 0 : 1;
            }
            r.CurDrive = (curDrive ?? sysDrive).ToUpperInvariant();

            var usage = Wmi.PageFiles();
            int peak = usage.Count > 0 ? usage.Max(u => u.PeakMb) : 0;
            if (usage.Count > 0)
            {
                sb.AppendLine();
                sb.Append(string.Format(L.T("Использование: сейчас {0} МБ, максимум с момента включения ПК {1} МБ"),
                    usage.Sum(u => u.CurrentMb), peak));
            }
            r.PageText = sb.ToString();

            // рекомендация: небольшой исходный размер, который растёт только при необходимости
            r.RecInit = Math.Max(4096, RoundUp((int)(peak * 1.25), 1024));
            r.RecMax = Math.Min(32768, Math.Max(r.RecInit * 2, 8192));
            r.RecommendText = string.Format(
                L.T("Рекомендуется: исходный {0} МБ, максимальный {1} МБ. На диске будет занято {2}, файл вырастет только если реально не хватит памяти."),
                r.RecInit, r.RecMax, Util.FormatSize((long)r.RecInit << 20));
            return r;
        }

        /// <summary>Режим гибернации: 0 — полный, 1 — сокращённый, 2 — отключить. Возвращает код powercfg.</summary>
        public static int SetHibernate(int mode, Action<string> log)
        {
            string cmd1 = mode == 2 ? "/h off" : "/h on";
            string cmd2 = mode == 2 ? null : (mode == 1 ? "/h /type reduced" : "/h /type full");
            int code = Util.Run("powercfg.exe", cmd1, Util.OemEncoding(), log);
            if (code == 0 && cmd2 != null) code = Util.Run("powercfg.exe", cmd2, Util.OemEncoding(), log);
            return code;
        }

        public static string HibernateDescription(int mode)
        {
            if (mode == 2) return L.T("ОТКЛЮЧИТЬ гибернацию и быстрый запуск. Файл hiberfil.sys будет удалён сразу.");
            if (mode == 1) return L.T("Сокращённый файл: останется только быстрый запуск Windows, режима гибернации не будет.");
            return L.T("Полный файл: гибернация и быстрый запуск.");
        }

        /// <summary>Значение PagingFiles и пояснение для режима: 0 — авто, 1 — фиксированный, 2 — без подкачки.</summary>
        public static string[] PagingValue(int mode, string drive, int init, int max, out string what)
        {
            if (mode == 0)
            {
                what = L.T("Размер файла подкачки будет выбирать Windows.");
                return new[] { @"?:\pagefile.sys" };
            }
            if (mode == 2)
            {
                what = L.T("Файл подкачки будет ОТКЛЮЧЁН.\r\n\r\n" +
                           "При нехватке оперативной памяти программы (браузер, Docker, IDE) будут аварийно закрываться, " +
                           "а при синем экране Windows не сможет сохранить дамп.");
                return new string[0];
            }
            what = string.Format(L.T("Файл подкачки только на диске {0}: исходный {1} МБ, максимальный {2} МБ.\r\n" +
                                     "Файлы подкачки на других дисках будут удалены после перезагрузки."), drive, init, max);
            return new[] { string.Format(@"{0}\pagefile.sys {1} {2}", drive, init, max) };
        }

        /// <summary>Записывает настройку подкачки в реестр (вступает в силу после перезагрузки). Бросает исключение при ошибке.</summary>
        public static void WritePagingFiles(string[] value, Action<string> log)
        {
            using (var k = Registry.LocalMachine.OpenSubKey(MemKey, true))
            {
                if (k == null) throw new Exception(L.T("раздел реестра недоступен"));
                var old = k.GetValue("PagingFiles") as string[];
                log(L.T("Было:  ") + (old == null || old.Length == 0 ? L.T("(пусто)") : string.Join(" | ", old)));
                k.SetValue("PagingFiles", value, RegistryValueKind.MultiString);
                log(L.T("Стало: ") + (value.Length == 0 ? L.T("(без подкачки)") : string.Join(" | ", value)));
            }
        }
    }
}
