using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Danilovl.WinCleaner
{
    /// <summary>
    /// Настройки пользователя (HKCU\Software\WinCleaner): исключения, свои места очистки,
    /// запомненные папки и положение окна, счётчик освобождённого места.
    /// </summary>
    static class Settings
    {
        const string RegKey = @"Software\WinCleaner";

        static string[] excluded = new string[0];
        static List<string> custom = new List<string>();

        /// <summary>Папки, которые программа никогда не считает и не удаляет.</summary>
        public static IList<string> Exclusions { get { return excluded; } }
        /// <summary>Свои места очистки: «папка» или «папка|маска».</summary>
        public static IList<string> CustomTargets { get { return custom; } }
        public static bool HasExclusions { get { return excluded.Length > 0; } }

        public static void Load()
        {
            excluded = Normalize(ReadList("Exclusions")).ToArray();
            custom = ReadList("CustomTargets");
        }

        static List<string> ReadList(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                {
                    var v = k == null ? null : k.GetValue(name) as string[];
                    return v == null ? new List<string>() : v.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                }
            }
            catch { return new List<string>(); }
        }

        static void WriteList(string name, IEnumerable<string> values)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
                    if (k != null) k.SetValue(name, values.ToArray(), RegistryValueKind.MultiString);
            }
            catch { }
        }

        static List<string> Normalize(IEnumerable<string> paths)
        {
            var result = new List<string>();
            foreach (string p in paths)
            {
                try
                {
                    string full = Path.GetFullPath(p.Trim()).TrimEnd('\\');
                    if (full.Length >= 2 && !result.Contains(full, StringComparer.OrdinalIgnoreCase)) result.Add(full);
                }
                catch { }
            }
            return result;
        }

        public static void SetExclusions(IEnumerable<string> paths)
        {
            excluded = Normalize(paths).ToArray();
            WriteList("Exclusions", excluded);
        }

        public static void SetCustomTargets(IEnumerable<string> specs)
        {
            custom = specs.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            WriteList("CustomTargets", custom);
        }

        static bool Under(string path, string folder)
        {
            return path.Length >= folder.Length && path.StartsWith(folder, StringComparison.OrdinalIgnoreCase) &&
                   (path.Length == folder.Length || path[folder.Length] == '\\');
        }

        /// <summary>Путь совпадает с исключением или лежит внутри него.</summary>
        public static bool IsExcluded(string path)
        {
            var list = excluded;
            if (list.Length == 0) return false;
            string p = path.Length > 3 ? path.TrimEnd('\\') : path;
            foreach (string ex in list) if (Under(p, ex)) return true;
            return false;
        }

        /// <summary>Путь сам исключён либо содержит исключённую папку где-то внутри.</summary>
        public static bool ContainsExcluded(string path)
        {
            var list = excluded;
            if (list.Length == 0) return false;
            string p = path.Length > 3 ? path.TrimEnd('\\') : path;
            foreach (string ex in list) if (Under(p, ex) || Under(ex, p)) return true;
            return false;
        }

        // ------------------------------------------------ простые значения

        public static string Get(string name, string fallback = null)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RegKey))
                    return (k == null ? null : k.GetValue(name) as string) ?? fallback;
            }
            catch { return fallback; }
        }

        public static void Set(string name, string value)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RegKey))
                    if (k != null) k.SetValue(name, value ?? "");
            }
            catch { }
        }

        static long GetLong(string name) { long v; return long.TryParse(Get(name, "0"), out v) ? v : 0; }

        /// <summary>Сколько места освобождено программой за всё время.</summary>
        public static long TotalFreed { get { return GetLong("TotalFreed"); } }
        /// <summary>Сколько раз выполнялась очистка.</summary>
        public static long Cleanups { get { return GetLong("Cleanups"); } }

        /// <summary>Учитывает завершённую очистку (нулевые не считаются).</summary>
        public static void AddFreed(long bytes)
        {
            if (bytes <= 0) return;
            Set("TotalFreed", (TotalFreed + bytes).ToString());
            Set("Cleanups", (Cleanups + 1).ToString());
        }
    }
}
