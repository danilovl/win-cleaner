using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Danilovl.WinCleaner.Wpf
{
    /// <summary>Мелкие «кирпичики» интерфейса.</summary>
    static class X
    {
        public static readonly FontFamily Icons = new FontFamily("Segoe MDL2 Assets");

        public static TextBlock Txt(string text, double size = 13, bool bold = false, string brush = "Text")
        {
            var t = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (bold) t.FontWeight = FontWeights.SemiBold;
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        /// <summary>Значок из шрифта Segoe MDL2 Assets (есть в Windows 10 и 11).</summary>
        public static TextBlock Icon(string glyph, double size = 16, string brush = "Text")
        {
            var t = new TextBlock { Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center };
            t.SetResourceReference(TextBlock.ForegroundProperty, brush);
            return t;
        }

        public static Button Btn(string text, Action click, string style = null)
        {
            var b = new Button { Content = text };
            if (style != null) b.SetResourceReference(FrameworkElement.StyleProperty, style);
            b.Click += delegate { click(); };
            return b;
        }

        public static CheckBox Chk(string text, bool isChecked, Action changed = null)
        {
            var c = new CheckBox { Content = text, IsChecked = isChecked, Margin = new Thickness(0, 0, 16, 8), VerticalAlignment = VerticalAlignment.Center };
            if (changed != null) { c.Checked += delegate { changed(); }; c.Unchecked += delegate { changed(); }; }
            return c;
        }

        public static bool On(this CheckBox c) { return c.IsChecked == true; }

        /// <summary>Карточка со скруглёнными углами.</summary>
        public static Border Card(UIElement child, double padding = 16)
        {
            var b = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(padding), Child = child };
            b.SetResourceReference(Border.BackgroundProperty, "Card");
            b.SetResourceReference(Border.BorderBrushProperty, "Line");
            return b;
        }

        /// <summary>Набор «таблеток» — выбор одного варианта из нескольких (замена выпадающего списка).</summary>
        public static WrapPanel Chips(string[] options, int selected, Action<int> changed)
        {
            var panel = new WrapPanel();
            string group = Guid.NewGuid().ToString("N");
            for (int i = 0; i < options.Length; i++)
            {
                int index = i;
                var rb = new RadioButton { Content = options[i], GroupName = group, IsChecked = i == selected };
                rb.SetResourceReference(FrameworkElement.StyleProperty, "Chip");
                rb.Checked += delegate { changed(index); };
                panel.Children.Add(rb);
            }
            return panel;
        }

        public static int SelectedChip(WrapPanel chips)
        {
            for (int i = 0; i < chips.Children.Count; i++)
                if (((RadioButton)chips.Children[i]).IsChecked == true) return i;
            return -1;
        }

        public static void OpenFolder(string path)
        {
            try { Process.Start("explorer.exe", "\"" + path + "\""); } catch { }
        }

        public static void ShowInExplorer(string file)
        {
            try { Process.Start("explorer.exe", "/select,\"" + file + "\""); } catch { }
        }

        public static string Age(DateTime t)
        {
            if (t == DateTime.MinValue) return "";
            var d = DateTime.Now - t;
            if (d.TotalDays < 1) return L.T("сегодня");
            if (d.TotalDays < 31) return (int)d.TotalDays + L.T(" дн. назад");
            if (d.TotalDays < 365) return (int)(d.TotalDays / 30) + L.T(" мес. назад");
            return (d.TotalDays / 365).ToString("0.#") + L.T(" г. назад");
        }

        public static string DateWithAge(DateTime t)
        {
            return t == DateTime.MinValue ? "" : t.ToString("yyyy-MM-dd") + "  (" + Age(t) + ")";
        }

        /// <summary>Выбор папки (стандартный диалог Windows).</summary>
        public static string PickFolder(string start, string description = null)
        {
            using (var dlg = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = start ?? "", Description = description ?? "" })
                return dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
        }
    }

    // ================================================================ диалоги

    /// <summary>Окна подтверждения и сообщений в стиле приложения (вместо системного MessageBox).</summary>
    static class Dlg
    {
        public static bool Ask(Window owner, string title, string text, bool danger = false, string yes = null)
        {
            return Show(owner, title, text, yes ?? L.T("Продолжить"), L.T("Отмена"), danger);
        }

        public static void Info(Window owner, string title, string text)
        {
            Show(owner, title, text, "OK", null, false);
        }

        static bool Show(Window owner, string title, string text, string yes, string no, bool danger)
        {
            var w = new Window
            {
                Title = title, Owner = owner, WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
                SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
                MinWidth = 420, MaxWidth = 760, MaxHeight = 640, FontFamily = new FontFamily("Segoe UI"), FontSize = 13
            };
            w.SetResourceReference(Control.BackgroundProperty, "Bg");
            w.SetResourceReference(Control.ForegroundProperty, "Text");
            w.SourceInitialized += delegate { Palette.ApplyTitleBar(w); };

            var body = X.Txt(text.TrimEnd(), 13);
            body.Margin = new Thickness(0, 0, 0, 20);
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 480 };

            bool result = false;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            if (no != null)
            {
                var bNo = X.Btn(no, delegate { w.Close(); });
                bNo.IsCancel = true;
                bNo.IsDefault = danger;        // для опасных действий Enter = «Отмена»
                buttons.Children.Add(bNo);
            }
            var bYes = X.Btn(yes, delegate { result = true; w.Close(); }, danger ? "DangerButton" : "AccentButton");
            bYes.IsDefault = !danger || no == null;
            if (no == null) bYes.IsCancel = true;
            bYes.Margin = new Thickness(0, 0, 0, 8);
            buttons.Children.Add(bYes);

            var panel = new DockPanel { Margin = new Thickness(24, 20, 24, 12) };
            DockPanel.SetDock(buttons, Dock.Bottom);
            panel.Children.Add(buttons);
            panel.Children.Add(scroll);
            w.Content = panel;
            w.ShowDialog();
            return result;
        }
    }

    // ================================================================ список с галочками

    /// <summary>Строка списка: ячейки-строки, галочка, цвет, группа. Данные лежат в Tag.</summary>
    class Row : INotifyPropertyChanged
    {
        readonly string[] cells;
        bool isChecked;
        bool canCheck = true;
        string colorKey = "Text";
        FontWeight weight = FontWeights.Normal;

        public object Tag;
        public Action<Row> CheckedChanged;
        /// <summary>Идёт массовая смена галочек: обработчикам не нужно пересчитывать итоги на каждой строке.</summary>
        [ThreadStatic] public static bool Bulk;
        public event PropertyChangedEventHandler PropertyChanged;

        public Row(object tag, params string[] cells) { Tag = tag; this.cells = cells; }

        void Raise(string name) { var h = PropertyChanged; if (h != null) h(this, new PropertyChangedEventArgs(name)); }

        public string this[int i]
        {
            get { return i < cells.Length ? cells[i] : ""; }
            set { if (cells[i] == value) return; cells[i] = value; Raise("Item[]"); }
        }

        public bool Checked
        {
            get { return isChecked; }
            set
            {
                if (value && !canCheck) { Raise("Checked"); return; }
                if (isChecked == value) return;
                isChecked = value;
                Raise("Checked");
                if (CheckedChanged != null) CheckedChanged(this);
            }
        }

        /// <summary>Меняет галочку без вызова CheckedChanged (массовые операции).</summary>
        public void SetCheckedSilently(bool value) { if (isChecked != value) { isChecked = value; Raise("Checked"); } }

        public bool CanCheck { get { return canCheck; } set { canCheck = value; Raise("CanCheck"); } }
        /// <summary>Имя цвета из палитры (Text, SubText, Accent, Warn, Ok …).</summary>
        public string ColorKey { get { return colorKey; } set { if (colorKey != value) { colorKey = value; Raise("Color"); } } }
        /// <summary>Кисть для привязки: всегда из текущей темы.</summary>
        public Brush Color { get { return Palette.Get(colorKey); } }
        public FontWeight Weight { get { return weight; } set { weight = value; Raise("Weight"); } }
        public string Group { get; set; }
        public string Tip { get; set; }
    }

    /// <summary>
    /// Список-таблица: столбцы, необязательные галочки, сортировка по щелчку на заголовке, группы,
    /// двойной щелчок и меню по правой кнопке для открытия в Проводнике.
    /// </summary>
    class CheckList : ListView
    {
        readonly GridView view = new GridView { AllowsColumnReorder = false };
        readonly List<string> titles = new List<string>();
        readonly bool checks;
        readonly CheckBox checkAll = new CheckBox { Margin = new Thickness(0), ToolTip = L.T("Отметить или снять все строки списка") };
        int firstDataColumn;

        /// <summary>Номер столбца данных, по которому щёлкнули.</summary>
        public event Action<int> HeaderClicked;
        /// <summary>Путь для строки: открыть в Проводнике / скопировать. null — нет пути.</summary>
        public Func<Row, string> PathOf;
        /// <summary>
        /// Своя обработка флажка «отметить все» — для списков «по требованию», где строки создаются только для видимой части
        /// и перебирать их все нельзя: страница сама меняет отметки в данных.
        /// </summary>
        public Action<bool> CheckAllOverride;
        public bool PathIsFile;

        public CheckList(bool withChecks)
        {
            checks = withChecks;
            // WPF ищет стиль по точному типу элемента: для наследника ListView стиль из темы надо назначить явно,
            // иначе список остаётся со стандартным белым фоном
            SetResourceReference(StyleProperty, typeof(ListView));
            View = view;
            SelectionMode = SelectionMode.Extended;
            if (checks)
            {
                var box = new FrameworkElementFactory(typeof(CheckBox));
                box.SetBinding(ToggleButton.IsCheckedProperty, new Binding("Checked") { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                box.SetBinding(IsEnabledProperty, new Binding("CanCheck"));
                box.SetValue(MarginProperty, new Thickness(6, 0, 0, 0));
                view.Columns.Add(new GridViewColumn { Width = 40, Header = checkAll, CellTemplate = new DataTemplate { VisualTree = box } });
                checkAll.Click += delegate { SetAll(checkAll.IsChecked == true); };
                firstDataColumn = 1;
            }
            AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnHeaderClick));
            MouseDoubleClick += delegate { OpenSelected(); };
            KeyDown += (s, e) =>
            {
                // пробел ставит/снимает галочку у всех выделенных строк
                if (e.Key != Key.Space || !checks || SelectedItems.Count == 0) return;
                bool value = !((Row)SelectedItems[0]).Checked;
                var selected = new List<Row>();
                foreach (Row r in SelectedItems) selected.Add(r);
                SetMany(selected, value);
                e.Handled = true;
            };
            BuildMenu();

            // после смены темы строки должны взять цвета из новой палитры
            Action recolor = () => { if (ItemsSource != null) Items.Refresh(); };
            Loaded += delegate { Palette.Changed -= recolor; Palette.Changed += recolor; };
            Unloaded += delegate { Palette.Changed -= recolor; };
        }

        public void Col(string header, double width, bool right = false)
        {
            int index = titles.Count;
            titles.Add(header);
            var tb = new FrameworkElementFactory(typeof(TextBlock));
            tb.SetBinding(TextBlock.TextProperty, new Binding("[" + index + "]"));
            tb.SetBinding(TextBlock.ForegroundProperty, new Binding("Color"));
            tb.SetBinding(TextBlock.FontWeightProperty, new Binding("Weight"));
            tb.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
            tb.SetValue(MarginProperty, new Thickness(4, 0, 4, 0));
            if (right) tb.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);
            var header1 = new GridViewColumnHeader { Content = header, Tag = index };
            if (right) header1.HorizontalContentAlignment = HorizontalAlignment.Right;
            view.Columns.Add(new GridViewColumn { Header = header1, Width = width, CellTemplate = new DataTemplate { VisualTree = tb } });
        }

        /// <summary>Ставит или снимает галочку у всех строк списка (кроме тех, которые отмечать нельзя).</summary>
        public void SetAll(bool value)
        {
            if (CheckAllOverride != null) { CheckAllOverride(value); checkAll.IsChecked = value; return; }
            var rows = new List<Row>();
            foreach (object o in Items) { var r = o as Row; if (r != null) rows.Add(r); }
            SetMany(rows, value);
            checkAll.IsChecked = value;
        }

        /// <summary>Массовая смена галочек: итоги на странице пересчитываются один раз, в конце.</summary>
        void SetMany(List<Row> rows, bool value)
        {
            Row last = null;
            Row.Bulk = true;
            try
            {
                foreach (var r in rows)
                {
                    if (!r.CanCheck || r.Checked == value) continue;
                    r.Checked = value;
                    last = r;
                }
            }
            finally { Row.Bulk = false; }
            if (last != null && last.CheckedChanged != null) last.CheckedChanged(last);
        }

        void OnHeaderClick(object sender, RoutedEventArgs e)
        {
            var h = e.OriginalSource as GridViewColumnHeader;
            if (h == null || !(h.Tag is int) || HeaderClicked == null) return;
            HeaderClicked((int)h.Tag);
        }

        /// <summary>Состояние флажка «отметить все» (когда страница ведёт отметки сама).</summary>
        public void SetHeaderChecked(bool value) { checkAll.IsChecked = value; }

        /// <summary>Стрелка сортировки в заголовке (column &lt; 0 — убрать).</summary>
        public void SortArrow(int column, bool desc)
        {
            for (int i = 0; i < titles.Count; i++)
            {
                var h = (GridViewColumnHeader)view.Columns[i + firstDataColumn].Header;
                h.Content = titles[i] + (i == column ? (desc ? "  ▼" : "  ▲") : "");
            }
        }

        /// <summary>Показывает строки. grouped — группировать по Row.Group (порядок групп = порядок появления в списке).</summary>
        public void SetRows(IList rows, bool grouped = false)
        {
            GroupStyle.Clear();
            if (checks && rows is List<Row>)       // список «по требованию» не перебираем: это создало бы все его строки
            {
                // флажок в заголовке показывает «отмечено всё»
                bool all = rows.Count > 0;
                foreach (object o in rows) { var r = o as Row; if (r != null && r.CanCheck && !r.Checked) { all = false; break; } }
                checkAll.IsChecked = all;
            }
            if (!grouped) { ItemsSource = rows; return; }
            var cv = new ListCollectionView(rows);
            cv.GroupDescriptions.Add(new PropertyGroupDescription("Group"));
            GroupStyle.Add(new GroupStyle { ContainerStyle = (Style)FindResource("GroupContainer") });
            ItemsSource = cv;
        }

        public IEnumerable<Row> SelectedRows()
        {
            foreach (object o in SelectedItems) { var r = o as Row; if (r != null) yield return r; }
        }

        void OpenSelected()
        {
            var r = SelectedItem as Row;
            string p = r == null || PathOf == null ? null : PathOf(r);
            if (p == null) return;
            if (PathIsFile) X.ShowInExplorer(p); else X.OpenFolder(p);
        }

        void BuildMenu()
        {
            var menu = new ContextMenu();
            var miOpen = new MenuItem();
            var miCopy = new MenuItem { Header = L.T("Копировать путь") };
            var miCheck = new MenuItem { Header = L.T("Отметить выделенные") };
            var miUncheck = new MenuItem { Header = L.T("Снять отметку с выделенных") };
            miOpen.Click += delegate { OpenSelected(); };
            miCopy.Click += delegate
            {
                var sb = new StringBuilder();
                foreach (Row r in SelectedRows()) { string p = PathOf == null ? null : PathOf(r); if (p != null) sb.AppendLine(p); }
                try { if (sb.Length > 0) Clipboard.SetText(sb.ToString().TrimEnd()); } catch { }
            };
            miCheck.Click += delegate { foreach (Row r in SelectedRows()) r.Checked = true; };
            miUncheck.Click += delegate { foreach (Row r in SelectedRows()) r.Checked = false; };
            menu.Items.Add(miOpen);
            menu.Items.Add(miCopy);
            if (checks) { menu.Items.Add(miCheck); menu.Items.Add(miUncheck); }
            menu.Opened += delegate
            {
                miOpen.Header = PathIsFile ? L.T("Показать в Проводнике") : L.T("Открыть папку");
                var r = SelectedItem as Row;
                bool has = r != null && PathOf != null && PathOf(r) != null;
                miOpen.IsEnabled = miCopy.IsEnabled = has;
                miCheck.IsEnabled = miUncheck.IsEnabled = r != null;
            };
            ContextMenu = menu;
        }
    }

    /// <summary>
    /// Список «по требованию» для очень больших таблиц (сотни тысяч удалённых файлов):
    /// строка интерфейса создаётся только для тех записей, которые сейчас видны на экране.
    /// </summary>
    class LazyRows<T> : IList, INotifyCollectionChanged
    {
        readonly IList<T> items;
        readonly Func<T, Row> make;
        readonly Dictionary<int, Row> cache = new Dictionary<int, Row>();

        public LazyRows(IList<T> items, Func<T, Row> make) { this.items = items; this.make = make; }

        public event NotifyCollectionChangedEventHandler CollectionChanged { add { } remove { } }

        public object this[int index]
        {
            get
            {
                Row r;
                if (cache.TryGetValue(index, out r)) return r;
                r = make(items[index]);
                cache[index] = r;
                return r;
            }
            set { throw new NotSupportedException(); }
        }

        public int Count { get { return items.Count; } }
        public int IndexOf(object value) { var r = value as Row; return r == null ? -1 : items.IndexOf((T)r.Tag); }
        public bool Contains(object value) { return IndexOf(value) >= 0; }
        public IEnumerator GetEnumerator() { for (int i = 0; i < items.Count; i++) yield return this[i]; }
        public bool IsFixedSize { get { return true; } }
        public bool IsReadOnly { get { return true; } }
        public bool IsSynchronized { get { return false; } }
        public object SyncRoot { get { return this; } }
        public int Add(object value) { throw new NotSupportedException(); }
        public void Clear() { throw new NotSupportedException(); }
        public void Insert(int index, object value) { throw new NotSupportedException(); }
        public void Remove(object value) { throw new NotSupportedException(); }
        public void RemoveAt(int index) { throw new NotSupportedException(); }
        public void CopyTo(Array array, int index) { for (int i = 0; i < items.Count; i++) array.SetValue(this[i], index + i); }
    }
}
