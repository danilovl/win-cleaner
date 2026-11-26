using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace Danilovl.WinCleaner.Wpf
{
    /// <summary>Пункт бокового меню: значок, название, пояснение и страница (создаётся при первом открытии).</summary>
    class NavEntry
    {
        public string Glyph, Title, Hint;
        public Func<FrameworkElement> Create;
        public FrameworkElement Page;
        public RadioButton Button;
        public TextBlock BusyDot;

        public bool Busy { get { var p = Page as PageBase; return p != null && p.Busy; } }
    }

    class MainWindow : Window
    {
        /// <summary>Название программы — одинаковое на всех языках.</summary>
        const string AppTitle = "WinCleaner";
        static readonly Dictionary<string, long> freeAtStart = new Dictionary<string, long>();

        readonly List<NavEntry> nav = new List<NavEntry>();
        ContentControl host;
        TextBlock drivesText, freedText;
        readonly DispatcherTimer timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        int current = -1;

        public MainWindow()
        {
            Width = 1280; Height = 840; MinWidth = 980; MinHeight = 620;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            SetResourceReference(BackgroundProperty, "Bg");
            SetResourceReference(ForegroundProperty, "Text");
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            try { Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                      System.Drawing.Icon.ExtractAssociatedIcon(System.Reflection.Assembly.GetExecutingAssembly().Location).Handle,
                      Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions()); } catch { }
            SourceInitialized += delegate { Palette.ApplyTitleBar(this); };

            RestorePosition();
            Rebuild(0);

            timer.Tick += delegate { UpdateDrives(); };
            Loaded += delegate { UpdateDrives(); timer.Start(); };
            Closing += (s, e) =>
            {
                if (!nav.Any(n => n.Busy)) return;
                if (!Dlg.Ask(this, AppTitle, L.T("Операция ещё выполняется. Закрыть программу?\r\n\r\n" +
                        "Если идёт сжатие диска .vhdx — дождитесь окончания, иначе диск может остаться подключённым."), true, L.T("Закрыть")))
                    e.Cancel = true;
            };
            Closed += delegate
            {
                timer.Stop();
                SavePosition();
                // ничего не оставляем работать в фоне после закрытия окна
                foreach (var n in nav) { var page = n.Page as PageBase; if (page != null) page.Shutdown(); }
            };
        }

        const int CleanerIndex = 1;

        /// <summary>Список мест очистки изменился (свои места в настройках) — страница очистки создаётся заново при следующем открытии.</summary>
        void ResetCleaner()
        {
            if (CleanerIndex < nav.Count && !nav[CleanerIndex].Busy) nav[CleanerIndex].Page = null;
        }

        void SavePosition()
        {
            var b = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
            if (b.IsEmpty || b.Width < 200) return;
            Settings.Set("Window", string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0} {1:0} {2:0} {3:0} {4}",
                b.Left, b.Top, b.Width, b.Height, WindowState == WindowState.Maximized ? 1 : 0));
        }

        /// <summary>Окно открывается там же и того же размера, что и в прошлый раз (если это место ещё на экране).</summary>
        void RestorePosition()
        {
            try
            {
                string[] v = (Settings.Get("Window", "") ?? "").Split(' ');
                if (v.Length != 5) return;
                var inv = System.Globalization.CultureInfo.InvariantCulture;
                double x = double.Parse(v[0], inv), y = double.Parse(v[1], inv), w = double.Parse(v[2], inv), h = double.Parse(v[3], inv);
                bool visible = x + 100 < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth && x + w - 100 > SystemParameters.VirtualScreenLeft &&
                               y >= SystemParameters.VirtualScreenTop - 10 && y + 60 < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
                if (!visible || w < MinWidth || h < MinHeight) return;
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = x; Top = y; Width = w; Height = h;
                if (v[4] == "1") WindowState = WindowState.Maximized;
            }
            catch { }
        }

        /// <summary>
        /// Создаёт меню и страницы заново на текущем языке — в этом же окне, без его закрытия.
        /// Вызывается при запуске и при смене языка (когда ни одна операция не выполняется).
        /// </summary>
        void Rebuild(int page)
        {
            Title = AppTitle;
            nav.Clear();
            host = new ContentControl();
            drivesText = X.Txt("", 12, false, "SubText");
            freedText = X.Txt("", 12, true, "Ok");

            nav.Add(new NavEntry { Glyph = "", Title = L.T("Обзор"), Create = () => new HomePage(nav, Navigate) });
            Add("", L.T("Очистка мусора и кэшей"), L.T("Временные файлы, кэши браузеров, программ и инструментов разработки"), () => new CleanerPage());
            Add("", L.T("Docker и WSL"), L.T("Что занимает место в Docker и очистка образов, контейнеров, кэша сборки"), () => new DockerPage());
            Add("", L.T("Сжатие дисков WSL"), L.T("Вернуть место, занятое виртуальными дисками .vhdx"), () => new WslCompactPage());
            Add("", L.T("Папки проектов"), L.T("node_modules, vendor, build, target и другие папки, которые создаются заново"), () => new ProjectsPage());
            Add("", L.T("Анализ AppData"), L.T("Размеры папок программ и остатки удалённых приложений"), () => new AppDataPage());
            Add("\uE8FD", L.T("Установленные программы"), L.T("Что установлено, сколько занимает, удаление"), () => new AppsPage());
            Add("", L.T("Реестр"), L.T("Устаревшие записи: удалённые программы, автозагрузка без программы и другое"), () => new RegistryPage());
            Add("", L.T("Большие файлы"), L.T("Самые крупные файлы в выбранной папке"), () => new BigFilesPage());
            Add("\uE8C8", L.T("Дубликаты"), L.T("Файлы с одинаковым содержимым"), () => new DuplicatesPage());
            Add("\uE8F1", L.T("Карта диска"), L.T("Дерево папок по размеру: видно, куда ушло место"), () => new DiskMapPage());
            Add("", L.T("Гибернация и подкачка"), L.T("Уменьшить или отключить hiberfil.sys и pagefile.sys"), () => new SystemFilesPage());
            Add("", L.T("Удалённые файлы (NTFS)"), L.T("Найти и восстановить удалённые файлы, затереть свободное место"), () => new UndeletePage());

            Add("\uE713", L.T("Настройки"), L.T("Исключения, свои места очистки, статистика"), () => new SettingsPage { CustomChanged = ResetCleaner });

            Content = BuildLayout();
            Navigate(page);
            UpdateDrives();
        }

        void Add(string glyph, string title, string hint, Func<FrameworkElement> create)
        {
            nav.Add(new NavEntry { Glyph = glyph, Title = title, Hint = hint, Create = create });
        }

        UIElement BuildLayout()
        {
            var root = new Grid();
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ---------------- боковое меню
            var side = new DockPanel();
            var sideBorder = new Border { Child = side, BorderThickness = new Thickness(0, 0, 1, 0) };
            sideBorder.SetResourceReference(Border.BackgroundProperty, "Panel");
            sideBorder.SetResourceReference(Border.BorderBrushProperty, "Line");

            var brand = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(20, 20, 12, 16) };
            var logo = X.Icon("", 20, "Accent");
            logo.Margin = new Thickness(0, 0, 10, 0);
            brand.Children.Add(logo);
            brand.Children.Add(X.Txt("WinCleaner", 17, true));
            DockPanel.SetDock(brand, Dock.Top);
            side.Children.Add(brand);

            // низ меню: тема и язык
            var options = new StackPanel { Margin = new Thickness(20, 8, 12, 14) };
            options.Children.Add(Caption(L.T("Тема")));
            options.Children.Add(X.Chips(new[] { L.T("Светлая"), L.T("Тёмная"), "OLED" }, Palette.Theme, i =>
            {
                Palette.Apply(i);
                Palette.Save();
                Palette.ApplyTitleBar(this);
            }));
            options.Children.Add(Caption(L.T("Язык / Language")));
            options.Children.Add(X.Chips(new[] { "English", "Русский" }, L.Russian ? 1 : 0, i => SwitchLanguage(i == 1)));
            DockPanel.SetDock(options, Dock.Bottom);
            side.Children.Add(options);

            var items = new StackPanel();
            for (int i = 0; i < nav.Count; i++)
            {
                int index = i;
                var n = nav[i];
                var content = new DockPanel();
                n.BusyDot = X.Txt("●", 10, false, "Accent");
                n.BusyDot.Visibility = Visibility.Collapsed;
                n.BusyDot.Margin = new Thickness(6, 0, 0, 0);
                DockPanel.SetDock(n.BusyDot, Dock.Right);
                content.Children.Add(n.BusyDot);
                var icon = X.Icon(n.Glyph, 16);
                icon.Width = 28;
                content.Children.Add(icon);
                var title = X.Txt(n.Title);
                title.TextWrapping = TextWrapping.NoWrap;
                title.TextTrimming = TextTrimming.CharacterEllipsis;
                content.Children.Add(title);

                n.Button = new RadioButton { Content = content, GroupName = "nav", ToolTip = n.Hint };
                n.Button.SetResourceReference(StyleProperty, "NavItem");
                n.Button.Checked += delegate { Navigate(index); };
                items.Children.Add(n.Button);
            }
            side.Children.Add(new ScrollViewer { Content = items, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
            root.Children.Add(sideBorder);

            // ---------------- содержимое и строка состояния
            var right = new DockPanel();
            var status = new DockPanel { Margin = new Thickness(28, 0, 28, 0), Height = 34, LastChildFill = true };
            DockPanel.SetDock(freedText, Dock.Right);
            status.Children.Add(freedText);
            drivesText.TextWrapping = TextWrapping.NoWrap;
            drivesText.TextTrimming = TextTrimming.CharacterEllipsis;
            status.Children.Add(drivesText);
            var statusBorder = new Border { Child = status, BorderThickness = new Thickness(0, 1, 0, 0) };
            statusBorder.SetResourceReference(Border.BorderBrushProperty, "Line");
            DockPanel.SetDock(statusBorder, Dock.Bottom);
            right.Children.Add(statusBorder);
            right.Children.Add(host);
            Grid.SetColumn(right, 1);
            root.Children.Add(right);
            return root;
        }

        static TextBlock Caption(string text)
        {
            var t = X.Txt(text, 11, false, "SubText");
            t.Margin = new Thickness(0, 6, 0, 6);
            return t;
        }

        /// <summary>Открывает страницу. Остальные страницы остаются в памяти и продолжают работу в фоне.</summary>
        public void Navigate(int index)
        {
            if (index < 0 || index >= nav.Count) index = 0;
            var n = nav[index];
            if (n.Page == null)
            {
                n.Page = n.Create();
                var p = n.Page as PageBase;
                if (p != null) p.BusyChanged = OnBusyChanged;
            }
            current = index;
            host.Content = n.Page;
            if (n.Button.IsChecked != true) n.Button.IsChecked = true;
        }

        void OnBusyChanged()
        {
            foreach (var n in nav) n.BusyDot.Visibility = n.Busy ? Visibility.Visible : Visibility.Collapsed;
            Title = nav.Any(n => n.Busy) ? AppTitle + L.T(" — выполняется…") : AppTitle;
            UpdateDrives();
        }

        void UpdateDrives()
        {
            var parts = new List<string>();
            long freed = 0;
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string name = d.Name.TrimEnd('\\');
                    long free = d.AvailableFreeSpace;
                    parts.Add(string.Format(L.T("{0} свободно {1} из {2}"), name, Util.FormatSize(free), Util.FormatSize(d.TotalSize)));
                    long start;
                    if (!freeAtStart.TryGetValue(name, out start)) freeAtStart[name] = start = free;
                    if (free > start) freed += free - start;
                }
                catch { }
            }
            drivesText.Text = string.Join("      ", parts);
            freedText.Text = freed >= (10L << 20) ? L.T("Освобождено с запуска: ") + Util.FormatSize(freed) : "";
            var home = nav[0].Page as HomePage;
            if (home != null && current == 0) home.Refresh();
        }

        void SwitchLanguage(bool russian)
        {
            if (L.Russian == russian) return;
            if (nav.Any(n => n.Busy))
            {
                Dlg.Info(this, AppTitle, L.T("Дождитесь окончания операции, чтобы сменить язык."));
                // вернуть переключатель в прежнее положение
                Dispatcher.BeginInvoke(new Action(() => Rebuild(current)));
                return;
            }
            L.Russian = russian;
            L.Save();
            // перестраиваем после выхода из обработчика щелчка: сам переключатель при этом пересоздаётся
            Dispatcher.BeginInvoke(new Action(() => Rebuild(current)));
        }
    }

    /// <summary>Стартовая страница: диски и быстрый переход к инструментам.</summary>
    class HomePage : ScrollViewer
    {
        readonly WrapPanel drives = new WrapPanel();

        public HomePage(List<NavEntry> nav, Action<int> navigate)
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var root = new StackPanel { Margin = new Thickness(28, 22, 28, 20) };
            root.Children.Add(X.Txt(L.T("Обзор"), 24, true));
            var sub = X.Txt(L.T("Сколько места на дисках и что можно освободить. Ничего не удаляется без подтверждения."), 13, false, "SubText");
            sub.Margin = new Thickness(0, 6, 0, 18);
            root.Children.Add(sub);

            root.Children.Add(Section(L.T("Диски")));
            root.Children.Add(drives);

            long freed = Settings.TotalFreed;
            if (freed > 0)
            {
                var total = X.Txt(string.Format(L.T("Освобождено за всё время: {0} (очисток: {1})"), Util.FormatSize(freed), Settings.Cleanups), 13, false, "Ok");
                total.Margin = new Thickness(0, 0, 0, 14);
                root.Children.Add(total);
            }

            root.Children.Add(Section(L.T("Инструменты")));
            var tiles = new WrapPanel();
            for (int i = 1; i < nav.Count; i++)
            {
                int index = i;
                var n = nav[i];
                var text = new StackPanel { Margin = new Thickness(14, 0, 0, 0) };
                text.Children.Add(X.Txt(n.Title, 14, true));
                var hint = X.Txt(n.Hint, 12, false, "SubText");
                hint.Margin = new Thickness(0, 4, 0, 0);
                text.Children.Add(hint);
                var row = new DockPanel();
                var icon = X.Icon(n.Glyph, 24, "Accent");
                icon.VerticalAlignment = VerticalAlignment.Top;
                icon.Margin = new Thickness(0, 2, 0, 0);
                row.Children.Add(icon);
                row.Children.Add(text);
                var card = X.Card(row);
                card.Width = 300; card.MinHeight = 96; card.Margin = new Thickness(0, 0, 14, 14);
                card.Cursor = System.Windows.Input.Cursors.Hand;
                card.MouseEnter += delegate { card.SetResourceReference(Border.BorderBrushProperty, "Accent"); };
                card.MouseLeave += delegate { card.SetResourceReference(Border.BorderBrushProperty, "Line"); };
                card.MouseLeftButtonUp += delegate { navigate(index); };
                tiles.Children.Add(card);
            }
            root.Children.Add(tiles);
            Content = root;
            Loaded += delegate { Refresh(); };
        }

        static TextBlock Section(string text)
        {
            var t = X.Txt(text, 15, true);
            t.Margin = new Thickness(0, 6, 0, 12);
            return t;
        }

        /// <summary>Карточки дисков: заполненность полосой, свободно / всего.</summary>
        public void Refresh()
        {
            drives.Children.Clear();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    long total = d.TotalSize, free = d.AvailableFreeSpace;
                    double used = total > 0 ? (total - free) * 100.0 / total : 0;
                    var panel = new StackPanel();
                    var head = new DockPanel();
                    var icon = X.Icon("", 18, "Accent");
                    icon.Margin = new Thickness(0, 0, 10, 0);
                    head.Children.Add(icon);
                    string label = string.IsNullOrEmpty(d.VolumeLabel) ? "" : "  " + d.VolumeLabel;
                    head.Children.Add(X.Txt(d.Name.TrimEnd('\\') + label, 15, true));
                    panel.Children.Add(head);
                    var bar = new ProgressBar { Value = used, Height = 8, Margin = new Thickness(0, 14, 0, 10) };
                    if (used >= 90) bar.SetResourceReference(Control.ForegroundProperty, "Danger");
                    panel.Children.Add(bar);
                    panel.Children.Add(X.Txt(string.Format(L.T("Свободно {0} из {1}"), Util.FormatSize(free), Util.FormatSize(total)), 13));
                    panel.Children.Add(X.Txt(string.Format(L.T("Занято {0:N0}%"), used), 12, false, "SubText"));
                    var card = X.Card(panel);
                    card.Width = 300; card.Margin = new Thickness(0, 0, 14, 14);
                    drives.Children.Add(card);
                }
                catch { }
            }
        }
    }
}
