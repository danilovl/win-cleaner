using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Danilovl.WinCleaner.Wpf
{
    // ==================================================================== Дубликаты

    /// <summary>Страница «Дубликаты»: файлы с одинаковым содержимым.</summary>
    class DuplicatesPage : FolderPage
    {
        readonly TextBox minBox = new TextBox { Width = 70, Text = "1" };
        readonly Button btnScan, btnKeepOldest, btnKeepNewest, btnNone, btnDelete;
        readonly CheckBox chkRecycle;
        List<DupGroup> groups = new List<DupGroup>();

        public DuplicatesPage()
            : base(L.T("Дубликаты"),
                   L.T("Файлы с полностью одинаковым содержимым (сравнивается содержимое, а не имена). В каждой группе оставьте хотя бы один файл. " +
                       "Не удаляйте дубликаты внутри папок программ и проектов — там одинаковые файлы часто нужны в каждой папке."),
                   Settings.Get("DuplicatesRoot", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))
        {
            var label = X.Txt(L.T("не меньше, МБ:"));
            label.Margin = new Thickness(4, 0, 8, 8);
            btnScan = X.Btn(L.T("Искать"), StartScan, "AccentButton");
            btnKeepOldest = X.Btn(L.T("Отметить всё, кроме самого старого"), () => Mark(g => g.Files.Skip(1)));
            btnKeepNewest = X.Btn(L.T("Отметить всё, кроме самого нового"), () => Mark(g => g.Files.Take(g.Files.Count - 1)));
            btnNone = X.Btn(L.T("Снять все"), () => Mark(g => Enumerable.Empty<DupFile>()));
            chkRecycle = X.Chk(L.T("Удалять в Корзину"), true);
            btnDelete = X.Btn(L.T("Удалить отмеченные…"), StartDelete, "DangerButton");
            foreach (UIElement e in new UIElement[] { label, minBox, btnScan, StopButton, btnKeepOldest, btnKeepNewest, btnNone, chkRecycle, btnDelete })
                Toolbar.Children.Add(e);

            List.Col(L.T("Файл"), 340);
            List.Col(L.T("Папка"), 560);
            List.Col(L.T("Размер"), 110, true);
            List.Col(L.T("Изменён"), 190);
            List.PathOf = r => ((DupFile)r.Tag).Path;
            List.PathIsFile = true;
            Status(L.T("Укажите папку и нажмите «Искать». Большие папки сравниваются долго: читается содержимое файлов."));
        }

        IEnumerable<DupFile> AllFiles() { return groups.SelectMany(g => g.Files); }

        protected override void UpdateState()
        {
            var sel = AllFiles().Where(f => f.Checked).ToList();
            Summary.Text = groups.Count == 0 ? "" : string.Format(L.T("Групп: {0}, лишних копий на {1}.     Отмечено: {2} ({3})"),
                groups.Count, Util.FormatSize(groups.Sum(g => g.Wasted)), sel.Count, Util.FormatSize(sel.Sum(f => f.Bytes)));
            btnScan.IsEnabled = Root.IsEnabled = minBox.IsEnabled = chkRecycle.IsEnabled = !Busy;
            btnKeepOldest.IsEnabled = btnKeepNewest.IsEnabled = btnNone.IsEnabled = !Busy && groups.Count > 0;
            btnDelete.IsEnabled = !Busy && sel.Count > 0;
            List.IsEnabled = !Busy;
        }

        void Mark(Func<DupGroup, IEnumerable<DupFile>> pick)
        {
            foreach (var g in groups)
            {
                var chosen = new HashSet<DupFile>(pick(g));
                foreach (var f in g.Files) f.Checked = chosen.Contains(f);
            }
            Fill();
        }

        void Fill()
        {
            var rows = new List<Row>();
            foreach (var g in groups)
            {
                string header = string.Format(L.T("{0} × {1}   —   лишнее: {2}"), g.Files.Count, Util.FormatSize(g.Bytes), Util.FormatSize(g.Wasted));
                // у одинаковых по размеру групп заголовки совпали бы и слились — добавляем имя первого файла
                header += "   ·   " + g.Files[0].Name;
                foreach (var f in g.Files)
                {
                    var r = new Row(f, f.Name, f.Folder, Util.FormatSize(f.Bytes), X.DateWithAge(f.Modified)) { Tip = f.Path, Group = header };
                    r.SetCheckedSilently(f.Checked);
                    r.CheckedChanged = x => { ((DupFile)x.Tag).Checked = x.Checked; if (!Row.Bulk) UpdateState(); };
                    rows.Add(r);
                }
            }
            List.SetRows(rows, true);
            UpdateState();
        }

        protected override void StartScan()
        {
            string root = CheckedRoot();
            if (root == null || Busy) return;
            long mb;
            if (!long.TryParse(minBox.Text.Trim(), out mb) || mb < 1) mb = 1;
            minBox.Text = mb.ToString();
            Settings.Set("DuplicatesRoot", root);
            ClearLog();
            groups = new List<DupGroup>();
            Fill();
            List<DupGroup> found = null;
            Run(ct =>
            {
                var sw = Stopwatch.StartNew();
                found = DuplicateFinder.Scan(root, mb << 20, ct, Status, Progress);
                Log(string.Format(L.T("Групп: {0}, лишних копий на {1} — за {2:N1} с"), found.Count, Util.FormatSize(found.Sum(g => g.Wasted)), sw.Elapsed.TotalSeconds));
            }, () =>
            {
                if (found != null) groups = found.Take(5000).ToList();     // тысячи групп в одном списке уже не разобрать
                Fill();
                Status(groups.Count == 0
                    ? L.T("Дубликатов не найдено.")
                    : L.T("Отметьте лишние копии и нажмите «Удалить отмеченные…». В каждой группе файлы идут от старого к новому."));
            });
        }

        void StartDelete()
        {
            var sel = AllFiles().Where(f => f.Checked).ToList();
            if (sel.Count == 0) return;
            bool recycle = chkRecycle.On();
            int emptied = groups.Count(g => g.Files.All(f => f.Checked));

            var sb = new StringBuilder();
            sb.AppendLine(string.Format(L.T("{0} {1} файлов ({2}):"), recycle ? L.T("Переместить в Корзину") : L.T("Удалить БЕЗВОЗВРАТНО"), sel.Count, Util.FormatSize(sel.Sum(f => f.Bytes))));
            foreach (var f in sel.OrderByDescending(x => x.Bytes).Take(15)) sb.AppendLine("   • " + f.Path);
            if (sel.Count > 15) sb.AppendLine(L.T("   … и ещё ") + (sel.Count - 15));
            if (emptied > 0)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format(L.T("ВНИМАНИЕ: в {0} группах отмечены ВСЕ копии — не останется ни одной."), emptied));
            }
            if (!Ask(L.T("Подтверждение"), sb.ToString(), !recycle || emptied > 0)) return;

            var gone = new HashSet<DupFile>();
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
                if (!recycle) Settings.AddFreed(freed);
                msg = string.Format("{0}: {1}{2}", recycle ? L.T("Перемещено в Корзину") : L.T("Удалено"), Util.FormatSize(freed),
                    failed > 0 ? L.T(", ошибок: ") + failed : "") + (recycle && freed > 0 ? L.T(". Место освободится после очистки Корзины.") : "");
                Log(msg);
            }, () =>
            {
                foreach (var g in groups) g.Files.RemoveAll(gone.Contains);
                groups.RemoveAll(g => g.Files.Count < 2);       // одна оставшаяся копия — уже не дубликат
                Fill();
                Status(msg);
                Info(L.T("Готово"), msg);
            }, true);
        }
    }
}
