using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace Danilovl.WinCleaner
{
    class DupFile
    {
        public string Path, Name, Folder;
        public long Bytes;
        public DateTime Modified;
        public bool Checked;
        public DupGroup Group;
    }

    /// <summary>Набор файлов с одинаковым содержимым.</summary>
    class DupGroup
    {
        public long Bytes;                               // размер одного файла
        public List<DupFile> Files = new List<DupFile>();
        /// <summary>Сколько места освободится, если оставить одну копию.</summary>
        public long Wasted { get { return Bytes * (Files.Count - 1); } }
    }

    /// <summary>
    /// Поиск одинаковых файлов. Сначала сравниваются размеры, затем хэш первых 64 КБ и только для оставшихся — хэш всего файла,
    /// поэтому целиком читается малая часть диска. Жёсткие ссылки на один и тот же файл дубликатами не считаются.
    /// </summary>
    static class DuplicateFinder
    {
        const int HeadBytes = 64 * 1024;

        class Candidate
        {
            public FsEntry Entry;
            public string Key;      // хэш (null — файл не удалось прочитать)
        }

        public static List<DupGroup> Scan(string root, long minBytes, CancellationToken ct, Action<string> status, Action<int> progress)
        {
            // 1. все файлы не меньше minBytes
            var all = new List<FsEntry>();
            long seen = 0;
            FileWalker.WalkParallel(root, ct, (dir, entries, descend) =>
            {
                var mine = new List<FsEntry>();
                foreach (var e in entries)
                {
                    if (e.IsDir) { if (!e.IsLink && !FileWalker.SkipDir(e)) descend.Add(e.Path); continue; }
                    if (e.IsLink || e.Size < minBytes) continue;
                    mine.Add(e);
                }
                long n = Interlocked.Add(ref seen, entries.Count);
                if (n / 20000 != (n - entries.Count) / 20000) status(L.T("Просмотрено файлов: ") + Util.FormatCount(n) + "  …" + dir);
                if (mine.Count > 0) lock (all) all.AddRange(mine);
            });

            // 2. одинаковый размер
            var sameSize = all.GroupBy(e => e.Size).Where(g => g.Count() > 1).SelectMany(g => g).Select(e => new Candidate { Entry = e }).ToList();
            status(string.Format(L.T("Файлов с совпадающим размером: {0}. Сравниваю начало файлов…"), Util.FormatCount(sameSize.Count)));
            progress(-1);

            // 3. хэш начала файла
            int workers = FileWalker.WorkersFor(root, true);     // чтение содержимого: на жёстком диске — в один поток
            Hash(sameSize, HeadBytes, ct, null, workers);
            var sameHead = sameSize.Where(c => c.Key != null).GroupBy(c => c.Entry.Size + ":" + c.Key).Where(g => g.Count() > 1).SelectMany(g => g).ToList();

            // 4. хэш всего файла — только для тех, что длиннее уже проверенного начала
            var toFull = sameHead.Where(c => c.Entry.Size > HeadBytes).ToList();
            long total = toFull.Sum(c => c.Entry.Size), done = 0;
            status(string.Format(L.T("Сравниваю содержимое: {0} файлов, {1}…"), Util.FormatCount(toFull.Count), Util.FormatSize(total)));
            progress(0);
            Hash(toFull, long.MaxValue, ct, read =>
            {
                long d = Interlocked.Add(ref done, read);
                if (total > 0) progress((int)(d * 100 / total));
            }, workers);

            // 5. группы; жёсткие ссылки на один файл — это не дубликаты (место занято один раз)
            var result = new List<DupGroup>();
            foreach (var g in sameHead.Where(c => c.Key != null).GroupBy(c => c.Entry.Size + ":" + c.Key))
            {
                var ids = new HashSet<string>();
                var group = new DupGroup { Bytes = g.First().Entry.Size };
                foreach (var c in g)
                {
                    string id = FileId(c.Entry.Path);
                    if (id != null && !ids.Add(id)) continue;
                    var e = c.Entry;
                    group.Files.Add(new DupFile { Path = e.Path, Name = e.Name, Folder = e.Dir.TrimEnd('\\'), Bytes = e.Size, Modified = e.Modified, Group = group });
                }
                if (group.Files.Count < 2) continue;
                group.Files.Sort((a, b) => a.Modified.CompareTo(b.Modified));     // самый старый — первым: его обычно и оставляют
                result.Add(group);
            }
            result.Sort((a, b) => b.Wasted.CompareTo(a.Wasted));
            return result;
        }

        /// <summary>Считает хэш первых limit байт каждого файла (в несколько потоков). Нечитаемые файлы получают Key = null.</summary>
        static void Hash(List<Candidate> items, long limit, CancellationToken ct, Action<long> onRead, int workers)
        {
            if (items.Count == 0) return;
            FileWalker.ForEachParallel(items, ct, c =>
            {
                c.Key = null;
                try
                {
                    using (var md5 = MD5.Create())
                    using (var fs = new FileStream(c.Entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
                    {
                        var buf = new byte[1 << 20];
                        long left = Math.Min(limit, fs.Length);
                        while (left > 0)
                        {
                            ct.ThrowIfCancellationRequested();
                            int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) break;
                            md5.TransformBlock(buf, 0, n, null, 0);
                            left -= n;
                            if (onRead != null) onRead(n);
                        }
                        md5.TransformFinalBlock(buf, 0, 0);
                        c.Key = Convert.ToBase64String(md5.Hash);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }       // занят, нет доступа, слишком длинный путь — просто не участвует в сравнении
            }, workers);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BY_HANDLE_FILE_INFORMATION
        {
            public uint FileAttributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
            public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, out BY_HANDLE_FILE_INFORMATION info);

        /// <summary>Идентификатор файла на томе: у жёстких ссылок на один файл он одинаковый. null — узнать не удалось.</summary>
        static string FileId(string path)
        {
            try
            {
                using (var h = Native.CreateFile(Native.LP(path), 0, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE | 4, IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero))
                {
                    BY_HANDLE_FILE_INFORMATION info;
                    if (h.IsInvalid || !GetFileInformationByHandle(h, out info)) return null;
                    return info.VolumeSerialNumber + ":" + info.FileIndexHigh + ":" + info.FileIndexLow;
                }
            }
            catch { return null; }
        }
    }
}
