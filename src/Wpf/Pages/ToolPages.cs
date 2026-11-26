using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Danilovl.WinCleaner.Wpf
{
    // ==================================================================== Docker

    /// <summary>Страница «Docker и WSL»: что занимает место в Docker и его очистка.</summary>
    class DockerPage : PageBase
    {
        readonly CheckList list = new CheckList(false);
        readonly CheckBox chkContainers, chkImages, chkBuild, chkNetworks, chkVolumes;
        readonly Button btnRefresh, btnPrune, btnCompact;
        bool dockerOk;

        public DockerPage()
            : base(L.T("Docker и WSL"),
                   L.T("Образы и контейнеры Docker хранятся внутри виртуального диска .vhdx. После очистки Docker место на диске Windows " +
                       "само не возвращается — после неё сожмите диск на странице «Сжатие дисков WSL»."))
        {
            btnPrune = X.Btn(L.T("Очистить Docker"), StartPrune, "AccentButton");
            btnRefresh = X.Btn(L.T("Обновить"), RefreshDf);
            btnCompact = X.Btn(L.T("Перейти к сжатию дисков"), () => { var w = Owner as MainWindow; if (w != null) w.Navigate(3); });
            chkContainers = X.Chk(L.T("Остановленные контейнеры"), true);
            chkImages = X.Chk(L.T("Неиспользуемые образы"), true);
            chkBuild = X.Chk(L.T("Кэш сборки"), true);
            chkNetworks = X.Chk(L.T("Сети"), true);
            chkVolumes = X.Chk(L.T("Volumes (данные БД и т.п.!)"), false);
            chkVolumes.SetResourceReference(Control.ForegroundProperty, "Danger");
            foreach (UIElement e in new UIElement[] { btnPrune, btnRefresh, btnCompact }) Toolbar.Children.Add(e);
            var options = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (UIElement e in new UIElement[] { chkContainers, chkImages, chkBuild, chkNetworks, chkVolumes }) options.Children.Add(e);
            Toolbar.Children.Add(options);

            list.Col(L.T("Тип"), 220);
            list.Col(L.T("Всего"), 110, true);
            list.Col(L.T("Используется"), 130, true);
            list.Col(L.T("Размер"), 130, true);
            list.Col(L.T("Можно освободить"), 220, true);
            Body.Child = list;
        }

        protected override void OnFirstShown() { RefreshDf(); }

        protected override void UpdateState()
        {
            btnRefresh.IsEnabled = !Busy;
            btnPrune.IsEnabled = !Busy && dockerOk;
        }

        void RefreshDf()
        {
            if (Busy) return;
            Status(L.T("Спрашиваю Docker…"));
            int code = 0;
            List<DockerDfRow> rows = null;
            Run(ct => rows = DockerTools.SystemDf(out code), () =>
            {
                dockerOk = code == 0 && rows != null;
                Summary.Text = dockerOk
                    ? L.T("Данные «docker system df». «Можно освободить» — то, что не используется ни одним контейнером.")
                    : DockerTools.DfError(code);
                Summary.SetResourceReference(TextBlock.ForegroundProperty, dockerOk ? "Text" : "Danger");
                var items = new List<Row>();
                if (dockerOk)
                    foreach (var r in rows) items.Add(new Row(r, r.Type, r.Total, r.Active, r.Size, r.Reclaimable));
                list.SetRows(items);
                Status("");
                UpdateState();
            });
        }

        void StartPrune()
        {
            var cmds = DockerTools.PruneCommands(chkContainers.On(), chkImages.On(), chkBuild.On(), chkNetworks.On(), chkVolumes.On());
            if (cmds.Count == 0) return;
            var sb = new StringBuilder(L.T("Будет удалено:\r\n"));
            foreach (var c in cmds) sb.AppendLine("   • " + c.What);
            sb.AppendLine();
            sb.AppendLine(L.T("Образы потом скачаются заново при docker pull / docker compose up."));
            if (!Ask(L.T("Очистка Docker"), sb.ToString(), chkVolumes.On())) return;

            ClearLog();
            Run(ct =>
            {
                DockerTools.RunPrune(cmds, Log, Status);
                Log(L.T("Готово. Теперь сожмите диск Docker, чтобы вернуть место на C:."));
            }, () =>
            {
                Status(L.T("Готово. Теперь сожмите диск Docker, чтобы вернуть место на C:."));
                RefreshDf();
            });
        }
    }

    // ==================================================================== Сжатие дисков WSL

    /// <summary>Страница «Сжатие дисков WSL»: docker prune → fstrim → остановка Docker и WSL → сжатие .vhdx → запуск Docker.</summary>
    class WslCompactPage : PageBase
    {
        readonly CheckList list = new CheckList(true);
        readonly Button btnRefresh, btnAdd, btnAll, btnCompact;
        readonly CheckBox chkTrim, chkPrune, chkVolumes, chkRestart;
        readonly HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> manualPaths = new List<string>();
        List<Row> rows = new List<Row>();

        public WslCompactPage()
            : base(L.T("Сжатие дисков WSL"),
                   L.T("Диски WSL2 и Docker Desktop (.vhdx) только растут: после удаления файлов внутри Linux место на диске Windows не возвращается. " +
                       "Здесь оно возвращается одной кнопкой: (по желанию) docker prune → fstrim → остановка Docker и WSL → сжатие → запуск Docker обратно. " +
                       "Данные внутри дисков не затрагиваются."))
        {
            btnCompact = X.Btn(L.T("Сжать выбранные"), StartCompact, "AccentButton");
            btnRefresh = X.Btn(L.T("Обновить список"), () => LoadDisks(true));
            btnAdd = X.Btn(L.T("Добавить файл…"), AddFile);
            btnAll = X.Btn(L.T("Выбрать все"), ToggleAll);
            foreach (UIElement e in new UIElement[] { btnCompact, btnRefresh, btnAdd, btnAll }) Toolbar.Children.Add(e);

            chkTrim = X.Chk(L.T("fstrim перед сжатием (рекомендуется)"), true);
            chkPrune = X.Chk(L.T("docker prune: удалить неиспользуемые образы и кэш"), false, () => UpdateState());
            chkVolumes = X.Chk(L.T("+ неиспользуемые volumes (данные БД!)"), false);
            chkVolumes.SetResourceReference(Control.ForegroundProperty, "Danger");
            chkRestart = X.Chk(L.T("Запустить Docker Desktop после"), true);
            var options = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            foreach (UIElement e in new UIElement[] { chkTrim, chkPrune, chkVolumes, chkRestart }) options.Children.Add(e);
            Toolbar.Children.Add(options);

            list.Col(L.T("Что это"), 380);
            list.Col(L.T("Размер"), 120, true);
            list.Col(L.T("Путь"), 640);
            list.PathOf = r => ((VhdxDisk)r.Tag).Path;
            list.PathIsFile = true;
            Body.Child = list;
        }

        protected override void OnFirstShown()
        {
            LoadDisks(true);
            Status(L.T("Отметьте диски и нажмите «Сжать выбранные». Нет нужного диска — «Добавить файл…»."));
        }

        List<VhdxDisk> CheckedDisks() { return rows.Where(r => r.Checked).Select(r => (VhdxDisk)r.Tag).ToList(); }

        Row MakeRow(VhdxDisk d, bool isChecked)
        {
            var r = new Row(d, d.Source, Util.FormatSize(d.Size), d.Path) { Tip = d.Path };
            r.SetCheckedSilently(isChecked);
            r.CheckedChanged = x => { if (!Row.Bulk) UpdateState(); };
            return r;
        }

        void LoadDisks(bool writeLog)
        {
            var previous = new HashSet<string>(CheckedDisks().Select(d => d.Path), StringComparer.OrdinalIgnoreCase);
            seen.Clear();
            var disks = VhdxTools.FindDisks(seen);
            foreach (string p in manualPaths) VhdxTools.TryAdd(disks, seen, p, VhdxTools.ManualSource);
            rows = disks.Select(d => MakeRow(d, previous.Contains(d.Path))).ToList();
            list.SetRows(rows);
            if (writeLog)
                Log(disks.Count == 0
                    ? L.T("Диски .vhdx не найдены. Если Docker хранит диск в другом месте — нажмите «Добавить файл…».")
                    : string.Format(L.T("Найдено дисков: {0}, общий размер {1}"), disks.Count, Util.FormatSize(disks.Sum(d => d.Size))));
            UpdateState();
        }

        void AddFile()
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = L.T("Виртуальные диски (*.vhdx)|*.vhdx"), Title = L.T("Выберите .vhdx") };
            if (dlg.ShowDialog(Owner) != true) return;
            string full = Path.GetFullPath(dlg.FileName);
            if (seen.Contains(full)) { Log(L.T("Этот файл уже в списке.")); return; }
            var tmp = new List<VhdxDisk>();
            if (!VhdxTools.TryAdd(tmp, seen, full, VhdxTools.ManualSource)) { Log(L.T("Не удалось прочитать файл: ") + full); return; }
            manualPaths.Add(full);
            rows.Add(MakeRow(tmp[0], true));
            list.SetRows(new List<Row>(rows));
            UpdateState();
        }

        void ToggleAll()
        {
            bool all = rows.Count > 0 && rows.All(r => r.Checked);
            foreach (var r in rows) r.SetCheckedSilently(!all);
            UpdateState();
        }

        protected override void UpdateState()
        {
            var sel = CheckedDisks();
            btnCompact.IsEnabled = !Busy && sel.Count > 0;
            btnRefresh.IsEnabled = btnAdd.IsEnabled = !Busy;
            btnAll.IsEnabled = !Busy && rows.Count > 0;
            list.IsEnabled = chkTrim.IsEnabled = chkPrune.IsEnabled = chkRestart.IsEnabled = !Busy;
            chkVolumes.IsEnabled = !Busy && chkPrune.On();
            if (!chkPrune.On()) chkVolumes.IsChecked = false;
            btnAll.Content = rows.Count > 0 && sel.Count == rows.Count ? L.T("Снять все") : L.T("Выбрать все");
            Summary.Text = rows.Count == 0
                ? L.T("Диски WSL / Docker не найдены")
                : sel.Count == 0
                    ? L.T("Отметьте диски галочками")
                    : string.Format(L.T("Выбрано: {0} ({1})"), sel.Count, Util.FormatSize(sel.Sum(d => d.Size)));
        }

        void StartCompact()
        {
            var sel = CheckedDisks();
            if (sel.Count == 0 || Busy) return;
            bool trim = chkTrim.On(), prune = chkPrune.On(), volumes = chkVolumes.On(), restart = chkRestart.On();

            var sb = new StringBuilder();
            sb.AppendLine(L.T("Будут сжаты:"));
            foreach (var d in sel) sb.AppendLine(string.Format("   • {0}  —  {1}", d.Source, Util.FormatSize(d.Size)));
            sb.AppendLine(L.T("Всего: ") + Util.FormatSize(sel.Sum(d => d.Size)));
            sb.AppendLine();
            sb.AppendLine(L.T("Docker Desktop будет остановлен, а ВСЕ дистрибутивы WSL выключены (wsl --shutdown)."));
            sb.AppendLine(L.T("Сохраните работу в WSL, закройте терминалы и VS Code Remote."));
            if (prune)
            {
                sb.AppendLine();
                sb.AppendLine(volumes
                    ? L.T("ВНИМАНИЕ: docker prune удалит неиспользуемые образы, контейнеры, кэш сборки И VOLUMES (данные баз и т.п.)!")
                    : L.T("docker prune удалит неиспользуемые образы, остановленные контейнеры и кэш сборки."));
            }
            if (!Ask(L.T("Сжатие дисков"), sb.ToString(), true)) return;

            ClearLog();
            string msg = "";
            Run(ct =>
            {
                long total = 0;
                int errors = 0;
                try
                {
                    if (prune)
                    {
                        Status(L.T("Очистка Docker…"));
                        Log(L.T("=== Очистка Docker ==="));
                        Util.Run("docker.exe", "system prune -a -f" + (volumes ? " --volumes" : ""), Encoding.UTF8, Log);
                        Util.Run("docker.exe", "builder prune -a -f", Encoding.UTF8, Log);
                    }
                    if (trim)
                    {
                        Status(L.T("fstrim внутри WSL…"));
                        Log(L.T("=== fstrim внутри WSL ==="));
                        VhdxTools.Fstrim(Log);
                    }
                    Status(L.T("Останавливаю Docker и WSL…"));
                    Log(L.T("=== Остановка Docker и WSL ==="));
                    bool dockerWasRunning = VhdxTools.StopDocker(Log);
                    VhdxTools.ShutdownWsl(Log);

                    Log(L.T("=== Сжатие ==="));
                    total = VhdxTools.CompactAll(sel, Log, Status, ref errors);
                    Log(L.T("ИТОГО освобождено: ") + Util.FormatSize(total));
                    if (dockerWasRunning && restart) VhdxTools.StartDockerDesktop(Log);
                }
                catch (Exception ex) { errors++; Log(L.T("ОШИБКА: ") + ex.Message); }
                Settings.AddFreed(total);
                msg = (errors == 0 ? L.T("Готово. ") : L.T("Готово, но были ошибки — смотрите лог. ")) + L.T("Освобождено: ") + Util.FormatSize(total);
            }, () =>
            {
                LoadDisks(false);
                Status(msg);
                Info(L.T("Сжатие дисков"), msg);
            });
            StopButton.Visibility = Visibility.Collapsed;   // сжатие прерывать нельзя: диск останется подключённым
        }
    }

    // ==================================================================== Гибернация и подкачка

    class SystemFilesPage : PageBase
    {
        readonly TextBlock hibText = X.Txt(""), pageText = X.Txt(""), recText = X.Txt("", 13, false, "Ok");
        readonly RadioButton[] hib = new RadioButton[3], page = new RadioButton[3];
        readonly TextBox initBox = new TextBox { Width = 90 }, maxBox = new TextBox { Width = 90 };
        readonly Border driveHost = new Border { Margin = new Thickness(8, 0, 4, 0) };
        readonly Button btnHib, btnPage, btnRefresh;
        WrapPanel driveChips;
        SystemFilesInfo info;
        bool filling;

        public SystemFilesPage()
            : base(L.T("Гибернация и подкачка"),
                   L.T("hiberfil.sys и pagefile.sys — два системных файла в корне диска, которые часто занимают десятки гигабайт. " +
                       "Их нельзя просто удалить, но можно уменьшить или отключить здесь. Изменения обратимы."))
        {
            btnRefresh = X.Btn(L.T("Обновить"), RefreshInfo);
            Toolbar.Children.Add(btnRefresh);

            // ---------------- гибернация
            var h = new StackPanel();
            h.Children.Add(X.Txt(L.T("Файл гибернации — hiberfil.sys"), 16, true));
            hibText.Margin = new Thickness(0, 8, 0, 14);
            h.Children.Add(hibText);
            string[] hibNames =
            {
                L.T("Полный — гибернация + быстрый запуск Windows (файл ≈ 40% ОЗУ)"),
                L.T("Сокращённый — только быстрый запуск, без режима гибернации (≈ 20% ОЗУ)"),
                L.T("Отключить — файл удаляется сразу; не будет гибернации и быстрого запуска")
            };
            for (int i = 0; i < 3; i++) { hib[i] = new RadioButton { Content = hibNames[i], GroupName = "hib" }; h.Children.Add(hib[i]); }
            btnHib = X.Btn(L.T("Применить для гибернации"), ApplyHibernate, "AccentButton");
            btnHib.HorizontalAlignment = HorizontalAlignment.Left;
            btnHib.Margin = new Thickness(0, 8, 0, 0);
            h.Children.Add(btnHib);

            // ---------------- подкачка
            var p = new StackPanel();
            p.Children.Add(X.Txt(L.T("Файл подкачки — pagefile.sys"), 16, true));
            pageText.Margin = new Thickness(0, 8, 0, 14);
            p.Children.Add(pageText);
            page[0] = new RadioButton { Content = L.T("Автоматически — размер выбирает Windows (обычно отсюда и берутся 20–30 ГБ)"), GroupName = "page" };
            page[1] = new RadioButton { Content = L.T("Задать размер на диске"), GroupName = "page", Margin = new Thickness(0) };
            page[2] = new RadioButton { Content = L.T("Без файла подкачки — не рекомендуется (при нехватке памяти программы будут падать)"), GroupName = "page" };
            p.Children.Add(page[0]);

            var fixedRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            page[1].VerticalAlignment = VerticalAlignment.Center;
            page[1].Margin = new Thickness(0, 0, 0, 8);
            fixedRow.Children.Add(page[1]);
            fixedRow.Children.Add(driveHost);
            fixedRow.Children.Add(FieldLabel(L.T("исходный, МБ:")));
            fixedRow.Children.Add(initBox);
            fixedRow.Children.Add(FieldLabel(L.T("максимальный, МБ:")));
            fixedRow.Children.Add(maxBox);
            fixedRow.Children.Add(X.Btn(L.T("Рекомендуемый"), () =>
            {
                if (info == null) return;
                initBox.Text = info.RecInit.ToString(); maxBox.Text = info.RecMax.ToString(); page[1].IsChecked = true;
            }));
            p.Children.Add(fixedRow);
            p.Children.Add(page[2]);
            recText.Margin = new Thickness(0, 4, 0, 10);
            p.Children.Add(recText);
            btnPage = X.Btn(L.T("Применить для подкачки (нужна перезагрузка)"), ApplyPageFile, "AccentButton");
            btnPage.HorizontalAlignment = HorizontalAlignment.Left;
            btnPage.Margin = new Thickness(0);
            p.Children.Add(btnPage);
            initBox.TextChanged += delegate { if (!filling) page[1].IsChecked = true; };
            maxBox.TextChanged += delegate { if (!filling) page[1].IsChecked = true; };

            var cards = new StackPanel();
            var c1 = X.Card(h, 20); c1.Margin = new Thickness(0, 0, 0, 14);
            cards.Children.Add(c1);
            cards.Children.Add(X.Card(p, 20));
            Body.Child = new ScrollViewer { Content = cards, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        }

        static TextBlock FieldLabel(string text)
        {
            var t = X.Txt(text);
            t.Margin = new Thickness(8, 0, 8, 8);
            return t;
        }

        protected override void OnFirstShown() { RefreshInfo(); }

        protected override void UpdateState()
        {
            btnHib.IsEnabled = btnPage.IsEnabled = btnRefresh.IsEnabled = !Busy && info != null;
        }

        void RefreshInfo()
        {
            if (Busy) return;
            SystemFilesInfo read = null;
            Run(ct => read = SystemFilesInfo.Read(), () =>
            {
                if (read == null) return;
                info = read;
                filling = true;
                hibText.Text = info.HibText;
                hib[!info.HibOn ? 2 : (info.HibReduced ? 1 : 0)].IsChecked = true;
                pageText.Text = info.PageText;
                recText.Text = info.RecommendText;
                int sel = Math.Max(0, info.Drives.IndexOf(info.CurDrive));
                driveChips = X.Chips(info.Drives.ToArray(), sel, i => { if (!filling) page[1].IsChecked = true; });
                driveHost.Child = driveChips;
                initBox.Text = (info.CurInit > 0 ? info.CurInit : info.RecInit).ToString();
                maxBox.Text = (info.CurMax > 0 ? info.CurMax : info.RecMax).ToString();
                page[info.PageMode].IsChecked = true;
                filling = false;
                UpdateState();
            });
        }

        static int Selected(RadioButton[] group)
        {
            for (int i = 0; i < group.Length; i++) if (group[i].IsChecked == true) return i;
            return 0;
        }

        void ApplyHibernate()
        {
            int mode = Selected(hib);
            if (!Ask(L.T("Гибернация"), SystemFilesInfo.HibernateDescription(mode), mode == 2)) return;
            Run(ct =>
            {
                int code = SystemFilesInfo.SetHibernate(mode, Log);
                Log(code == 0 ? L.T("Готово.") : L.T("powercfg вернул код ") + code);
            }, RefreshInfo);
        }

        void ApplyPageFile()
        {
            int mode = Selected(page);
            int init = 0, max = 0;
            string drive = null;
            if (mode == 1)
            {
                int d = driveChips == null ? -1 : X.SelectedChip(driveChips);
                if (d < 0) { Info(L.T("Подкачка"), L.T("Выберите диск.")); return; }
                drive = info.Drives[d];
                if (!int.TryParse(initBox.Text.Trim(), out init) || !int.TryParse(maxBox.Text.Trim(), out max) || init < 256 || max < 256)
                { Info(L.T("Подкачка"), L.T("Укажите размеры числами, не меньше 256 МБ.")); return; }
                if (max < init) { Info(L.T("Подкачка"), L.T("Максимальный размер не может быть меньше исходного.")); return; }
            }
            string what;
            string[] value = SystemFilesInfo.PagingValue(mode, drive, init, max, out what);
            if (!Ask(L.T("Файл подкачки"), what + "\r\n\r\n" + L.T("Изменения вступят в силу после перезагрузки."), mode == 2)) return;

            try { SystemFilesInfo.WritePagingFiles(value, Log); }
            catch (Exception ex)
            {
                Log(L.T("ОШИБКА: ") + ex.Message);
                Info(L.T("Ошибка"), L.T("Не удалось изменить настройку: ") + ex.Message);
                return;
            }
            if (Dlg.Ask(Owner, L.T("Перезагрузка"), L.T("Настройка сохранена. Перезагрузить компьютер сейчас?\r\n(Сохраните открытые документы.)"), true, L.T("Перезагрузить")))
            {
                try { Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 5") { CreateNoWindow = true, UseShellExecute = false }); }
                catch (Exception ex) { Log(L.T("Не удалось перезагрузить: ") + ex.Message); }
            }
            else Log(L.T("Перезагрузите компьютер, чтобы новый размер файла подкачки вступил в силу."));
        }
    }

    // ==================================================================== Удалённые файлы (NTFS)

    class UndeletePage : PageBase
    {
        readonly CheckList list = new CheckList(true);
        readonly Button btnScan, btnRestore, btnWipe;
        readonly TextBox search = new TextBox { Width = 280 };
        readonly CheckBox chkHideEmpty;
        readonly WrapPanel stateChips;
        readonly Border driveHost = new Border();
        readonly TextBlock countText = X.Txt("", 12, false, "SubText");
        readonly DispatcherTimer filterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        readonly List<string> driveRoots = new List<string>();
        WrapPanel driveChips;
        List<DeletedFile> all = new List<DeletedFile>();
        List<DeletedFile> view = new List<DeletedFile>();
        string scannedDrive;
        readonly Sorter<DeletedFile> sort = new Sorter<DeletedFile>(3, new Comparison<DeletedFile>[]
        {
            (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase),
            (a, b) => string.Compare(a.Folder, b.Folder, StringComparison.OrdinalIgnoreCase),
            (a, b) => a.Size.CompareTo(b.Size),
            (a, b) => a.Modified.CompareTo(b.Modified),
            CompareState
        }, 2, 3);

        /// <summary>Порядок состояний от лучшего к худшему; «частично перезаписанные» — по проценту потерь, затем по имени.</summary>
        static int StateRank(DeletedFile f)
        {
            switch (f.State)
            {
                case RecoverState.Good: return 0;
                case RecoverState.Resident: return 1;
                case RecoverState.Partial: return 2;
                case RecoverState.Overwritten: return 3;
                case RecoverState.Empty: return 4;
                default: return 5;
            }
        }

        static int CompareState(DeletedFile a, DeletedFile b)
        {
            int c = StateRank(a).CompareTo(StateRank(b));
            if (c == 0) c = a.OverwrittenPercent.CompareTo(b.OverwrittenPercent);
            return c != 0 ? c : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Что означает состояние файла и что с ним можно сделать — показывается при выборе строки.</summary>
        static string Explain(DeletedFile f)
        {
            switch (f.State)
            {
                case RecoverState.Good: return L.T("данные на месте — файл можно восстановить полностью. Отметьте его галочкой.");
                case RecoverState.Resident: return L.T("маленький файл, хранится прямо в таблице NTFS — можно восстановить. Отметьте его галочкой.");
                case RecoverState.Partial: return string.Format(L.T("{0}% данных уже занято другими файлами — восстановится с повреждениями. Отметить можно, но целым файл не будет."), f.OverwrittenPercent);
                case RecoverState.Overwritten: return L.T("данные полностью перезаписаны другими файлами — восстановить нельзя, осталось только имя.");
                case RecoverState.Empty: return L.T("файл был пустым — восстанавливать нечего.");
                default: return L.T("нет сведений о том, где лежали данные, — восстановить нельзя.");
            }
        }

        public UndeletePage()
            : base(L.T("Удалённые файлы (NTFS)"),
                   L.T("Здесь видны файлы, которые удалены (в т.ч. мимо корзины), но их записи ещё остались в таблице NTFS. " +
                       "Такие файлы НЕ занимают место — Windows считает его свободным и постепенно перезаписывает.\r\n" +
                       "На SSD (TRIM) содержимое обычно уже обнулено, даже если имя видно. Восстанавливайте только на ДРУГОЙ диск. " +
                       "Чтобы удалённые данные нельзя было восстановить — «Затереть свободное место»."))
        {
            btnScan = X.Btn(L.T("Найти удалённые файлы"), StartScan);
            btnRestore = X.Btn(L.T("Восстановить отмеченные…"), StartRestore, "AccentButton");
            btnRestore.ToolTip = L.T("Скопировать отмеченные файлы в выбранную папку. Отметить можно только файлы, данные которых ещё не перезаписаны");
            btnWipe = X.Btn(L.T("Затереть всё свободное место диска…"), StartWipe);
            btnWipe.ToolTip = L.T("Сделать ВСЕ удалённые файлы этого диска невосстановимыми. Удалённые файлы уже не занимают место, поэтому «удалить» отдельный файл нельзя — можно только затереть свободное место целиком");
            var driveLabel = X.Txt(L.T("Диск:"));
            driveLabel.Margin = new Thickness(0, 0, 8, 8);
            foreach (UIElement e in new UIElement[] { driveLabel, driveHost, btnScan, StopButton, btnRestore, btnWipe }) Toolbar.Children.Add(e);

            var filters = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
            var searchLabel = X.Txt(L.T("Поиск (имя / папка / .ext):"));
            searchLabel.Margin = new Thickness(0, 0, 8, 8);
            stateChips = X.Chips(new[] { L.T("Все состояния"), L.T("Только восстановимые"), L.T("Только перезаписанные") }, 1, i => ApplyFilter());
            chkHideEmpty = X.Chk(L.T("Скрыть пустые (0 байт)"), true, ApplyFilter);
            chkHideEmpty.Margin = new Thickness(8, 0, 16, 8);
            countText.Margin = new Thickness(0, 0, 0, 8);
            foreach (UIElement e in new UIElement[] { searchLabel, search, stateChips, chkHideEmpty, countText }) filters.Children.Add(e);
            Toolbar.Children.Add(filters);

            filterTimer.Tick += delegate { filterTimer.Stop(); ApplyFilter(); };
            search.TextChanged += delegate { filterTimer.Stop(); filterTimer.Start(); };

            list.Col(L.T("Имя"), 300);
            list.Col(L.T("Где был"), 480);
            list.Col(L.T("Размер"), 110, true);
            list.Col(L.T("Изменён"), 150);
            list.Col(L.T("Состояние"), 240);
            list.HeaderClicked += c => { sort.Click(c); ApplyFilter(); };
            list.SelectionChanged += delegate { UpdateState(); ExplainSelection(); };
            // «отметить все» относится ко всем показанным файлам (с учётом поиска и фильтров), а не только к видимым на экране
            list.CheckAllOverride = value =>
            {
                foreach (var f in view) f.Checked = value && f.Recoverable;
                list.SetRows(new LazyRows<DeletedFile>(view, MakeRow));
                UpdateState();
            };
            Body.Child = list;
        }

        protected override void OnFirstShown()
        {
            var names = new List<string>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady || d.DriveFormat != "NTFS") continue;
                    if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable) continue;
                    string root = d.Name.TrimEnd('\\');
                    driveRoots.Add(root);
                    names.Add(string.Format("{0}  ({1})", root, Util.FormatSize(d.TotalSize)));
                }
                catch { }
            }
            driveChips = X.Chips(names.ToArray(), names.Count > 0 ? 0 : -1, i => UpdateState());
            driveHost.Child = driveChips;
            Status(L.T("Выберите диск и нажмите «Найти удалённые файлы». Сканирование только читает диск."));
            CheckLeftovers();
        }

        string SelectedDrive
        {
            get { int i = driveChips == null ? -1 : X.SelectedChip(driveChips); return i < 0 ? null : driveRoots[i]; }
        }

        protected override void UpdateState()
        {
            btnScan.IsEnabled = btnWipe.IsEnabled = !Busy && SelectedDrive != null;
            driveHost.IsEnabled = !Busy;
            // что можно сделать сейчас: восстановить отмеченные галочкой, а если их нет — выделенную восстановимую строку
            var targets = RestoreTargets();
            long bytes = targets.Sum(f => f.Size);
            int damaged = targets.Count(f => f.State == RecoverState.Partial);
            btnRestore.IsEnabled = !Busy && targets.Count > 0;
            btnRestore.Content = targets.Count > 0
                ? string.Format(L.T("Восстановить: {0} ({1})…"), Util.FormatCount(targets.Count), Util.FormatSize(bytes))
                : L.T("Восстановить отмеченные…");
            if (all.Count == 0) Summary.Text = "";
            else if (targets.Count == 0) Summary.Text = L.T("Ничего не отмечено. Поставьте галочки у файлов, которые нужно восстановить (зелёные — целые, оранжевые — с повреждениями).");
            else Summary.Text = string.Format(L.T("К восстановлению: {0} ({1})"), Util.FormatCount(targets.Count), Util.FormatSize(bytes)) +
                                (damaged > 0 ? string.Format(L.T(", из них с повреждениями: {0}"), damaged) : "");
        }

        /// <summary>Отмеченные галочкой; если их нет — выделенные строки, которые можно восстановить.</summary>
        List<DeletedFile> RestoreTargets()
        {
            var marked = view.Where(f => f.Checked && f.Recoverable).ToList();
            if (marked.Count > 0) return marked;
            return list.SelectedRows().Select(r => (DeletedFile)r.Tag).Where(f => f.Recoverable).ToList();
        }

        /// <summary>Строка состояния объясняет, что можно сделать с выбранным файлом.</summary>
        void ExplainSelection()
        {
            if (Busy) return;
            var rows = list.SelectedRows().ToList();
            if (rows.Count != 1) return;
            var f = (DeletedFile)rows[0].Tag;
            Status(f.Name + ": " + Explain(f));
        }

        Row MakeRow(DeletedFile f)
        {
            var r = new Row(f, f.Name, f.Folder, Util.FormatSize(f.Size),
                f.Modified == DateTime.MinValue ? "" : f.Modified.ToString("yyyy-MM-dd HH:mm"), f.StateText);
            if (f.State == RecoverState.Overwritten || f.State == RecoverState.Unknown) r.ColorKey = "SubText";
            else if (f.State == RecoverState.Partial) r.ColorKey = "Warn";
            else if (f.Recoverable) r.ColorKey = "Ok";
            r.CanCheck = f.Recoverable;         // перезаписанные, пустые и «без данных» отметить нельзя — восстанавливать нечего
            r.Tip = f.Name + ": " + Explain(f);
            r.SetCheckedSilently(f.Checked && f.Recoverable);
            r.CheckedChanged = x => { ((DeletedFile)x.Tag).Checked = x.Checked; if (!Row.Bulk) UpdateState(); };
            return r;
        }

        void ApplyFilter()
        {
            string q = search.Text.Trim();
            int st = X.SelectedChip(stateChips);
            IEnumerable<DeletedFile> src = all;
            if (chkHideEmpty.On()) src = src.Where(f => f.Size > 0);
            if (st == 1) src = src.Where(f => f.Recoverable);
            else if (st == 2) src = src.Where(f => f.State == RecoverState.Overwritten || f.State == RecoverState.Partial);
            if (q.Length > 0)
                src = src.Where(f => f.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     (f.Folder != null && f.Folder.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            view = sort.Apply(src);
            // отметки файлов, которые фильтр скрыл, снимаются: восстанавливается только то, что видно в списке
            var visible = new HashSet<DeletedFile>(view);
            foreach (var f in all) if (f.Checked && !visible.Contains(f)) f.Checked = false;
            list.SetHeaderChecked(view.Any(f => f.Recoverable) && view.Where(f => f.Recoverable).All(f => f.Checked));
            // строки создаются только для видимой части списка — записей могут быть сотни тысяч
            list.SetRows(new LazyRows<DeletedFile>(view, MakeRow));
            list.SortArrow(sort.Column, sort.Desc);
            countText.Text = string.Format(L.T("Показано: {0} из {1}   ({2})"),
                Util.FormatCount(view.Count), Util.FormatCount(all.Count), Util.FormatSize(view.Sum(f => f.Size)));
            UpdateState();
        }

        void StartScan()
        {
            string drive = SelectedDrive;
            if (drive == null || Busy) return;
            ClearLog();
            all = new List<DeletedFile>();
            ApplyFilter();
            List<DeletedFile> found = null;
            Run(ct =>
            {
                Progress(0);
                var sw = Stopwatch.StartNew();
                using (var vol = new NtfsVolume(drive))
                {
                    Log(string.Format(L.T("{0}: кластер {1} B, запись MFT {2} B, записей в MFT: {3}"),
                        drive, vol.ClusterSize, vol.RecordSize, Util.FormatCount(vol.RecordCount)));
                    var files = vol.FindDeleted(p => { Progress(p); Status(L.T("Читаю таблицу MFT диска ") + drive + "… " + p + "%"); }, ct);
                    Log(string.Format(L.T("Найдено удалённых файлов: {0} за {1:N1} с."), Util.FormatCount(files.Count), sw.Elapsed.TotalSeconds));
                    Log(string.Format(L.T("Из них с данными, которые ещё не перезаписаны: {0}"),
                        Util.FormatCount(files.Count(f => f.Recoverable && f.Size > 0))));
                    found = files;
                }
            }, () =>
            {
                if (found != null) { all = found; scannedDrive = drive; }
                ApplyFilter();
                Status(found == null ? L.T("Сканирование не выполнено.") :
                    L.T("Отметьте файлы галочками (флажок в заголовке — все показанные) и нажмите «Восстановить отмеченные…». Клик по заголовку — сортировка."));
            });
        }

        void StartRestore()
        {
            if (Busy || scannedDrive == null) return;
            var sel = RestoreTargets();
            if (sel.Count == 0) return;

            string outDir = X.PickFolder(null, L.T("Куда сохранить восстановленные файлы (лучше на другой диск)"));
            if (outDir == null) return;
            string outRoot = Path.GetPathRoot(outDir).TrimEnd('\\');
            if (string.Equals(outRoot, scannedDrive, StringComparison.OrdinalIgnoreCase) &&
                !Ask(L.T("Внимание"), L.T("Вы сохраняете на тот же диск ") + scannedDrive +
                    L.T(", с которого восстанавливаете.\r\n\r\n" +
                        "Новые файлы могут занять место ещё не восстановленных и испортить их навсегда.\r\n" +
                        "Рекомендуется выбрать другой диск или флешку.\r\n\r\nВсё равно продолжить?"), true))
                return;

            int bad = sel.Count(f => f.State == RecoverState.Partial);
            var sb = new StringBuilder();
            sb.AppendLine(string.Format(L.T("Восстановить {0} файлов ({1}) в папку:"), sel.Count, Util.FormatSize(sel.Sum(f => f.Size))));
            sb.AppendLine(outDir);
            if (bad > 0)
            {
                sb.AppendLine();
                sb.AppendLine(string.Format(L.T("{0} из них частично перезаписаны — они восстановятся с повреждениями."), bad));
            }
            if (!Ask(L.T("Подтверждение"), sb.ToString())) return;

            string drive = scannedDrive, msg = "";
            Run(ct =>
            {
                int ok = 0, failed = 0, zeros = 0;
                Progress(0);
                using (var vol = new NtfsVolume(drive))
                {
                    for (int i = 0; i < sel.Count && !ct.IsCancellationRequested; i++)
                    {
                        var f = sel[i];
                        Status(string.Format(L.T("Восстанавливаю {0}/{1}: {2}"), i + 1, sel.Count, f.Name));
                        Progress(i * 100 / sel.Count);
                        string target = UniquePath(outDir, f.Name);
                        try
                        {
                            bool hasData = vol.Restore(f, target);
                            ok++;
                            if (!hasData && f.Size > 0)
                            {
                                zeros++;
                                Log("  ~ " + f.Name + L.T(" — восстановлен, но содержимое из одних нулей (стёрто TRIM на SSD)"));
                            }
                            else Log("  + " + Path.GetFileName(target) + "  (" + Util.FormatSize(f.Size) + ")");
                        }
                        catch (Exception ex)
                        {
                            failed++;
                            Log("  ! " + f.Name + ": " + ex.Message);
                            try { if (File.Exists(target) && new FileInfo(target).Length == 0) File.Delete(target); } catch { }
                        }
                    }
                }
                msg = string.Format(L.T("Восстановлено: {0}, ошибок: {1}"), ok, failed) +
                      (zeros > 0 ? string.Format(L.T("\r\nИз них пустые (данные стёрты SSD): {0}"), zeros) : "");
                Log(msg.Replace("\r\n", "; "));
            }, () =>
            {
                if (msg.Length == 0) return;
                Status(msg.Replace("\r\n", "; "));
                if (Dlg.Ask(Owner, L.T("Восстановление"), msg, false, L.T("Открыть папку"))) X.OpenFolder(outDir);
            });
        }

        static string UniquePath(string dir, string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = "file";
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) return path;
            string bare = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                path = Path.Combine(dir, string.Format("{0} ({1}){2}", bare, i, ext));
                if (!File.Exists(path)) return path;
            }
        }

        void StartWipe()
        {
            string drive = SelectedDrive;
            if (drive == null || Busy) return;
            if (!Ask(L.T("Затирание свободного места"),
                    L.T("Будет запущена встроенная утилита Windows:  cipher /w:") + drive +
                    L.T("\\\r\n\r\n" +
                        "Она перезаписывает всё СВОБОДНОЕ место на диске (нулями, единицами и случайными данными). " +
                        "Существующие файлы не затрагиваются, но удалённые файлы после этого восстановить будет невозможно.\r\n\r\n" +
                        "Это может занять от десятков минут до нескольких часов, диск будет сильно загружен. " +
                        "На SSD обычно не нужно — TRIM и так стирает удалённые данные.\r\n\r\nЗапустить?") +
                    "\r\n\r\n" + L.T("ВАЖНО: во время работы свободное место на диске ТРИЖДЫ падает до нуля и потом возвращается — так устроена утилита " +
                                     "(она заполняет диск временным файлом). Пока диск заполнен, другим программам может не хватить места для сохранения файлов.") +
                    "\r\n\r\n" + L.T("Операцию можно прервать кнопкой «Стоп»: уже затёртое останется затёртым, временные файлы будут удалены."), true))
                return;

            ClearLog();
            int code = -1;
            bool stopped = false;
            wipingDrive = drive;
            Run(ct =>
            {
                // cipher делает три прохода (нули, единицы, случайные данные). Каждый проход заполняет свободное место
                // временным файлом, поэтому ход работы виден по тому, сколько свободного места осталось.
                int pass = 0;
                long freeAtStart = FreeSpace(drive);
                bool running = true;
                Progress(0);
                var watcher = new System.Threading.Thread(() =>
                {
                    while (running)
                    {
                        System.Threading.Thread.Sleep(1500);
                        if (!running) break;
                        int p = Math.Max(1, pass);
                        long free = FreeSpace(drive);
                        double inPass = freeAtStart > 0 ? Math.Max(0, Math.Min(1, 1.0 - (double)free / freeAtStart)) : 0;
                        Progress((int)(((p - 1) + inPass) * 100 / 3));
                        Status(string.Format(L.T("Затираю свободное место на {0}: проход {1} из 3, {2:N0}% прохода. Не выключайте компьютер; «Стоп» прерывает операцию."),
                            drive, p, inPass * 100));
                    }
                }) { IsBackground = true };
                watcher.Start();
                try
                {
                    code = Util.Run("cipher.exe", "/w:" + drive + "\\", Util.OemEncoding(), line =>
                    {
                        // строки с 0x00 и 0xFF — начало первого и второго прохода; следующая строка с текстом — третий
                        if (line.Contains("0x00")) pass = 1;
                        else if (line.Contains("0xFF")) pass = 2;
                        else if (pass == 2 && line.Trim().Trim('.').Length > 0 && !line.TrimStart().StartsWith("$")) pass = 3;
                        if (line.Trim().Trim('.').Length > 0) Log(line);        // строки из одних точек в журнал не пишем
                    }, ct);
                }
                finally { running = false; }

                if (ct.IsCancellationRequested)
                {
                    stopped = true;
                    // прерванный cipher оставляет на диске папку с временным файлом на всё свободное место — убираем её
                    Status(L.T("Останавливаю: удаляю временные файлы…"));
                    Log(RemoveWipeTemp(drive) ? L.T("Затирание прервано, временные файлы удалены.")
                                              : L.T("Не удалось удалить временную папку — удалите её вручную: ") + WipeTemp(drive));
                }
                wipingDrive = null;
            }, () =>
            {
                Status(stopped ? L.T("Затирание прервано. Часть свободного места уже перезаписана, остальное — нет.")
                     : code == 0 ? L.T("Свободное место затёрто.") : L.T("cipher завершился с кодом ") + code);
                all = new List<DeletedFile>();
                ApplyFilter();
            });
        }

        string wipingDrive;      // диск, на котором сейчас идёт затирание

        static string WipeTemp(string drive) { return drive + "\\EFSTMPWP"; }

        /// <summary>Удаляет временную папку утилиты cipher (в ней файл на всё свободное место). true — папки больше нет.</summary>
        static bool RemoveWipeTemp(string drive)
        {
            string temp = WipeTemp(drive);
            try
            {
                if (!Native.Exists(temp)) return true;
                // процесс cipher после завершения отпускает файл не мгновенно — даём ему пару секунд
                for (int attempt = 0; attempt < 10 && Native.Exists(temp); attempt++)
                {
                    FileWalker.Delete(temp, null, new WalkStats(), System.Threading.CancellationToken.None, false);
                    FileWalker.RemoveDir(temp);
                    if (Native.Exists(temp)) System.Threading.Thread.Sleep(300);
                }
            }
            catch { }
            return !Native.Exists(temp);
        }

        /// <summary>При закрытии программы затирание останавливается и временный файл удаляется — диск не остаётся заполненным.</summary>
        public override void Shutdown()
        {
            string drive = wipingDrive;
            base.Shutdown();                 // завершает процесс cipher
            if (drive != null) RemoveWipeTemp(drive);
        }

        /// <summary>
        /// Следы прошлого прерванного затирания: если на диске осталась временная папка, а утилита уже не работает,
        /// предлагаем её удалить — иначе место так и останется занятым.
        /// </summary>
        void CheckLeftovers()
        {
            try
            {
                if (System.Diagnostics.Process.GetProcessesByName("cipher").Length > 0) return;   // затирание ещё идёт (например, запущено вручную)
                foreach (string drive in driveRoots)
                {
                    if (!Native.Exists(WipeTemp(drive))) continue;
                    if (!Ask(L.T("Затирание свободного места"), string.Format(L.T("На диске {0} осталась временная папка прерванного затирания свободного места ({1}). " +
                            "Она может занимать очень много места. Удалить её?"), drive, WipeTemp(drive)), false))
                        continue;
                    Log(RemoveWipeTemp(drive) ? L.T("Временная папка удалена: ") + WipeTemp(drive)
                                              : L.T("Не удалось удалить временную папку — удалите её вручную: ") + WipeTemp(drive));
                }
            }
            catch { }
        }

        static long FreeSpace(string drive)
        {
            try { return new DriveInfo(drive + "\\").AvailableFreeSpace; } catch { return 0; }
        }
    }
}
