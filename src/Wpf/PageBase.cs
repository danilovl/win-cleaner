using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Danilovl.WinCleaner.Wpf
{
    /// <summary>
    /// Общий каркас страницы: заголовок с пояснением, панель кнопок, строка итогов, содержимое,
    /// сворачиваемый журнал и строка состояния с индикатором хода работы.
    /// </summary>
    abstract class PageBase : Grid
    {
        protected readonly WrapPanel Toolbar = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        protected readonly TextBlock Summary = X.Txt("", 15, true);
        protected readonly Border Body = new Border();
        readonly TextBox logBox = new TextBox();
        readonly TextBlock statusText = X.Txt("", 12, false, "SubText");
        readonly ProgressBar bar = new ProgressBar { Width = 220, Visibility = Visibility.Collapsed, Margin = new Thickness(12, 0, 0, 0) };
        protected readonly Button StopButton;
        readonly RowDefinition logRow = new RowDefinition { Height = new GridLength(130) };
        bool shown;

        protected CancellationTokenSource Cts;
        public bool Busy { get; private set; }
        /// <summary>Сообщает главному окну, что страница начала или закончила операцию.</summary>
        public Action BusyChanged;

        protected PageBase(string title, string description)
        {
            Margin = new Thickness(28, 22, 28, 12);
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 0 заголовок
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 1 кнопки
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 2 итог
            RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });  // 3 содержимое
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 4 заголовок журнала
            RowDefinitions.Add(logRow);                                                               // 5 журнал
            RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                       // 6 состояние

            var head = new StackPanel();
            head.Children.Add(X.Txt(title, 24, true));
            if (!string.IsNullOrEmpty(description))
            {
                var d = X.Txt(description, 13, false, "SubText");
                d.Margin = new Thickness(0, 6, 0, 0);
                d.MaxWidth = 1100;
                d.HorizontalAlignment = HorizontalAlignment.Left;
                head.Children.Add(d);
            }
            Place(head, 0);
            Place(Toolbar, 1);
            Summary.Margin = new Thickness(0, 4, 0, 10);
            Place(Summary, 2);
            Place(Body, 3);

            // журнал: щелчок по заголовку сворачивает его
            var logHead = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 6), Cursor = System.Windows.Input.Cursors.Hand, Background = Brushes.Transparent };
            var arrow = X.Icon("", 10, "SubText");
            arrow.Margin = new Thickness(0, 0, 8, 0);
            logHead.Children.Add(arrow);
            logHead.Children.Add(X.Txt(L.T("Журнал"), 12, true, "SubText"));
            logHead.MouseLeftButtonUp += delegate
            {
                bool open = logBox.Visibility != Visibility.Visible;
                logBox.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
                logRow.Height = open ? new GridLength(130) : new GridLength(0);
                arrow.Text = open ? "" : "";
            };
            Place(logHead, 4);
            logBox.SetResourceReference(StyleProperty, "LogBox");
            Place(logBox, 5);

            var status = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
            DockPanel.SetDock(bar, Dock.Right);
            status.Children.Add(bar);
            statusText.TextWrapping = TextWrapping.NoWrap;
            statusText.TextTrimming = TextTrimming.CharacterEllipsis;
            status.Children.Add(statusText);
            Place(status, 6);

            StopButton = X.Btn(L.T("Стоп"), delegate { if (Cts != null) Cts.Cancel(); });
            StopButton.Visibility = Visibility.Collapsed;

            Loaded += delegate
            {
                if (shown) return;
                shown = true;
                OnFirstShown();
                UpdateState();
            };
        }

        void Place(UIElement e, int row) { SetRow(e, row); Children.Add(e); }

        /// <summary>Вызывается один раз, когда страницу открыли впервые.</summary>
        protected virtual void OnFirstShown() { }
        /// <summary>Включить/выключить кнопки и обновить строку итогов.</summary>
        protected abstract void UpdateState();

        protected Window Owner { get { return Window.GetWindow(this); } }

        // ------------------------------------------------------------ можно вызывать из любого потока

        protected void Post(Action a)
        {
            if (Dispatcher.HasShutdownStarted) return;
            if (Dispatcher.CheckAccess()) a(); else Dispatcher.BeginInvoke(a);
        }

        protected void Log(string s)
        {
            Post(() => logBox.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + s + Environment.NewLine));
            Post(() => logBox.ScrollToEnd());
        }

        protected void ClearLog() { logBox.Clear(); }

        protected void Status(string s) { Post(() => statusText.Text = s); }

        /// <summary>pct &lt; 0 — «бегущая» полоса, иначе проценты.</summary>
        protected void Progress(int pct)
        {
            Post(() =>
            {
                bar.IsIndeterminate = pct < 0;
                if (pct >= 0) bar.Value = Math.Max(0, Math.Min(100, pct));
            });
        }

        protected void SetBusy(bool busy)
        {
            Busy = busy;
            bar.Visibility = StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) { bar.IsIndeterminate = true; Cts = new CancellationTokenSource(); }
            UpdateState();
            if (BusyChanged != null) BusyChanged();
        }

        /// <summary>
        /// Запускает работу в фоне. work получает токен остановки; done вызывается в потоке окна после завершения
        /// (в том числе после остановки или ошибки — они записываются в журнал).
        /// </summary>
        protected void Run(Action<CancellationToken> work, Action done, bool sta = false)
        {
            if (Busy) return;
            SetBusy(true);
            var ct = Cts.Token;
            var t = new Thread(() =>
            {
                try { work(ct); }
                catch (OperationCanceledException) { Log(L.T("Остановлено.")); }
                catch (Exception ex) { Log(L.T("ОШИБКА: ") + ex.Message); }
                Post(() =>
                {
                    SetBusy(false);
                    if (done != null) done();
                });
            }) { IsBackground = true };
            if (sta) t.SetApartmentState(ApartmentState.STA);   // для диалогов Корзины
            t.Start();
        }

        /// <summary>
        /// Программа закрывается: остановить то, что страница запустила. Внешние утилиты (cipher, diskpart, docker)
        /// иначе продолжили бы работать уже без окна.
        /// </summary>
        public virtual void Shutdown()
        {
            try { if (Cts != null) Cts.Cancel(); } catch { }
        }

        protected bool Ask(string title, string text, bool danger = false) { return Dlg.Ask(Owner, title, text, danger); }
        protected void Info(string title, string text) { Dlg.Info(Owner, title, text); }
    }
}
