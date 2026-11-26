using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Danilovl.WinCleaner.Wpf
{
    // ==================================================================== Установленные программы

    /// <summary>
    /// Страница «Установленные программы»: что записано в реестре как установленное, есть ли это на самом деле,
    /// сколько занимает; удаление штатным деинсталлятором или — для устаревших записей — удаление самой записи.
    /// </summary>
    class AppsPage : PageBase
    {
        readonly CheckList list = new CheckList(false);
        readonly Button btnRefresh, btnUninstall, btnForget, btnOpen;
        readonly WrapPanel filter;
        List<InstalledApp> apps = new List<InstalledApp>();
        readonly Sorter<InstalledApp> sort = new Sorter<InstalledApp>(3, new Comparison<InstalledApp>[]
        {
            (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
            (a, b) => string.Compare(a.Publisher, b.Publisher, StringComparison.CurrentCultureIgnoreCase),
            (a, b) => string.Compare(a.Version, b.Version, StringComparison.OrdinalIgnoreCase),
            (a, b) => a.Bytes.CompareTo(b.Bytes),
            (a, b) => a.Installed.CompareTo(b.Installed),
            (a, b) => a.Stale.CompareTo(b.Stale)
        }, 3, 4, 5);

        public AppsPage()
            : base(L.T("Установленные программы"),
                   L.T("Список берётся из реестра, но каждая запись проверяется: есть ли программа на самом деле. Оранжевые строки — устаревшие записи: " +
                       "программы уже нет, а запись осталась. Размер — реальный размер папки программы; со знаком ≈ — оценка установщика, которая бывает неточной. " +
                       "Приложения из Microsoft Store здесь не показываются."))
        {
            btnRefresh = X.Btn(L.T("Обновить"), Reload, "AccentButton");
            btnUninstall = X.Btn(L.T("Удалить программу…"), Uninstall, "DangerButton");
            btnForget = X.Btn(L.T("Убрать устаревшую запись…"), Forget);
            btnForget.ToolTip = L.T("Удалить из реестра запись о программе, которой уже нет (с резервной копией)");
            btnOpen = X.Btn(L.T("Открыть папку"), () => { var a = Selected(); if (a != null && Directory.Exists(a.Location)) X.OpenFolder(a.Location); });
            filter = X.Chips(new[] { L.T("Все"), L.T("Только устаревшие записи") }, 0, i => Fill());
            foreach (UIElement e in new UIElement[] { btnRefresh, StopButton, btnUninstall, btnForget, btnOpen, filter }) Toolbar.Children.Add(e);

            list.Col(L.T("Программа"), 360);
            list.Col(L.T("Издатель"), 220);
            list.Col(L.T("Версия"), 120);
            list.Col(L.T("Размер"), 110, true);
            list.Col(L.T("Установлена"), 180);
            list.Col(L.T("Состояние"), 420);
            list.PathOf = r => { string p = ((InstalledApp)r.Tag).Location; return p.Length > 0 && Directory.Exists(p) ? p : null; };
            list.HeaderClicked += c => { sort.Click(c); Fill(); };
            list.SelectionChanged += delegate { UpdateState(); };
            list.SelectionMode = SelectionMode.Single;
            Body.Child = list;
        }

        protected override void OnFirstShown() { Reload(); }

        InstalledApp Selected() { var r = list.SelectedItem as Row; return r == null ? null : (InstalledApp)r.Tag; }

        protected override void UpdateState()
        {
            int stale = apps.Count(a => a.Stale);
            Summary.Text = apps.Count == 0 ? "" : string.Format(L.T("Записей: {0}, из них устаревших: {1}.     Занято программами: {2}"),
                apps.Count, stale, Util.FormatSize(apps.Where(a => !a.Stale).Sum(a => a.Bytes)));
            btnRefresh.IsEnabled = filter.IsEnabled = !Busy;
            var a1 = Selected();
            btnUninstall.IsEnabled = !Busy && a1 != null && !a1.Stale;
            btnForget.IsEnabled = !Busy && a1 != null && a1.Stale;
            btnOpen.IsEnabled = a1 != null && a1.Location.Length > 0 && Directory.Exists(a1.Location);
        }

        static string SizeText(InstalledApp a)
        {
            if (a.Bytes <= 0) return "—";
            return (a.SizeIsEstimate ? "≈ " : "") + Util.FormatSize(a.Bytes);
        }

        void Fill()
        {
            bool onlyStale = X.SelectedChip(filter) == 1;
            var rows = new List<Row>();
            foreach (var a in sort.Apply(apps.Where(x => !onlyStale || x.Stale)))
            {
                string state = a.Stale ? L.T("Запись устарела — ") + a.StaleReason : L.T("Установлена");
                var r = new Row(a, a.Name, a.Publisher, a.Version, SizeText(a), X.DateWithAge(a.Installed), state)
                    { Tip = a.Stale ? a.StaleReason : (a.Location.Length > 0 ? a.Location : a.Name) };
                if (a.Stale) r.ColorKey = "Warn";
                rows.Add(r);
            }
            list.SetRows(rows);
            list.SortArrow(sort.Column, sort.Desc);
            UpdateState();
        }

        void Reload()
        {
            if (Busy) return;
            List<InstalledApp> found = null;
            Status(L.T("Читаю список установленных программ…"));
            Run(ct => found = InstalledApps.List(ct, Status), () =>
            {
                if (found != null) apps = found;
                Fill();
                int stale = apps.Count(a => a.Stale);
                Status(stale > 0
                    ? string.Format(L.T("Найдено устаревших записей: {0}. Их можно убрать здесь по одной или все сразу на странице «Реестр»."), stale)
                    : L.T("Клик по заголовку — сортировка. Выберите программу и нажмите «Удалить программу…»."));
            });
        }

        void Uninstall()
        {
            var a = Selected();
            if (a == null || a.Stale) return;
            if (!Ask(L.T("Удаление программы"), string.Format(L.T("Запустить удаление программы «{0}»?\r\n\r\nОткроется её собственный деинсталлятор. " +
                    "После его завершения нажмите «Обновить»."), a.Name), true))
                return;
            try { InstalledApps.Uninstall(a); Log(L.T("Запущено удаление: ") + a.Name + "   (" + a.UninstallCommand + ")"); }
            catch (Exception ex) { Log(L.T("ОШИБКА: ") + ex.Message); Info(L.T("Ошибка"), ex.Message); }
        }

        void Forget()
        {
            var a = Selected();
            if (a == null || !a.Stale) return;
            if (!Ask(L.T("Устаревшая запись"), string.Format(L.T("Убрать из реестра запись о программе «{0}»?\r\n\r\nПричина: {1}\r\n\r\n" +
                    "Удаляется только запись в списке установленных программ; файлы не затрагиваются. Раздел реестра сначала сохраняется в .reg-файл."),
                    a.Name, a.StaleReason), true))
                return;
            string backup;
            int done = RegistryCleaner.Fix(new[] { InstalledApps.AsIssue(a) }, Log, out backup);
            if (done > 0) { apps.Remove(a); Fill(); Status(L.T("Запись удалена. Резервная копия: ") + backup); }
            else Info(L.T("Ошибка"), L.T("Запись удалить не удалось — подробности в журнале."));
        }
    }

    // ==================================================================== Реестр

    /// <summary>Страница «Реестр»: записи, которые ссылаются на несуществующие файлы и программы.</summary>
    class RegistryPage : PageBase
    {
        readonly CheckList list = new CheckList(true);
        readonly Button btnScan, btnFix, btnRegedit, btnBackups;
        List<RegIssue> issues = new List<RegIssue>();

        public RegistryPage()
            : base(L.T("Реестр"),
                   L.T("Записи реестра, которые указывают на несуществующие файлы: программы, которых уже нет в списке установленных, " +
                       "автозагрузка без программы, пути приложений, общие библиотеки, кэш названий. Места это почти не освобождает — зато убирает " +
                       "«призраков» из списков Windows. Перед удалением затронутые разделы сохраняются в .reg-файлы: двойной щелчок по файлу возвращает всё назад."))
        {
            btnScan = X.Btn(L.T("Проверить реестр"), StartScan, "AccentButton");
            btnFix = X.Btn(L.T("Удалить отмеченные записи…"), StartFix, "DangerButton");
            btnRegedit = X.Btn(L.T("Открыть в редакторе реестра"), () =>
            {
                var r = list.SelectedItem as Row;
                if (r != null) RegistryCleaner.OpenInRegedit(((RegIssue)r.Tag).FullKey);
            });
            btnBackups = X.Btn(L.T("Папка резервных копий"), () =>
            {
                Directory.CreateDirectory(RegistryCleaner.BackupFolder);
                X.OpenFolder(RegistryCleaner.BackupFolder);
            });
            foreach (UIElement e in new UIElement[] { btnScan, StopButton, btnFix, btnRegedit, btnBackups }) Toolbar.Children.Add(e);

            list.Col(L.T("Запись"), 320);
            list.Col(L.T("Чего не хватает"), 520);
            list.Col(L.T("Раздел реестра"), 620);
            list.SelectionChanged += delegate { UpdateState(); };
            Body.Child = list;
            Status(L.T("Нажмите «Проверить реестр». Проверка только читает реестр и ничего не меняет."));
        }

        protected override void UpdateState()
        {
            int sel = issues.Count(i => i.Checked);
            Summary.Text = issues.Count == 0 ? "" : string.Format(L.T("Найдено устаревших записей: {0}.     Отмечено: {1}"), issues.Count, sel);
            btnScan.IsEnabled = !Busy;
            btnFix.IsEnabled = !Busy && sel > 0;
            btnRegedit.IsEnabled = list.SelectedItem != null;
            list.IsEnabled = !Busy;
        }

        void Fill()
        {
            var rows = new List<Row>();
            foreach (var g in issues.GroupBy(i => i.Kind))
            {
                string header = g.Key + "   ·   " + g.Count();
                foreach (var i in g)
                {
                    var r = new Row(i, i.Name, i.Missing, i.FullKey + (i.ValueName != null ? "  →  " + i.ValueName : "")) { Group = header, Tip = i.Missing };
                    r.SetCheckedSilently(i.Checked);
                    r.CheckedChanged = x => { ((RegIssue)x.Tag).Checked = x.Checked; if (!Row.Bulk) UpdateState(); };
                    rows.Add(r);
                }
            }
            list.SetRows(rows, true);
            UpdateState();
        }

        void StartScan()
        {
            if (Busy) return;
            ClearLog();
            issues = new List<RegIssue>();
            Fill();
            List<RegIssue> found = null;
            Run(ct =>
            {
                found = RegistryCleaner.Scan(ct, Status);
                foreach (var g in found.GroupBy(i => i.Kind)) Log(g.Key + ": " + g.Count());
            }, () =>
            {
                if (found != null) issues = found;
                Fill();
                Status(issues.Count == 0
                    ? L.T("Устаревших записей не найдено.")
                    : L.T("Отмеченное безопасно удалить. Записи об удалённых программах не отмечены — просмотрите их и отметьте сами."));
            });
        }

        void StartFix()
        {
            var sel = issues.Where(i => i.Checked).ToList();
            if (sel.Count == 0) return;
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(L.T("Удалить из реестра записей: {0}"), sel.Count));
            sb.AppendLine();
            foreach (var g in sel.GroupBy(i => i.Kind)) sb.AppendLine(string.Format("   • {0}: {1}", g.Key, g.Count()));
            sb.AppendLine();
            sb.AppendLine(L.T("Файлы на диске не затрагиваются. Затронутые разделы сначала сохраняются в .reg-файлы в папке резервных копий."));
            if (!Ask(L.T("Реестр"), sb.ToString(), true)) return;

            string backup = "";
            int done = 0;
            Run(ct => done = RegistryCleaner.Fix(sel, Log, out backup), () =>
            {
                // повторная проверка показывает, что осталось на самом деле
                string msg = string.Format(L.T("Удалено записей: {0} из {1}. Резервная копия: {2}"), done, sel.Count, backup);
                Log(msg);
                Info(L.T("Реестр"), msg);
                StartScan();
            });
        }
    }
}
