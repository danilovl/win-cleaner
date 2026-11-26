using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Danilovl.WinCleaner
{
    class WalkStats
    {
        public long Bytes;
        public long Files;
        public long FailedFiles;
        public long FailedBytes;
        public long Scheduled;        // поставлено на удаление при перезагрузке
        public long ScheduledBytes;
        public long NewestWrite;      // FILETIME самого свежего элемента (заполняет Measure)
    }

    /// <summary>Элемент папки: только то, что реально нужно, без полного WIN32_FIND_DATA.</summary>
    struct FsEntry
    {
        public string Dir;        // папка с завершающим «\»
        public string Name;
        public uint Attributes;
        public long Size;
        public long WriteTime;    // FILETIME (UTC)
        public long CreateTime;   // FILETIME (UTC): когда файл появился в этой папке (скачан, скопирован)

        public string Path { get { return Dir + Name; } }
        public bool IsDir { get { return (Attributes & Native.FILE_ATTRIBUTE_DIRECTORY) != 0; } }
        public bool IsLink { get { return (Attributes & Native.FILE_ATTRIBUTE_REPARSE_POINT) != 0; } }
        public bool IsReadOnly { get { return (Attributes & Native.FILE_ATTRIBUTE_READONLY) != 0; } }
        public DateTime Modified { get { return FileWalker.ToLocalTime(WriteTime); } }
    }

    /// <summary>
    /// Обход и удаление файлов через WinAPI: длинные пути (\\?\), без перехода по ссылкам/junction.
    /// </summary>
    static class FileWalker
    {
        // ------------------------------------------------------------ сколько потоков можно занять

        /// <summary>Логических процессоров в системе.</summary>
        public static readonly int Cores = Math.Max(1, Environment.ProcessorCount);

        /// <summary>Этот поток уже работает внутри параллельной операции: вложенные операции выполняются в нём же, без новых потоков.</summary>
        [ThreadStatic] static bool inWorker;

        static readonly Dictionary<char, int> driveKinds = new Dictionary<char, int>();   // 1 — SSD, 2 — жёсткий диск, 0 — неизвестно

        /// <summary>Тип диска, на котором лежит путь: 1 — SSD (нет задержки на перемещение головки), 2 — жёсткий диск, 0 — узнать не удалось.</summary>
        public static int DriveKind(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length < 2 || path[1] != ':') return 0;
            char letter = char.ToUpperInvariant(path[0]);
            lock (driveKinds)
            {
                int kind;
                if (driveKinds.TryGetValue(letter, out kind)) return kind;
                kind = Native.SeekPenalty(letter);
                driveKinds[letter] = kind;
                return kind;
            }
        }

        /// <summary>
        /// Сколько потоков имеет смысл использовать для работы с этим путём — по числу ядер и типу диска.
        /// На одном ядре всё идёт в один поток. На жёстком диске много потоков только мешают друг другу (головка одна),
        /// особенно при чтении содержимого файлов (readsContent). На SSD — по числу ядер, но не больше 8.
        /// </summary>
        public static int WorkersFor(string path, bool readsContent = false)
        {
            if (Cores <= 1) return 1;
            switch (DriveKind(path))
            {
                case 1: return Math.Min(8, Cores);
                case 2: return readsContent ? 1 : Math.Min(2, Cores);
                default: return Math.Min(4, Cores);
            }
        }

        /// <summary>Описание для журнала: «потоков: 8 (SSD)».</summary>
        public static string WorkersText(string path, bool readsContent = false)
        {
            int kind = DriveKind(path);
            return string.Format(L.T("потоков: {0} ({1}, ядер: {2})"), WorkersFor(path, readsContent),
                kind == 1 ? "SSD" : kind == 2 ? "HDD" : L.T("тип диска неизвестен"), Cores);
        }

        public static DateTime ToLocalTime(long fileTime)
        {
            try { return fileTime > 0 ? DateTime.FromFileTimeUtc(fileTime).ToLocalTime() : DateTime.MinValue; }
            catch { return DateTime.MinValue; }
        }

        /// <summary>Перечисляет элементы папки dir по маске (без рекурсии).</summary>
        public static List<FsEntry> ListDir(string dir, string mask)
        {
            var result = new List<FsEntry>();
            string baseDir = dir.EndsWith("\\") ? dir : dir + "\\";
            IntPtr buf = Marshal.AllocHGlobal(Native.FIND_DATA_SIZE);
            try
            {
                IntPtr h = Native.FindFirstFileEx(Native.LP(baseDir + mask), 1, buf, 0, IntPtr.Zero, 2);
                if (h == Native.INVALID_HANDLE_VALUE) return result;
                try
                {
                    do
                    {
                        // пропускаем «.» и «..»
                        if (Marshal.ReadInt16(buf, Native.FD_NAME) == '.')
                        {
                            short c1 = Marshal.ReadInt16(buf, Native.FD_NAME + 2);
                            if (c1 == 0 || (c1 == '.' && Marshal.ReadInt16(buf, Native.FD_NAME + 4) == 0)) continue;
                        }
                        FsEntry e;
                        e.Dir = baseDir;
                        e.Name = Marshal.PtrToStringUni(buf + Native.FD_NAME);
                        e.Attributes = (uint)Marshal.ReadInt32(buf, Native.FD_ATTRIBUTES);
                        e.Size = ((long)(uint)Marshal.ReadInt32(buf, Native.FD_SIZE_HIGH) << 32) | (uint)Marshal.ReadInt32(buf, Native.FD_SIZE_LOW);
                        e.WriteTime = ((long)(uint)Marshal.ReadInt32(buf, Native.FD_WRITE_HIGH) << 32) | (uint)Marshal.ReadInt32(buf, Native.FD_WRITE_LOW);
                        e.CreateTime = ((long)(uint)Marshal.ReadInt32(buf, Native.FD_CREATE_HIGH) << 32) | (uint)Marshal.ReadInt32(buf, Native.FD_CREATE_LOW);
                        result.Add(e);
                    } while (Native.FindNextFile(h, buf));
                }
                finally { Native.FindClose(h); }
            }
            finally { Marshal.FreeHGlobal(buf); }
            return result;
        }

        /// <summary>Подсчёт: mask == null — всё содержимое рекурсивно, иначе только файлы по маске в самой папке.</summary>
        public static void Measure(string dir, string mask, WalkStats st, CancellationToken ct)
        {
            if (Settings.IsExcluded(dir)) return;      // папка в списке исключений — её как будто нет
            if (mask != null)
            {
                foreach (var e in ListDir(dir, mask))
                    if (!e.IsDir) { st.Files++; st.Bytes += e.Size; }
                return;
            }

            // большое дерево считаем в несколько потоков, если система это позволяет и мы ещё не внутри параллельной операции
            if (!inWorker && WorkersFor(dir) > 1)
            {
                MeasureMany(new[] { new KeyValuePair<string, WalkStats>(dir, st) }, ct, null);
                return;
            }

            var stack = new Stack<string>();
            stack.Push(dir);
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var e in ListDir(stack.Pop(), "*"))
                {
                    if (e.WriteTime > st.NewestWrite) st.NewestWrite = e.WriteTime;
                    if (e.IsDir)
                    {
                        if (e.IsLink) continue;              // по ссылкам не ходим
                        string sub = e.Path;
                        if (!Settings.IsExcluded(sub)) stack.Push(sub);
                    }
                    else { st.Files++; st.Bytes += e.Size; }
                }
            }
        }

        /// <summary>Папки, в которые при поиске не заходим: системные — только в корне диска, .git — везде.</summary>
        public static bool SkipDir(FsEntry e)
        {
            string name = e.Name.ToLowerInvariant();
            if (name == ".git") return true;
            if (Settings.HasExclusions && Settings.IsExcluded(e.Path)) return true;
            if (e.Dir.Length > 3) return false;          // не корень диска — обычная папка с таким же именем
            switch (name)
            {
                case "$recycle.bin": case "system volume information": case "windows": case "$windows.~bt":
                case "$windows.~ws": case "windows.old": case "recovery": case "config.msi":
                    return true;
                default:
                    return false;
            }
        }

        // ------------------------------------------------------------ параллельная работа

        /// <summary>
        /// Считает размеры сразу многих папок одним общим параллельным обходом: потоки делят между собой все папки всех деревьев,
        /// поэтому одно огромное дерево не считается в один поток, пока остальные простаивают.
        /// У каждой папки свой WalkStats (один WalkStats можно дать нескольким папкам — размеры сложатся).
        /// onDirs получает число просмотренных папок — для показа хода работы.
        /// </summary>
        public static void MeasureMany(IList<KeyValuePair<string, WalkStats>> roots, CancellationToken ct, Action<long> onDirs)
        {
            var items = new List<KeyValuePair<string, WalkStats>>();
            int workers = int.MaxValue;
            foreach (var r in roots)
            {
                if (Settings.IsExcluded(r.Key)) continue;
                items.Add(r);
                workers = Math.Min(workers, WorkersFor(r.Key));     // если папки на разных дисках — ориентируемся на самый медленный
            }
            if (items.Count == 0) return;
            long dirs = 0;
            WalkParallel(items, ct, (dir, st, entries, descend) =>
            {
                long bytes = 0, files = 0, newest = 0;
                foreach (var e in entries)
                {
                    if (e.WriteTime > newest) newest = e.WriteTime;
                    if (e.IsDir)
                    {
                        if (e.IsLink) continue;              // по ссылкам не ходим
                        string sub = e.Path;
                        if (!Settings.IsExcluded(sub)) descend.Add(new KeyValuePair<string, WalkStats>(sub, st));
                    }
                    else { files++; bytes += e.Size; }
                }
                lock (st)
                {
                    st.Bytes += bytes; st.Files += files;
                    if (newest > st.NewestWrite) st.NewestWrite = newest;
                }
                if (onDirs != null) onDirs(Interlocked.Increment(ref dirs));
            }, workers);
        }

        /// <summary>Выполняет body для каждого элемента в несколько потоков. workers = 0 — по числу ядер (не больше 8).</summary>
        public static void ForEachParallel<T>(IList<T> items, CancellationToken ct, Action<T> body, int workers = 0)
        {
            int next = -1;
            Exception error = null;
            ThreadStart run = () =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && error == null)
                    {
                        int i = Interlocked.Increment(ref next);
                        if (i >= items.Count) break;
                        body(items[i]);
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Interlocked.CompareExchange(ref error, ex, null); }
            };
            if (workers <= 0) workers = Math.Min(8, Cores);
            RunWorkers(Math.Min(workers, items.Count), run);
            ct.ThrowIfCancellationRequested();
            if (error != null) throw new Exception(error.Message, error);
        }

        /// <summary>
        /// Запускает run в count потоках (текущий — один из них) и ждёт завершения.
        /// Если текущий поток сам уже рабочий поток другой параллельной операции, новых потоков не создаёт:
        /// иначе вложенные операции породили бы потоков больше, чем есть ядер.
        /// </summary>
        static void RunWorkers(int count, ThreadStart run)
        {
            if (inWorker || count <= 1) { run(); return; }
            ThreadStart marked = () => { inWorker = true; run(); };
            var threads = new List<Thread>();
            for (int i = 1; i < count; i++)
            {
                var t = new Thread(marked) { IsBackground = true };
                threads.Add(t);
                t.Start();
            }
            inWorker = true;
            try { run(); }                           // текущий поток тоже работает
            finally { inWorker = false; }
            foreach (var t in threads) t.Join();
        }

        /// <summary>
        /// Вызывается для каждой папки (из разных потоков!): entries — её содержимое,
        /// в descend нужно добавить подпапки, в которые следует зайти.
        /// </summary>
        public delegate void DirVisitor(string dir, List<FsEntry> entries, List<string> descend);

        /// <summary>То же, но с произвольными данными папки (state), которые передаются от родителя к потомку.</summary>
        public delegate void DirVisitor<T>(string dir, T state, List<FsEntry> entries, List<KeyValuePair<string, T>> descend);

        /// <summary>Обход дерева папок в несколько потоков.</summary>
        public static void WalkParallel(string root, CancellationToken ct, DirVisitor visit)
        {
            WalkParallel<object>(root, null, ct, (dir, state, entries, descend) =>
            {
                var list = new List<string>();
                visit(dir, entries, list);
                foreach (string d in list) descend.Add(new KeyValuePair<string, object>(d, null));
            });
        }

        /// <summary>Обход дерева папок в несколько потоков; у каждой папки свои данные (например, узел дерева).</summary>
        public static void WalkParallel<T>(string root, T rootState, CancellationToken ct, DirVisitor<T> visit, int workers = 0)
        {
            WalkParallel(new[] { new KeyValuePair<string, T>(root, rootState) }, ct, visit, workers > 0 ? workers : WorkersFor(root));
        }

        /// <summary>Обход сразу нескольких деревьев общей очередью папок. workers — число потоков.</summary>
        public static void WalkParallel<T>(IList<KeyValuePair<string, T>> roots, CancellationToken ct, DirVisitor<T> visit, int workers)
        {
            if (roots.Count == 0) return;
            using (var queue = new BlockingCollection<KeyValuePair<string, T>>(new ConcurrentStack<KeyValuePair<string, T>>()))
            {
                int pending = roots.Count;            // папок в очереди и в обработке
                bool stop = false;
                Exception error = null;
                foreach (var r in roots) queue.Add(r);

                ThreadStart run = () =>
                {
                    var descend = new List<KeyValuePair<string, T>>();
                    try
                    {
                        KeyValuePair<string, T> item;
                        while (queue.TryTake(out item, Timeout.Infinite))
                        {
                            if (stop) break;
                            if (ct.IsCancellationRequested) { stop = true; queue.CompleteAdding(); break; }
                            descend.Clear();
                            visit(item.Key, item.Value, ListDir(item.Key, "*"), descend);
                            if (descend.Count > 0)
                            {
                                Interlocked.Add(ref pending, descend.Count);
                                foreach (var d in descend) queue.Add(d);
                            }
                            if (Interlocked.Decrement(ref pending) == 0) queue.CompleteAdding();
                        }
                    }
                    catch (Exception ex)
                    {
                        // если уже останавливаемся, это всего лишь «очередь закрыта»
                        if (!stop) Interlocked.CompareExchange(ref error, ex, null);
                        stop = true;
                        try { queue.CompleteAdding(); } catch { }
                    }
                };
                RunWorkers(Math.Max(1, workers), run);
                ct.ThrowIfCancellationRequested();
                if (error != null) throw new Exception(error.Message, error);
            }
        }

        // ------------------------------------------------------------ удаление

        /// <summary>Удаление: mask == null — всё содержимое папки (сама папка остаётся), иначе файлы по маске.</summary>
        public static void Delete(string dir, string mask, WalkStats st, CancellationToken ct, bool rebootFallback)
        {
            if (Settings.IsExcluded(dir)) return;      // исключения не удаляем никогда
            if (mask != null)
            {
                foreach (var e in ListDir(dir, mask))
                    if (!e.IsDir) DeleteOneFile(e, st, rebootFallback);
                return;
            }
            int workers = inWorker ? 1 : WorkersFor(dir);
            if (workers <= 1) { DeleteContents(dir, st, ct, 0, rebootFallback); return; }
            DeleteParallel(dir, st, ct, rebootFallback, workers);
        }

        /// <summary>
        /// Параллельное удаление содержимого папки: файлы удаляются в несколько потоков (каждая папка — своим потоком),
        /// а сами папки — после этого, от самых глубоких к верхним. Результат тот же, что у DeleteContents.
        /// </summary>
        static void DeleteParallel(string dir, WalkStats st, CancellationToken ct, bool rebootFallback, int workers)
        {
            var dirs = new List<string>();
            WalkParallel(dir, 0, ct, (d, depth, entries, descend) =>
            {
                var local = new WalkStats();
                List<string> mine = null;
                foreach (var e in entries)
                {
                    if (!e.IsDir) { DeleteOneFile(e, local, rebootFallback); continue; }
                    string path = e.Path;
                    if (Settings.IsExcluded(path)) continue;
                    if (e.IsLink)
                    {
                        // удаляем только саму ссылку, не трогая то, на что она указывает
                        Native.RemoveDirectory(Native.LP(path));
                        continue;
                    }
                    if (mine == null) mine = new List<string>();
                    mine.Add(path);
                    if (depth < 400) descend.Add(new KeyValuePair<string, int>(path, depth + 1));
                }
                if (mine != null) lock (dirs) dirs.AddRange(mine);
                lock (st)
                {
                    st.Files += local.Files; st.Bytes += local.Bytes;
                    st.FailedFiles += local.FailedFiles; st.FailedBytes += local.FailedBytes;
                    st.Scheduled += local.Scheduled; st.ScheduledBytes += local.ScheduledBytes;
                }
            }, workers);

            // вложенная папка всегда длиннее родительской по пути — так удаляем снизу вверх
            dirs.Sort((a, b) => b.Length.CompareTo(a.Length));
            foreach (string d in dirs)
            {
                ct.ThrowIfCancellationRequested();
                RemoveDir(d);
            }
        }

        /// <summary>Удаляет папку (уже пустую); при неудаче снимает атрибут «только чтение» и пробует ещё раз.</summary>
        public static bool RemoveDir(string path)
        {
            string lp = Native.LP(path);
            if (Native.RemoveDirectory(lp)) return true;
            Native.SetFileAttributes(lp, Native.FILE_ATTRIBUTE_NORMAL);
            return Native.RemoveDirectory(lp);
        }

        static void DeleteContents(string dir, WalkStats st, CancellationToken ct, int depth, bool rebootFallback)
        {
            ct.ThrowIfCancellationRequested();
            if (depth > 400) return;
            foreach (var e in ListDir(dir, "*"))
            {
                if (e.IsDir)
                {
                    string path = e.Path;
                    if (Settings.IsExcluded(path)) continue;
                    if (e.IsLink)
                    {
                        // удаляем только саму ссылку, не трогая то, на что она указывает
                        Native.RemoveDirectory(Native.LP(path));
                        continue;
                    }
                    DeleteContents(path, st, ct, depth + 1, rebootFallback);
                    RemoveDir(path);
                }
                else DeleteOneFile(e, st, rebootFallback);
            }
        }

        static void DeleteOneFile(FsEntry e, WalkStats st, bool rebootFallback)
        {
            string lp = Native.LP(e.Path);
            bool ok = Native.DeleteFile(lp);
            if (!ok && e.IsReadOnly)
            {
                Native.SetFileAttributes(lp, Native.FILE_ATTRIBUTE_NORMAL);
                ok = Native.DeleteFile(lp);
            }
            if (ok) { st.Files++; st.Bytes += e.Size; }
            else if (rebootFallback && Native.MoveFileEx(lp, null, Native.MOVEFILE_DELAY_UNTIL_REBOOT)) { st.Scheduled++; st.ScheduledBytes += e.Size; }
            else { st.FailedFiles++; st.FailedBytes += e.Size; }
        }

        /// <summary>
        /// Раскрывает шаблон папки: переменные окружения и * / ? в любом сегменте пути.
        /// Пример: %LOCALAPPDATA%\Google\Chrome\User Data\*\Cache
        /// </summary>
        public static List<string> ExpandDirs(string pattern)
        {
            string p = Environment.ExpandEnvironmentVariables(pattern);
            var result = new List<string>();
            if (p.Contains("%")) return result; // переменная не определена (например, нет ProgramFiles(x86))

            string[] parts = p.Split(new[] { '\\' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) return result;
            var current = new List<string> { parts[0] + "\\" };

            for (int i = 1; i < parts.Length; i++)
            {
                string seg = parts[i];
                var next = new List<string>();
                bool wild = seg.IndexOfAny(new[] { '*', '?' }) >= 0;
                foreach (string c in current)
                {
                    try
                    {
                        if (wild)
                        {
                            foreach (var e in ListDir(c, seg))
                                if (e.IsDir && !e.IsLink) next.Add(e.Path);
                        }
                        else
                        {
                            string cand = c.TrimEnd('\\') + "\\" + seg;
                            if (Directory.Exists(cand)) next.Add(cand);
                        }
                    }
                    catch { }
                }
                current = next;
                if (current.Count == 0) break;
            }
            foreach (string c in current) result.Add(c.TrimEnd('\\'));
            return result;
        }
    }
}
