using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace Danilovl.WinCleaner.Wpf
{
    /// <summary>Страница «Очистка мусора и кэшей»: сканирование мест, выбор, подтверждение, удаление.</summary>
    class CleanerPage : PageBase
    {
        readonly List<CleanTarget> targets = Targets.All();
        readonly Dictionary<CleanTarget, Row> rowOf = new Dictionary<CleanTarget, Row>();
        readonly CheckList list = new CheckList(true);
        readonly Button btnScan, btnSafe, btnNone, btnClean, btnPreview;
        readonly CheckBox chkMissing;
        // пока идёт подсчёт, размеры в списке растут на глазах
        readonly System.Windows.Threading.DispatcherTimer liveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };

        public CleanerPage()
            : base(L.T("Очистка мусора и кэшей"),
                   L.T("Места сгруппированы по видам. Рекомендуемое уже отмечено; то, что придётся скачивать заново или что меняет поведение системы, — нет."))
        {
            foreach (var t in targets) t.Checked = t.Safe;

            btnScan = X.Btn(L.T("Сканировать заново"), StartScan);
            btnSafe = X.Btn(L.T("Отметить рекомендуемые"), () => SetChecks(true));
            btnNone = X.Btn(L.T("Снять все"), () => SetChecks(false));
            btnClean = X.Btn(L.T("Очистить выбранные"), StartClean, "AccentButton");
            btnPreview = X.Btn(L.T("Предпросмотр…"), StartPreview);
            btnPreview.ToolTip = L.T("Показать точный список файлов, которые будут удалены, ничего не удаляя");
            chkMissing = X.Chk(L.T("Показывать ненайденные"), false, BuildRows);
            chkMissing.Margin = new Thickness(8, 0, 0, 8);
            foreach (UIElement e in new UIElement[] { btnClean, btnPreview, btnScan, btnSafe, btnNone, StopButton, chkMissing }) Toolbar.Children.Add(e);

            list.Col(L.T("Что очищать"), 380);
            list.Col(L.T("Размер"), 110, true);
            list.Col(L.T("Файлов"), 90, true);
            list.Col(L.T("Примечание"), 620);
            list.PathOf = r => { var p = ((CleanTarget)r.Tag).Resolved.FirstOrDefault(); return p == null ? null : p.Dir; };
            Body.Child = list;
            liveTimer.Tick += delegate { foreach (var r in rowOf.Values) Fill(r); UpdateState(); };
        }

        protected override void OnFirstShown() { BuildRows(); StartScan(); }

        // ------------------------------------------------------------ список

        int CategoryIndex(string cat)
        {
            for (int i = 0; i < targets.Count; i++) if (targets[i].Category == cat) return i;
            return 0;
        }

        void BuildRows()
        {
            bool scanDone = targets.All(x => x.Scanned);
            IEnumerable<CleanTarget> ordered = targets;
            if (scanDone)   // после сканирования — по убыванию размера внутри группы
                ordered = targets.Select((t, i) => new { t, i })
                                 .OrderBy(x => CategoryIndex(x.t.Category)).ThenByDescending(x => x.t.Bytes).ThenBy(x => x.i)
                                 .Select(x => x.t);
            var shown = ordered.Where(t => !(t.Scanned && !t.Exists && !chkMissing.On())).ToList();

            // заголовок группы: название, сколько можно освободить, число мест
            var headers = new Dictionary<string, string>();
            foreach (var g in shown.GroupBy(t => t.Category))
                headers[g.Key] = scanDone
                    ? string.Format("{0}   —   {1}  ·  {2}", g.Key,
                        Util.FormatSize(g.Where(t => t.Special != SpecialKind.Dism).Sum(t => t.Bytes)), g.Count())
                    : g.Key;

            rowOf.Clear();
            var rows = new List<Row>();
            foreach (var t in shown)
            {
                var r = new Row(t, t.Name, "", "", t.Note) { Group = headers[t.Category] };
                Fill(r);
                r.CheckedChanged = x => { ((CleanTarget)x.Tag).Checked = x.Checked; if (!Row.Bulk) UpdateState(); };
                rowOf[t] = r;
                rows.Add(r);
            }
            list.SetRows(rows, true);
            UpdateState();
        }

        void Fill(Row r)
        {
            var t = (CleanTarget)r.Tag;
            string size, files, color = "Text";
            if (t.Special == SpecialKind.Dism) { size = "—"; files = "—"; }
            else if (!t.Scanned)
            {
                // ещё считается: показываем, сколько уже насчитано
                var live = t.Live;
                size = live != null && live.Bytes > 0 ? Util.FormatSize(live.Bytes) + " …" : "…";
                files = live != null && live.Files > 0 ? Util.FormatCount(live.Files) : "";
                color = "SubText";
            }
            else if (!t.Exists) { size = t.Special == SpecialKind.ShadowCopies ? L.T("нет") : L.T("не найдено"); files = ""; color = "SubText"; }
            else if (t.Bytes == 0 && t.Files == 0) { size = L.T("пусто"); files = "0"; color = "SubText"; }
            else { size = Util.FormatSize(t.Bytes); files = Util.FormatCount(t.Files); }

            r[1] = size;
            r[2] = files;
            r.ColorKey = color;
            // всё, что крупнее 1 ГБ, выделяем жирным — сразу видно, где основное место
            r.Weight = t.Scanned && t.Bytes >= (1L << 30) ? FontWeights.SemiBold : FontWeights.Normal;
            r.SetCheckedSilently(t.Checked && (CleanerEngine.HasSomething(t) || !t.Scanned));

            var resolved = t.Resolved;
            var tip = new StringBuilder(t.Name);
            if (resolved.Count > 0)
            {
                tip.AppendLine();
                foreach (var p in resolved.Take(12)) tip.AppendLine(p.Dir + (p.Mask != null ? "\\" + p.Mask : "\\*"));
                if (resolved.Count > 12) tip.AppendLine(L.T("… и ещё ") + (resolved.Count - 12));
                tip.Append(L.T("(двойной клик — открыть папку)"));
            }
            r.Tip = tip.ToString();
        }

        void SetChecks(bool recommended)
        {
            foreach (var t in targets) t.Checked = recommended && t.Safe;
            foreach (var r in rowOf.Values) Fill(r);
            UpdateState();
        }

        List<CleanTarget> Selected()
        {
            return targets.Where(t => t.Checked && CleanerEngine.HasSomething(t)).ToList();
        }

        protected override void UpdateState()
        {
            long total = targets.Where(t => t.Scanned && t.Special != SpecialKind.Dism).Sum(t => t.Bytes);
            var sel = Selected();
            Summary.Text = string.Format(L.T("Можно освободить: {0}     Выбрано: {1} ({2} мест)"),
                Util.FormatSize(total), Util.FormatSize(sel.Sum(t => t.Bytes)), sel.Count);
            btnClean.IsEnabled = btnPreview.IsEnabled = !Busy && sel.Count > 0;
            btnScan.IsEnabled = btnSafe.IsEnabled = btnNone.IsEnabled = chkMissing.IsEnabled = !Busy;
            list.IsEnabled = !Busy;
        }

        // ------------------------------------------------------------ сканирование

        void StartScan()
        {
            if (Busy) return;
            foreach (var t in targets) t.Scanned = false;
            BuildRows();
            ClearLog();
            Log(L.T("Сканирование ") + targets.Count + L.T(" мест...") + "  " + FileWalker.WorkersText(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
            liveTimer.Start();
            Run(ct =>
            {
                Progress(0);
                var sw = Stopwatch.StartNew();
                try
                {
                    Progress(-1);
                    CleanerEngine.ScanAll(targets, ct, Status);
                    Log(string.Format(L.T("Готово за {0:N1} с."), sw.Elapsed.TotalSeconds));
                }
                finally
                {
                    foreach (var t in targets) if (!t.Scanned) { t.Scanned = true; t.Exists = false; }
                }
            }, () =>
            {
                liveTimer.Stop();
                BuildRows();
                Status(L.T("Отметьте, что очистить, и нажмите «Очистить выбранные». Двойной клик — открыть папку, правая кнопка — меню."));
            });
        }

        // ------------------------------------------------------------ предпросмотр

        void StartPreview()
        {
            var sel = Selected();
            if (sel.Count == 0 || Busy) return;
            const int max = 200000;
            List<PreviewFile> files = null;
            long totalFiles = 0, totalBytes = 0;
            Status(L.T("Составляю список файлов…"));
            Run(ct => files = CleanerEngine.ListFiles(sel, ct, max, out totalFiles, out totalBytes), () =>
            {
                Status("");
                if (files == null) return;
                // у этих мест нет списка файлов — предупреждаем отдельной строкой
                var notes = sel.Where(t => t.Special != SpecialKind.None)
                               .Select(t => string.Format(L.T("«{0}» очищается средствами Windows — в списке файлов его нет."), t.Name)).ToList();
                new PreviewWindow(Owner, files, totalFiles, totalBytes, notes).ShowDialog();
            });
        }

        // ------------------------------------------------------------ очистка

        void StartClean()
        {
            var sel = Selected();
            if (sel.Count == 0) return;

            var sb = new StringBuilder();
            sb.AppendLine(L.T("Будет очищено:"));
            const int maxLines = 18;
            foreach (var t in sel.OrderByDescending(x => x.Bytes).Take(maxLines))
                sb.AppendLine(string.Format("   • {0}  —  {1}", t.Name, t.Special == SpecialKind.Dism ? L.T("размер неизвестен") : Util.FormatSize(t.Bytes)));
            if (sel.Count > maxLines)
                sb.AppendLine(string.Format(L.T("   … и ещё {0} мест ({1})"), sel.Count - maxLines,
                    Util.FormatSize(sel.OrderByDescending(x => x.Bytes).Skip(maxLines).Sum(x => x.Bytes))));
            sb.AppendLine();
            sb.AppendLine(L.T("Всего: ") + Util.FormatSize(sel.Sum(t => t.Bytes)));
            sb.AppendLine(L.T("Файлы удаляются безвозвратно (не в корзину)."));

            var running = CleanerEngine.RunningApps(sel);
            if (running.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(L.T("Сейчас запущены — лучше закрыть их, иначе часть кэша не удалится:"));
                foreach (string r in running) sb.AppendLine("   • " + r);
            }
            if (sel.Any(t => t.TakeOwnership))
            {
                sb.AppendLine();
                sb.AppendLine(L.T("Для системных папок (Windows.old и т.п.) программа сначала станет их владельцем — это займёт несколько минут."));
            }
            if (sel.Any(t => t.Special == SpecialKind.Dism))
            {
                sb.AppendLine();
                sb.AppendLine(L.T("Очистка DISM может идти 5–30 минут, её нельзя прервать кнопкой «Стоп»."));
            }
            if (!Ask(L.T("Подтверждение очистки"), sb.ToString(), true)) return;

            ClearLog();
            string result = "";
            Run(ct =>
            {
                long freed = 0, failed = 0, failedBytes = 0, scheduled = 0, scheduledBytes = 0;
                bool cancelled = false;
                Progress(0);
                try
                {
                    for (int n = 0; n < sel.Count; n++)
                    {
                        var t = sel[n];
                        ct.ThrowIfCancellationRequested();
                        Status(string.Format(L.T("Очищаю {0}/{1}: {2}"), n + 1, sel.Count, t.Name));
                        Progress(n * 100 / sel.Count);
                        Log(L.T("Очистка: ") + t.Name);
                        var st = new WalkStats();
                        try { CleanerEngine.CleanOne(t, st, ct, Log); }
                        finally
                        {
                            // считаем и то, что успели удалить до нажатия «Стоп»
                            freed += st.Bytes;
                            failed += st.FailedFiles; failedBytes += st.FailedBytes;
                            scheduled += st.Scheduled; scheduledBytes += st.ScheduledBytes;
                        }
                        if (t.Special != SpecialKind.Dism)
                            Log(string.Format(L.T("    удалено {0} ({1} файлов){2}{3}"),
                                Util.FormatSize(st.Bytes), Util.FormatCount(st.Files),
                                st.FailedFiles > 0 ? string.Format(L.T(", пропущено занятых: {0} ({1})"), st.FailedFiles, Util.FormatSize(st.FailedBytes)) : "",
                                st.Scheduled > 0 ? string.Format(L.T(", удалятся при перезагрузке: {0}"), st.Scheduled) : ""));
                    }
                }
                catch (OperationCanceledException) { cancelled = true; Log(L.T("Остановлено.")); }
                catch (Exception ex) { Log(L.T("ОШИБКА: ") + ex.Message); }

                // пересканировать очищенное
                Status(L.T("Пересчитываю..."));
                Progress(-1);
                try
                {
                    CleanerEngine.ScanAll(sel, CancellationToken.None, null);
                }
                catch { }

                var msg = new StringBuilder();
                msg.AppendLine((cancelled ? L.T("Остановлено. ") : L.T("Готово. ")) + L.T("Освобождено: ") + Util.FormatSize(freed));
                if (failed > 0) msg.AppendLine(string.Format(L.T("Не удалось удалить (заняты другими программами): {0} файлов, {1}"), Util.FormatCount(failed), Util.FormatSize(failedBytes)));
                if (scheduled > 0) msg.AppendLine(string.Format(L.T("Удалятся при следующей перезагрузке: {0} файлов, {1}"), Util.FormatCount(scheduled), Util.FormatSize(scheduledBytes)));
                result = msg.ToString().TrimEnd();
                Log(result);
                Settings.AddFreed(freed);
            }, () =>
            {
                BuildRows();
                Status(result.Split('\n')[0].Trim());
                Info(L.T("Очистка"), result);
            });
        }
    }
}
