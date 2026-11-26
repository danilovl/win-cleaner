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
    /// <summary>Файл, который будет удалён (для предпросмотра).</summary>
    class PreviewFile
    {
        public string Path, Target;
        public long Bytes;
    }

    /// <summary>Подсчёт и очистка одного места из списка Targets. Общая логика для обеих версий интерфейса.</summary>
    static class CleanerEngine
    {
        /// <summary>Есть ли что чистить (для DISM размер заранее неизвестен — считаем, что есть).</summary>
        public static bool HasSomething(CleanTarget t)
        {
            if (t.Special == SpecialKind.Dism) return true;
            return t.Scanned && t.Bytes > 0;
        }

        /// <summary>Места, чей кэш сейчас занят запущенной программой.</summary>
        public static SortedSet<string> RunningApps(IEnumerable<CleanTarget> targets)
        {
            var running = new SortedSet<string>();
            foreach (var t in targets)
                foreach (string p in t.Processes)
                {
                    var procs = Process.GetProcessesByName(p);
                    if (procs.Length > 0) running.Add(t.Name);
                    foreach (var pr in procs) pr.Dispose();
                }
            return running;
        }

        /// <summary>
        /// Подготовка места к подсчёту: раскрывает шаблоны путей, сразу считает «особые» места и файлы по маске
        /// и возвращает папки, которые нужно обойти рекурсивно (их размер попадёт в t.Live).
        /// </summary>
        static List<string> Prepare(CleanTarget t, CancellationToken ct)
        {
            t.Bytes = 0; t.Files = 0; t.Exists = false; t.Scanned = false;
            // новый список собираем отдельно и подменяем целиком: старый в это время может читать окно
            var resolved = new List<PathSpec>();
            t.Resolved = resolved;
            t.Live = null;
            var roots = new List<string>();

            if (t.Special == SpecialKind.RecycleBin)
            {
                var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                if (Native.SHQueryRecycleBin(null, ref info) == 0) { t.Bytes = info.i64Size; t.Files = info.i64NumItems; }
                t.Exists = true;
                t.Scanned = true;
                return roots;
            }
            if (t.Special == SpecialKind.Dism) { t.Exists = true; t.Scanned = true; return roots; }
            if (t.Special == SpecialKind.ShadowCopies)
            {
                int count;
                t.Bytes = Wmi.ShadowStorageUsed(out count);
                t.Files = count;
                t.Exists = count > 0;
                t.Scanned = true;
                return roots;
            }

            var st = new WalkStats();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var spec in t.Paths)
            {
                foreach (string dir in FileWalker.ExpandDirs(spec.Dir))
                {
                    if (Targets.IsDangerous(dir, spec.Mask)) continue;
                    if (!seen.Add(dir + "|" + spec.Mask)) continue;
                    resolved.Add(new PathSpec { Dir = dir, Mask = spec.Mask });
                    t.Exists = true;
                    if (spec.Mask == null) { roots.Add(dir); continue; }
                    try { FileWalker.Measure(dir, spec.Mask, st, ct); }       // файлы по маске в одной папке — это быстро
                    catch (OperationCanceledException) { throw; }
                    catch { }
                }
            }
            if (t.CustomDirs != null)
            {
                List<string> extra;
                try { extra = t.CustomDirs(); } catch { extra = new List<string>(); }
                foreach (string dir in extra)
                {
                    if (Targets.IsDangerous(dir, null) || !seen.Add(dir + "|")) continue;
                    resolved.Add(new PathSpec { Dir = dir, Mask = null });
                    t.Exists = true;
                    roots.Add(dir);
                }
            }
            t.Live = st;
            return roots;
        }

        static void Finish(CleanTarget t)
        {
            if (t.Scanned) return;
            if (t.Live != null) { t.Bytes = t.Live.Bytes; t.Files = t.Live.Files; }
            t.Scanned = true;
        }

        /// <summary>Подсчёт одного места.</summary>
        public static void ScanTarget(CleanTarget t, CancellationToken ct)
        {
            var roots = Prepare(t, ct);
            if (!t.Scanned) FileWalker.MeasureMany(roots.Select(r => new KeyValuePair<string, WalkStats>(r, t.Live)).ToList(), ct, null);
            Finish(t);
        }

        /// <summary>
        /// Подсчёт многих мест сразу. Все папки всех мест обходятся одной общей параллельной очередью:
        /// свободные потоки сразу берут папки из любого места, поэтому одно огромное место не тормозит всё остальное.
        /// Пока идёт подсчёт, текущий размер каждого места можно читать из t.Live.
        /// </summary>
        public static void ScanAll(IList<CleanTarget> targets, CancellationToken ct, Action<string> status)
        {
            foreach (var t in targets) t.Scanned = false;
            var all = new List<KeyValuePair<string, WalkStats>>();
            FileWalker.ForEachParallel(targets, ct, t =>
            {
                var roots = Prepare(t, ct);
                if (roots.Count > 0) lock (all) foreach (string r in roots) all.Add(new KeyValuePair<string, WalkStats>(r, t.Live));
            });
            FileWalker.MeasureMany(all, ct, n => { if (status != null && n % 1000 == 0) status(L.T("Просмотрено папок: ") + Util.FormatCount(n)); });
            foreach (var t in targets) Finish(t);
        }

        /// <summary>
        /// Предпросмотр: точный список файлов, которые удалит очистка выбранных мест (ничего не удаляет).
        /// В списке не больше max самых первых файлов; totalFiles и totalBytes считаются по всем.
        /// </summary>
        public static List<PreviewFile> ListFiles(IList<CleanTarget> targets, CancellationToken ct, int max, out long totalFiles, out long totalBytes)
        {
            var result = new List<PreviewFile>();
            totalFiles = 0; totalBytes = 0;
            foreach (var t in targets)
            {
                if (t.Special != SpecialKind.None) continue;      // корзина, DISM и точки восстановления — не файлы в папках
                foreach (var r in t.Resolved)
                {
                    if (Targets.IsDangerous(r.Dir, r.Mask) || Settings.IsExcluded(r.Dir)) continue;
                    var stack = new Stack<string>();
                    stack.Push(r.Dir);
                    while (stack.Count > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        foreach (var e in FileWalker.ListDir(stack.Pop(), r.Mask ?? "*"))
                        {
                            if (e.IsDir)
                            {
                                if (r.Mask != null || e.IsLink) continue;
                                string sub = e.Path;
                                if (!Settings.IsExcluded(sub)) stack.Push(sub);
                                continue;
                            }
                            totalFiles++; totalBytes += e.Size;
                            if (result.Count < max) result.Add(new PreviewFile { Path = e.Path, Bytes = e.Size, Target = t.Name });
                        }
                    }
                }
            }
            return result;
        }

        public static void CleanOne(CleanTarget t, WalkStats st, CancellationToken ct, Action<string> log)
        {
            if (t.Special == SpecialKind.RecycleBin)
            {
                var info = new Native.SHQUERYRBINFO { cbSize = Marshal.SizeOf(typeof(Native.SHQUERYRBINFO)) };
                Native.SHQueryRecycleBin(null, ref info);
                int hr = Native.SHEmptyRecycleBin(IntPtr.Zero, null, 7);
                if (hr == 0) { st.Bytes += info.i64Size; st.Files += info.i64NumItems; }
                else log(L.T("    не удалось очистить корзину (код 0x") + hr.ToString("X") + ")");
                return;
            }
            if (t.Special == SpecialKind.Dism)
            {
                Util.Run("dism.exe", "/Online /Cleanup-Image /StartComponentCleanup", Util.OemEncoding(), log);
                return;
            }
            if (t.Special == SpecialKind.ShadowCopies)
            {
                int before, after;
                long usedBefore = Wmi.ShadowStorageUsed(out before);
                int deleted = Wmi.DeleteOldShadowCopies(log);
                long usedAfter = Wmi.ShadowStorageUsed(out after);
                st.Files += deleted;
                st.Bytes += Math.Max(0, usedBefore - usedAfter);
                return;
            }

            // потом запускаем обратно только те службы, которые действительно работали и были остановлены нами
            var stopped = new List<string>();
            foreach (string svc in t.Services)
                if (Util.Run("net.exe", "stop " + svc + " /y", Util.OemEncoding(), null) == 0) stopped.Add(svc);
            try
            {
                foreach (var r in t.Resolved)
                {
                    if (Targets.IsDangerous(r.Dir, r.Mask)) { log(L.T("    пропущено (защищённая папка): ") + r.Dir); continue; }
                    if (t.TakeOwnership)
                    {
                        // язык-независимый SID группы «Администраторы»
                        log(L.T("    получаю права на ") + r.Dir + L.T(" (может занять пару минут)"));
                        Util.Run("icacls.exe", "\"" + r.Dir + "\" /setowner *S-1-5-32-544 /T /C /Q", Util.OemEncoding(), null);
                        Util.Run("icacls.exe", "\"" + r.Dir + "\" /grant *S-1-5-32-544:F /T /C /Q", Util.OemEncoding(), null);
                    }
                    FileWalker.Delete(r.Dir, r.Mask, st, ct, t.RebootFallback);
                    if (t.RemoveRoot && r.Mask == null) FileWalker.RemoveDir(r.Dir);
                }
            }
            finally
            {
                foreach (string svc in stopped) Util.Run("net.exe", "start " + svc, Util.OemEncoding(), null);
            }
        }
    }
}
