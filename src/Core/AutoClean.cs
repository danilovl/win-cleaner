using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Danilovl.WinCleaner
{
    /// <summary>
    /// Автоочистка без окна: запуск с ключом --auto-clean чистит только рекомендуемые места
    /// (те, что отмечены по умолчанию) с учётом списка исключений и пишет отчёт в журнал.
    /// Расписание — обычная задача в Планировщике Windows.
    /// </summary>
    static class AutoClean
    {
        public const string Argument = "--auto-clean";
        const string TaskName = "WinCleaner auto clean";

        public static string LogPath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCleaner", "auto-clean.log"); }
        }

        static void Write(string line)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine, Encoding.UTF8);
            }
            catch { }
        }

        /// <summary>Сканирует и чистит рекомендуемые места. Возвращает освобождённые байты.</summary>
        public static long Run()
        {
            long freed = 0;
            try
            {
                var targets = Targets.All().Where(t => t.Safe && t.Special == SpecialKind.None).ToList();
                Write("=== " + L.T("Автоочистка") + ": " + targets.Count + " ===");
                try { CleanerEngine.ScanAll(targets, CancellationToken.None, null); } catch { }
                foreach (var t in targets.Where(t => t.Scanned && t.Bytes > 0).OrderByDescending(t => t.Bytes))
                {
                    var st = new WalkStats();
                    try { CleanerEngine.CleanOne(t, st, CancellationToken.None, delegate { }); }
                    catch (Exception ex) { Write("  ! " + t.Name + ": " + ex.Message); }
                    freed += st.Bytes;
                    if (st.Bytes > 0 || st.FailedFiles > 0)
                        Write(string.Format("  {0,12}  {1}{2}", Util.FormatSize(st.Bytes), t.Name, st.FailedFiles > 0 ? "  (" + L.T("занято файлов: ") + st.FailedFiles + ")" : ""));
                }
                Write(L.T("Освобождено: ") + Util.FormatSize(freed));
                Settings.AddFreed(freed);
            }
            catch (Exception ex) { Write(L.T("ОШИБКА: ") + ex.Message); }
            return freed;
        }

        // ------------------------------------------------ расписание

        /// <summary>0 — выключено, 1 — раз в неделю, 2 — раз в месяц.</summary>
        public static int ScheduleMode
        {
            get
            {
                List<string> lines;
                if (Util.RunCapture("schtasks.exe", "/Query /TN \"" + TaskName + "\"", Util.OemEncoding(), out lines) != 0) return 0;
                return Settings.Get("AutoClean", "1") == "2" ? 2 : 1;
            }
        }

        /// <summary>Создаёт, меняет или удаляет задачу Планировщика. Возвращает true при успехе.</summary>
        public static bool SetSchedule(int mode, string exePath)
        {
            List<string> lines;
            if (mode == 0)
            {
                Util.RunCapture("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"", Util.OemEncoding(), out lines);
                Settings.Set("AutoClean", "0");
                return true;
            }
            // программе нужны права администратора — задача запускается с наивысшими правами, без запроса UAC
            string args = string.Format("/Create /F /TN \"{0}\" /TR \"\\\"{1}\\\" {2}\" /SC {3} /RL HIGHEST",
                TaskName, exePath, Argument, mode == 2 ? "MONTHLY" : "WEEKLY");
            bool ok = Util.RunCapture("schtasks.exe", args, Util.OemEncoding(), out lines) == 0;
            if (ok) Settings.Set("AutoClean", mode.ToString());
            return ok;
        }
    }
}
