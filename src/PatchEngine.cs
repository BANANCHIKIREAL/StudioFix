using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace StudioFix
{
    public static class AutoRules
    {
        const string Id = "[A-Za-z_$][A-Za-z0-9_$]{0,80}";
        const string Patterns = "\"*://*.tiktokv.com/*\",\"*://tiktokv.com/*\",\"*://*.tiktok.com/*\",\"*://tiktok.com/*\",\"*://*.tiktokv.us/*\",\"*://tiktokv.us/*\",\"*://*.tiktokv.eu/*\",\"*://tiktokv.eu/*\"";
        static MatchCollection Matches(string text, string pattern)
        { return Regex.Matches(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(500)); }
        static IEnumerable<int> Indices(string text, string token)
        {
            int start = 0;
            while (start < text.Length)
            { int i = text.IndexOf(token, start, StringComparison.Ordinal); if (i < 0) yield break; yield return i; start = i + token.Length; }
        }
        static string Chunk(string text, int start, int length)
        { start = Math.Max(0, start); return text.Substring(start, Math.Min(length, text.Length - start)); }
        public static BuildSpec Analyze(byte[] bytes, string version)
        {
            string text = Encoding.ASCII.GetString(bytes), needle = null, replacement = null, mechanism = null;
            var candidates = new List<Match>();
            var tnc = "(?<config>" + Id + ")=\\{enable:!0,appId:\"8311\",(?<fields>[^{}]{0,1400})\\};try\\{[^{}]{0,400}?await " + Id + "\\.TTChromiumNetwork\\.startTNC\\(\\k<config>\\)";
            foreach (int i in Indices(text, ".TTChromiumNetwork.startTNC("))
                candidates.AddRange(Matches(Chunk(text, i - 2400, 2800), tnc).Cast<Match>());
            if (candidates.Count == 1)
            {
                var fields = candidates[0].Groups["fields"].Value;
                if (fields.Contains("platform:\"win\"") && fields.Contains("tncServerHost1:") && fields.Contains(".tiktokv.com\"") && fields.Contains("deviceId:") && fields.Contains("appVersion:"))
                { needle = candidates[0].Value; replacement = needle.Replace("enable:!0", "enable:!1"); mechanism = "WindowsTNC"; }
            }
            if (needle == null)
            {
                var winNames = new HashSet<string>();
                foreach (int i in Indices(text, "=process.platform===\"win32\""))
                    foreach (Match m in Matches(Chunk(text, i - 82, 120), "(?<win>" + Id + ")=process\\.platform===\"win32\"")) winNames.Add(m.Groups["win"].Value);
                var options = new List<BuildSpec>();
                foreach (int i in Indices(text, ".ttnet.setBypassTTNetURLPatterns;"))
                {
                    int start = Math.Max(0, i - 200);
                    foreach (Match m in Matches(Chunk(text, start, 260), "let (?<method>" + Id + ")=(?<owner>" + Id + ")\\.ttnet\\.setBypassTTNetURLPatterns;"))
                    {
                        int absolute = start + m.Index;
                        string prefix = text.Substring(Math.Max(0, absolute - 800), Math.Min(800, absolute));
                        if (!winNames.Any(w => Matches(prefix, "=\\(\\)=>\\{if\\(!" + Regex.Escape(w) + "&&!" + Id + "\\)return [^;]{0,500};$").Count == 1)) continue;
                        string body = Chunk(text, absolute + m.Length, 1800), method = Regex.Escape(m.Groups["method"].Value), owner = Regex.Escape(m.Groups["owner"].Value);
                        if (Matches(body, "^if\\(typeof " + method + "!==?\"function\"\\)").Count != 1) continue;
                        var calls = Matches(body, "try\\{" + method + "\\.call\\(" + owner + "\\.ttnet,(?<list>" + Id + ")\\);");
                        if (calls.Count != 1) continue;
                        string list = calls[0].Groups["list"].Value;
                        var locations = Indices(text, list + "=[").ToArray();
                        if (locations.Length != 1) continue;
                        var arrays = Matches(Chunk(text, locations[0], 8192), "^" + Regex.Escape(list) + "=\\[(?<items>\"[^\"\\\\\\r\\n]+\"(?:,\"[^\"\\\\\\r\\n]+\")*)\\]");
                        if (arrays.Count != 1) continue;
                        string items = arrays[0].Groups["items"].Value;
                        var urls = Data.Json().Deserialize<string[]>("[" + items + "]");
                        if (urls.Length < 2 || urls.Any(u => Matches(u, "^\\*://[*.A-Za-z0-9_-]+/\\*$").Count != 1) || urls.Any(u => u.Contains("tiktok"))) continue;
                        options.Add(new BuildSpec { Needle = arrays[0].Value, Replacement = list + "=[" + Patterns + "," + items + "]", Mechanism = "WindowsTTNetBypass" });
                    }
                }
                if (options.Count == 1) { needle = options[0].Needle; replacement = options[0].Replacement; mechanism = options[0].Mechanism; }
            }
            if (needle == null) return null;
            var spec = new BuildSpec { Version = version, Needle = needle, Replacement = replacement, Mechanism = mechanism, OriginalHash = Data.Hash(bytes) };
            spec.PatchedHash = Data.Hash(PatchEngine.Splice(bytes, spec)); return spec;
        }
    }

    public sealed class PatchEngine
    {
        readonly RuleCatalog catalog;
        readonly Func<bool> studioRunning;
        public PatchEngine(RuleCatalog rules, Func<bool> isRunning = null)
        { catalog = rules; studioRunning = isRunning ?? Discovery.IsRunning; }
        public static byte[] Splice(byte[] source, BuildSpec spec)
        {
            string text = Encoding.ASCII.GetString(source);
            int index = text.IndexOf(spec.Needle, StringComparison.Ordinal);
            if (index < 0 || text.IndexOf(spec.Needle, index + 1, StringComparison.Ordinal) >= 0)
                throw new InvalidDataException("Нужный участок кода не найден или встречается несколько раз. Файл не изменён.");
            byte[] replacement = Encoding.ASCII.GetBytes(spec.Replacement);
            int suffix = index + spec.Needle.Length;
            byte[] result = new byte[index + replacement.Length + source.Length - suffix];
            Buffer.BlockCopy(source, 0, result, 0, index);
            Buffer.BlockCopy(replacement, 0, result, index, replacement.Length);
            Buffer.BlockCopy(source, suffix, result, index + replacement.Length, source.Length - suffix);
            return result;
        }
        static byte[] ReadMain(string path)
        {
            if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Размер сетевого файла отличается от ожидаемого.");
            return File.ReadAllBytes(path);
        }
        public Plan Inspect(string directory)
        {
            var plan = new Plan { Directory = Path.GetFullPath(directory), State = BuildState.Unsupported };
            string configFile = Path.Combine(plan.Directory, "ttnet.json");
            var config = File.Exists(configFile) ? Data.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(configFile, Encoding.UTF8)) : new Dictionary<string, object>();
            var parameters = config.ContainsKey("ttnetParams") ? config["ttnetParams"] as Dictionary<string, object> : null;
            plan.Version = parameters != null && parameters.ContainsKey("versionCode") ? Convert.ToString(parameters["versionCode"]) : "неизвестна";
            if (plan.Version == "неизвестна")
            {
                string package = Path.Combine(plan.Directory, "resources", "app", "package.json");
                try { var info = Data.Read<Dictionary<string, object>>(package); if (info.ContainsKey("version")) plan.Version = Convert.ToString(info["version"]); } catch { }
                if (plan.Version == "неизвестна") { try { plan.Version = System.Diagnostics.FileVersionInfo.GetVersionInfo(Path.Combine(plan.Directory, Discovery.Executable)).ProductVersion ?? "неизвестна"; } catch { } }
            }
            if (!File.Exists(plan.MainFile))
            {
                plan.CurrentHash = Data.HashFile(Path.Combine(plan.Directory, Discovery.Executable));
                plan.Detail = "Структура сетевых файлов отличается. Можно запустить Studio без правки."; return plan;
            }
            byte[] main = ReadMain(plan.MainFile); plan.CurrentHash = Data.Hash(main);
            string backup = plan.MainFile + ".network-fix.backup", recordPath = plan.MainFile + ".network-fix.json";
            plan.HasBackup = File.Exists(backup);
            var known = catalog.Builds.Where(b => b.Version == plan.Version).ToList();
            BuildSpec spec = known.FirstOrDefault(b => Data.Same(b.OriginalHash, plan.CurrentHash) || Data.Same(b.PatchedHash, plan.CurrentHash));
            if (File.Exists(recordPath))
            {
                try
                {
                    var record = Data.Read<PatchRecord>(recordPath);
                    if (record.Schema != 1 || record.Version != plan.Version || !Data.Same(Path.GetFullPath(record.File), plan.MainFile) || !plan.HasBackup || !Data.Same(Data.HashFile(backup), record.OriginalHash) || (!Data.Same(plan.CurrentHash, record.OriginalHash) && !Data.Same(plan.CurrentHash, record.PatchedHash))) throw new InvalidDataException();
                    if (spec == null)
                    {
                        byte[] original = ReadMain(backup);
                        // The old CMD records can be reconstructed from the original file.
                        if (!String.IsNullOrEmpty(record.Needle) && !String.IsNullOrEmpty(record.Replacement))
                            spec = new BuildSpec { Version = record.Version, OriginalHash = record.OriginalHash, PatchedHash = record.PatchedHash, Needle = record.Needle, Replacement = record.Replacement, Mechanism = record.Mechanism };
                        else spec = AutoRules.Analyze(original, plan.Version);
                        if (spec == null || !Data.Same(Data.Hash(Splice(original, spec)), record.PatchedHash)) throw new InvalidDataException();
                    }
                    if (!Data.Same(spec.OriginalHash, record.OriginalHash) || !Data.Same(spec.PatchedHash, record.PatchedHash)) throw new InvalidDataException();
                }
                catch { plan.State = BuildState.Conflict; plan.Detail = "Файл или резервная копия изменились. Автоматическая замена остановлена."; return plan; }
            }
            else if (spec == null)
            {
                if (known.Count > 0) { plan.State = BuildState.Conflict; plan.Detail = "Эта сборка отличается от проверенной. Можно открыть Studio без новой правки."; return plan; }
                try { spec = AutoRules.Analyze(main, plan.Version); }
                catch (RegexMatchTimeoutException) { spec = null; }
            }
            if (spec == null) { plan.Detail = "Для этой версии подходящая правка пока не найдена."; return plan; }
            plan.Spec = spec;
            plan.Verified = known.Any(b => Data.Same(b.OriginalHash, spec.OriginalHash) && Data.Same(b.PatchedHash, spec.PatchedHash));
            plan.State = Data.Same(plan.CurrentHash, spec.PatchedHash) ? BuildState.Patched : BuildState.Original;
            if (plan.HasBackup && !Data.Same(Data.HashFile(backup), spec.OriginalHash))
            { plan.State = BuildState.Conflict; plan.Detail = "Существующая резервная копия не совпадает с исходным файлом."; }
            return plan;
        }
        public Plan Apply(string directory, string expectedHash = null, bool restore = false)
        {
            string key = Data.Hash(Encoding.UTF8.GetBytes(Path.GetFullPath(directory).ToUpperInvariant())).Substring(0, 24);
            using (var mutex = new Mutex(false, "Local\\StudioFixPatch-" + key))
            {
                bool locked; try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) throw new IOException("Правка уже выполняется другим процессом. Подожди и повтори проверку.");
                try { return ApplyLocked(directory, expectedHash, restore); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        Plan ApplyLocked(string directory, string expectedHash, bool restore)
        {
            if (studioRunning()) throw new IOException("Сначала полностью закрой TikTok LIVE Studio, затем повтори запуск.");
            var plan = Inspect(directory);
            if (expectedHash != null && !Data.Same(plan.CurrentHash, expectedHash)) throw new IOException("Studio обновилась во время проверки. Повтори поиск.");
            if (restore && plan.State == BuildState.Original) return plan;
            if (!restore && plan.State == BuildState.Patched) return plan;
            if (plan.Spec == null || plan.State == BuildState.Unsupported || plan.State == BuildState.Conflict) throw new InvalidDataException(plan.Detail ?? "Подходящая правка не найдена.");
            string main = plan.MainFile, backup = main + ".network-fix.backup", record = main + ".network-fix.json";
            byte[] original, output;
            string expected;
            if (restore)
            {
                if (!File.Exists(backup) || !Data.Same(Data.HashFile(backup), plan.Spec.OriginalHash)) throw new InvalidDataException("Исходная резервная копия не найдена или изменена. Откат остановлен.");
                output = ReadMain(backup); original = output; expected = plan.Spec.OriginalHash;
            }
            else
            {
                original = ReadMain(main); output = Splice(original, plan.Spec); expected = plan.Spec.PatchedHash;
                if (!Data.Same(Data.Hash(output), expected)) throw new InvalidDataException("Контрольная сумма новой правки не совпала. Файл не изменён.");
            }
            string tmp = main + ".studiofix-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                // Verify directory access before creating backup or metadata.
                using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(output, 0, output.Length); file.Flush(true); }
                if (!Data.Same(Data.HashFile(tmp), expected)) throw new IOException("Проверка записанного файла не прошла.");
                if (File.Exists(backup))
                { if (!Data.Same(Data.HashFile(backup), plan.Spec.OriginalHash)) throw new InvalidDataException("Резервная копия не совпала. Замена остановлена."); }
                else
                {
                    if (restore) throw new IOException("Резервная копия отсутствует.");
                    using (var file = new FileStream(backup, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(original, 0, original.Length); file.Flush(true); }
                    if (!Data.Same(Data.HashFile(backup), plan.Spec.OriginalHash)) throw new IOException("Не удалось проверить резервную копию.");
                }
                if (studioRunning() || !Data.Same(Data.HashFile(main), plan.CurrentHash)) throw new IOException("Studio запустилась или обновилась во время замены. Повтори проверку.");
                if (!restore) Data.Save(record, new PatchRecord { Schema = 1, Version = plan.Version, File = main, OriginalHash = plan.Spec.OriginalHash, PatchedHash = plan.Spec.PatchedHash, Created = DateTimeOffset.Now.ToString("o"), Needle = plan.Spec.Needle, Replacement = plan.Spec.Replacement, Mechanism = plan.Spec.Mechanism });
                File.Replace(tmp, main, null);
                if (!Data.Same(Data.HashFile(main), expected)) throw new IOException("Финальная проверка не прошла. Исходный файл сохранён в резервной копии.");
            }
            finally { if (File.Exists(tmp)) File.Delete(tmp); }
            return Inspect(directory);
        }
    }
}
