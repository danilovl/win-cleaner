using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Microsoft.Win32;

namespace Danilovl.WinCleaner
{
    class ProjectJunk
    {
        public string Path;
        public string Project;
        public string Label;      // имя папки для показа (node_modules, var\cache …)
        public string Kind;
        public long Bytes;
        public long Files;
        public DateTime ProjectChanged;
        public bool Checked;
    }

    /// <summary>Поиск папок, которые инструменты сборки создают заново (node_modules, vendor, build …).</summary>
    static class ProjectScanner
    {
        static bool Has(HashSet<string> names, params string[] any)
        {
            foreach (string a in any) if (names.Contains(a)) return true;
            return false;
        }

        static bool HasExt(HashSet<string> names, string ext)
        {
            foreach (string n in names) if (n.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Тип мусорной папки dirName внутри проекта с файлами parentNames, или null.</summary>
        static string Classify(string dirName, string dirPath, HashSet<string> parentNames)
        {
            string d = dirName.ToLowerInvariant();
            string parentDir = System.IO.Path.GetDirectoryName(dirPath) ?? dirPath;
            switch (d)
            {
                case "node_modules": return Has(parentNames, "package.json") ? L.T("Node.js — зависимости") : null;
                case "vendor": return Has(parentNames, "composer.json") ? L.T("PHP Composer — зависимости") : null;
                case ".gradle": return IsGradle(parentNames) ? L.T("Gradle — кэш проекта") : null;
                case "build":
                    if (IsGradle(parentNames)) return L.T("Gradle — результат сборки");
                    if (Has(parentNames, "pubspec.yaml")) return L.T("Flutter — результат сборки");
                    return null;
                case ".dart_tool": return Has(parentNames, "pubspec.yaml") ? L.T("Dart — кэш инструментов") : null;
                case "target":
                    if (Has(parentNames, "pom.xml")) return L.T("Maven — результат сборки");
                    if (Has(parentNames, "cargo.toml")) return L.T("Rust — результат сборки");
                    if (Has(parentNames, "build.sbt")) return L.T("sbt — результат сборки");
                    return null;
                case ".next": return Has(parentNames, "package.json") ? L.T("Next.js — кэш сборки") : null;
                case ".nuxt": return Has(parentNames, "package.json") ? L.T("Nuxt — кэш сборки") : null;
                case ".svelte-kit": return Has(parentNames, "package.json") ? L.T("SvelteKit — кэш") : null;
                case ".angular": return Has(parentNames, "package.json") ? L.T("Angular — кэш") : null;
                case ".parcel-cache": return Has(parentNames, "package.json") ? L.T("Parcel — кэш") : null;
                case ".turbo": return Has(parentNames, "package.json") ? L.T("Turborepo — кэш") : null;
                case "bin": return IsDotnet(parentNames) ? L.T(".NET — результат сборки") : null;
                case "obj": return IsDotnet(parentNames) ? L.T(".NET — промежуточные файлы") : null;
                // Python: служебные кэши узнаются по имени, файл проекта рядом не нужен
                case "__pycache__": return L.T("Python — скомпилированный байт-код");
                case ".pytest_cache": return L.T("pytest — кэш");
                case ".mypy_cache": return L.T("mypy — кэш");
                case ".ruff_cache": return L.T("Ruff — кэш");
                case ".tox": return Has(parentNames, "tox.ini", "pyproject.toml", "setup.py", "setup.cfg") ? L.T("tox — окружения для тестов") : null;
                case ".expo": return Has(parentNames, "package.json") ? L.T("Expo — кэш") : null;
                case ".terraform": return HasExt(parentNames, ".tf") ? L.T("Terraform — скачанные провайдеры") : null;
                case ".vs": return HasExt(parentNames, ".sln") || IsDotnet(parentNames) || HasExt(parentNames, ".vcxproj")
                    ? L.T("Visual Studio — кэш решения") : null;
                case "library":   // Unity: рядом обязательно лежат Assets и ProjectSettings
                    return Directory.Exists(System.IO.Path.Combine(parentDir, "Assets")) && Directory.Exists(System.IO.Path.Combine(parentDir, "ProjectSettings"))
                        ? L.T("Unity — кэш импорта (Library)") : null;
                case "intermediate": return HasExt(parentNames, ".uproject") ? L.T("Unreal — промежуточные файлы") : null;
                case "deriveddatacache": return HasExt(parentNames, ".uproject") ? L.T("Unreal — DerivedDataCache проекта") : null;
                case ".venv":
                case "venv":
                    return File.Exists(System.IO.Path.Combine(dirPath, "pyvenv.cfg")) ? L.T("Python venv (пересоздать: pip install)") : null;
            }
            if (d.StartsWith("cmake-build-") && Has(parentNames, "cmakelists.txt")) return L.T("CLion — сборка CMake");
            return null;
        }

        static bool IsGradle(HashSet<string> names)
        {
            return Has(names, "build.gradle", "build.gradle.kts", "settings.gradle", "settings.gradle.kts", "gradlew");
        }

        static bool IsDotnet(HashSet<string> names)
        {
            return HasExt(names, ".csproj") || HasExt(names, ".fsproj") || HasExt(names, ".vbproj");
        }

        /// <summary>
        /// Ищет «мусорные» папки проектов под root и считает их размер. Найденное добавляется в found по ходу работы,
        /// поэтому при остановке (OperationCanceledException) в списке остаётся то, что успели найти.
        /// </summary>
        public static void Scan(string root, CancellationToken ct, List<ProjectJunk> found,
            Action<string> status, Action<int> progress, Action<string> log)
        {
            string appData = Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            var sw = Stopwatch.StartNew();
            int dirs = 0;
            FileWalker.WalkParallel(root, ct, (dir, entries, descend) =>
            {
                int n = Interlocked.Increment(ref dirs);
                if (n % 500 == 0) status(L.T("Просмотрено папок: ") + Util.FormatCount(n) + "  …" + dir);

                // имена файлов нужны, только если в папке вообще есть подпапки
                HashSet<string> names = null;
                long newest = 0;
                string project = dir.Length > 3 ? dir.TrimEnd('\\') : dir;

                foreach (var e in entries)
                {
                    if (!e.IsDir || e.IsLink || FileWalker.SkipDir(e)) continue;
                    if (names == null)
                    {
                        names = new HashSet<string>();
                        foreach (var f in entries)
                            if (!f.IsDir)
                            {
                                names.Add(f.Name.ToLowerInvariant());
                                if (f.WriteTime > newest) newest = f.WriteTime;
                            }
                    }
                    string name = e.Name, path = e.Path;
                    if (string.Equals(path, appData, StringComparison.OrdinalIgnoreCase)) continue;

                    // Symfony: var/cache и var/log
                    if (name.Equals("var", StringComparison.OrdinalIgnoreCase) && (names.Contains("symfony.lock") ||
                        File.Exists(System.IO.Path.Combine(project, "bin", "console"))))
                    {
                        foreach (string sub in new[] { "cache", "log" })
                        {
                            string p = System.IO.Path.Combine(path, sub);
                            if (!Directory.Exists(p)) continue;
                            var j = new ProjectJunk
                            {
                                Path = p, Project = project, Label = "var\\" + sub,
                                Kind = sub == "cache" ? L.T("Symfony — кэш") : L.T("Symfony — логи"), ProjectChanged = FileWalker.ToLocalTime(newest)
                            };
                            lock (found) found.Add(j);
                        }
                        continue;
                    }

                    string kind = Classify(name, path, names);
                    if (kind != null)
                    {
                        var j = new ProjectJunk { Path = path, Project = project, Label = name, Kind = kind, ProjectChanged = FileWalker.ToLocalTime(newest) };
                        lock (found) found.Add(j);
                        continue;   // внутрь не заходим
                    }
                    if (name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                    descend.Add(path);
                }
            });

            List<ProjectJunk> list;
            lock (found) list = new List<ProjectJunk>(found);
            var stats = list.Select(j => new KeyValuePair<string, WalkStats>(j.Path, new WalkStats())).ToList();
            progress(-1);
            try
            {
                FileWalker.MeasureMany(stats, ct, n => { if (n % 1000 == 0) status(L.T("Считаю размеры. Просмотрено папок: ") + Util.FormatCount(n)); });
            }
            finally
            {
                for (int i = 0; i < list.Count; i++) { list[i].Bytes = stats[i].Value.Bytes; list[i].Files = stats[i].Value.Files; }
            }
            log(string.Format(L.T("Найдено {0} папок, {1} — за {2:N1} с"), found.Count, Util.FormatSize(found.Sum(f => f.Bytes)), sw.Elapsed.TotalSeconds));
        }
    }
}
