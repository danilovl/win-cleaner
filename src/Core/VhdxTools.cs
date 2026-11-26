using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Danilovl.WinCleaner
{
    class VhdxDisk
    {
        public string Path;
        public string Source;
        public long Size;
    }

    /// <summary>
    /// Виртуальные диски WSL2 / Docker Desktop (.vhdx): поиск, остановка Docker и WSL, сжатие через diskpart.
    /// Общая логика для вкладок «Docker и WSL» и «Сжатие дисков WSL».
    /// </summary>
    static class VhdxTools
    {
        public static string ManualSource { get { return L.T("добавлен вручную"); } }

        /// <summary>Добавляет диск в список, если файл существует и его там ещё нет.</summary>
        public static bool TryAdd(List<VhdxDisk> list, HashSet<string> seen, string path, string source)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
                string full = Path.GetFullPath(path);
                if (!seen.Add(full)) return false;
                list.Add(new VhdxDisk { Path = full, Source = source, Size = new FileInfo(full).Length });
                return true;
            }
            catch { return false; }
        }

        /// <summary>Диски Docker Desktop и всех дистрибутивов WSL (из реестра и из Microsoft Store).</summary>
        public static List<VhdxDisk> FindDisks(HashSet<string> seen)
        {
            var result = new List<VhdxDisk>();
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            // Docker Desktop — первыми, чтобы у них было понятное описание
            TryAdd(result, seen, Path.Combine(la, @"Docker\wsl\disk\docker_data.vhdx"), L.T("Docker: данные (образы, контейнеры, volumes)"));
            TryAdd(result, seen, Path.Combine(la, @"Docker\wsl\data\ext4.vhdx"), L.T("Docker: данные (старый формат)"));
            TryAdd(result, seen, Path.Combine(la, @"Docker\wsl\main\ext4.vhdx"), L.T("Docker: служебный дистрибутив"));

            // все зарегистрированные дистрибутивы WSL
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss"))
                {
                    if (root != null)
                        foreach (string sub in root.GetSubKeyNames())
                            using (var k = root.OpenSubKey(sub))
                            {
                                if (k == null) continue;
                                string name = k.GetValue("DistributionName") as string;
                                string bp = k.GetValue("BasePath") as string;
                                string vhd = (k.GetValue("VhdFileName") as string) ?? "ext4.vhdx";
                                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(bp)) continue;
                                if (bp.StartsWith(@"\\?\")) bp = bp.Substring(4);
                                TryAdd(result, seen, Path.Combine(bp, vhd), "WSL: " + name);
                            }
                }
            }
            catch { }

            // дистрибутивы из Microsoft Store
            try
            {
                string packages = Path.Combine(la, "Packages");
                if (Directory.Exists(packages))
                    foreach (string dir in Directory.GetDirectories(packages))
                    {
                        string pkg = Path.GetFileName(dir);
                        int us = pkg.IndexOf('_');
                        TryAdd(result, seen, Path.Combine(dir, "LocalState", "ext4.vhdx"), "WSL (Store): " + (us > 0 ? pkg.Substring(0, us) : pkg));
                    }
            }
            catch { }
            return result;
        }

        public static bool IsDockerData(VhdxDisk d)
        {
            // по пути, а не по описанию: описание зависит от языка интерфейса
            return d.Path.IndexOf(@"\Docker\wsl\disk\", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   d.Path.IndexOf(@"\Docker\wsl\data\", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Имена запущенных дистрибутивов WSL.</summary>
        public static List<string> RunningDistros()
        {
            List<string> lines;
            // wsl.exe по умолчанию пишет в UTF-16
            if (Util.RunCapture("wsl.exe", "--list --running --quiet", Encoding.Unicode, out lines) != 0) return new List<string>();
            return lines.Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        }

        /// <summary>fstrim во всех запущенных дистрибутивах: помечает свободные блоки, иначе сжимать нечего.</summary>
        public static void Fstrim(Action<string> log)
        {
            var running = RunningDistros();
            if (running.Count == 0) { log(L.T("Нет запущенных дистрибутивов WSL — fstrim пропущен.")); return; }
            foreach (string d in running)
                Util.Run("wsl.exe", "-d \"" + d + "\" -u root -- fstrim -av", Encoding.UTF8, log);
        }

        /// <summary>Останавливает Docker Desktop. Возвращает true, если он был запущен.</summary>
        public static bool StopDocker(Action<string> log)
        {
            var desktop = Process.GetProcessesByName("Docker Desktop");
            bool was = desktop.Length > 0;
            foreach (var p in desktop) p.Dispose();
            foreach (string n in new[] { "Docker Desktop", "com.docker.backend", "com.docker.build", "com.docker.dev-envs", "docker" })
                foreach (var p in Process.GetProcessesByName(n))
                {
                    try { log(L.T("Останавливаю ") + n + " (pid " + p.Id + ")"); p.Kill(); p.WaitForExit(5000); } catch { }
                    finally { p.Dispose(); }
                }
            return was;
        }

        /// <summary>wsl --shutdown и ожидание, пока виртуальная машина отпустит файлы дисков.</summary>
        public static void ShutdownWsl(Action<string> log)
        {
            Util.Run("wsl.exe", "--shutdown", Encoding.Unicode, log);
            for (int i = 0; i < 30; i++)
            {
                // vmmem без суффикса — это ещё и обычные ВМ Hyper-V, их не ждём
                var vm = Process.GetProcessesByName("vmmemWSL");
                foreach (var p in vm) p.Dispose();
                if (vm.Length == 0) break;
                Thread.Sleep(1000);
            }
            Thread.Sleep(2000);
        }

        public static void StartDockerDesktop(Action<string> log)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Docker\Docker\Docker Desktop.exe");
            if (!File.Exists(exe)) return;
            log(L.T("Запускаю Docker Desktop…"));
            try { Process.Start(exe); } catch (Exception ex) { log(L.T("Не удалось: ") + ex.Message); }
        }

        /// <summary>Сжимает один .vhdx через diskpart (attach readonly → compact → detach).</summary>
        public static void Compact(string path, Action<string> log)
        {
            string target = DiskpartSafePath(path);
            string script = Path.Combine(Path.GetTempPath(), "compact_vhdx_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(script, "select vdisk file=\"" + target + "\"\r\nattach vdisk readonly\r\ncompact vdisk\r\ndetach vdisk\r\nexit\r\n", Encoding.ASCII);
                int code = Util.Run("diskpart.exe", "/s \"" + script + "\"", Util.OemEncoding(), log);
                if (code != 0) throw new Exception(L.T("diskpart завершился с кодом ") + code);
            }
            finally
            {
                // на случай сбоя — отключаем диск, если он остался подключён
                try
                {
                    File.WriteAllText(script, "select vdisk file=\"" + target + "\"\r\ndetach vdisk\r\nexit\r\n", Encoding.ASCII);
                    Util.Run("diskpart.exe", "/s \"" + script + "\"", Util.OemEncoding(), null);
                }
                catch { }
                try { File.Delete(script); } catch { }
            }
        }

        /// <summary>
        /// Сжимает диски по очереди, пишет в лог «было → стало». Возвращает освобождённые байты;
        /// errors увеличивается на число дисков, которые сжать не удалось.
        /// </summary>
        public static long CompactAll(List<VhdxDisk> disks, Action<string> log, Action<string> status, ref int errors)
        {
            long total = 0;
            foreach (var d in disks)
            {
                if (status != null) status(L.T("Сжимаю: ") + d.Source);
                log("> " + d.Source + " — " + d.Path);
                try
                {
                    if (!File.Exists(d.Path)) { errors++; log(L.T("  файл не найден — пропущен")); continue; }
                    long before = new FileInfo(d.Path).Length;
                    try { Compact(d.Path, log); }
                    catch (Exception ex) { errors++; log(L.T("ОШИБКА: ") + ex.Message); }
                    long after = new FileInfo(d.Path).Length;
                    long freed = Math.Max(0, before - after);
                    total += freed;
                    log(string.Format(L.T("  {0} → {1}  (освобождено {2})"), Util.FormatSize(before), Util.FormatSize(after), Util.FormatSize(freed)));
                }
                catch (Exception ex) { errors++; log(L.T("ОШИБКА: ") + ex.Message); }
            }
            return total;
        }

        // diskpart читает скрипт в однобайтовой кодировке — путь с кириллицей (имя пользователя!)
        // заменяем коротким именем 8.3, которое всегда латиницей.
        static string DiskpartSafePath(string path)
        {
            if (path.All(c => c < 128)) return path;
            var sb = new StringBuilder(1024);
            uint n = GetShortPathName(path, sb, (uint)sb.Capacity);
            string sp = n > 0 ? sb.ToString() : null;
            if (sp != null && sp.All(c => c < 128)) return sp;
            throw new Exception(L.T("Путь содержит нелатинские символы, а короткие имена 8.3 отключены. " +
                                "Перенесите диск в папку с латинским путём (Docker: Settings → Resources → Disk image location)."));
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint bufferSize);
    }
}
