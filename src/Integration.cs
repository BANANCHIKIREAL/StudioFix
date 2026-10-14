using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace StudioFix
{
    // Compatibility with earlier builds: restore recorded Studio shortcuts only.
    public sealed class ShortcutRecord
    {
        public string Path { get; set; }
        public string Backup { get; set; }
        public string BackupHash { get; set; }
        public string InstalledHash { get; set; }
        public bool Created { get; set; }
    }
    public sealed class IntegrationState
    {
        public int Schema { get; set; } = 1;
        public string Target { get; set; }
        public List<ShortcutRecord> Shortcuts { get; set; } = new List<ShortcutRecord>();
    }
    public sealed class Integration
    {
        readonly AppStore store;
        readonly string[] folders;
        string StatePath { get { return Path.Combine(store.Root, "shortcuts.json"); } }
        public Integration(AppStore storage, string executable, string[] shortcutFolders = null)
        {
            store = storage;
            folders = shortcutFolders ?? Discovery.UserShortcutFolders().Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }).Where(f => !String.IsNullOrEmpty(f)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
        bool Allowed(string path)
        { return path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && folders.Any(f => path.StartsWith(Path.GetFullPath(f).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)); }
        IntegrationState Load()
        {
            if (!File.Exists(StatePath)) return new IntegrationState();
            var state = Data.Read<IntegrationState>(StatePath);
            if (state == null || state.Schema != 1 || state.Shortcuts == null || state.Shortcuts.Count > 100) throw new InvalidDataException("Файл старой настройки ярлыков повреждён.");
            return state;
        }
        public bool Connected
        {
            get { try { var state = Load(); return state.Shortcuts.Any(s => File.Exists(s.Path) && Data.Same(ShellLinks.Target(s.Path), state.Target)); } catch { return false; } }
        }
        public int Restore()
        {
            var state = Load(); int restored = 0;
            foreach (var item in state.Shortcuts.ToArray())
            {
                string path = Path.GetFullPath(item.Path);
                if (!Allowed(path)) throw new InvalidDataException("Сохранённый ярлык находится вне папок рабочего стола и меню Пуск.");
                if (File.Exists(path) && (!Data.Same(ShellLinks.Target(path), state.Target) || (!String.IsNullOrEmpty(item.InstalledHash) && !Data.Same(Data.HashFile(path), item.InstalledHash)))) continue;
                if (item.Created) { if (File.Exists(path)) File.Delete(path); }
                else
                {
                    string allowed = Path.Combine(store.Root, "shortcut-backups") + Path.DirectorySeparatorChar;
                    if (String.IsNullOrEmpty(item.Backup) || !Path.GetFullPath(item.Backup).StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !File.Exists(item.Backup) || !Data.Same(Data.HashFile(item.Backup), item.BackupHash)) throw new InvalidDataException("Резервная копия старого ярлыка отсутствует или изменена.");
                    Data.SaveBytes(path, File.ReadAllBytes(item.Backup));
                }
                state.Shortcuts.Remove(item); Data.Save(StatePath, state); restored++;
            }
            return restored;
        }
    }
    public static class IntegrationService
    {
        public static void Restore(AppStore store, string exe)
        {
            try { new Integration(store, exe).Restore(); return; }
            catch (UnauthorizedAccessException) { }
            string sessions = Path.Combine(store.Root, "sessions"); Directory.CreateDirectory(sessions);
            string result = Path.Combine(sessions, Guid.NewGuid().ToString("N") + ".json");
            try
            {
                string args = "--shortcut-helper --restore --data-root " + Data.Quote(store.Root) + " --result " + Data.Quote(result);
                using (var process = Process.Start(new ProcessStartInfo { FileName = exe, Arguments = args, Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })) process.WaitForExit();
                if (!File.Exists(result)) throw new IOException("Помощник восстановления ярлыка не вернул результат.");
                var response = Data.Read<HelperResult>(result);
                if (!response.Success) throw new IOException(response.Message ?? "Не удалось вернуть оригинальный ярлык Studio.");
            }
            finally { if (File.Exists(result)) File.Delete(result); }
        }
    }
}
