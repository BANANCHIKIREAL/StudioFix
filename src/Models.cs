using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace StudioFix
{
    public static class BuildInfo
    {
        public const string Repository = "BANANCHIKIREAL/StudioFix";
        public const string AppVersion = "1.1.0";
        public const string InstallerAsset = "StudioFix-Setup.exe";
    }
    public sealed class BuildSpec
    {
        public string Version { get; set; }
        public string OriginalHash { get; set; }
        public string PatchedHash { get; set; }
        public string Needle { get; set; }
        public string Replacement { get; set; }
        public string Mechanism { get; set; }
    }
    public sealed class RuleCatalog
    {
        public int Schema { get; set; }
        public int CatalogVersion { get; set; }
        public string Published { get; set; }
        public List<BuildSpec> Builds { get; set; }
    }
    public sealed class PatchRecord
    {
        public int Schema { get; set; }
        public string Version { get; set; }
        public string File { get; set; }
        public string OriginalHash { get; set; }
        public string PatchedHash { get; set; }
        public string Created { get; set; }
        public string Needle { get; set; }
        public string Replacement { get; set; }
        public string Mechanism { get; set; }
    }
    public enum BuildState { Missing, Original, Patched, Unsupported, Conflict }
    public sealed class Plan
    {
        public string Directory { get; set; }
        public string Version { get; set; }
        public string CurrentHash { get; set; }
        public BuildState State { get; set; }
        public BuildSpec Spec { get; set; }
        public bool Verified { get; set; }
        public bool HasBackup { get; set; }
        public string Detail { get; set; }
        public string MainFile { get { return Path.Combine(Directory, "resources", "app", "electron", "main.js"); } }
        public bool CanFix { get { return State == BuildState.Original && Spec != null; } }
    }
    public sealed class Settings
    {
        public string StudioRoot { get; set; }
        public string Repository { get; set; } = BuildInfo.Repository;
        public bool AutoCheckUpdates { get; set; } = true;
    }
    public static class Data
    {
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false);
        public static JavaScriptSerializer Json()
        {
            return new JavaScriptSerializer { MaxJsonLength = 2 * 1024 * 1024, RecursionLimit = 32 };
        }
        public static T Read<T>(string path) { return Json().Deserialize<T>(File.ReadAllText(path, Encoding.UTF8)); }
        public static string Hash(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "");
        }
        public static string HashFile(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path)) return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
        }
        public static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        public static void Save(string path, object value) { SaveBytes(path, Utf8.GetBytes(Json().Serialize(value))); }
        public static void SaveBytes(string path, byte[] bytes)
        {
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(bytes, 0, bytes.Length); file.Flush(true); }
                if (File.Exists(path)) File.Replace(tmp, path, null); else File.Move(tmp, path);
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
        }
        public static string Quote(string value)
        {
            if (value == null) return "\"\"";
            var output = new StringBuilder("\""); int slashes = 0;
            foreach (char c in value)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { output.Append('\\', slashes * 2 + 1); output.Append(c); }
                else { output.Append('\\', slashes); output.Append(c); }
                slashes = 0;
            }
            output.Append('\\', slashes * 2); return output.Append('"').ToString();
        }
        public static byte[] Resource(string name)
        {
            using (var stream = typeof(Data).Assembly.GetManifestResourceStream(name))
            {
                if (stream == null) throw new IOException("Не найден встроенный ресурс: " + name);
                using (var memory = new MemoryStream()) { stream.CopyTo(memory); return memory.ToArray(); }
            }
        }
    }
    public sealed class AppStore
    {
        public string Root { get; private set; }
        public string SettingsFile { get { return Path.Combine(Root, "settings.json"); } }
        public AppStore(string root = null)
        {
            Root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StudioFixLauncher"));
        }
        public Settings LoadSettings()
        {
            try
            {
                var settings = File.Exists(SettingsFile) ? Data.Read<Settings>(SettingsFile) ?? new Settings() : new Settings();
                if (String.IsNullOrWhiteSpace(settings.Repository)) settings.Repository = BuildInfo.Repository;
                return settings;
            }
            catch { return new Settings(); }
        }
        public void SaveSettings(Settings settings) { Data.Save(SettingsFile, settings); }
        public void Log(string stage, Exception error)
        {
            try
            {
                System.IO.Directory.CreateDirectory(Root);
                var path = Path.Combine(Root, "launcher.log");
                if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Delete(path);
                File.AppendAllText(path, DateTimeOffset.Now.ToString("o") + " " + stage + " " + error.GetType().Name + ": " + error.Message + Environment.NewLine, Data.Utf8);
            }
            catch { }
        }
    }
}
