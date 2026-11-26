using System;
using System.Collections.Generic;
using System.Threading;

namespace Danilovl.WinCleaner
{
    class BigFile
    {
        public string Path;
        public string Name;
        public string Folder;
        public long Bytes;
        public DateTime Modified;
        public bool Checked;
    }

    /// <summary>Поиск самых крупных файлов в папке.</summary>
    static class BigFileScanner
    {
        static readonly HashSet<string> Protected = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "dumpstack.log.tmp" };

        /// <summary>
        /// Добавляет в found файлы не меньше minBytes. Возвращает число просмотренных файлов.
        /// При остановке бросает OperationCanceledException, в found остаётся найденное.
        /// </summary>
        /// <param name="olderThan">если задано — только файлы, которые созданы и изменены раньше этой даты (DateTime.MinValue — любые)</param>
        public static long Scan(string root, long minBytes, DateTime olderThan, CancellationToken ct, List<BigFile> found, Action<string> status)
        {
            long limit = olderThan == DateTime.MinValue ? 0 : olderThan.ToFileTimeUtc();
            long scanned = 0;
            FileWalker.WalkParallel(root, ct, (dir, entries, descend) =>
            {
                int files = 0;
                foreach (var e in entries)
                {
                    if (e.IsDir)
                    {
                        if (!e.IsLink && !FileWalker.SkipDir(e)) descend.Add(e.Path);
                        continue;
                    }
                    files++;
                    if (e.Size < minBytes || Protected.Contains(e.Name)) continue;
                    // «давно не трогали»: и появился здесь, и менялся раньше указанной даты
                    if (limit > 0 && Math.Max(e.WriteTime, e.CreateTime) >= limit) continue;
                    var f = new BigFile { Path = e.Path, Name = e.Name, Folder = dir.TrimEnd('\\'), Bytes = e.Size, Modified = e.Modified };
                    lock (found) found.Add(f);
                }
                long before = Interlocked.Add(ref scanned, files) - files;
                if (before / 20000 != (before + files) / 20000)
                    status(L.T("Просмотрено файлов: ") + Util.FormatCount(before + files) + "  …" + dir);
            });
            return Interlocked.Read(ref scanned);
        }

        /// <summary>Удаляет файл (в Корзину или насовсем). true — файла больше нет.</summary>
        public static bool Delete(string path, bool recycle)
        {
            if (Settings.IsExcluded(path)) throw new Exception(L.T("в списке исключений — не удаляется"));
            if (recycle)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
            else if (!Native.DeleteFile(Native.LP(path)))
            {
                Native.SetFileAttributes(Native.LP(path), Native.FILE_ATTRIBUTE_NORMAL);
                Native.DeleteFile(Native.LP(path));
            }
            return !Native.Exists(path);
        }

        /// <summary>Удаляет папку целиком (в Корзину или насовсем). true — папки больше нет.</summary>
        public static bool DeleteFolder(string path, bool recycle, CancellationToken ct, out long failedFiles)
        {
            failedFiles = 0;
            // папку целиком (в т.ч. в Корзину) нельзя удалять, если внутри есть исключение
            if (Settings.ContainsExcluded(path)) throw new Exception(L.T("содержит папку из списка исключений — не удаляется"));
            if (recycle)
                Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(path,
                    Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                    Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                    Microsoft.VisualBasic.FileIO.UICancelOption.DoNothing);
            else
            {
                var st = new WalkStats();
                FileWalker.Delete(path, null, st, ct, false);
                FileWalker.RemoveDir(path);
                failedFiles = st.FailedFiles;
            }
            return !Native.Exists(path);
        }
    }
}
