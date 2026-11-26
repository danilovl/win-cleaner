using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace Danilovl.WinCleaner.Wpf
{
    /// <summary>Страница «Настройки»: исключения, свои места очистки, статистика.</summary>
    class SettingsPage : ScrollViewer
    {
        readonly CheckList exclusions = new CheckList(false) { Height = 170 };
        readonly CheckList custom = new CheckList(false) { Height = 170 };
        readonly TextBox maskBox = new TextBox { Width = 150 };
        readonly TextBlock stats = X.Txt("", 13);
        readonly Border scheduleHost = new Border { Margin = new Thickness(0, 0, 0, 4) };
        /// <summary>Список своих мест изменился — страницу очистки нужно построить заново.</summary>
        public Action CustomChanged;

        public SettingsPage()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            var root = new StackPanel { Margin = new Thickness(28, 22, 28, 20) };
            root.Children.Add(X.Txt(L.T("Настройки"), 24, true));

            // ---------------- исключения
            var ex = new StackPanel();
            ex.Children.Add(X.Txt(L.T("Исключения"), 16, true));
            ex.Children.Add(Note(L.T("Папки, которые программа никогда не трогает: они не учитываются в размерах и не удаляются ни одним инструментом, " +
                                     "даже если лежат внутри очищаемого места.")));
            var exButtons = new WrapPanel();
            exButtons.Children.Add(X.Btn(L.T("Добавить папку…"), AddExclusion, "AccentButton"));
            exButtons.Children.Add(X.Btn(L.T("Убрать выбранные"), () => Remove(exclusions, Settings.Exclusions, Settings.SetExclusions, false)));
            ex.Children.Add(exButtons);
            exclusions.Col(L.T("Папка"), 900);
            exclusions.PathOf = r => (string)r.Tag;
            ex.Children.Add(exclusions);

            // ---------------- свои места
            var cu = new StackPanel();
            cu.Children.Add(X.Txt(L.T("Свои места очистки"), 16, true));
            cu.Children.Add(Note(L.T("Ваши папки с мусором (логи, временные файлы программ). Появятся на странице очистки в группе «Свои места», " +
                                     "по умолчанию не отмечены. Удаляется содержимое папки; с маской — только подходящие файлы прямо в ней.")));
            var cuButtons = new WrapPanel();
            var maskLabel = X.Txt(L.T("Маска (необязательно, например *.log):"));
            maskLabel.Margin = new Thickness(0, 0, 8, 8);
            cuButtons.Children.Add(maskLabel);
            cuButtons.Children.Add(maskBox);
            cuButtons.Children.Add(X.Btn(L.T("Добавить папку…"), AddCustom, "AccentButton"));
            cuButtons.Children.Add(X.Btn(L.T("Убрать выбранные"), () => Remove(custom, Settings.CustomTargets, Settings.SetCustomTargets, true)));
            cu.Children.Add(cuButtons);
            custom.Col(L.T("Папка"), 700);
            custom.Col(L.T("Маска"), 200);
            custom.PathOf = r => ((string)r.Tag).Split('|')[0];
            cu.Children.Add(custom);

            // ---------------- автоочистка
            var ac = new StackPanel();
            ac.Children.Add(X.Txt(L.T("Автоочистка по расписанию"), 16, true));
            ac.Children.Add(Note(L.T("Программа сама, без окна, чистит только рекомендуемые места — те, что отмечены по умолчанию на странице очистки " +
                                     "(кэши, логи, временные файлы), с учётом исключений. Создаётся задача в Планировщике Windows; отчёт пишется в журнал.")));
            ac.Children.Add(scheduleHost);
            var acButtons = new WrapPanel();
            acButtons.Children.Add(X.Btn(L.T("Открыть журнал автоочистки"), () =>
            {
                if (File.Exists(AutoClean.LogPath)) X.ShowInExplorer(AutoClean.LogPath);
                else Dlg.Info(Window.GetWindow(this), L.T("Настройки"), L.T("Журнала ещё нет: автоочистка ни разу не запускалась."));
            }));
            ac.Children.Add(acButtons);

            // ---------------- статистика
            var st = new StackPanel();
            st.Children.Add(X.Txt(L.T("Статистика"), 16, true));
            stats.Margin = new Thickness(0, 8, 0, 0);
            st.Children.Add(stats);

            foreach (var panel in new[] { ex, cu, ac, st })
            {
                var card = X.Card(panel, 20);
                card.Margin = new Thickness(0, 16, 0, 0);
                root.Children.Add(card);
            }
            Content = root;
            Loaded += delegate { Refresh(); };
        }

        static TextBlock Note(string text)
        {
            var t = X.Txt(text, 12, false, "SubText");
            t.Margin = new Thickness(0, 6, 0, 12);
            t.MaxWidth = 1000;
            t.HorizontalAlignment = HorizontalAlignment.Left;
            return t;
        }

        void Refresh()
        {
            exclusions.SetRows(Settings.Exclusions.Select(p => new Row(p, p)).ToList());
            custom.SetRows(Settings.CustomTargets.Select(s =>
            {
                var parts = s.Split('|');
                return new Row(s, parts[0], parts.Length > 1 ? parts[1] : L.T("всё содержимое"));
            }).ToList());
            stats.Text = string.Format(L.T("Освобождено за всё время: {0} (очисток: {1})"), Util.FormatSize(Settings.TotalFreed), Settings.Cleanups);
            scheduleHost.Child = X.Chips(new[] { L.T("Выключено"), L.T("Раз в неделю"), L.T("Раз в месяц") }, AutoClean.ScheduleMode, SetSchedule);
        }

        void SetSchedule(int mode)
        {
            string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            if (AutoClean.SetSchedule(mode, exe)) return;
            Dlg.Info(Window.GetWindow(this), L.T("Настройки"), L.T("Не удалось создать задачу в Планировщике Windows."));
            Dispatcher.BeginInvoke(new Action(Refresh));      // вернуть переключатель в настоящее состояние
        }

        void AddExclusion()
        {
            string p = X.PickFolder(null, L.T("Папка, которую никогда не трогать"));
            if (p == null) return;
            Settings.SetExclusions(Settings.Exclusions.Concat(new[] { p }));
            Refresh();
        }

        void AddCustom()
        {
            string p = X.PickFolder(null, L.T("Папка, которую нужно очищать"));
            if (p == null) return;
            string mask = maskBox.Text.Trim();
            if (mask.IndexOfAny(new[] { '\\', '/', '|' }) >= 0) { Dlg.Info(Window.GetWindow(this), L.T("Настройки"), L.T("Маска — это только имя файла со звёздочками, без папок.")); return; }
            if (Targets.IsDangerous(p, mask.Length > 0 ? mask : null))
            {
                Dlg.Info(Window.GetWindow(this), L.T("Настройки"), L.T("Эту папку нельзя очищать целиком: это корень диска или системная папка (Windows, профиль пользователя, AppData, Program Files…)."));
                return;
            }
            Settings.SetCustomTargets(Settings.CustomTargets.Concat(new[] { mask.Length > 0 ? p + "|" + mask : p }));
            Refresh();
            if (CustomChanged != null) CustomChanged();
        }

        void Remove(CheckList list, IList<string> current, Action<IEnumerable<string>> save, bool isCustom)
        {
            var selected = new HashSet<string>(list.SelectedRows().Select(r => (string)r.Tag), StringComparer.OrdinalIgnoreCase);
            if (selected.Count == 0) return;
            save(current.Where(x => !selected.Contains(x)).ToList());
            Refresh();
            if (isCustom && CustomChanged != null) CustomChanged();
        }
    }

    /// <summary>Окно предпросмотра: точный список файлов, которые удалит очистка.</summary>
    class PreviewWindow : Window
    {
        public PreviewWindow(Window owner, List<PreviewFile> files, long totalFiles, long totalBytes, List<string> notes)
        {
            Title = L.T("Предпросмотр — что будет удалено");
            Owner = owner;
            Width = 1000; Height = 680; MinWidth = 640; MinHeight = 400;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            FontFamily = new System.Windows.Media.FontFamily("Segoe UI");
            FontSize = 13;
            SetResourceReference(BackgroundProperty, "Bg");
            SetResourceReference(ForegroundProperty, "Text");
            SourceInitialized += delegate { Palette.ApplyTitleBar(this); };

            files.Sort((a, b) => b.Bytes.CompareTo(a.Bytes));
            var head = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
            head.Children.Add(X.Txt(string.Format(L.T("Файлов: {0}, всего {1}. Ничего ещё не удалено."), Util.FormatCount(totalFiles), Util.FormatSize(totalBytes)), 15, true));
            if (totalFiles > files.Count)
                head.Children.Add(X.Txt(string.Format(L.T("Показаны первые {0} файлов; в сохранённый список попадут они же."), Util.FormatCount(files.Count)), 12, false, "SubText"));
            foreach (string n in notes) head.Children.Add(X.Txt(n, 12, false, "Warn"));

            var list = new CheckList(false);
            list.Col(L.T("Файл"), 560);
            list.Col(L.T("Размер"), 110, true);
            list.Col(L.T("Что очищать"), 280);
            list.PathOf = r => ((PreviewFile)r.Tag).Path;
            list.PathIsFile = true;
            list.SetRows(new LazyRows<PreviewFile>(files, f => new Row(f, f.Path, Util.FormatSize(f.Bytes), f.Target)));

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
            buttons.Children.Add(X.Btn(L.T("Сохранить список…"), () => Save(files)));
            var close = X.Btn(L.T("Закрыть"), Close, "AccentButton");
            close.IsCancel = true; close.IsDefault = true; close.Margin = new Thickness(0, 0, 0, 8);
            buttons.Children.Add(close);

            var panel = new DockPanel { Margin = new Thickness(20, 16, 20, 8) };
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(head);
            panel.Children.Add(buttons);
            panel.Children.Add(list);
            Content = panel;
        }

        void Save(List<PreviewFile> files)
        {
            var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "Text (*.txt)|*.txt", FileName = "wincleaner-preview.txt" };
            if (dlg.ShowDialog(this) != true) return;
            try
            {
                var sb = new StringBuilder();
                foreach (var f in files) sb.Append(f.Bytes).Append('\t').Append(f.Target).Append('\t').AppendLine(f.Path);
                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) { Dlg.Info(this, Title, L.T("ОШИБКА: ") + ex.Message); }
        }
    }
}
