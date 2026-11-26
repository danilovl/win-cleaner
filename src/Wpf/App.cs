// WinCleaner — версия с интерфейсом на WPF.
// Логика (папка Core) общая с версией на WinForms; здесь только другой интерфейс:
// боковое меню, обзор дисков, светлая и тёмная тема, сворачиваемые группы.
// Собирается тем же встроенным в Windows компилятором C# — см. build.cmd. Код на C# 5, без XAML-компиляции:
// оформление лежит в Theme.xaml (встроенный ресурс, читается при запуске), разметка страниц строится в коде.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("WinCleaner")]
[assembly: System.Reflection.AssemblyProduct("WinCleaner")]
[assembly: System.Reflection.AssemblyVersion("1.3.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.3.0.0")]

namespace Danilovl.WinCleaner.Wpf
{
    static class App
    {
        [STAThread]
        static void Main(string[] args)
        {
            L.Load();
            // запуск по расписанию: очистка рекомендуемых мест без окна, отчёт — в журнал
            if (Array.IndexOf(args, AutoClean.Argument) >= 0) { AutoClean.Run(); return; }

            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;   // непредвиденная ошибка не должна молча закрывать программу посреди операции
                MessageBox.Show(L.T("Непредвиденная ошибка:\r\n\r\n") + e.Exception, "WinCleaner", MessageBoxButton.OK, MessageBoxImage.Error);
            };

            Palette.Apply(Palette.LoadTheme());
            using (var s = typeof(App).Assembly.GetManifestResourceStream("Theme.xaml"))
                app.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Load(s));

            app.Run(new MainWindow());
        }
    }

    /// <summary>
    /// Цвета интерфейса. При смене темы кисти в ресурсах приложения заменяются новыми (менять цвет у существующих нельзя:
    /// WPF «замораживает» всё, что лежит в ресурсах). Всё, что ссылается на них через DynamicResource, перекрашивается само;
    /// списки дополнительно перечитывают цвета строк по событию Changed.
    /// </summary>
    static class Palette
    {
        const string RegKey = @"Software\WinCleaner";

        static readonly string[] Keys = { "Bg", "Panel", "Card", "Line", "Text", "SubText", "Accent", "AccentText", "Hover", "Selected", "Danger", "Warn", "Ok" };
        static readonly string[] Light = { "#F3F3F3", "#E9E9E9", "#FFFFFF", "#DCDCDC", "#1B1B1B", "#5F5F5F", "#0067C0", "#FFFFFF", "#12000000", "#450067C0", "#C42B1C", "#9D5D00", "#0F7B0F" };
        static readonly string[] Dark = { "#202020", "#191919", "#2B2B2B", "#3D3D3D", "#F2F2F2", "#A8A8A8", "#60CDFF", "#00223A", "#16FFFFFF", "#4A60CDFF", "#E5484D", "#F5C518", "#6CCB5F" };
        // OLED: настоящий чёрный фон (пиксели выключены), без больших светлых плоскостей; карточки отделены тонкой линией
        static readonly string[] Oled = { "#000000", "#000000", "#0A0A0A", "#2A2A2A", "#E6E6E6", "#8C8C8C", "#4CC2FF", "#001824", "#14FFFFFF", "#454CC2FF", "#E5484D", "#E0B400", "#5DBB52" };

        /// <summary>0 — светлая, 1 — тёмная, 2 — чёрная для OLED.</summary>
        public static int Theme { get; private set; }
        public static bool IsDark { get { return Theme != 0; } }

        /// <summary>Тема изменилась — тем, кто держит кисти напрямую, пора взять новые.</summary>
        public static event Action Changed;

        public static void Apply(int theme)
        {
            Theme = theme < 0 || theme > 2 ? 0 : theme;
            var res = Application.Current.Resources;
            string[] colors = Theme == 2 ? Oled : Theme == 1 ? Dark : Light;
            for (int i = 0; i < Keys.Length; i++)
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
                b.Freeze();
                res[Keys[i]] = b;
            }
            var h = Changed;
            if (h != null) h();
        }

        public static Brush Get(string key) { return (Brush)Application.Current.Resources[key]; }

        /// <summary>Сохранённый выбор темы; если его нет — светлая или тёмная, как в настройках Windows.</summary>
        public static int LoadTheme()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    string v = k == null ? null : k.GetValue("Theme") as string;
                    if (v != null) return v == "oled" ? 2 : v == "dark" ? 1 : 0;
                }
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                    return v != null && Convert.ToInt32(v) == 0 ? 1 : 0;
                }
            }
            catch { return 0; }
        }

        public static void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
                    if (k != null) k.SetValue("Theme", Theme == 2 ? "oled" : Theme == 1 ? "dark" : "light");
            }
            catch { }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Тёмный или светлый заголовок окна (Windows 10 2004+ / 11).</summary>
        public static void ApplyTitleBar(Window w)
        {
            try
            {
                IntPtr h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int on = IsDark ? 1 : 0;
                DwmSetWindowAttribute(h, 20, ref on, 4);
            }
            catch { }
        }
    }
}
