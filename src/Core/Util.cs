using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Danilovl.WinCleaner
{
    static class Util
    {
        public static string FormatSize(long bytes)
        {
            var c = CultureInfo.CurrentCulture;
            if (bytes >= 1L << 30) return string.Format(c, "{0:N2} GB", bytes / (double)(1L << 30));
            if (bytes >= 1L << 20) return string.Format(c, "{0:N1} MB", bytes / (double)(1L << 20));
            if (bytes >= 1L << 10) return string.Format(c, "{0:N0} KB", bytes / (double)(1L << 10));
            return bytes + " B";
        }

        public static string FormatCount(long n)
        {
            return n.ToString("N0", CultureInfo.CurrentCulture);
        }

        public static Encoding OemEncoding()
        {
            try { return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage); }
            catch { return Encoding.Default; }
        }

        /// <summary>Запускает программу без окна и возвращает её вывод построчно.</summary>
        public static int RunCapture(string file, string args, Encoding enc, out List<string> lines)
        {
            var result = new List<string>();
            lines = result;
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                CreateNoWindow = true, StandardOutputEncoding = enc, StandardErrorEncoding = enc
            };
            psi.EnvironmentVariables.Remove("WSL_UTF8");
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += (s, e) =>
                    {
                        if (e.Data == null) return;
                        string l = e.Data.Replace("\0", "").TrimEnd();
                        if (l.Length > 0) lock (result) result.Add(l);
                    };
                    p.ErrorDataReceived += (s, e) => { };
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        /// <summary>Запускает консольную программу без окна, построчно отдаёт вывод в log.</summary>
        public static int Run(string file, string args, Encoding enc, Action<string> log)
        {
            return Run(file, args, enc, log, System.Threading.CancellationToken.None);
        }

        /// <summary>То же, но программу можно прервать: при отмене её процесс завершается.</summary>
        public static int Run(string file, string args, Encoding enc, Action<string> log, System.Threading.CancellationToken ct)
        {
            if (log != null) log("$ " + file + " " + args);
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = enc,
                StandardErrorEncoding = enc
            };
            DataReceivedEventHandler h = (s, e) =>
            {
                if (e.Data == null || log == null) return;
                string line = e.Data.Replace("\0", "").TrimEnd();
                if (line.Length > 0) log("    " + line);
            };
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    p.OutputDataReceived += h;
                    p.ErrorDataReceived += h;
                    p.Start();
                    p.BeginOutputReadLine();
                    p.BeginErrorReadLine();
                    using (ct.Register(() => { try { if (!p.HasExited) p.Kill(); } catch { } }))
                        p.WaitForExit();
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                if (log != null) log(L.T("    не удалось запустить: ") + ex.Message);
                return -1;
            }
        }
    }
}
