using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;

namespace StudioFix
{
    public static class ShellLinks
    {
        public static string Target(string path)
        {
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                link = ((dynamic)shell).CreateShortcut(path); return (string)((dynamic)link).TargetPath;
            }
            finally
            { if (link != null) Marshal.FinalReleaseComObject(link); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
        public static void Write(string path, string target, string args = null, string icon = null)
        {
            object shell = null, link = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                link = ((dynamic)shell).CreateShortcut(path);
                ((dynamic)link).TargetPath = target;
                ((dynamic)link).Arguments = args ?? "";
                ((dynamic)link).WorkingDirectory = Path.GetDirectoryName(target);
                ((dynamic)link).Description = "StudioFix — проверка и запуск TikTok LIVE Studio";
                ((dynamic)link).IconLocation = (icon ?? target) + ",0";
                ((dynamic)link).Save();
            }
            finally
            { if (link != null) Marshal.FinalReleaseComObject(link); if (shell != null) Marshal.FinalReleaseComObject(shell); }
        }
    }
    public static class Discovery
    {
        public const string Executable = "TikTok LIVE Studio.exe";
        public static bool IsRunning()
        {
            foreach (var process in Process.GetProcessesByName("TikTok LIVE Studio"))
            { using (process) { try { if (!process.HasExited) return true; } catch { return true; } } }
            return false;
        }
        public static string Resolve(string input) { return Resolve(input, 0); }
        static string Resolve(string input, int depth)
        {
            if (depth > 8) return null;
            if (String.IsNullOrWhiteSpace(input)) return null;
            string value = Environment.ExpandEnvironmentVariables(input.Trim().Trim('"'));
            var command = Regex.Match(input.Trim(), "^\\s*\"([^\"]+\\.(?:exe|lnk))\"", RegexOptions.IgnoreCase);
            if (command.Success) value = Environment.ExpandEnvironmentVariables(command.Groups[1].Value);
            string root = Path.GetFullPath(value);
            if (File.Exists(root) && root.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                string target = ShellLinks.Target(root);
                if (Data.Same(Path.GetFileName(target), "StudioFix.exe"))
                {
                    try
                    {
                        var storage = new AppStore(); var state = Data.Read<IntegrationState>(Path.Combine(storage.Root, "shortcuts.json"));
                        var original = state.Shortcuts.FirstOrDefault(s => Data.Same(s.Path, root) && !s.Created && s.Backup != null);
                        if (original != null && Path.GetFullPath(original.Backup).StartsWith(Path.Combine(storage.Root, "shortcut-backups") + "\\", StringComparison.OrdinalIgnoreCase) && Data.Same(Data.HashFile(original.Backup), original.BackupHash)) target = ShellLinks.Target(original.Backup);
                    }
                    catch { }
                }
                return Resolve(target, depth + 1);
            }
            if (File.Exists(root)) root = Path.GetDirectoryName(root);
            if (!Directory.Exists(root)) return null;
            // Pasted main.js/resources paths are normalized back to the runtime folder.
            string ancestor = root;
            for (int i = 0; i < 5 && ancestor != null; i++)
            { if (IsVersion(ancestor)) { root = ancestor; break; } var up = Directory.GetParent(ancestor); ancestor = up == null ? null : up.FullName; }
            // A saved version path follows the installer's active version after updates.
            var parent = Directory.GetParent(root);
            if (Regex.IsMatch(Path.GetFileName(root), @"^\d+\.\d+\.\d+(\.\d+)?$") && parent != null && File.Exists(Path.Combine(parent.FullName, "launcher_config.json"))) root = parent.FullName;
            string config = Path.Combine(root, "launcher_config.json");
            if (File.Exists(config))
            {
                try
                {
                    var values = Data.Read<Dictionary<string, object>>(config);
                    if (values.ContainsKey("cur_path"))
                    {
                        string chosen = Path.GetFullPath(Path.Combine(root, Convert.ToString(values["cur_path"])));
                        if (chosen.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) && IsVersion(chosen)) return chosen;
                    }
                }
                catch { }
            }
            if (IsVersion(root)) return root;
            foreach (var folder in Directory.EnumerateDirectories(root).Select(p => new { Path = p, Version = ParseVersion(Path.GetFileName(p)) }).Where(p => p.Version != null).OrderByDescending(p => p.Version))
                if (IsVersion(folder.Path)) return folder.Path;
            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                if (IsVersion(folder)) return folder;
                string nested = Path.Combine(folder, "bin"); if (IsVersion(nested)) return nested;
            }
            return null;
        }
        static Version ParseVersion(string value) { Version version; return Version.TryParse(value, out version) ? version : null; }
        public static bool IsVersion(string root)
        {
            return File.Exists(Path.Combine(root, Executable));
        }
        public static string Find(Settings settings)
        {
            var roots = new List<string>();
            if (!String.IsNullOrWhiteSpace(settings.StudioRoot)) roots.Add(settings.StudioRoot);
            foreach (var process in Process.GetProcessesByName("TikTok LIVE Studio"))
                using (process) { try { roots.Add(Path.GetDirectoryName(process.MainModule.FileName)); } catch { } }
            foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
                foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
                {
                    try
                    {
                        using (var registry = RegistryKey.OpenBaseKey(hive, view))
                        using (var uninstall = registry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall"))
                        {
                            if (uninstall != null) foreach (var name in uninstall.GetSubKeyNames())
                                using (var entry = uninstall.OpenSubKey(name))
                                {
                                    if (entry == null || !Regex.IsMatch(Convert.ToString(entry.GetValue("DisplayName")), @"(?:TikTok.*Studio|LIVE\s*Studio)", RegexOptions.IgnoreCase)) continue;
                                    foreach (var property in new[] { "InstallLocation", "InstallPath", "InstallDir" }) { string location = Convert.ToString(entry.GetValue(property)); if (!String.IsNullOrWhiteSpace(location)) roots.Add(location); }
                                    foreach (string value in new[] { Convert.ToString(entry.GetValue("DisplayIcon")), Convert.ToString(entry.GetValue("UninstallString")) })
                                    {
                                        string exeRoot = ExeRoot(value); if (exeRoot != null) roots.Add(exeRoot);
                                    }
                                }
                        }
                        using (var appRegistry = RegistryKey.OpenBaseKey(hive, view))
                        using (var appPath = appRegistry.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\TikTok LIVE Studio.exe"))
                        { if (appPath != null) { string exeRoot = ExeRoot(Convert.ToString(appPath.GetValue(""))); if (exeRoot != null) roots.Add(exeRoot); } }
                    }
                    catch { }
                }
            foreach (string pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
                if (!String.IsNullOrEmpty(pf)) roots.Add(Path.Combine(pf, "TikTok LIVE Studio"));
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            roots.Add(Path.Combine(local, "TikTok LIVE Studio")); roots.Add(Path.Combine(local, "Programs", "TikTok LIVE Studio"));
            foreach (string directory in UserShortcutFolders().Concat(new[] { Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms) }))
                foreach (string shortcut in Shortcuts(directory))
                {
                    try { string target = ShellLinks.Target(shortcut); if (target.IndexOf("TikTok", StringComparison.OrdinalIgnoreCase) >= 0) roots.Add(Path.GetDirectoryName(target)); } catch { }
                }
            foreach (string drive in Environment.GetLogicalDrives())
                foreach (string tail in new[] { "TikTok LIVE Studio", @"Apps\TikTok LIVE Studio", @"Programs\TikTok LIVE Studio", @"Program Files\TikTok LIVE Studio" }) roots.Add(Path.Combine(drive, tail));
            foreach (string baseFolder in new[] { local, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Path.Combine(local, "Programs") })
                try { if (Directory.Exists(baseFolder)) roots.AddRange(Directory.EnumerateDirectories(baseFolder).Where(p => Regex.IsMatch(Path.GetFileName(p), @"TikTok|Live.?Studio", RegexOptions.IgnoreCase))); } catch { }
            foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
            { try { var found = Resolve(root); if (found != null) return found; } catch { } }
            return null;
        }
        public static string DeepFind(CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew();
            var queue = new Queue<Tuple<string, int>>();
            foreach (string folder in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") })
                if (!String.IsNullOrEmpty(folder) && Directory.Exists(folder)) queue.Enqueue(Tuple.Create(folder, 0));
            foreach (var drive in DriveInfo.GetDrives())
                try { if (drive.IsReady && (drive.DriveType == DriveType.Fixed || drive.DriveType == DriveType.Removable)) queue.Enqueue(Tuple.Create(drive.RootDirectory.FullName, 1)); } catch { }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); int visited = 0;
            while (queue.Count > 0 && visited < 8000 && clock.Elapsed < TimeSpan.FromSeconds(12))
            {
                cancellation.ThrowIfCancellationRequested(); var item = queue.Dequeue();
                if (!seen.Add(item.Item1)) continue; visited++;
                try
                {
                    if (IsVersion(item.Item1)) return Resolve(item.Item1);
                    if (File.Exists(Path.Combine(item.Item1, "launcher_config.json"))) { string active = Resolve(item.Item1); if (active != null) return active; }
                    if (item.Item2 >= 5) continue;
                    foreach (string child in Directory.EnumerateDirectories(item.Item1))
                    {
                        string name = Path.GetFileName(child);
                        if (Regex.IsMatch(name, @"^(Windows|WinSxS|node_modules|\.git|\.codex|System Volume Information|\$Recycle\.Bin)$", RegexOptions.IgnoreCase)) continue;
                        if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                        queue.Enqueue(Tuple.Create(child, item.Item2 + 1));
                    }
                }
                catch (UnauthorizedAccessException) { } catch (IOException) { }
            }
            return null;
        }
        public static string ExeRoot(string value)
        {
            if (String.IsNullOrWhiteSpace(value)) return null;
            var match = Regex.Match(value, "^\\s*\"([^\"]+\\.exe)\"", RegexOptions.IgnoreCase);
            if (!match.Success) match = Regex.Match(value, "^\\s*([^\"\\r\\n]+?\\.exe)(?:\\s|,|$)", RegexOptions.IgnoreCase);
            return match.Success ? Path.GetDirectoryName(match.Groups[1].Value) : null;
        }
        public static IEnumerable<string> UserShortcutFolders()
        { return new[] { Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Environment.GetFolderPath(Environment.SpecialFolder.Programs) }; }
        public static IEnumerable<string> Shortcuts(string directory)
        {
            if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return new string[0];
            try { return Directory.GetFiles(directory, "*TikTok*.lnk", SearchOption.AllDirectories); } catch { return new string[0]; }
        }
        public static Process Start(Plan plan, PatchEngine engine, bool allowUnsupported = false, Func<bool> isRunning = null)
        {
            if ((isRunning ?? IsRunning)()) throw new IOException("Studio уже запущена.");
            var actual = engine.Inspect(plan.Directory);
            if (!Data.Same(actual.CurrentHash, plan.CurrentHash)) throw new IOException("Версия Studio изменилась перед запуском. Повтори проверку.");
            if (!allowUnsupported && actual.State != BuildState.Patched) throw new InvalidDataException("Фикс ещё не применён.");
            string executable = Path.Combine(actual.Directory, Executable);
            if (!File.Exists(executable)) throw new FileNotFoundException("Исполняемый файл Studio не найден.");
            return Process.Start(new ProcessStartInfo { FileName = executable, WorkingDirectory = actual.Directory, UseShellExecute = true });
        }
    }
}
