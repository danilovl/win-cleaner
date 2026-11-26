using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Danilovl.WinCleaner
{
    class PathSpec
    {
        public string Dir;   // шаблон папки (env + * ?)
        public string Mask;  // null = всё содержимое рекурсивно; иначе маска файлов в самой папке
    }

    enum SpecialKind { None, RecycleBin, Dism, ShadowCopies }

    class CleanTarget
    {
        public string Category;
        public string Name;
        public string Note;
        public bool Safe;                 // отмечать по умолчанию
        public SpecialKind Special;
        public string[] Processes = new string[0];   // процессы, которые лучше закрыть
        public string[] Services = new string[0];    // службы, которые надо остановить на время очистки
        public List<PathSpec> Paths = new List<PathSpec>();

        // результаты сканирования
        public bool Scanned;
        public bool Exists;
        public long Bytes;
        public long Files;
        public List<PathSpec> Resolved = new List<PathSpec>();   // реальные папки после раскрытия шаблонов
        public WalkStats Live;            // счётчики, которые растут прямо во время подсчёта (null — считать нечего)
        public bool Checked;
        public bool RebootFallback;       // занятые файлы удалить при перезагрузке
        public bool TakeOwnership;        // перед удалением стать владельцем (Windows.old и т.п.)
        public bool RemoveRoot;           // удалить и саму папку
        public Func<List<string>> CustomDirs;   // папки, вычисляемые кодом (удаляются целиком)
    }

    /// <summary>
    /// Список мест очистки. Группы идут в том порядке, в котором они показываются в списке:
    /// Windows → браузеры → программы по видам → разработка → ИИ.
    /// Правило для флажка «отмечено по умолчанию» (safe): true только для того, что пересоздаётся само
    /// и без заметных последствий (кэш, логи, временные файлы). Всё, что придётся скачивать заново
    /// или что меняет поведение системы, — false.
    /// </summary>
    static class Targets
    {
        // ------------------------------------------------ группы
        static string WIN_TMP { get { return L.T("Windows — временные файлы и кэши"); } }
        static string WIN_UPD { get { return L.T("Windows — обновления и остатки установки"); } }
        static string WIN_LOG { get { return L.T("Windows — журналы, отчёты и дампы"); } }
        static string BRW { get { return L.T("Браузеры (только кэш — пароли, история и вкладки не трогаются)"); } }
        static string MSG { get { return L.T("Мессенджеры и связь"); } }
        static string GAME { get { return L.T("Игры и лаунчеры"); } }
        static string MEDIA { get { return L.T("Медиа, графика и офис"); } }
        static string APP { get { return L.T("Другие программы"); } }
        static string IDE { get { return L.T("Редакторы кода и IDE"); } }
        static string PKG { get { return L.T("Разработка — пакеты и зависимости (скачаются заново)"); } }
        static string BUILD { get { return L.T("Разработка — сборка, SDK и инструменты"); } }
        static string INFRA { get { return L.T("Разработка — контейнеры, ВМ и инфраструктура"); } }
        static string AI_APP { get { return L.T("ИИ — приложения и агенты (настройки и вход в аккаунт не трогаются)"); } }
        static string AI_MODEL { get { return L.T("ИИ — скачанные модели"); } }
        static string CUSTOM { get { return L.T("Свои места (добавлены в настройках)"); } }

        // ------------------------------------------------ помощники

        static CleanTarget T(string cat, string name, bool safe, string note, params string[] specs)
        {
            var t = new CleanTarget { Category = cat, Name = name, Safe = safe, Note = note };
            foreach (string s in specs)
            {
                int bar = s.IndexOf('|');
                t.Paths.Add(bar < 0
                    ? new PathSpec { Dir = s, Mask = null }
                    : new PathSpec { Dir = s.Substring(0, bar), Mask = s.Substring(bar + 1) });
            }
            return t;
        }

        static CleanTarget WithProcs(CleanTarget t, params string[] procs) { t.Processes = procs; return t; }

        static CleanTarget Add(CleanTarget t, params string[] dirs)
        {
            foreach (string d in dirs) t.Paths.Add(new PathSpec { Dir = d });
            return t;
        }

        /// <summary>Кэш браузера на Chromium (Chrome, Edge, Brave, Vivaldi, Яндекс...). Все профили.</summary>
        static CleanTarget Chromium(string name, string userData, params string[] procs)
        {
            string u = userData;
            var t = T(BRW, name, true, L.T("Кэш страниц, скриптов и шейдеров во всех профилях, отчёты о сбоях"),
                u + @"\*\Cache", u + @"\*\Code Cache", u + @"\*\GPUCache",
                u + @"\*\Service Worker\CacheStorage", u + @"\*\Service Worker\ScriptCache",
                u + @"\*\DawnCache", u + @"\*\DawnGraphiteCache", u + @"\*\DawnWebGPUCache",
                u + @"\ShaderCache", u + @"\GrShaderCache", u + @"\GraphiteDawnCache",
                u + @"\Crashpad\reports", u + @"\component_crx_cache", u + @"\BrowserMetrics");
            t.Processes = procs;
            return t;
        }

        static readonly string[] ElectronCacheDirs =
            { "Cache", "Code Cache", "GPUCache", "DawnCache", @"Service Worker\CacheStorage", @"Service Worker\ScriptCache" };

        /// <summary>Добавляет к цели кэш-папки профиля Electron (в т.ч. версии из Microsoft Store) и его логи.</summary>
        static CleanTarget AddElectronDirs(CleanTarget t, string dir)
        {
            foreach (string sub in ElectronCacheDirs) t.Paths.Add(new PathSpec { Dir = dir + "\\" + sub });
            t.Paths.Add(new PathSpec { Dir = dir + @"\logs" });
            return t;
        }

        /// <summary>Кэш и логи Electron-приложения (Discord, Slack, VS Code...).</summary>
        static CleanTarget Electron(string cat, string name, string dir, params string[] procs)
        {
            var t = new CleanTarget { Category = cat, Name = name, Safe = true, Note = L.T("Кэш интерфейса и логи") };
            AddElectronDirs(t, dir);
            t.Processes = procs;
            return t;
        }

        /// <summary>Редактор кода на базе VS Code: кэш интерфейса, скомпилированный код, логи.</summary>
        static CleanTarget CodeEditor(string cat, string name, string dir, params string[] procs)
        {
            return Add(Electron(cat, name, dir, procs), dir + @"\CachedData", dir + @"\CachedExtensionVSIXs", dir + @"\Crashpad\reports");
        }

        // ------------------------------------------------ список

        public static List<CleanTarget> All()
        {
            var all = new List<CleanTarget>();
            // частые части названий и примечаний
            string cache = L.T("кэш"), logs = L.T("логи");
            string redownload = L.T("Скачается заново при необходимости");
            string models = L.T("Модели придётся скачать заново");
            string shaders = L.T("Игры пересоздадут при запуске");

            // ================================================ Windows — временные файлы и кэши
            all.Add(T(WIN_TMP, L.T("Временные файлы пользователя"), true, L.T("%TEMP% — занятые файлы будут пропущены"), @"%TEMP%"));
            all.Add(T(WIN_TMP, L.T("Временные файлы Windows"), true, @"C:\Windows\Temp", @"%WINDIR%\Temp"));

            var thumbs = T(WIN_TMP, L.T("Кэш эскизов (превью картинок и видео)"), true,
                L.T("Эскизы создадутся заново. Файлы, занятые Проводником, удалятся при перезагрузке"),
                @"%LOCALAPPDATA%\Microsoft\Windows\Explorer|thumbcache_*.db");
            thumbs.RebootFallback = true;
            all.Add(thumbs);

            var icons = T(WIN_TMP, L.T("Кэш значков"), false, L.T("После очистки значки могут обновиться только после перезагрузки"),
                @"%LOCALAPPDATA%\Microsoft\Windows\Explorer|iconcache_*.db", @"%LOCALAPPDATA%|IconCache.db");
            icons.RebootFallback = true;
            all.Add(icons);

            var rb = T(WIN_TMP, L.T("Корзина"), false, L.T("Все файлы в корзине на всех дисках будут удалены окончательно"));
            rb.Special = SpecialKind.RecycleBin;
            all.Add(rb);

            all.Add(T(WIN_TMP, L.T("Интернет-кэш Windows (INetCache)"), true, L.T("Кэш встроенных веб-компонентов"),
                @"%LOCALAPPDATA%\Microsoft\Windows\INetCache"));
            all.Add(T(WIN_TMP, L.T("Временные файлы приложений Store"), true, L.T("Кэш и временные папки UWP-приложений"),
                @"%LOCALAPPDATA%\Packages\*\AC\INetCache", @"%LOCALAPPDATA%\Packages\*\AC\Temp", @"%LOCALAPPDATA%\Packages\*\TempState"));
            all.Add(T(WIN_TMP, L.T("Кэш Microsoft Store"), true, L.T("То же, что команда wsreset"),
                @"%LOCALAPPDATA%\Packages\Microsoft.WindowsStore_*\LocalCache"));
            all.Add(T(WIN_TMP, L.T("Кэш шейдеров DirectX"), true, shaders, @"%LOCALAPPDATA%\D3DSCache"));
            all.Add(T(WIN_TMP, L.T("Кэш шейдеров NVIDIA"), true, shaders,
                @"%LOCALAPPDATA%\NVIDIA\DXCache", @"%LOCALAPPDATA%\NVIDIA\GLCache",
                @"%LOCALAPPDATA%\NVIDIA Corporation\NV_Cache", @"%PROGRAMDATA%\NVIDIA Corporation\NV_Cache"));
            all.Add(T(WIN_TMP, L.T("Кэш шейдеров AMD"), true, shaders,
                @"%LOCALAPPDATA%\AMD\DxCache", @"%LOCALAPPDATA%\AMD\DxcCache", @"%LOCALAPPDATA%\AMD\GLCache",
                @"%LOCALAPPDATA%\AMD\VkCache"));
            all.Add(T(WIN_TMP, L.T("Кэш Intel графики"), true, shaders, @"%LOCALAPPDATA%\Intel\ShaderCache"));
            all.Add(T(WIN_TMP, "Prefetch", false, L.T("Windows пересоздаст; первые запуски программ будут чуть медленнее"), @"%WINDIR%\Prefetch|*.pf"));
            var fonts = T(WIN_TMP, L.T("Кэш шрифтов"), false, L.T("Служба кэша шрифтов будет перезапущена; кэш создастся заново"),
                @"%WINDIR%\ServiceProfiles\LocalService\AppData\Local\FontCache");
            fonts.Services = new[] { "FontCache" };
            all.Add(fonts);
            all.Add(T(WIN_TMP, L.T("Список недавних файлов"), false, L.T("Очистит «Недавние» в Проводнике и в меню Пуск (приватность)"),
                @"%APPDATA%\Microsoft\Windows\Recent|*.lnk", @"%APPDATA%\Microsoft\Windows\Recent\AutomaticDestinations",
                @"%APPDATA%\Microsoft\Windows\Recent\CustomDestinations"));

            // ================================================ Windows — обновления и остатки установки
            var wu = T(WIN_UPD, L.T("Загрузки Центра обновления Windows"), false,
                L.T("Уже установленные обновления. Службы wuauserv и BITS будут перезапущены"),
                @"%WINDIR%\SoftwareDistribution\Download");
            wu.Services = new[] { "wuauserv", "bits" };
            all.Add(wu);

            var dosvc = T(WIN_UPD, L.T("Кэш оптимизации доставки"), true, L.T("Файлы обновлений для раздачи другим ПК"),
                @"%WINDIR%\ServiceProfiles\NetworkService\AppData\Local\Microsoft\Windows\DeliveryOptimization\Cache");
            dosvc.Services = new[] { "DoSvc" };
            all.Add(dosvc);

            var winOld = T(WIN_UPD, L.T("Предыдущая версия Windows (Windows.old)"), false,
                L.T("Остаётся после обновления системы. После удаления откатиться на прошлую версию будет нельзя"),
                @"%SYSTEMDRIVE%\Windows.old");
            winOld.TakeOwnership = true; winOld.RemoveRoot = true;
            all.Add(winOld);
            var setupLeft = T(WIN_UPD, L.T("Остатки установки Windows"), false,
                L.T("Папки $Windows.~BT / ~WS, $WinREAgent, $SysReset, $GetCurrent, Windows10Upgrade после обновления системы"),
                @"%SYSTEMDRIVE%\$Windows.~BT", @"%SYSTEMDRIVE%\$Windows.~WS", @"%SYSTEMDRIVE%\$WinREAgent",
                @"%SYSTEMDRIVE%\$SysReset", @"%SYSTEMDRIVE%\$GetCurrent", @"%SYSTEMDRIVE%\Windows10Upgrade");
            setupLeft.TakeOwnership = true; setupLeft.RemoveRoot = true;
            all.Add(setupLeft);
            all.Add(T(WIN_UPD, L.T("Файлы обновления Windows (ESD)"), false, L.T("Образы, скачанные помощником по обновлению"), @"%SYSTEMDRIVE%\ESD"));

            var shadows = T(WIN_UPD, L.T("Старые точки восстановления системы"), false,
                L.T("Удаляются все, кроме самой последней. Размер — место, занятое теневыми копиями на всех дисках"));
            shadows.Special = SpecialKind.ShadowCopies;
            all.Add(shadows);

            all.Add(T(WIN_UPD, L.T("Кэш патчей установщика ($PatchCache$)"), false,
                L.T("Резервные копии файлов для обновлений MSI-программ; при необходимости их запросят с диска установки"),
                @"%WINDIR%\Installer\$PatchCache$"));
            all.Add(T(WIN_UPD, L.T("Распакованные установщики драйверов"), false,
                L.T("Папки, куда NVIDIA / AMD распаковывают драйверы перед установкой. Сами драйверы не удаляются"),
                @"%SYSTEMDRIVE%\NVIDIA", @"%SYSTEMDRIVE%\AMD", @"%PROGRAMDATA%\NVIDIA Corporation\Downloader"));

            var dism = T(WIN_UPD, L.T("Очистка хранилища компонентов (DISM)"), false,
                L.T("Удаляет старые версии системных компонентов после обновлений. Долго (5–30 мин), размер заранее неизвестен"));
            dism.Special = SpecialKind.Dism;
            all.Add(dism);

            // ================================================ Windows — журналы, отчёты и дампы
            all.Add(T(WIN_LOG, L.T("Отчёты об ошибках (WER)"), true, L.T("Отчёты о сбоях программ"),
                @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportArchive", @"%PROGRAMDATA%\Microsoft\Windows\WER\ReportQueue",
                @"%PROGRAMDATA%\Microsoft\Windows\WER\Temp", @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportArchive",
                @"%LOCALAPPDATA%\Microsoft\Windows\WER\ReportQueue"));
            all.Add(T(WIN_LOG, L.T("Дампы памяти после сбоев"), true, L.T("Нужны только для отладки синих экранов и падений"),
                @"%LOCALAPPDATA%\CrashDumps", @"%WINDIR%\Minidump", @"%WINDIR%|MEMORY.DMP", @"%WINDIR%\LiveKernelReports"));
            all.Add(T(WIN_LOG, L.T("Журналы обслуживания Windows"), false, L.T("Логи CBS / DISM / установки"),
                @"%WINDIR%\Logs\CBS|*.log", @"%WINDIR%\Logs\CBS|*.cab", @"%WINDIR%\Logs\DISM|*.log",
                @"%WINDIR%\Logs\MoSetup|*.log", @"%WINDIR%\Panther|*.log"));
            all.Add(T(WIN_LOG, L.T("Журналы Центра обновления"), true, "",
                @"%WINDIR%\Logs\WindowsUpdate|*.etl", @"%WINDIR%\SoftwareDistribution\DataStore\Logs|*.log",
                @"%WINDIR%\SoftwareDistribution\DataStore\Logs|*.jrs"));
            all.Add(T(WIN_LOG, L.T("Журналы диагностики и телеметрии"), true, L.T("Трассировки ETL"),
                @"%PROGRAMDATA%\Microsoft\Diagnosis\ETLLogs|*.etl", @"%PROGRAMDATA%\Microsoft\Diagnosis\ETLLogs\AutoLogger|*.etl",
                @"%PROGRAMDATA%\Microsoft\Diagnosis\ETLLogs\ShutdownLogger|*.etl", @"%LOCALAPPDATA%\Diagnostics"));
            all.Add(T(WIN_LOG, L.T("Прочие журналы Windows"), true, L.T("Трассировки сети и обновлений, отладочные логи, PerfLogs"),
                @"%WINDIR%\Logs\NetSetup|*.etl", @"%WINDIR%\Logs\SIH|*.etl", @"%WINDIR%\Logs\waasmedic|*.etl",
                @"%WINDIR%\debug|*.log", @"%SYSTEMDRIVE%\PerfLogs"));
            all.Add(T(WIN_LOG, L.T("Журнал проверок Защитника Windows"), false,
                L.T("История сканирований. Защита от подделки может не дать удалить — тогда файлы будут пропущены"),
                @"%PROGRAMDATA%\Microsoft\Windows Defender\Scans\History\Service"));
            all.Add(T(WIN_LOG, L.T("Логи OneDrive"), true, "", @"%LOCALAPPDATA%\Microsoft\OneDrive\logs"));

            // ================================================ Браузеры
            all.Add(Chromium("Google Chrome", @"%LOCALAPPDATA%\Google\Chrome\User Data", "chrome"));
            all.Add(Chromium("Microsoft Edge", @"%LOCALAPPDATA%\Microsoft\Edge\User Data", "msedge"));
            all.Add(Chromium("Brave", @"%LOCALAPPDATA%\BraveSoftware\Brave-Browser\User Data", "brave"));
            all.Add(Chromium("Vivaldi", @"%LOCALAPPDATA%\Vivaldi\User Data", "vivaldi"));
            all.Add(Chromium(L.T("Яндекс Браузер"), @"%LOCALAPPDATA%\Yandex\YandexBrowser\User Data", "browser"));
            all.Add(Chromium("Chromium", @"%LOCALAPPDATA%\Chromium\User Data", "chrome"));
            all.Add(Chromium("Arc", @"%LOCALAPPDATA%\Packages\TheBrowserCompany.Arc_*\LocalCache\Local\Arc\User Data", "Arc"));
            all.Add(WithProcs(T(BRW, "Opera / Opera GX", true, L.T("Кэш страниц и скриптов"),
                @"%LOCALAPPDATA%\Opera Software\*\Cache", @"%LOCALAPPDATA%\Opera Software\*\*\Cache",
                @"%APPDATA%\Opera Software\*\Code Cache", @"%APPDATA%\Opera Software\*\GPUCache",
                @"%APPDATA%\Opera Software\*\*\Code Cache", @"%APPDATA%\Opera Software\*\*\GPUCache",
                @"%APPDATA%\Opera Software\*\ShaderCache"), "opera"));
            all.Add(WithProcs(T(BRW, "Mozilla Firefox", true, L.T("Кэш страниц, эскизы, кэш запуска и отчёты о сбоях во всех профилях"),
                @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\cache2", @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\startupCache",
                @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\thumbnails", @"%LOCALAPPDATA%\Mozilla\Firefox\Profiles\*\jumpListCache",
                @"%APPDATA%\Mozilla\Firefox\Profiles\*\shader-cache", @"%APPDATA%\Mozilla\Firefox\Crash Reports\pending",
                @"%APPDATA%\Mozilla\Firefox\Crash Reports\submitted", @"%APPDATA%\Mozilla\Firefox\Profiles\*\minidumps"), "firefox"));
            all.Add(WithProcs(T(BRW, "LibreWolf / Waterfox / Zen", true, L.T("Кэш страниц"),
                @"%LOCALAPPDATA%\librewolf\Profiles\*\cache2", @"%LOCALAPPDATA%\Waterfox\Profiles\*\cache2",
                @"%LOCALAPPDATA%\zen\Profiles\*\cache2"), "librewolf", "waterfox", "zen"));
            all.Add(WithProcs(T(BRW, "Tor Browser", true, L.T("Кэш страниц"),
                @"%USERPROFILE%\Desktop\Tor Browser\Browser\TorBrowser\Data\Browser\Caches"), "firefox"));
            all.Add(WithProcs(T(BRW, "Mozilla Thunderbird", true, L.T("Кэш почтового клиента"),
                @"%LOCALAPPDATA%\Thunderbird\Profiles\*\cache2", @"%LOCALAPPDATA%\Thunderbird\Profiles\*\startupCache"), "thunderbird"));

            // ================================================ Мессенджеры и связь
            all.Add(Electron(MSG, "Discord", @"%APPDATA%\discord", "Discord"));
            all.Add(Electron(MSG, "Slack", @"%APPDATA%\Slack", "slack"));
            all.Add(Electron(MSG, "Signal", @"%APPDATA%\Signal", "Signal"));
            all.Add(Electron(MSG, "Element", @"%APPDATA%\Element", "Element"));
            all.Add(Electron(MSG, "Skype", @"%APPDATA%\Microsoft\Skype for Desktop", "Skype"));
            all.Add(Electron(MSG, L.T("Microsoft Teams (классический)"), @"%APPDATA%\Microsoft\Teams", "Teams"));
            all.Add(WithProcs(T(MSG, L.T("Microsoft Teams (новый)"), true, L.T("Кэш встроенного браузера"),
                @"%LOCALAPPDATA%\Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\EBWebView\*\Cache",
                @"%LOCALAPPDATA%\Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\EBWebView\*\Code Cache",
                @"%LOCALAPPDATA%\Packages\MSTeams_*\LocalCache\Microsoft\MSTeams\EBWebView\*\GPUCache"), "ms-teams"));
            all.Add(WithProcs(T(MSG, "WhatsApp", true, L.T("Кэш встроенного браузера"),
                @"%LOCALAPPDATA%\Packages\5319275A.WhatsAppDesktop_*\LocalCache\EBWebView\*\Cache",
                @"%LOCALAPPDATA%\Packages\5319275A.WhatsAppDesktop_*\LocalCache\EBWebView\*\Code Cache",
                @"%LOCALAPPDATA%\Packages\5319275A.WhatsAppDesktop_*\LocalCache\EBWebView\*\GPUCache"), "WhatsApp"));
            all.Add(WithProcs(T(MSG, L.T("Telegram Desktop — медиакэш"), false, L.T("Просмотренные фото/видео скачаются заново при открытии"),
                @"%APPDATA%\Telegram Desktop\tdata\user_data\cache", @"%APPDATA%\Telegram Desktop\tdata\user_data\media_cache",
                @"%APPDATA%\Telegram Desktop\tdata\emoji"), "Telegram"));
            all.Add(WithProcs(T(MSG, "Zoom — " + logs, true, "", @"%APPDATA%\Zoom\logs"), "Zoom"));

            // ================================================ Игры и лаунчеры
            all.Add(WithProcs(T(GAME, L.T("Steam — веб-кэш"), true, L.T("Кэш магазина и интерфейса"),
                @"%LOCALAPPDATA%\Steam\htmlcache", @"%ProgramFiles(x86)%\Steam\appcache\httpcache", @"%ProgramFiles(x86)%\Steam\logs"),
                "steam", "steamwebhelper"));
            all.Add(WithProcs(T(GAME, L.T("Steam — кэш шейдеров игр"), false, L.T("Игры пересоздадут шейдеры (первый запуск дольше)"),
                @"%ProgramFiles(x86)%\Steam\steamapps\shadercache"), "steam"));
            all.Add(WithProcs(T(GAME, L.T("Steam — незавершённые загрузки"), false, L.T("Недокачанные игры и обновления начнут качаться заново"),
                @"%ProgramFiles(x86)%\Steam\steamapps\downloading", @"%ProgramFiles(x86)%\Steam\steamapps\temp",
                @"%ProgramFiles(x86)%\Steam\depotcache"), "steam"));
            all.Add(WithProcs(T(GAME, L.T("Epic Games Launcher — веб-кэш"), true, "",
                @"%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache", @"%LOCALAPPDATA%\EpicGamesLauncher\Saved\webcache_*",
                @"%LOCALAPPDATA%\EpicGamesLauncher\Saved\Logs"), "EpicGamesLauncher"));
            all.Add(WithProcs(T(GAME, "Battle.net — " + cache, true, "",
                @"%PROGRAMDATA%\Blizzard Entertainment\Battle.net\Cache", @"%LOCALAPPDATA%\Battle.net\Cache",
                @"%LOCALAPPDATA%\Battle.net\BrowserCache"), "Battle.net"));
            all.Add(WithProcs(T(GAME, "Ubisoft Connect — " + cache, true, "",
                @"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\cache", @"%ProgramFiles(x86)%\Ubisoft\Ubisoft Game Launcher\logs"),
                "upc", "UbisoftConnect"));
            all.Add(WithProcs(T(GAME, "GOG Galaxy — " + cache, true, "",
                @"%PROGRAMDATA%\GOG.com\Galaxy\webcache", @"%PROGRAMDATA%\GOG.com\Galaxy\logs"), "GalaxyClient"));
            all.Add(T(GAME, L.T("NVIDIA app / GeForce Experience — веб-кэш"), true, "",
                @"%LOCALAPPDATA%\NVIDIA Corporation\NVIDIA app\CefCache", @"%LOCALAPPDATA%\NVIDIA Corporation\NVIDIA GeForce Experience\CefCache"));
            all.Add(T(GAME, L.T("Minecraft — логи и отчёты о сбоях"), true, "",
                @"%APPDATA%\.minecraft\logs", @"%APPDATA%\.minecraft\crash-reports"));

            // ================================================ Медиа, графика и офис
            all.Add(WithProcs(T(MEDIA, L.T("Spotify — кэш музыки"), true, L.T("Офлайн-загрузки не затрагиваются"),
                @"%LOCALAPPDATA%\Spotify\Storage", @"%LOCALAPPDATA%\Spotify\Data", @"%LOCALAPPDATA%\Spotify\Browser\Cache"), "Spotify"));
            all.Add(WithProcs(T(MEDIA, L.T("Adobe — медиакэш (Premiere, After Effects)"), true, L.T("Кэш аудио/видео — пересоздаётся при открытии проектов"),
                @"%APPDATA%\Adobe\Common\Media Cache Files", @"%APPDATA%\Adobe\Common\Media Cache",
                @"%APPDATA%\Adobe\Common\Peak Files"), "Adobe Premiere Pro", "AfterFX"));
            all.Add(T(MEDIA, "DaVinci Resolve — " + logs, true, "", @"%APPDATA%\Blackmagic Design\DaVinci Resolve\Support\logs"));
            all.Add(T(MEDIA, L.T("OBS Studio — логи и отчёты о сбоях"), true, "",
                @"%APPDATA%\obs-studio\logs", @"%APPDATA%\obs-studio\crashes"));
            all.Add(T(MEDIA, L.T("VLC — кэш обложек"), true, "", @"%APPDATA%\vlc\art"));
            all.Add(Electron(MEDIA, "Figma", @"%APPDATA%\Figma\DesktopProfile\*", "Figma"));
            all.Add(WithProcs(T(MEDIA, L.T("Microsoft Office — кэш документов"), false,
                L.T("Кэш Office Upload Center. Закройте Word/Excel; несинхронизированные правки сначала сохраните"),
                @"%LOCALAPPDATA%\Microsoft\Office\16.0\OfficeFileCache", @"%LOCALAPPDATA%\Microsoft\Office\16.0\Wef"),
                "WINWORD", "EXCEL", "POWERPNT", "OUTLOOK"));

            // ================================================ Другие программы
            all.Add(Electron(APP, "Postman", @"%APPDATA%\Postman", "Postman"));
            all.Add(Electron(APP, "Notion", @"%APPDATA%\Notion", "Notion"));
            all.Add(Electron(APP, "Obsidian", @"%APPDATA%\obsidian", "Obsidian"));
            all.Add(Electron(APP, "GitHub Desktop", @"%APPDATA%\GitHub Desktop", "GitHubDesktop"));
            all.Add(T(APP, L.T("WebView2 в других программах"), true, L.T("Кэш встроенного браузера Edge внутри приложений"),
                @"%LOCALAPPDATA%\*\EBWebView\*\Cache", @"%LOCALAPPDATA%\*\EBWebView\*\Code Cache",
                @"%LOCALAPPDATA%\*\EBWebView\*\GPUCache", @"%LOCALAPPDATA%\*\*\EBWebView\*\Cache",
                @"%LOCALAPPDATA%\*\*\EBWebView\*\Code Cache", @"%LOCALAPPDATA%\*\*\EBWebView\*\GPUCache"));
            all.Add(T(APP, L.T("Установщики обновлений приложений (Squirrel)"), true,
                L.T("Пакеты .nupkg, которые Discord, Slack, GitHub Desktop и др. хранят после обновления"),
                @"%LOCALAPPDATA%\*\packages|*.nupkg"));
            all.Add(T(APP, "Dropbox — " + cache, true, L.T("Временные копии удалённых и синхронизируемых файлов"),
                @"%USERPROFILE%\Dropbox\.dropbox.cache"));
            all.Add(T(APP, L.T("Java — кэш апплетов"), true, "", @"%USERPROFILE%\AppData\LocalLow\Sun\Java\Deployment\cache"));

            // ================================================ Редакторы кода и IDE
            all.Add(Add(CodeEditor(IDE, "Visual Studio Code", @"%APPDATA%\Code", "Code")));
            all.Add(T(IDE, L.T("Visual Studio Code — состояние рабочих областей и история правок"), false,
                L.T("Запомненные открытые файлы и состояние расширений по каждому проекту, локальная история (Timeline)"),
                @"%APPDATA%\Code\User\workspaceStorage", @"%APPDATA%\Code\User\History"));
            all.Add(WithProcs(T(IDE, L.T("Visual Studio — кэш компонентов"), true, L.T("Пересоздаётся при запуске VS"),
                @"%LOCALAPPDATA%\Microsoft\VisualStudio\*\ComponentModelCache"), "devenv"));
            all.Add(T(IDE, L.T("Visual Studio — кэш установщика"), false,
                L.T("Пакеты, из которых устанавливалась VS; при изменении или восстановлении VS скачаются заново"),
                @"%PROGRAMDATA%\Microsoft\VisualStudio\Packages"));
            all.Add(WithProcs(T(IDE, L.T("JetBrains IDE — кэши и логи"), false,
                L.T("PhpStorm, IntelliJ, Android Studio и др. Проекты будут заново проиндексированы"),
                @"%LOCALAPPDATA%\JetBrains\*\caches", @"%LOCALAPPDATA%\JetBrains\*\index", @"%LOCALAPPDATA%\JetBrains\*\log",
                @"%LOCALAPPDATA%\JetBrains\*\tmp", @"%LOCALAPPDATA%\Google\AndroidStudio*\caches",
                @"%LOCALAPPDATA%\Google\AndroidStudio*\index", @"%LOCALAPPDATA%\Google\AndroidStudio*\log",
                @"%LOCALAPPDATA%\Google\AndroidStudio*\tmp"),
                "phpstorm64", "idea64", "studio64", "webstorm64", "pycharm64", "goland64", "rider64", "clion64", "datagrip64"));
            var oldIde = T(IDE, L.T("Папки старых версий IDE JetBrains / Android Studio"), false,
                L.T("Для каждой IDE остаётся только самая новая версия (например, PhpStorm2023.3 удаляется, если есть PhpStorm2025.2)"));
            oldIde.CustomDirs = OldIdeVersionDirs;
            oldIde.RemoveRoot = true;
            all.Add(oldIde);
            all.Add(T(IDE, L.T("JetBrains Toolbox — загрузки"), true, L.T("Скачанные установщики IDE"), @"%LOCALAPPDATA%\JetBrains\Toolbox\download"));
            all.Add(T(IDE, L.T("JDK, скачанные IntelliJ (~/.jdks)"), false,
                L.T("Проекты, которым назначен удалённый JDK, придётся перенастроить (Project Structure → SDK)"), @"%USERPROFILE%\.jdks"));

            // ================================================ Разработка — пакеты и зависимости
            all.Add(T(PKG, L.T("npm — кэш"), false, L.T("То же, что npm cache clean --force"), @"%LOCALAPPDATA%\npm-cache"));
            all.Add(T(PKG, L.T("Yarn — кэш"), false, "", @"%LOCALAPPDATA%\Yarn\Cache", @"%LOCALAPPDATA%\Yarn\Berry\cache"));
            all.Add(T(PKG, L.T("pnpm — хранилище"), false,
                L.T("Файлы связаны жёсткими ссылками с node_modules — место освободится не полностью. Лучше: pnpm store prune"),
                @"%LOCALAPPDATA%\pnpm\store", @"%LOCALAPPDATA%\pnpm-cache"));
            all.Add(T(PKG, L.T("Bun — кэш"), false, "", @"%USERPROFILE%\.bun\install\cache"));
            all.Add(T(PKG, L.T("Deno — кэш"), false, "", @"%LOCALAPPDATA%\deno"));
            all.Add(T(PKG, "Corepack — " + cache, false, L.T("Скачанные версии yarn / pnpm"), @"%LOCALAPPDATA%\node\corepack"));
            all.Add(T(PKG, L.T("Composer — кэш"), false, L.T("Пакеты скачаются заново"),
                @"%LOCALAPPDATA%\Composer\files", @"%LOCALAPPDATA%\Composer\repo", @"%LOCALAPPDATA%\Composer\vcs",
                @"%APPDATA%\Composer\cache"));
            all.Add(T(PKG, L.T("pip / uv — кэш Python"), false, "", @"%LOCALAPPDATA%\pip\cache", @"%LOCALAPPDATA%\uv\cache"));
            all.Add(T(PKG, "Poetry — " + cache, false, "", @"%LOCALAPPDATA%\pypoetry\Cache"));
            all.Add(T(PKG, L.T("Conda — скачанные пакеты"), false, L.T("То же, что conda clean --all"),
                @"%USERPROFILE%\miniconda3\pkgs", @"%USERPROFILE%\anaconda3\pkgs", @"%USERPROFILE%\miniforge3\pkgs"));
            all.Add(T(PKG, L.T("Maven — локальный репозиторий"), false, L.T("Все артефакты скачаются заново"), @"%USERPROFILE%\.m2\repository"));
            all.Add(T(PKG, L.T("Maven wrapper — дистрибутивы"), true, "", @"%USERPROFILE%\.m2\wrapper\dists"));
            all.Add(T(PKG, L.T("Gradle — кэш зависимостей и сборки"), false,
                L.T("Перед очисткой остановите демона: gradlew --stop. Зависимости скачаются заново"),
                @"%USERPROFILE%\.gradle\caches"));
            all.Add(T(PKG, L.T("Gradle — скачанные дистрибутивы (wrapper)"), false, L.T("Нужная версия Gradle скачается при сборке"),
                @"%USERPROFILE%\.gradle\wrapper\dists"));
            all.Add(T(PKG, L.T("Ivy / sbt / Coursier — кэш"), false, "",
                @"%USERPROFILE%\.ivy2\cache", @"%USERPROFILE%\.sbt\boot", @"%LOCALAPPDATA%\Coursier\cache"));
            all.Add(T(PKG, L.T("JBang — кэш"), false, "", @"%USERPROFILE%\.jbang\cache"));
            all.Add(T(PKG, L.T("NuGet — HTTP-кэш"), true, "", @"%LOCALAPPDATA%\NuGet\v3-cache", @"%LOCALAPPDATA%\NuGet\plugins-cache"));
            all.Add(T(PKG, L.T("NuGet — пакеты"), false, L.T("Пакеты .NET скачаются заново"), @"%USERPROFILE%\.nuget\packages"));
            all.Add(T(PKG, L.T("Go — скачанные модули"), false, L.T("То же, что go clean -modcache"), @"%USERPROFILE%\go\pkg\mod"));
            all.Add(T(PKG, L.T("Rust Cargo — скачанные архивы"), false, "", @"%USERPROFILE%\.cargo\registry\cache"));
            all.Add(T(PKG, L.T("Rust Cargo — распакованные исходники и git-зависимости"), false, L.T("Cargo распакует и склонирует заново"),
                @"%USERPROFILE%\.cargo\registry\src", @"%USERPROFILE%\.cargo\git\checkouts"));
            all.Add(T(PKG, L.T("rustup — загрузки и временные файлы"), true, "", @"%USERPROFILE%\.rustup\downloads", @"%USERPROFILE%\.rustup\tmp"));
            all.Add(T(PKG, "vcpkg — " + cache, false, L.T("Собранные пакеты C++ будут пересобраны"),
                @"%LOCALAPPDATA%\vcpkg\archives", @"%LOCALAPPDATA%\vcpkg\downloads"));
            all.Add(T(PKG, "Conan — " + cache, false, L.T("Пакеты C++ скачаются или соберутся заново"),
                @"%USERPROFILE%\.conan\data", @"%USERPROFILE%\.conan2\p"));
            all.Add(T(PKG, L.T("Dart / Flutter — pub cache"), false, L.T("Пакеты скачаются заново (flutter pub get)"), @"%LOCALAPPDATA%\Pub\Cache"));
            all.Add(T(PKG, L.T("Scoop — кэш установщиков"), true, "", @"%USERPROFILE%\scoop\cache"));
            all.Add(T(PKG, L.T("Chocolatey — временные файлы"), true, "", @"%TEMP%\chocolatey", @"%PROGRAMDATA%\chocolatey\lib-bad"));
            all.Add(T(PKG, L.T("winget — логи и временные файлы"), true, "",
                @"%LOCALAPPDATA%\Packages\Microsoft.DesktopAppInstaller_*\LocalState\DiagOutputDir"));
            all.Add(T(PKG, L.T("Electron — скачанные бинарники"), true, "", @"%LOCALAPPDATA%\electron\Cache", @"%LOCALAPPDATA%\electron-builder\Cache"));
            all.Add(T(PKG, L.T("node-gyp — заголовки Node"), true, "", @"%LOCALAPPDATA%\node-gyp\Cache"));
            all.Add(T(PKG, L.T("Playwright — браузеры"), false, L.T("Тесты скачают браузеры заново (playwright install)"), @"%LOCALAPPDATA%\ms-playwright"));
            all.Add(T(PKG, L.T("Cypress — бинарники"), false, "", @"%LOCALAPPDATA%\Cypress\Cache"));
            all.Add(T(PKG, L.T("Puppeteer / Selenium — браузеры и драйверы"), false, redownload,
                @"%USERPROFILE%\.cache\puppeteer", @"%USERPROFILE%\.cache\selenium"));
            all.Add(T(PKG, L.T("Общий кэш ~/.cache"), false, L.T("Кэши разных утилит, в т.ч. скачанные ML-модели Hugging Face (бывают десятки ГБ)"),
                @"%USERPROFILE%\.cache"));

            // ================================================ Разработка — сборка, SDK и инструменты
            all.Add(T(BUILD, L.T("Go — кэш сборки"), false, "", @"%LOCALAPPDATA%\go-build"));
            all.Add(T(BUILD, L.T("Gradle — логи демонов"), true, "", @"%USERPROFILE%\.gradle\daemon\*|*.log"));
            all.Add(T(BUILD, L.T("Gradle — автоматически скачанные JDK"), false, L.T("JDK из toolchains; скачаются заново при необходимости"),
                @"%USERPROFILE%\.gradle\jdks"));
            all.Add(T(BUILD, L.T("Kotlin daemon — логи"), true, "", @"%LOCALAPPDATA%\kotlin\daemon|*.log"));
            all.Add(T(BUILD, L.T("Kotlin/Native — кэш"), false, "", @"%USERPROFILE%\.konan\cache"));
            all.Add(T(BUILD, L.T("Android — кэш сборки и SDK"), true, L.T("Временные файлы Android SDK / AGP"),
                @"%USERPROFILE%\.android\cache", @"%USERPROFILE%\.android\build-cache", @"%LOCALAPPDATA%\Android\Sdk\.temp",
                @"%LOCALAPPDATA%\Android\Sdk\.downloadIntermediates"));
            all.Add(T(BUILD, L.T("Android — снимки эмуляторов"), false, L.T("Быстрый старт эмулятора; после удаления первая загрузка будет холодной"),
                @"%USERPROFILE%\.android\avd\*.avd\snapshots"));
            all.Add(T(BUILD, L.T("TypeScript — кэш типов"), true, "", @"%LOCALAPPDATA%\Microsoft\TypeScript"));
            all.Add(T(BUILD, "ccache — " + cache, false, L.T("Кэш компиляции C/C++; следующая сборка будет полной"), @"%LOCALAPPDATA%\ccache"));
            all.Add(T(BUILD, "Bazel — " + cache, false, L.T("Результаты сборки и скачанные зависимости Bazel"), @"%USERPROFILE%\_bazel_%USERNAME%"));
            all.Add(T(BUILD, "Symfony CLI — " + cache, true, "", @"%USERPROFILE%\.symfony5\cache", @"%USERPROFILE%\.symfony5\log"));
            all.Add(T(BUILD, L.T("Unity — кэш"), false, "", @"%LOCALAPPDATA%\Unity\cache", @"%APPDATA%\UnityHub\Cache"));
            all.Add(T(BUILD, L.T("Unreal Engine — DerivedDataCache"), false, L.T("Пересобирается при открытии проектов (долго)"),
                @"%LOCALAPPDATA%\UnrealEngine\Common\DerivedDataCache"));

            // ================================================ Разработка — контейнеры, ВМ и инфраструктура
            all.Add(WithProcs(T(INFRA, L.T("Docker Desktop — логи"), true, L.T("Логи самого Docker Desktop (не контейнеров)"),
                @"%LOCALAPPDATA%\Docker\log", @"%APPDATA%\Docker\log"), "Docker Desktop"));
            all.Add(T(INFRA, L.T("Vagrant — скачанные boxes"), false, L.T("Образы ВМ скачаются заново при vagrant up"), @"%USERPROFILE%\.vagrant.d\boxes"));
            all.Add(T(INFRA, "kubectl — " + cache, true, "", @"%USERPROFILE%\.kube\cache", @"%USERPROFILE%\.kube\http-cache"));
            all.Add(T(INFRA, "minikube — " + cache, false, L.T("Скачанные образы и бинарники Kubernetes"), @"%USERPROFILE%\.minikube\cache"));
            all.Add(T(INFRA, L.T("Terraform — кэш плагинов"), false, L.T("Провайдеры скачаются заново при terraform init"),
                @"%APPDATA%\terraform.d\plugin-cache"));

            // ================================================ ИИ — приложения и агенты
            all.Add(T(AI_APP, L.T("Claude Code — временные файлы"), true,
                L.T("Снимки оболочки, отладочные логи, статистика, старые todo, история правок файлов, кэш вставок"),
                @"%USERPROFILE%\.claude\shell-snapshots", @"%USERPROFILE%\.claude\debug", @"%USERPROFILE%\.claude\statsig",
                @"%USERPROFILE%\.claude\todos", @"%USERPROFILE%\.claude\file-history",
                @"%USERPROFILE%\.claude\paste-cache", @"%USERPROFILE%\.claude\telemetry", @"%USERPROFILE%\.claude\cache",
                @"%LOCALAPPDATA%\claude-cli-nodejs\Cache", @"%USERPROFILE%\.cache\claude\staging"));
            all.Add(T(AI_APP, L.T("Claude Code — история сессий"), false,
                L.T("Логи прошлых диалогов (папка projects). После удаления нельзя будет продолжить старые сессии (--resume)"),
                @"%USERPROFILE%\.claude\projects"));
            // версия Claude Desktop из Microsoft Store хранит профиль внутри папки пакета
            var claudeDesktop = Electron(AI_APP, "Claude Desktop", @"%APPDATA%\Claude", "Claude");
            AddElectronDirs(claudeDesktop, @"%LOCALAPPDATA%\Packages\Claude_*\LocalCache\Roaming\Claude");
            all.Add(Add(claudeDesktop, @"%LOCALAPPDATA%\Claude\logs"));
            all.Add(AddElectronDirs(Electron(AI_APP, "ChatGPT Desktop", @"%APPDATA%\ChatGPT", "ChatGPT"),
                @"%LOCALAPPDATA%\Packages\OpenAI.ChatGPT-Desktop_*\LocalCache\Roaming\ChatGPT"));
            all.Add(Electron(AI_APP, "Chatbox", @"%APPDATA%\xyz.chatboxapp.app", "Chatbox"));
            all.Add(Electron(AI_APP, "Jan", @"%APPDATA%\Jan", "Jan"));
            all.Add(Electron(AI_APP, "AnythingLLM", @"%APPDATA%\anythingllm-desktop", "AnythingLLM"));
            all.Add(Electron(AI_APP, "Msty", @"%APPDATA%\Msty", "Msty"));
            all.Add(Add(Electron(AI_APP, "LM Studio", @"%APPDATA%\LM Studio", "LM Studio"),
                @"%USERPROFILE%\.lmstudio\server-logs", @"%LOCALAPPDATA%\lm-studio-updater"));
            all.Add(T(AI_APP, L.T("Ollama — логи и загруженные обновления"), true, "",
                @"%LOCALAPPDATA%\Ollama|*.log", @"%LOCALAPPDATA%\Ollama\updates"));
            // редакторы кода с ИИ (форки VS Code)
            all.Add(CodeEditor(AI_APP, "Cursor", @"%APPDATA%\Cursor", "Cursor"));
            all.Add(CodeEditor(AI_APP, "Windsurf", @"%APPDATA%\Windsurf", "Windsurf"));
            all.Add(CodeEditor(AI_APP, "Kiro", @"%APPDATA%\Kiro", "Kiro"));
            all.Add(CodeEditor(AI_APP, "Antigravity", @"%APPDATA%\Antigravity", "Antigravity"));
            all.Add(CodeEditor(AI_APP, "Trae", @"%APPDATA%\Trae", "Trae"));
            // консольные агенты
            all.Add(T(AI_APP, L.T("OpenAI Codex CLI — временные файлы"), true, "", @"%USERPROFILE%\.codex\tmp"));
            all.Add(T(AI_APP, L.T("OpenAI Codex CLI — сессии и логи"), false, L.T("История сессий Codex"),
                @"%USERPROFILE%\.codex\sessions", @"%USERPROFILE%\.codex\log"));
            all.Add(T(AI_APP, L.T("Gemini CLI — временные файлы"), true, "", @"%USERPROFILE%\.gemini\tmp"));
            all.Add(T(AI_APP, L.T("Qwen Code — временные файлы"), true, "", @"%USERPROFILE%\.qwen\tmp"));
            all.Add(T(AI_APP, "Aider — " + cache, true, "", @"%USERPROFILE%\.aider\caches"));
            all.Add(T(AI_APP, L.T("Continue — индекс кода и логи"), false, L.T("Индекс проектов будет построен заново"),
                @"%USERPROFILE%\.continue\index", @"%USERPROFILE%\.continue\logs"));
            all.Add(T(AI_APP, L.T("JetBrains Junie — логи и кэш"), true, "",
                @"%USERPROFILE%\.junie\logs", @"%USERPROFILE%\.junie\cache", @"%USERPROFILE%\.junie\tmp",
                @"%LOCALAPPDATA%\JetBrains\*\junie\cache", @"%LOCALAPPDATA%\JetBrains\*\junie\logs"));

            // ================================================ ИИ — скачанные модели
            all.Add(T(AI_MODEL, "Ollama", false, L.T("Модели скачаются заново командой ollama pull (часто десятки ГБ)"),
                @"%USERPROFILE%\.ollama\models"));
            all.Add(T(AI_MODEL, "LM Studio", false, models,
                @"%USERPROFILE%\.lmstudio\models", @"%USERPROFILE%\.cache\lm-studio\models"));
            all.Add(T(AI_MODEL, "Jan", false, models, @"%APPDATA%\Jan\data\models", @"%USERPROFILE%\jan\models"));
            all.Add(T(AI_MODEL, "GPT4All", false, models, @"%LOCALAPPDATA%\nomic.ai\GPT4All|*.gguf"));
            all.Add(T(AI_MODEL, "Hugging Face / PyTorch / Whisper", false,
                L.T("Модели и датасеты, которые скачали Python-библиотеки; при следующем запуске скачаются заново (бывают десятки ГБ)"),
                @"%USERPROFILE%\.cache\huggingface", @"%USERPROFILE%\.cache\torch", @"%USERPROFILE%\.cache\whisper"));

            // ================================================ Свои места
            foreach (string spec in Settings.CustomTargets)
                all.Add(T(CUSTOM, spec, false, L.T("Удаляется всё содержимое папки (или файлы по маске). Сама папка остаётся"), spec));

            return all;
        }

        /// <summary>Папки старых версий IDE: PhpStorm2023.3, IntelliJIdea2024.1, AndroidStudio2023.1 ... (кроме самой новой).</summary>
        static List<string> OldIdeVersionDirs()
        {
            var result = new List<string>();
            string la = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string ra = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var rx = new Regex(@"^([A-Za-z]+?)(\d{4}\.\d+(?:\.\d+)?)$");
            foreach (string root in new[] { la + @"\JetBrains", ra + @"\JetBrains", la + @"\Google", ra + @"\Google" })
            {
                if (!Directory.Exists(root)) continue;
                var groups = new Dictionary<string, List<KeyValuePair<Version, string>>>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string d in Directory.GetDirectories(root))
                    {
                        var m = rx.Match(Path.GetFileName(d));
                        Version v;
                        if (!m.Success || !Version.TryParse(m.Groups[2].Value, out v)) continue;
                        List<KeyValuePair<Version, string>> g;
                        if (!groups.TryGetValue(m.Groups[1].Value, out g)) { g = new List<KeyValuePair<Version, string>>(); groups[m.Groups[1].Value] = g; }
                        g.Add(new KeyValuePair<Version, string>(v, d));
                    }
                }
                catch { }
                foreach (var g in groups.Values)
                    if (g.Count > 1)
                        result.AddRange(g.OrderByDescending(x => x.Key).Skip(1).Select(x => x.Value));
            }
            return result;
        }

        // ------------------------------------------------ защита от ошибок в шаблонах
        static readonly string[] ForbiddenEnv =
        {
            "SYSTEMDRIVE", "WINDIR", "USERPROFILE", "LOCALAPPDATA", "APPDATA", "PROGRAMDATA",
            "ProgramFiles", "ProgramFiles(x86)", "PUBLIC"
        };

        /// <summary>Никогда не чистим содержимое корневых и «важных» папок целиком.</summary>
        public static bool IsDangerous(string dir, string mask)
        {
            string full;
            try { full = Path.GetFullPath(dir).TrimEnd('\\'); } catch { return true; }
            if (full.Length <= 3) return true;                                     // C:\
            int depth = full.Split('\\').Length - 1;
            if (mask == null && depth < 1) return true;
            foreach (string env in ForbiddenEnv)
            {
                string v = Environment.GetEnvironmentVariable(env);
                if (string.IsNullOrEmpty(v)) continue;
                if (mask == null && string.Equals(full, v.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return true;
            }
            string users = Path.Combine(Environment.GetEnvironmentVariable("SYSTEMDRIVE") + "\\", "Users");
            if (mask == null && string.Equals(full, users, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }
}
