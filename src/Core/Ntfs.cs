using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace Danilovl.WinCleaner
{
    class DataRun
    {
        public long Lcn;      // -1 = разреженный участок (нули)
        public long Length;   // в кластерах
    }

    enum RecoverState { Good, Partial, Overwritten, Resident, Empty, Unknown }

    class DeletedFile
    {
        public long Record;
        public string Name;
        public long Parent;
        public int ParentSeq;
        public string Folder;
        public long Size;
        public DateTime Modified;
        public RecoverState State;
        public int OverwrittenPercent;
        public bool Checked;            // отмечен в списке для восстановления

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case RecoverState.Good: return L.T("Можно восстановить");
                    case RecoverState.Resident: return L.T("Можно восстановить (в MFT)");
                    case RecoverState.Partial: return L.T("Частично перезаписан (") + OverwrittenPercent + "%)";
                    case RecoverState.Overwritten: return L.T("Перезаписан");
                    case RecoverState.Empty: return L.T("Пустой файл");
                    default: return L.T("Нет данных о размещении");
                }
            }
        }

        public bool Recoverable { get { return State == RecoverState.Good || State == RecoverState.Resident || State == RecoverState.Partial; } }
    }

    class NtfsAttr
    {
        public uint Type;
        public int Offset;     // смещение атрибута в буфере
        public int Length;
        public bool NonResident;
        public int NameLength;
    }

    /// <summary>
    /// Чтение NTFS напрямую с тома (\\.\C:): MFT, $Bitmap, данные удалённых файлов.
    /// Только чтение — на исследуемый диск ничего не пишется.
    /// </summary>
    class NtfsVolume : IDisposable
    {
        const uint ATTR_STANDARD_INFORMATION = 0x10;
        const uint ATTR_FILE_NAME = 0x30;
        const uint ATTR_DATA = 0x80;
        const long ROOT_RECORD = 5;

        readonly SafeFileHandle h;
        public readonly string Drive;    // "C:"
        public int BytesPerSector;
        public int ClusterSize;
        public int RecordSize;
        public long RecordCount;
        List<DataRun> mftRuns;
        byte[] bitmap;

        public NtfsVolume(string drive)
        {
            Drive = drive.TrimEnd('\\');
            h = Native.CreateFile(@"\\.\" + Drive, Native.GENERIC_READ, Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid)
                throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("Не удалось открыть том ") + Drive + L.T(" (нужны права администратора)"));

            BytesPerSector = 4096;                       // для первого чтения — безопасное выравнивание
            byte[] boot = ReadBytes(0, 4096);
            if (Encoding.ASCII.GetString(boot, 3, 4) != "NTFS")
                throw new Exception(L.T("Том ") + Drive + L.T(" не NTFS"));

            BytesPerSector = BitConverter.ToUInt16(boot, 0x0B);
            int spc = boot[0x0D];
            if (spc > 0x80) spc = 1 << (256 - spc);     // большие кластеры (>64K)
            ClusterSize = BytesPerSector * spc;
            long mftLcn = BitConverter.ToInt64(boot, 0x30);
            sbyte cpr = (sbyte)boot[0x40];
            RecordSize = cpr > 0 ? cpr * ClusterSize : 1 << -cpr;

            // запись 0 = сам $MFT: из неё берём карту размещения таблицы
            byte[] rec0 = ReadBytes(mftLcn * ClusterSize, RecordSize);
            if (!Fixup(rec0, 0)) throw new Exception(L.T("Повреждена запись $MFT"));
            long size;
            mftRuns = ReadDataRuns(rec0, 0, out size);
            if (mftRuns == null) throw new Exception(L.T("Не удалось прочитать размещение $MFT"));
            RecordCount = size / RecordSize;
        }

        public void Dispose() { h.Dispose(); }

        // ------------------------------------------------------------ низкоуровневое чтение

        void ReadAligned(long offset, byte[] buf, int count)
        {
            long newPos;
            if (!Native.SetFilePointerEx(h, offset, out newPos, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            uint read;
            if (!Native.ReadFile(h, buf, (uint)count, out read, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            if (read < count) Array.Clear(buf, (int)read, count - (int)read);
        }

        /// <summary>Чтение произвольного диапазона (с выравниванием по сектору).</summary>
        byte[] ReadBytes(long offset, int count)
        {
            long start = offset - offset % BytesPerSector;
            long end = offset + count;
            if (end % BytesPerSector != 0) end += BytesPerSector - end % BytesPerSector;
            var tmp = new byte[end - start];
            ReadAligned(start, tmp, tmp.Length);
            var result = new byte[count];
            Buffer.BlockCopy(tmp, (int)(offset - start), result, 0, count);
            return result;
        }

        static ushort U16(byte[] b, int p) { return BitConverter.ToUInt16(b, p); }
        static uint U32(byte[] b, int p) { return BitConverter.ToUInt32(b, p); }
        static long I64(byte[] b, int p) { return BitConverter.ToInt64(b, p); }

        /// <summary>Проверка сигнатуры FILE и восстановление последних байт секторов (update sequence).</summary>
        bool Fixup(byte[] b, int off)
        {
            if (b[off] != 'F' || b[off + 1] != 'I' || b[off + 2] != 'L' || b[off + 3] != 'E') return false;
            int usaOfs = U16(b, off + 4);
            int usaCount = U16(b, off + 6);
            if (usaCount == 0 || usaOfs + usaCount * 2 > RecordSize) return false;
            ushort usn = U16(b, off + usaOfs);
            for (int i = 1; i < usaCount; i++)
            {
                int pos = off + i * 512 - 2;
                if (pos + 2 > off + RecordSize) break;
                if (U16(b, pos) != usn) return false;
                b[pos] = b[off + usaOfs + i * 2];
                b[pos + 1] = b[off + usaOfs + i * 2 + 1];
            }
            return true;
        }

        List<NtfsAttr> Attributes(byte[] b, int off)
        {
            var list = new List<NtfsAttr>();
            int p = off + U16(b, off + 0x14);
            int used = (int)Math.Min(U32(b, off + 0x18), (uint)RecordSize);
            int end = off + used;
            while (p + 16 <= end)
            {
                uint type = U32(b, p);
                if (type == 0xFFFFFFFF) break;
                int len = (int)U32(b, p + 4);
                if (len < 16 || p + len > end) break;
                list.Add(new NtfsAttr { Type = type, Offset = p, Length = len, NonResident = b[p + 8] != 0, NameLength = b[p + 9] });
                p += len;
            }
            return list;
        }

        static List<DataRun> DecodeRuns(byte[] b, int p, int end)
        {
            var runs = new List<DataRun>();
            long lcn = 0;
            while (p < end)
            {
                byte hdr = b[p];
                if (hdr == 0) break;
                int lenSize = hdr & 0x0F, offSize = hdr >> 4;
                p++;
                if (lenSize == 0 || lenSize > 8 || offSize > 8 || p + lenSize + offSize > end) return null;
                long length = 0;
                for (int i = 0; i < lenSize; i++) length |= (long)b[p + i] << (8 * i);
                p += lenSize;
                if (offSize == 0)
                {
                    runs.Add(new DataRun { Lcn = -1, Length = length });
                }
                else
                {
                    long delta = 0;
                    for (int i = 0; i < offSize; i++) delta |= (long)b[p + i] << (8 * i);
                    if ((b[p + offSize - 1] & 0x80) != 0)
                        for (int i = offSize; i < 8; i++) delta |= (long)0xFF << (8 * i);
                    p += offSize;
                    lcn += delta;
                    runs.Add(new DataRun { Lcn = lcn, Length = length });
                }
            }
            return runs;
        }

        /// <summary>Неименованный $DATA: карта кластеров (null если резидентный/нет/неполный).</summary>
        List<DataRun> ReadDataRuns(byte[] b, int off, out long realSize)
        {
            realSize = 0;
            foreach (var a in Attributes(b, off))
            {
                if (a.Type != ATTR_DATA || a.NameLength != 0 || !a.NonResident) continue;
                if (a.Length < 0x40) return null;
                if (I64(b, a.Offset + 0x10) != 0) return null;           // не первый фрагмент
                realSize = I64(b, a.Offset + 0x30);
                long allocated = I64(b, a.Offset + 0x28);
                long lastVcn = I64(b, a.Offset + 0x18);
                if ((lastVcn + 1) * ClusterSize < allocated) return null; // продолжение в других записях
                return DecodeRuns(b, a.Offset + U16(b, a.Offset + 0x20), a.Offset + a.Length);
            }
            return null;
        }

        long MftByteToVolume(long mftByte)
        {
            long vcnByte = 0;
            foreach (var r in mftRuns)
            {
                long bytes = r.Length * ClusterSize;
                if (mftByte < vcnByte + bytes)
                    return r.Lcn < 0 ? -1 : r.Lcn * ClusterSize + (mftByte - vcnByte);
                vcnByte += bytes;
            }
            return -1;
        }

        byte[] ReadRecord(long index)
        {
            long vol = MftByteToVolume(index * RecordSize);
            if (vol < 0) throw new Exception(L.T("Запись MFT вне таблицы"));
            byte[] rec = ReadBytes(vol, RecordSize);
            if (!Fixup(rec, 0)) throw new Exception(L.T("Запись MFT повреждена или уже переиспользована"));
            return rec;
        }

        /// <summary>Читает целиком нерезидентный поток (для $Bitmap).</summary>
        byte[] ReadStream(List<DataRun> runs, long size)
        {
            var result = new byte[size];
            long pos = 0;
            var buf = new byte[1 << 20];
            foreach (var r in runs)
            {
                long bytes = r.Length * ClusterSize;
                long done = 0;
                while (done < bytes && pos < size)
                {
                    int chunk = (int)Math.Min(buf.Length, bytes - done);
                    if (r.Lcn >= 0) ReadAligned(r.Lcn * ClusterSize + done, buf, chunk);
                    else Array.Clear(buf, 0, chunk);
                    int n = (int)Math.Min(chunk, size - pos);
                    Buffer.BlockCopy(buf, 0, result, (int)pos, n);
                    pos += n; done += chunk;
                }
            }
            return result;
        }

        void LoadBitmap()
        {
            byte[] rec = ReadRecord(6);   // $Bitmap
            long size;
            var runs = ReadDataRuns(rec, 0, out size);
            if (runs == null || size <= 0 || size > 512L * 1024 * 1024) { bitmap = null; return; }
            bitmap = ReadStream(runs, size);
        }

        bool ClusterInUse(long lcn)
        {
            long byteIdx = lcn >> 3;
            if (bitmap == null || byteIdx < 0 || byteIdx >= bitmap.Length) return true;
            return (bitmap[byteIdx] & (1 << (int)(lcn & 7))) != 0;
        }

        // ------------------------------------------------------------ сканирование

        class DirNode
        {
            public string Name;
            public long Parent;
            public int ParentSeq;   // номер версии родителя, записанный в ссылке
            public int Seq;         // номер версии самой записи
            public bool InUse;
        }

        /// <summary>
        /// Запись MFT могли переиспользовать под другую папку. Ссылка «ребёнок → родитель» хранит номер версии:
        /// у живой папки он совпадает, у удалённой — на единицу больше (увеличивается при удалении).
        /// </summary>
        static bool SameDir(DirNode n, int refSeq)
        {
            if (refSeq == 0 || n.Seq == 0) return true;            // номер неизвестен — не придираемся
            return n.Seq == refSeq || (!n.InUse && n.Seq == ((refSeq + 1) & 0xFFFF));
        }

        /// <summary>Находит удалённые файлы. progress: 0..100.</summary>
        public List<DeletedFile> FindDeleted(Action<int> progress, CancellationToken ct)
        {
            LoadBitmap();
            var dirs = new Dictionary<long, DirNode>();
            var files = new List<DeletedFile>();

            // читаем MFT кусками по 4 МБ в один и тот же буфер (без выделения памяти на каждый кусок)
            int recordsPerChunk = Math.Max(1, (4 << 20) / RecordSize);
            var buf = new byte[recordsPerChunk * RecordSize];
            long index = 0;
            int lastPct = -1;

            foreach (var run in mftRuns)
            {
                long runBytes = run.Length * ClusterSize;
                long done = 0;
                while (done < runBytes && index < RecordCount)
                {
                    ct.ThrowIfCancellationRequested();
                    int chunk = (int)Math.Min((long)recordsPerChunk * RecordSize, runBytes - done);
                    chunk -= chunk % RecordSize;
                    if (chunk <= 0) break;
                    if (run.Lcn >= 0) ReadAligned(run.Lcn * ClusterSize + done, buf, chunk);

                    for (int off = 0; off < chunk && index < RecordCount; off += RecordSize, index++)
                        if (run.Lcn >= 0) ParseRecord(buf, off, index, dirs, files);

                    done += chunk;
                    int pct = (int)(index * 100 / Math.Max(1, RecordCount));
                    if (pct != lastPct) { lastPct = pct; if (progress != null) progress(pct); }
                }
            }

            // восстанавливаем пути
            var cache = new Dictionary<long, string>();
            foreach (var f in files) f.Folder = ResolvePath(f.Parent, f.ParentSeq, dirs, cache);
            return files;
        }

        void ParseRecord(byte[] b, int off, long index, Dictionary<long, DirNode> dirs, List<DeletedFile> files)
        {
            if (!Fixup(b, off)) return;
            int flags = U16(b, off + 0x16);
            bool inUse = (flags & 1) != 0, isDir = (flags & 2) != 0;
            long baseRef = I64(b, off + 0x20) & 0xFFFFFFFFFFFFL;
            if (baseRef != 0) return;                       // запись-продолжение
            if (inUse && !isDir) return;                    // обычный существующий файл — не интересен

            string name = null;
            int bestNs = -1;
            long parent = 0;
            int parentSeq = 0;
            DateTime modified = DateTime.MinValue;
            NtfsAttr data = null;

            foreach (var a in Attributes(b, off))
            {
                if (a.Type == ATTR_FILE_NAME && !a.NonResident)
                {
                    int v = a.Offset + U16(b, a.Offset + 0x14);
                    if (v + 0x42 > off + RecordSize) continue;
                    int nameLen = b[v + 0x40];
                    int ns = b[v + 0x41];
                    if (v + 0x42 + nameLen * 2 > off + RecordSize) continue;
                    // предпочтение: Win32 (1) / Win32&DOS (3) / POSIX (0); короткое DOS-имя (2) — в крайнем случае
                    int rank = ns == 2 ? 0 : 1;
                    if (rank > bestNs)
                    {
                        bestNs = rank;
                        name = Encoding.Unicode.GetString(b, v + 0x42, nameLen * 2);
                        parent = I64(b, v) & 0xFFFFFFFFFFFFL;
                        parentSeq = U16(b, v + 6);
                        if (modified == DateTime.MinValue) modified = FromFileTime(I64(b, v + 0x10));
                    }
                }
                else if (a.Type == ATTR_STANDARD_INFORMATION && !a.NonResident)
                {
                    int v = a.Offset + U16(b, a.Offset + 0x14);
                    if (v + 0x10 <= off + RecordSize) modified = FromFileTime(I64(b, v + 0x08));
                }
                else if (a.Type == ATTR_DATA && a.NameLength == 0 && data == null)
                {
                    data = a;
                }
            }
            if (name == null) return;

            if (isDir)
            {
                dirs[index] = new DirNode { Name = name, Parent = parent, ParentSeq = parentSeq, Seq = U16(b, off + 0x10), InUse = inUse };
                return;
            }

            // удалённый файл
            var f = new DeletedFile { Record = index, Name = name, Parent = parent, ParentSeq = parentSeq, Modified = modified };
            if (data == null)
            {
                f.State = RecoverState.Unknown;
            }
            else if (!data.NonResident)
            {
                f.Size = U32(b, data.Offset + 0x10);
                f.State = f.Size == 0 ? RecoverState.Empty : RecoverState.Resident;
            }
            else
            {
                long size;
                var runs = ReadDataRuns(b, off, out size);
                f.Size = size;
                if (size == 0) f.State = RecoverState.Empty;
                else if (runs == null) f.State = RecoverState.Unknown;
                else Assess(f, runs);
            }
            files.Add(f);
        }

        void Assess(DeletedFile f, List<DataRun> runs)
        {
            if (bitmap == null) { f.State = RecoverState.Good; return; }
            long total = 0, used = 0;
            long needed = (f.Size + ClusterSize - 1) / ClusterSize;
            foreach (var r in runs)
            {
                if (r.Lcn < 0) { total += r.Length; continue; }
                for (long c = 0; c < r.Length && total < needed; c++, total++)
                    if (ClusterInUse(r.Lcn + c)) used++;
                if (total >= needed) break;
            }
            if (total == 0 || used == 0) { f.State = RecoverState.Good; return; }
            int pct = (int)Math.Ceiling(used * 100.0 / total);
            f.OverwrittenPercent = Math.Min(100, pct);
            f.State = used >= total ? RecoverState.Overwritten : RecoverState.Partial;
        }

        static DateTime FromFileTime(long ft)
        {
            try { return ft > 0 ? DateTime.FromFileTimeUtc(ft).ToLocalTime() : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        string ResolvePath(long parent, int parentSeq, Dictionary<long, DirNode> dirs, Dictionary<long, string> cache)
        {
            long key = parent | ((long)parentSeq << 48);
            string cached;
            if (cache.TryGetValue(key, out cached)) return cached;
            var parts = new List<string>();
            long cur = parent;
            int seq = parentSeq;
            for (int i = 0; i < 128 && cur != ROOT_RECORD; i++)
            {
                DirNode n;
                // папки нет или её запись уже занята другой папкой — дальше путь неизвестен
                if (!dirs.TryGetValue(cur, out n) || !SameDir(n, seq)) { parts.Add("?"); break; }
                parts.Add(n.Name);
                if (n.Parent == cur) break;
                cur = n.Parent;
                seq = n.ParentSeq;
            }
            parts.Reverse();
            string result = Drive + "\\" + string.Join("\\", parts);
            cache[key] = result;
            return result;
        }

        // ------------------------------------------------------------ восстановление

        /// <summary>Копирует данные удалённого файла в outPath. Возвращает true, если данные не сплошные нули.</summary>
        public bool Restore(DeletedFile f, string outPath)
        {
            byte[] rec = ReadRecord(f.Record);
            if ((U16(rec, 0x16) & 1) != 0)
                throw new Exception(L.T("Запись уже занята другим файлом — восстановить нельзя"));

            NtfsAttr data = null;
            foreach (var a in Attributes(rec, 0))
                if (a.Type == ATTR_DATA && a.NameLength == 0) { data = a; break; }
            if (data == null) throw new Exception(L.T("Нет сведений о данных файла"));

            bool nonZero = false;
            using (var outFs = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write))
            {
                if (!data.NonResident)
                {
                    int len = (int)U32(rec, data.Offset + 0x10);
                    int vo = U16(rec, data.Offset + 0x14);
                    if (len < 0 || data.Offset + vo + len > rec.Length) throw new Exception(L.T("Запись MFT повреждена"));
                    outFs.Write(rec, data.Offset + vo, len);
                    for (int i = 0; i < len; i++) if (rec[data.Offset + vo + i] != 0) { nonZero = true; break; }
                    return nonZero;
                }

                long size;
                var runs = ReadDataRuns(rec, 0, out size);
                if (runs == null) throw new Exception(L.T("Файл сильно фрагментирован — карта размещения недоступна"));

                long remaining = size;
                var buf = new byte[1 << 20];
                foreach (var r in runs)
                {
                    long bytes = r.Length * ClusterSize;
                    long done = 0;
                    while (done < bytes && remaining > 0)
                    {
                        int chunk = (int)Math.Min(buf.Length, bytes - done);
                        if (r.Lcn >= 0) ReadAligned(r.Lcn * ClusterSize + done, buf, chunk);
                        else Array.Clear(buf, 0, chunk);
                        int n = (int)Math.Min(chunk, remaining);
                        if (!nonZero)
                            for (int i = 0; i < n; i++) if (buf[i] != 0) { nonZero = true; break; }
                        outFs.Write(buf, 0, n);
                        remaining -= n;
                        done += chunk;
                    }
                    if (remaining <= 0) break;
                }
            }
            return nonZero;
        }
    }
}
