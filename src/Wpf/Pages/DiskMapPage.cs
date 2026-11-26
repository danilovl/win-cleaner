using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Danilovl.WinCleaner.Wpf
{
    /// <summary>
    /// Страница «Карта диска»: дерево папок, отсортированное по размеру, — видно, куда ушло место,
    /// даже если это не «известный мусор». Подпапки и файлы подгружаются при раскрытии папки.
    /// Папки и файлы отмечаются галочками; отмеченное удаляется в Корзину одной кнопкой.
    /// </summary>
    class DiskMapPage : FolderPage
    {
        const int MaxFiles = 100;     // столько самых крупных файлов показываем в папке
        const int MaxFolders = 400;   // и столько подпапок (остальные — сводной строкой)

        /// <summary>Отмеченная строка дерева: папка или файл.</summary>
        class Mark
        {
            public DiskNode Node;           // папка…
            public DiskFile File;           // …или файл
            public DiskNode FileFolder;     // папка, в которой лежит файл
            public string Path { get { return Node != null ? Node.Path : File.Path; } }
            public long Bytes { get { return Node != null ? Node.Bytes : File.Bytes; } }
        }

        readonly TreeView tree = new TreeView();
        readonly Button btnScan, btnOpen, btnDelete, btnUncheck;
        readonly HashSet<TreeViewItem> filled = new HashSet<TreeViewItem>();
        readonly Dictionary<object, Mark> marks = new Dictionary<object, Mark>();   // ключ — DiskNode или DiskFile
        readonly List<CheckBox> boxes = new List<CheckBox>();
        DiskNode root;

        public DiskMapPage()
            : base(L.T("Карта диска"),
                   L.T("Все папки по убыванию размера. Раскрывайте самые большие, чтобы найти, что занимает место. " +
                       "Полоса и процент показывают долю от родительской папки. Ссылки и junction не учитываются."),
                   Settings.Get("DiskMapRoot", (Environment.GetEnvironmentVariable("SYSTEMDRIVE") ?? "C:") + "\\"))
        {
            btnScan = X.Btn(L.T("Построить карту"), StartScan, "AccentButton");
            btnOpen = X.Btn(L.T("Открыть в Проводнике"), OpenSelected);
            btnUncheck = X.Btn(L.T("Снять все"), UncheckAll);
            btnDelete = X.Btn(L.T("Удалить отмеченные в Корзину…"), DeleteMarked, "DangerButton");
            foreach (UIElement e in new UIElement[] { btnScan, StopButton, btnOpen, btnUncheck, btnDelete }) Toolbar.Children.Add(e);

            tree.SelectedItemChanged += delegate { UpdateState(); };
            var menu = new ContextMenu();
            var miOpen = new MenuItem { Header = L.T("Открыть в Проводнике") };
            var miCopy = new MenuItem { Header = L.T("Копировать путь") };
            miOpen.Click += delegate { OpenSelected(); };
            miCopy.Click += delegate { string p = SelectedPath(); try { if (p != null) Clipboard.SetText(p); } catch { } };
            menu.Items.Add(miOpen); menu.Items.Add(miCopy);
            menu.Opened += delegate { miOpen.IsEnabled = miCopy.IsEnabled = SelectedPath() != null; };
            tree.ContextMenu = menu;
            Body.Child = tree;
            Status(L.T("Укажите диск или папку и нажмите «Построить карту». Целый диск может занять минуту."));
        }

        // ------------------------------------------------------------ дерево

        /// <summary>Строка дерева: галочка, полоса доли, размер, процент, значок, имя, пояснение.</summary>
        static FrameworkElement Header(CheckBox box, string glyph, string name, long bytes, long parentBytes, string note)
        {
            double pct = parentBytes > 0 ? bytes * 100.0 / parentBytes : 100;
            var p = new StackPanel { Orientation = Orientation.Horizontal };
            // у строк без галочки (корень, сводные строки) на её месте пустое место — чтобы столбцы стояли ровно
            if (box != null) { box.Margin = new Thickness(0, 0, 10, 0); p.Children.Add(box); }
            else p.Children.Add(new Border { Width = 28 });
            p.Children.Add(new ProgressBar { Width = 70, Value = Math.Max(0, Math.Min(100, pct)), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
            var size = X.Txt(Util.FormatSize(bytes), 13, true);
            size.Width = 84; size.TextAlignment = TextAlignment.Right; size.TextWrapping = TextWrapping.NoWrap;
            p.Children.Add(size);
            var percent = X.Txt(pct.ToString(pct >= 10 ? "0" : "0.#") + "%", 12, false, "SubText");
            percent.Width = 46; percent.TextAlignment = TextAlignment.Right; percent.TextWrapping = TextWrapping.NoWrap;
            p.Children.Add(percent);
            if (glyph != null)
            {
                var icon = X.Icon(glyph, 14, "SubText");
                icon.Margin = new Thickness(12, 0, 8, 0);
                p.Children.Add(icon);
            }
            var title = X.Txt(name);
            title.TextWrapping = TextWrapping.NoWrap;
            if (glyph == null) { title.Margin = new Thickness(12, 0, 0, 0); title.SetResourceReference(TextBlock.ForegroundProperty, "SubText"); }
            p.Children.Add(title);
            if (!string.IsNullOrEmpty(note))
            {
                var n = X.Txt(note, 12, false, "SubText");
                n.Margin = new Thickness(14, 0, 0, 0); n.TextWrapping = TextWrapping.NoWrap;
                p.Children.Add(n);
            }
            return p;
        }

        /// <summary>Галочка строки: отмеченное запоминается в marks.</summary>
        CheckBox MarkBox(object key, Mark mark)
        {
            var box = new CheckBox { IsChecked = marks.ContainsKey(key), VerticalAlignment = VerticalAlignment.Center };
            RoutedEventHandler changed = delegate
            {
                if (box.IsChecked == true) marks[key] = mark; else marks.Remove(key);
                UpdateState();
            };
            box.Checked += changed;
            box.Unchecked += changed;
            boxes.Add(box);
            return box;
        }

        TreeViewItem FolderItem(DiskNode n)
        {
            long parentBytes = n.Parent == null ? n.Bytes : n.Parent.Bytes;
            // корень карты отметить нельзя: удалять целиком то, что вы сами указали для анализа, незачем
            var box = n.Parent == null ? null : MarkBox(n, new Mark { Node = n });
            var item = new TreeViewItem
            {
                Tag = n,
                Header = Header(box, "", n.Name, n.Bytes, parentBytes, string.Format(L.T("файлов: {0}"), Util.FormatCount(n.Files)))
            };
            bool hasContent = (n.Children != null && n.Children.Count > 0) || n.OwnFiles > 0;
            if (hasContent)
            {
                item.Items.Add(new TreeViewItem { Header = "…" });      // заглушка: настоящее содержимое подгружается при раскрытии
                item.Expanded += (s, e) =>
                {
                    if (!ReferenceEquals(e.OriginalSource, item) || !filled.Add(item)) return;
                    Fill(item, n);
                };
            }
            return item;
        }

        void Fill(TreeViewItem item, DiskNode n)
        {
            item.Items.Clear();
            if (n.Children != null)
            {
                foreach (var c in n.Children.Take(MaxFolders)) item.Items.Add(FolderItem(c));
                if (n.Children.Count > MaxFolders)
                    item.Items.Add(new TreeViewItem
                    {
                        Header = Header(null, null, string.Format(L.T("… и ещё папок: {0}"), n.Children.Count - MaxFolders),
                            n.Children.Skip(MaxFolders).Sum(c => c.Bytes), n.Bytes, null)
                    });
            }
            List<DiskFile> files;
            try { files = DiskTree.FilesOf(n, MaxFiles); } catch { files = new List<DiskFile>(); }
            foreach (var f in files)
                item.Items.Add(f.MoreCount > 0
                    ? new TreeViewItem { Header = Header(null, null, string.Format(L.T("… и ещё файлов: {0}"), Util.FormatCount(f.MoreCount)), f.Bytes, n.Bytes, null) }
                    : new TreeViewItem { Tag = f, Header = Header(MarkBox(f, new Mark { File = f, FileFolder = n }), "", f.Name, f.Bytes, n.Bytes, null) });
        }

        void ShowTree()
        {
            filled.Clear();
            marks.Clear();
            boxes.Clear();
            tree.Items.Clear();
            if (root == null) { UpdateState(); return; }
            var top = FolderItem(root);
            tree.Items.Add(top);
            top.IsExpanded = true;
            UpdateState();
        }

        void UncheckAll()
        {
            marks.Clear();
            foreach (var b in boxes) b.IsChecked = false;
            UpdateState();
        }

        // ------------------------------------------------------------ выбранное

        string SelectedPath()
        {
            var it = tree.SelectedItem as TreeViewItem;
            if (it == null) return null;
            var n = it.Tag as DiskNode;
            if (n != null) return n.Path;
            var f = it.Tag as DiskFile;
            return f == null ? null : f.Path;
        }

        void OpenSelected()
        {
            var it = tree.SelectedItem as TreeViewItem;
            if (it == null) return;
            if (it.Tag is DiskNode) X.OpenFolder(((DiskNode)it.Tag).Path);
            else if (it.Tag is DiskFile) X.ShowInExplorer(((DiskFile)it.Tag).Path);
        }

        /// <summary>Отмеченное без вложенного: если отмечена папка, то отдельно отмеченные файлы и папки внутри неё уже учтены.</summary>
        List<Mark> TopMarks()
        {
            var folders = marks.Values.Where(m => m.Node != null).Select(m => m.Node.Path.TrimEnd('\\') + "\\").ToList();
            return marks.Values.Where(m =>
            {
                string p = m.Path;
                return !folders.Any(f => p.Length > f.Length && p.StartsWith(f, StringComparison.OrdinalIgnoreCase));
            }).OrderByDescending(m => m.Bytes).ToList();
        }

        protected override void UpdateState()
        {
            var top = TopMarks();
            string total = root == null ? "" : string.Format(L.T("{0}: {1}, файлов: {2}"), root.Name, Util.FormatSize(root.Bytes), Util.FormatCount(root.Files));
            Summary.Text = top.Count == 0 ? total : total + "     " + string.Format(L.T("Отмечено: {0} ({1})"), top.Count, Util.FormatSize(top.Sum(m => m.Bytes)));
            btnScan.IsEnabled = Root.IsEnabled = !Busy;
            btnOpen.IsEnabled = SelectedPath() != null;
            btnDelete.IsEnabled = !Busy && top.Count > 0;
            btnUncheck.IsEnabled = !Busy && marks.Count > 0;
            tree.IsEnabled = !Busy;
        }

        // ------------------------------------------------------------ построение

        protected override void StartScan()
        {
            string path = CheckedRoot();
            if (path == null || Busy) return;
            Settings.Set("DiskMapRoot", path);
            ClearLog();
            root = null;
            ShowTree();
            DiskNode built = null;
            Run(ct =>
            {
                var sw = Stopwatch.StartNew();
                built = DiskTree.Scan(path, ct, Status);
                Log(string.Format(L.T("{0}: {1}, файлов: {2}"), path, Util.FormatSize(built.Bytes), Util.FormatCount(built.Files)) +
                    string.Format(L.T(" — за {0:N1} с"), sw.Elapsed.TotalSeconds));
            }, () =>
            {
                root = built;
                ShowTree();
                Status(built == null ? "" : L.T("Раскрывайте папки стрелкой и отмечайте лишнее галочками. Правая кнопка — открыть в Проводнике или скопировать путь."));
            });
        }

        // ------------------------------------------------------------ удаление

        void DeleteMarked()
        {
            if (Busy) return;
            var sel = TopMarks();
            if (sel.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine(string.Format(L.T("Переместить в Корзину: {0} ({1})"), sel.Count, Util.FormatSize(sel.Sum(m => m.Bytes))));
            sb.AppendLine();
            foreach (var m in sel.Take(15)) sb.AppendLine("   • " + m.Path + "  —  " + Util.FormatSize(m.Bytes));
            if (sel.Count > 15) sb.AppendLine(L.T("   … и ещё ") + (sel.Count - 15));
            if (sel.Any(m => m.Node != null)) sb.AppendLine(L.T("\r\nОчень большие папки Windows может предложить удалить сразу, мимо Корзины."));
            if (!Ask(L.T("Подтверждение"), sb.ToString(), true)) return;

            var gone = new List<Mark>();
            string msg = "";
            Run(ct =>
            {
                long freed = 0; int failed = 0;
                Progress(0);
                for (int i = 0; i < sel.Count && !ct.IsCancellationRequested; i++)
                {
                    var m = sel[i];
                    string path = m.Path;
                    Status(L.T("Удаляю: ") + path);
                    Progress(i * 100 / sel.Count);
                    try
                    {
                        long locked;
                        bool ok = m.Node != null ? BigFileScanner.DeleteFolder(path, true, ct, out locked) : BigFileScanner.Delete(path, true);
                        if (ok) { gone.Add(m); freed += m.Bytes; Log("  - " + path + "  (" + Util.FormatSize(m.Bytes) + ")"); }
                        else { failed++; Log("  ! " + path + L.T(" — не удалён (занят?)")); }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { failed++; Log("  ! " + path + ": " + ex.Message); }
                }
                msg = L.T("Перемещено в Корзину") + ": " + Util.FormatSize(freed) + (failed > 0 ? L.T(", ошибок: ") + failed : "") +
                      (freed > 0 ? L.T(". Место освободится после очистки Корзины.") : "");
                Log(msg);
            }, () =>
            {
                foreach (var m in gone)
                {
                    if (m.Node != null) DiskTree.Remove(m.Node);
                    else DiskTree.RemoveFile(m.FileFolder, m.Bytes);
                }
                ShowTree();     // размеры родителей изменились — перерисовываем дерево
                Status(msg);
                Info(L.T("Готово"), msg);
            }, true);
        }
    }
}
