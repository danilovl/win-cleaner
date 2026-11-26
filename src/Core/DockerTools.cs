using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Danilovl.WinCleaner
{
    /// <summary>Строка «docker system df».</summary>
    class DockerDfRow
    {
        public string Type, Total, Active, Size, Reclaimable;
    }

    /// <summary>Команда очистки Docker и её описание для окна подтверждения.</summary>
    class DockerPrune
    {
        public string Args, What;
    }

    static class DockerTools
    {
        /// <summary>code: 0 — успех, -1 — docker.exe не найден, иначе Docker не запущен.</summary>
        public static List<DockerDfRow> SystemDf(out int code)
        {
            List<string> lines;
            code = Util.RunCapture("docker.exe", "system df --format \"{{json .}}\"", Encoding.UTF8, out lines);
            var rows = new List<DockerDfRow>();
            if (code != 0) return rows;
            var rx = new Regex("\"(\\w+)\":\"((?:[^\"\\\\]|\\\\.)*)\"");
            foreach (string l in lines)
            {
                var d = new Dictionary<string, string>();
                foreach (Match m in rx.Matches(l)) d[m.Groups[1].Value] = m.Groups[2].Value;
                if (!d.ContainsKey("Type")) continue;
                rows.Add(new DockerDfRow
                {
                    Type = TypeName(Get(d, "Type")), Total = Get(d, "TotalCount"), Active = Get(d, "Active"),
                    Size = Get(d, "Size"), Reclaimable = Get(d, "Reclaimable")
                });
            }
            return rows;
        }

        public static string DfError(int code)
        {
            return code == -1
                ? L.T("Docker не установлен (docker.exe не найден).")
                : L.T("Docker не запущен — запустите Docker Desktop, чтобы увидеть и очистить образы.");
        }

        static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : ""; }

        static string TypeName(string t)
        {
            switch (t)
            {
                case "Images": return L.T("Образы");
                case "Containers": return L.T("Контейнеры");
                case "Local Volumes": return "Volumes";
                case "Build Cache": return L.T("Кэш сборки");
                default: return t;
            }
        }

        public static List<DockerPrune> PruneCommands(bool containers, bool images, bool build, bool networks, bool volumes)
        {
            var cmds = new List<DockerPrune>();
            if (containers) cmds.Add(new DockerPrune { Args = "container prune -f", What = L.T("остановленные контейнеры") });
            if (images) cmds.Add(new DockerPrune { Args = "image prune -a -f", What = L.T("образы, которые не используются ни одним контейнером") });
            if (build) cmds.Add(new DockerPrune { Args = "builder prune -a -f", What = L.T("весь кэш сборки") });
            if (networks) cmds.Add(new DockerPrune { Args = "network prune -f", What = L.T("неиспользуемые сети") });
            if (volumes) cmds.Add(new DockerPrune { Args = "volume prune -a -f", What = L.T("ВСЕ volumes, не подключённые к контейнерам (данные баз!)") });
            return cmds;
        }

        /// <summary>Выполняет команды очистки по очереди, вывод идёт в log.</summary>
        public static void RunPrune(List<DockerPrune> cmds, Action<string> log, Action<string> status)
        {
            foreach (var c in cmds)
            {
                if (status != null) status("docker " + c.Args);
                int code = Util.Run("docker.exe", c.Args, Encoding.UTF8, log);
                if (code != 0) log(L.T("    код завершения ") + code);
                // старые версии Docker не знают -a у volume prune
                if (code != 0 && c.Args.StartsWith("volume")) Util.Run("docker.exe", "volume prune -f", Encoding.UTF8, log);
            }
        }
    }
}
