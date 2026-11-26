using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Danilovl.WinCleaner.Wpf
{
    /// <summary>Сортировка списка по столбцу: повторный щелчок меняет направление.</summary>
    class Sorter<T>
    {
        readonly Comparison<T>[] by;
        readonly HashSet<int> descFirst;
        public int Column;
        public bool Desc = true;

        /// <param name="by">сравнение для каждого столбца</param>
        /// <param name="descFirst">столбцы, которые при первом щелчке сортируются по убыванию (размеры, даты)</param>
        public Sorter(int column, Comparison<T>[] by, params int[] descFirst)
        {
            Column = column; this.by = by; this.descFirst = new HashSet<int>(descFirst);
        }

        public void Click(int column)
        {
            if (column >= by.Length || by[column] == null) return;
            if (Column == column) Desc = !Desc; else { Column = column; Desc = descFirst.Contains(column); }
        }

        public List<T> Apply(IEnumerable<T> items)
        {
            var list = items.ToList();
            var cmp = by[Column];
            if (Desc) list.Sort((a, b) => cmp(b, a)); else list.Sort(cmp);
            return list;
        }
    }

    /// <summary>Общая часть страниц «папка → поиск → список → удаление»: поле пути и кнопка «Обзор».</summary>
    abstract class FolderPage : PageBase
    {
        protected readonly TextBox Root = new TextBox { Width = 360 };
        protected readonly CheckList List = new CheckList(true);

        protected FolderPage(string title, string description, string defaultRoot) : base(title, description)
        {
            Root.Text = defaultRoot;
            Root.KeyDown += (s, e) => { if (e.Key == System.Windows.Input.Key.Enter && !Busy) StartScan(); };
            var label = X.Txt(L.T("Где искать:"));
            label.Margin = new Thickness(0, 0, 8, 8);
            Toolbar.Children.Add(label);
            Toolbar.Children.Add(Root);
            Toolbar.Children.Add(X.Btn(L.T("Обзор…"), () => { string p = X.PickFolder(Root.Text); if (p != null) Root.Text = p; }));
            Body.Child = List;
        }

        protected abstract void StartScan();

        /// <summary>Проверяет папку из поля ввода; null — папки нет (сообщение уже показано).</summary>
        protected string CheckedRoot()
        {
            string root = Root.Text.Trim();
            if (Directory.Exists(root)) return root;
            Info(L.T("Поиск"), L.T("Папка не найдена."));
            return null;
        }
    }

    // ==================================================================== Папки проектов

    class ProjectsPage : FolderPage
    {
        readonly Button btnScan, btnAll, btnOld, btnNone, btnDelete;
        List<ProjectJunk> items = new List<ProjectJunk>();
        readonly Sorter<ProjectJunk> sort = new Sorter<ProjectJunk>(3, new Comparison<ProjectJunk>[]
        {
            (a, b) => string.Compare(a.Project, b.Project, StringComparison.OrdinalIgnoreCase),
            (a, b) => string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase),
            (a, b) => string.Compare(a.Kind, b.Kind, StringComparison.OrdinalIgnoreCase),
            (a, b) => a.Bytes.CompareTo(b.Bytes),
            (a, b) => a.Files.CompareTo(b.Files),
            (a, b) => a.ProjectChanged.CompareTo(b.ProjectChanged)
        }, 3, 4);

        public ProjectsPage()
            : base(L.T("Папки проектов"),
                   L.T("Папки, которые инструменты сборки создают заново: node_modules, vendor, build, target, кэш Symfony и т.д. " +
                       "Папка попадает в список, только если рядом лежит файл проекта (package.json, composer.json, build.gradle …). " +
                       "Исходный код не затрагивается; после удаления выполните npm install / composer install / сборку."),
                   Settings.Get("ProjectsRoot", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            btnScan = X.Btn(L.T("Искать"), StartScan, "AccentButton");
            btnAll = X.Btn(L.T("Отметить все"), () => SetChecks(x => true));
            btnOld = X.Btn(L.T("Отметить проекты старше 3 мес."), () => SetChecks(x => x.ProjectChanged < DateTime.Now.AddMonths(-3) && !x.Kind.Contains("venv")));
            btnNone = X.Btn(L.T("Снять все"), () => SetChecks(x => false));
            btnDelete = X.Btn(L.T("Удалить отмеченные…"), StartDelete, "DangerButton");
            foreach (UIElement e in new UIElement[] { btnScan, StopButton, btnAll, btnOld, btnNone, btnDelete }) Toolbar.Children.Add(e);

            List.Col(L.T("Проект"), 400);
            List.Col(L.T("Папка"), 140);
            List.Col(L.T("Тип"), 220);
            List.Col(L.T("Размер"), 100, true);
            List.Col(L.T("Файлов"), 90, true);
            List.Col(L.T("Проект менялся"), 190);
            List.PathOf = r => ((ProjectJunk)r.Tag).Path;
            List.HeaderClicked += c => { sort.Click(c); Fill(); };
            Status(L.T("Укажите папку с проектами (например D:\\Projects) и нажмите «Искать»."));
        }

        protected override void UpdateState()
        {
            long selBytes = 0, total = 0; int selCount = 0;
            foreach (var i in items) { total += i.Bytes; if (i.Checked) { selCount++; selBytes += i.Bytes; } }
            Summary.Text = items.Count == 0 ? "" : string.Format(L.T("Найдено: {0} в {1} папках.     Отмечено: {2} ({3} папок)"),
                Util.FormatSize(total), items.Count, Util.FormatSize(selBytes), selCount);
            btnScan.IsEnabled = Root.IsEnabled = !Busy;
            btnAll.IsEnabled = btnOld.IsEnabled = btnNone.IsEnabled = !Busy && items.Count > 0;
            btnDelete.IsEnabled = !Busy && selCount > 0;
            List.IsEnabled = !Busy;
        }

        void SetChecks(Func<ProjectJunk, bool> rule)
        {
            foreach (var i in items) i.Checked = rule(i);
            Fill();
        }

        void Fill()
        {
            var rows = new List<Row>();
            foreach (var i in sort.Apply(items))
            {
                var r = new Row(i, i.Project, i.Label, i.Kind, Util.FormatSize(i.Bytes), Util.FormatCount(i.Files), X.DateWithAge(i.ProjectChanged)) { Tip = i.Path };
                r.SetCheckedSilently(i.Checked);
                r.CheckedChanged = x => { ((ProjectJunk)x.Tag).Checked = x.Checked; if (!Row.Bulk) UpdateState(); };
                rows.Add(r);
            }
            List.SetRows(rows);
            List.SortArrow(sort.Column, sort.Desc);
            UpdateState();
        }

        protected override void StartScan()
        {
            string root = CheckedRoot();
            if (root == null || Busy) return;
            Settings.Set("ProjectsRoot", root);
            ClearLog();
            items = new List<ProjectJunk>();
            Fill();
            var found = new List<ProjectJunk>();
            Run(ct => ProjectScanner.Scan(root, ct, found, Status, Progress, Log), () =>
            {
                items = found.Where(f => f.Bytes > 0).ToList();
                Fill();
                Status(items.Count == 0
                    ? L.T("Ничего не найдено. Проверьте, что выбрана папка с проектами.")
                    : L.T("Отметьте папки и нажмите «Удалить отмеченные…». Двойной клик — открыть папку, правая кнопка — меню."));
            });
        }

        void StartDelete()
        {
            var sel = items.Where(i => i.Checked).ToList();
            if (sel.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(L.T("Удалить {0} папок ({1}) БЕЗВОЗВРАТНО?"), sel.Count, Util.FormatSize(sel.Sum(i => i.Bytes))));
            sb.AppendLine(L.T("(Корзина не используется: в node_modules слишком длинные пути и сотни тысяч файлов.)"));
            sb.AppendLine();
            foreach (var g in sel.GroupBy(i => i.Kind).OrderByDescending(g => g.Sum(i => i.Bytes)))
                sb.AppendLine(string.Format(L.T("   • {0}: {1} шт., {2}"), g.Key, g.Count(), Util.FormatSize(g.Sum(i => i.Bytes))));
            sb.AppendLine();
            sb.AppendLine(L.T("Исходный код не затрагивается. Зависимости и сборка восстановятся командами npm install, composer install, gradle build и т.п."));
            if (!Ask(L.T("Подтверждение"), sb.ToString(), true)) return;

            var gone = new HashSet<ProjectJunk>();
            string msg = "";
            long failed = 0;
            Run(ct =>
            {
                long freed = 0;
                Progress(0);
                try
                {
                    for (int n = 0; n < sel.Count; n++)
                    {
                        var i = sel[n];
                        Status(string.Format(L.T("Удаляю {0}/{1}: {2}"), n + 1, sel.Count, i.Path));
                        Progress(n * 100 / sel.Count);
                        var st = new WalkStats();
                        try { FileWalker.Delete(i.Path, null, st, ct, false); }
                        finally
                        {
                            // учитываем и то, что успели удалить до нажатия «Стоп»
                            freed += st.Bytes; failed += st.FailedFiles;
                            i.Bytes = Math.Max(0, i.Bytes - st.Bytes);
                            i.Files = Math.Max(0, i.Files - st.Files);
                        }
                        if (FileWalker.RemoveDir(i.Path) || !Native.Exists(i.Path)) lock (gone) gone.Add(i);
                        Log(string.Format("  - {0}  ({1}){2}", i.Path, Util.FormatSize(st.Bytes), st.FailedFiles > 0 ? L.T(", занято файлов: ") + st.FailedFiles : ""));
                    }
                }
                finally
                {
                    Settings.AddFreed(freed);
                    msg = L.T("Освобождено: ") + Util.FormatSize(freed) + (failed > 0 ? L.T(", не удалось удалить файлов: ") + failed + L.T(" (заняты — закройте IDE / dev-серверы)") : "");
                    Log(msg);
                }
            }, () =>
            {
                items.RemoveAll(gone.Contains);
                Fill();
                Status(msg);
                Info(L.T("Готово"), msg);
            });
        }
    }

    // ==================================================================== Большие файлы

    class BigFilesPage : FolderPage
    {
        readonly TextBox minBox = new TextBox { Width = 70, Text = "300" };
        readonly TextBox ageBox = new TextBox { Width = 50, Text = "0", ToolTip = L.T("0 — любые. Иначе только файлы, которые появились и менялись раньше, чем столько месяцев назад") };
        readonly CheckBox chkRecycle;
        readonly Button btnScan, btnDelete;
        List<BigFile> items = new List<BigFile>();
        readonly Sorter<BigFile> sort = new Sorter<BigFile>(2, new Comparison<BigFile>[]
        {
            (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            (a, b) => string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase),
            (a, b) => a.Bytes.CompareTo(b.Bytes),
            (a, b) => a.Modified.CompareTo(b.Modified)
        }, 2, 3);

        public BigFilesPage()
            : base(L.T("Большие файлы"),
                   L.T("Самые крупные файлы: образы ISO, старые установщики, архивы, видео, дистрибутивы. " +
                       "Системные файлы (подкачка, гибернация) и папка Windows не показываются. Диски .vhdx сжимайте на вкладке «Docker и WSL», а не удаляйте."),
                   Settings.Get("BigFilesRoot", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            var label = X.Txt(L.T("больше, МБ:"));
            label.Margin = new Thickness(4, 0, 8, 8);
            btnScan = X.Btn(L.T("Искать"), StartScan, "AccentButton");
            chkRecycle = X.Chk(L.T("Удалять в Корзину"), true);
            btnDelete = X.Btn(L.T("Удалить отмеченные…"), StartDelete, "DangerButton");
            var ageLabel = X.Txt(L.T("не трогали, мес.:"));
            ageLabel.Margin = new Thickness(4, 0, 8, 8);
            var btnDownloads = X.Btn(L.T("Старые загрузки"), () =>
            {
                // типичный мусор: то, что давно лежит в «Загрузках»
                Root.Text = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                minBox.Text = "10"; ageBox.Text = "6";
                StartScan();
            });
            btnDownloads.ToolTip = L.T("Найти в папке «Загрузки» файлы от 10 МБ, которые не трогали полгода");
            foreach (UIElement e in new UIElement[] { label, minBox, ageLabel, ageBox, btnScan, StopButton, btnDownloads, chkRecycle, btnDelete }) Toolbar.Children.Add(e);

            List.Col(L.T("Файл"), 340);
            List.Col(L.T("Папка"), 520);
            List.Col(L.T("Размер"), 110, true);
            List.Col(L.T("Изменён"), 190);
            List.PathOf = r => ((BigFile)r.Tag).Path;
            List.PathIsFile = true;
            List.HeaderClicked += c => { sort.Click(c); Fill(); };
            Status(L.T(@"Укажите папку (например C:\ для всего диска) и нажмите «Искать»."));
        }

        long MinMb()
        {
            long v;
            if (!long.TryParse(minBox.Text.Trim(), out v)) v = 300;
            return Math.Max(1, Math.Min(100000, v));
        }

        int AgeMonths()
        {
            int v;
            if (!int.TryParse(ageBox.Text.Trim(), out v)) v = 0;
            return Math.Max(0, Math.Min(600, v));
        }

        protected override void UpdateState()
        {
            long selBytes = 0, total = 0; int selCount = 0;
            foreach (var i in items) { total += i.Bytes; if (i.Checked) { selCount++; selBytes += i.Bytes; } }
            Summary.Text = items.Count == 0 ? "" : string.Format(L.T("Найдено файлов: {0} ({1}).     Отмечено: {2} ({3})"),
                items.Count, Util.FormatSize(total), selCount, Util.FormatSize(selBytes));
            btnScan.IsEnabled = Root.IsEnabled = minBox.IsEnabled = ageBox.IsEnabled = chkRecycle.IsEnabled = !Busy;
            btnDelete.IsEnabled = !Busy && selCount > 0;
            List.IsEnabled = !Busy;
        }

        void Fill()
        {
            var rows = new List<Row>();
            foreach (var f in sort.Apply(items))
            {
                var r = new Row(f, f.Name, f.Folder, Util.FormatSize(f.Bytes), X.DateWithAge(f.Modified)) { Tip = f.Path };
                r.SetCheckedSilently(f.Checked);
                r.CheckedChanged = x => { ((BigFile)x.Tag).Checked = x.Checked; if (!Row.Bulk) UpdateState(); };
                rows.Add(r);
            }
            List.SetRows(rows);
            List.SortArrow(sort.Column, sort.Desc);
            UpdateState();
        }

        protected override void StartScan()
        {
            string root = CheckedRoot();
            if (root == null || Busy) return;
            long mb = MinMb();
            int months = AgeMonths();
            ageBox.Text = months.ToString();
            DateTime olderThan = months > 0 ? DateTime.Now.AddMonths(-months) : DateTime.MinValue;
            minBox.Text = mb.ToString();
            Settings.Set("BigFilesRoot", root);
            ClearLog();
            items = new List<BigFile>();
            Fill();
            var found = new List<BigFile>();
            Run(ct =>
            {
                var sw = Stopwatch.StartNew();
                long scanned = BigFileScanner.Scan(root, mb << 20, olderThan, ct, found, Status);
                Log(string.Format(L.T("Просмотрено файлов: {0} за {1:N1} с, крупных: {2} ({3})"), Util.FormatCount(scanned),
                    sw.Elapsed.TotalSeconds, found.Count, Util.FormatSize(found.Sum(f => f.Bytes))));
            }, () =>
            {
                items = found;
                Fill();
                Status(found.Count == 0
                    ? L.T("Файлов крупнее ") + mb + L.T(" МБ не найдено. Уменьшите порог или выберите другую папку.")
                    : L.T("Отметьте файлы и нажмите «Удалить отмеченные…». Двойной клик — показать в папке, правая кнопка — меню."));
            });
        }

        void StartDelete()
        {
            var sel = items.Where(i => i.Checked).ToList();
            if (sel.Count == 0) return;
            bool recycle = chkRecycle.On();
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(L.T("{0} {1} файлов ({2}):"), recycle ? L.T("Переместить в Корзину") : L.T("Удалить БЕЗВОЗВРАТНО"), sel.Count, Util.FormatSize(sel.Sum(i => i.Bytes))));
            foreach (var f in sel.OrderByDescending(x => x.Bytes).Take(20)) sb.AppendLine("   • " + f.Name + "  —  " + Util.FormatSize(f.Bytes));
            if (sel.Count > 20) sb.AppendLine(L.T("   … и ещё ") + (sel.Count - 20));
            if (sel.Any(f => f.Name.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) || f.Name.EndsWith(".vmdk", StringComparison.OrdinalIgnoreCase)))
                sb.AppendLine(L.T("\r\nВНИМАНИЕ: среди них есть диски виртуальных машин — вместе с ними пропадут все данные ВМ / WSL / Docker!"));
            if (!Ask(L.T("Подтверждение"), sb.ToString(), !recycle)) return;

            var gone = new HashSet<BigFile>();
            string msg = "";
            Run(ct =>
            {
                long freed = 0; int failed = 0;
                Progress(0);
                for (int n = 0; n < sel.Count && !ct.IsCancellationRequested; n++)
                {
                    var f = sel[n];
                    Status(L.T("Удаляю: ") + f.Name);
                    Progress(n * 100 / sel.Count);
                    try
                    {
                        if (BigFileScanner.Delete(f.Path, recycle)) { freed += f.Bytes; gone.Add(f); Log("  - " + f.Path); }
                        else { failed++; Log("  ! " + f.Path + L.T(" — не удалён (занят?)")); }
                    }
                    catch (Exception ex) { failed++; Log("  ! " + f.Path + ": " + ex.Message); }
                }
                msg = string.Format("{0}: {1}{2}", recycle ? L.T("Перемещено в Корзину") : L.T("Удалено"), Util.FormatSize(freed),
                    failed > 0 ? L.T(", ошибок: ") + failed : "") + (recycle && freed > 0 ? L.T(". Место освободится после очистки Корзины.") : "");
                Log(msg);
            }, () =>
            {
                items.RemoveAll(gone.Contains);
                Fill();
                Status(msg);
                Info(L.T("Готово"), msg);
            }, true);
        }
    }

    // ==================================================================== Анализ AppData

    class AppDataPage : PageBase
    {
        readonly CheckList list = new CheckList(true);
        readonly Button btnScan, btnDelete;
        readonly CheckBox chkSystem, chkRecycle;
        readonly WrapPanel filter;
        List<AppFolder> all = new List<AppFolder>();
        readonly Sorter<AppFolder> sort = new Sorter<AppFolder>(2, new Comparison<AppFolder>[]
        {
            (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            (a, b) => string.Compare(a.Where, b.Where, StringComparison.Ordinal),
            (a, b) => a.Bytes.CompareTo(b.Bytes),
            (a, b) => a.Files.CompareTo(b.Files),
            (a, b) => a.Newest.CompareTo(b.Newest),
            (a, b) => ((int)a.Kind).CompareTo((int)b.Kind)
        }, 2, 3);

        public AppDataPage()
            : base(L.T("Анализ AppData"),
                   L.T("Папки программ в AppData и ProgramData. Оранжевым — папки, для которых не нашлось установленной программы: " +
                       "часто это остатки давно удалённых приложений. Проверка приблизительная — смотрите на название и дату. " +
                       "По умолчанию удаление идёт в Корзину, чтобы можно было вернуть."))
        {
            btnScan = X.Btn(L.T("Сканировать"), StartScan, "AccentButton");
            btnDelete = X.Btn(L.T("Удалить отмеченные…"), StartDelete, "DangerButton");
            filter = X.Chips(new[] { L.T("Все папки"), L.T("Только возможные остатки"), L.T("Не менялись больше года") }, 0, i => ApplyView());
            chkSystem = X.Chk(L.T("Показывать системные"), false, ApplyView);
            chkRecycle = X.Chk(L.T("Удалять в Корзину"), true);
            foreach (UIElement e in new UIElement[] { btnScan, StopButton, btnDelete, filter, chkSystem, chkRecycle }) Toolbar.Children.Add(e);

            list.Col(L.T("Папка"), 280);
            list.Col(L.T("Где"), 110);
            list.Col(L.T("Размер"), 100, true);
            list.Col(L.T("Файлов"), 90, true);
            list.Col(L.T("Последнее изменение"), 190);
            list.Col(L.T("Состояние"), 420);
            list.PathOf = r => ((AppFolder)r.Tag).Path;
            list.HeaderClicked += c => { sort.Click(c); ApplyView(); };
            Body.Child = list;
            Status(L.T("Нажмите «Сканировать». Подсчёт размеров может занять минуту."));
        }

        static bool Locked(AppFolder f) { return f.Kind == FolderKind.System || f.Kind == FolderKind.Running; }

        protected override void UpdateState()
        {
            var sel = all.Where(f => f.Checked).ToList();
            long orphan = all.Where(f => f.Kind == FolderKind.Orphan).Sum(f => f.Bytes);
            Summary.Text = all.Count == 0 ? "" : string.Format(
                L.T("Всего в папках программ: {0}     Возможные остатки: {1}     Отмечено: {2} ({3} папок)"),
                Util.FormatSize(all.Sum(f => f.Bytes)), Util.FormatSize(orphan), Util.FormatSize(sel.Sum(f => f.Bytes)), sel.Count);
            btnScan.IsEnabled = filter.IsEnabled = chkSystem.IsEnabled = chkRecycle.IsEnabled = !Busy;
            btnDelete.IsEnabled = !Busy && sel.Count > 0;
            list.IsEnabled = !Busy;
        }

        void ApplyView()
        {
            IEnumerable<AppFolder> src = all;
            int mode = X.SelectedChip(filter);
            if (!chkSystem.On()) src = src.Where(f => f.Kind != FolderKind.System);
            if (mode == 1) src = src.Where(f => f.Kind == FolderKind.Orphan);
            else if (mode == 2) src = src.Where(f => f.Newest != DateTime.MinValue && f.Newest < DateTime.Now.AddYears(-1));

            var rows = new List<Row>();
            foreach (var f in sort.Apply(src))
            {
                var r = new Row(f, f.Name, f.Where, Util.FormatSize(f.Bytes), Util.FormatCount(f.Files), X.DateWithAge(f.Newest), f.KindText)
                    { Tip = f.Path, CanCheck = !Locked(f) };
                if (f.Kind == FolderKind.System || f.Kind == FolderKind.Driver) r.ColorKey = "SubText";
                else if (f.Kind == FolderKind.Running) r.ColorKey = "Accent";
                else if (f.Kind == FolderKind.Orphan) r.ColorKey = "Warn";
                r.SetCheckedSilently(f.Checked);
                r.CheckedChanged = x => { ((AppFolder)x.Tag).Checked = x.Checked; if (!Row.Bulk) UpdateState(); };
                rows.Add(r);
            }
            list.SetRows(rows);
            list.SortArrow(sort.Column, sort.Desc);
            UpdateState();
        }

        void StartScan()
        {
            if (Busy) return;
            ClearLog();
            var result = new List<AppFolder>();
            Run(ct => { Progress(0); AppDataScanner.Scan(ct, result, Status, Progress, Log); }, () =>
            {
                all = result;
                ApplyView();
                Status(L.T("Отметьте ненужные папки и нажмите «Удалить отмеченные…». Двойной клик — открыть папку."));
            });
        }

        void StartDelete()
        {
            var sel = all.Where(f => f.Checked && !Locked(f)).ToList();
            if (sel.Count == 0) return;
            bool recycle = chkRecycle.On();

            var sb = new StringBuilder();
            sb.AppendLine(recycle ? L.T("Переместить в Корзину:") : L.T("Удалить БЕЗВОЗВРАТНО:"));
            foreach (var f in sel.Take(25)) sb.AppendLine(string.Format("   • {0}\\{1}  —  {2}", f.Where, f.Name, Util.FormatSize(f.Bytes)));
            if (sel.Count > 25) sb.AppendLine(L.T("   … и ещё ") + (sel.Count - 25));
            sb.AppendLine();
            sb.AppendLine(L.T("Всего: ") + Util.FormatSize(sel.Sum(f => f.Bytes)));
            var installed = sel.Where(f => f.Kind == FolderKind.Installed || f.Kind == FolderKind.Driver).ToList();
            if (installed.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("ВНИМАНИЕ: ") + installed.Count + L.T(" из них принадлежат установленным программам или драйверам — " +
                              "эти программы потеряют настройки или перестанут работать."));
            }
            if (recycle) sb.AppendLine(L.T("\r\nОчень большие папки Windows может предложить удалить сразу, мимо Корзины."));
            if (!Ask(L.T("Подтверждение"), sb.ToString(), installed.Count > 0 || !recycle)) return;

            var gone = new HashSet<AppFolder>();
            string msg = "";
            int failed = 0;
            Run(ct =>
            {
                long freed = 0; int ok = 0;
                Progress(0);
                for (int i = 0; i < sel.Count && !ct.IsCancellationRequested; i++)
                {
                    var f = sel[i];
                    Status(L.T("Удаляю: ") + f.Name);
                    Progress(i * 100 / sel.Count);
                    try
                    {
                        long locked;
                        if (BigFileScanner.DeleteFolder(f.Path, recycle, ct, out locked))
                        {
                            ok++; freed += f.Bytes; gone.Add(f);
                            Log("  - " + f.Where + "\\" + f.Name + "  (" + Util.FormatSize(f.Bytes) + ")");
                        }
                        else { failed++; Log("  ! " + f.Name + L.T(" — удалена не полностью (файлы заняты?)")); }
                    }
                    catch (OperationCanceledException) { break; }
                    catch (Exception ex) { failed++; Log("  ! " + f.Name + ": " + ex.Message); }
                }
                msg = string.Format(L.T("Удалено папок: {0} ({1}){2}"), ok, Util.FormatSize(freed),
                    failed > 0 ? L.T(", с ошибками: ") + failed : "") + (recycle && ok > 0 ? L.T(". Место освободится после очистки Корзины.") : "");
                Log(msg);
            }, () =>
            {
                all.RemoveAll(gone.Contains);
                foreach (var f in all) f.Checked = false;
                ApplyView();
                Status(msg);
                Info(L.T("Готово"), msg);
            }, true);
        }
    }
}
