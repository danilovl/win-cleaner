using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace Danilovl.WinCleaner
{
    /// <summary>Папка в карте диска: размер вместе со всем содержимым.</summary>
    class DiskNode
    {
        public string Name;                 // у корня — полный путь
        public DiskNode Parent;
        public List<DiskNode> Children;     // null — подпапок нет
        public long Bytes, Files;           // вместе с подпапками
        public long OwnBytes, OwnFiles;     // только файлы прямо в этой папке

        public string Path
        {
            get
            {
                if (Parent == null) return Name;
                var parts = new List<string>();
                DiskNode n = this;
                for (; n.Parent != null; n = n.Parent) parts.Add(n.Name);
                parts.Reverse();
                return n.Name.TrimEnd('\\') + "\\" + string.Join("\\", parts);
            }
        }
    }

    /// <summary>Файл или сводная строка «ещё N файлов» при показе содержимого папки.</summary>
    class DiskFile
    {
        public string Path, Name;
        public long Bytes;
        public long MoreCount;              // &gt; 0 — это сводная строка про остальные файлы
    }

    /// <summary>Карта диска: дерево папок с размерами.</summary>
    static class DiskTree
    {
        /// <summary>
        /// Строит дерево папок под root. Хранятся только папки (файлы читаются при раскрытии папки),
        /// поэтому даже целый диск занимает немного памяти. По ссылкам и junction не ходит.
        /// </summary>
        public static DiskNode Scan(string root, CancellationToken ct, Action<string> status)
        {
            var rootNode = new DiskNode { Name = root };
            int dirs = 0;
            FileWalker.WalkParallel(root, rootNode, ct, (dir, node, entries, descend) =>
            {
                long bytes = 0, files = 0;
                List<DiskNode> children = null;
                foreach (var e in entries)
                {
                    if (!e.IsDir) { files++; bytes += e.Size; continue; }
                    if (e.IsLink) continue;
                    var child = new DiskNode { Name = e.Name, Parent = node };
                    if (children == null) children = new List<DiskNode>();
                    children.Add(child);
                    descend.Add(new KeyValuePair<string, DiskNode>(e.Path, child));
                }
                // узел заполняет только тот поток, который обходит его папку, — блокировки не нужны
                node.OwnBytes = bytes;
                node.OwnFiles = files;
                node.Children = children;
                int n = Interlocked.Increment(ref dirs);
                if (status != null && n % 2000 == 0) status(L.T("Просмотрено папок: ") + Util.FormatCount(n) + "  …" + dir);
            });
            Total(rootNode);
            return rootNode;
        }

        /// <summary>Суммирует размеры снизу вверх и сортирует подпапки по убыванию размера (без рекурсии — дерево бывает глубоким).</summary>
        static void Total(DiskNode root)
        {
            var order = new List<DiskNode>();
            var stack = new Stack<DiskNode>();
            stack.Push(root);
            while (stack.Count > 0)
            {
                var n = stack.Pop();
                order.Add(n);
                if (n.Children != null) foreach (var c in n.Children) stack.Push(c);
            }
            for (int i = order.Count - 1; i >= 0; i--)      // потомки всегда идут в списке после родителя
            {
                var n = order[i];
                n.Bytes = n.OwnBytes;
                n.Files = n.OwnFiles;
                if (n.Children == null) continue;
                foreach (var c in n.Children) { n.Bytes += c.Bytes; n.Files += c.Files; }
                n.Children.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            }
        }

        /// <summary>Самые крупные файлы прямо в папке; если их больше max, последним идёт сводная строка.</summary>
        public static List<DiskFile> FilesOf(DiskNode node, int max)
        {
            var files = FileWalker.ListDir(node.Path, "*").Where(e => !e.IsDir).OrderByDescending(e => e.Size).ToList();
            var result = files.Take(max).Select(e => new DiskFile { Path = e.Path, Name = e.Name, Bytes = e.Size }).ToList();
            if (files.Count > max)
                result.Add(new DiskFile { MoreCount = files.Count - max, Bytes = files.Skip(max).Sum(e => e.Size) });
            return result;
        }

        /// <summary>Убирает папку из дерева (после удаления с диска) и уменьшает размеры всех родителей.</summary>
        public static void Remove(DiskNode node)
        {
            if (node.Parent == null) return;
            if (node.Parent.Children != null) node.Parent.Children.Remove(node);
            for (var p = node.Parent; p != null; p = p.Parent) { p.Bytes -= node.Bytes; p.Files -= node.Files; }
        }

        /// <summary>Учитывает удаление одного файла из папки.</summary>
        public static void RemoveFile(DiskNode folder, long bytes)
        {
            folder.OwnBytes -= bytes; folder.OwnFiles--;
            for (var p = folder; p != null; p = p.Parent) { p.Bytes -= bytes; p.Files--; }
        }
    }
}
