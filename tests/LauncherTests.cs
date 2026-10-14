using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StudioFix.Tests
{
    public sealed class FakeGithub : HttpMessageHandler
    {
        readonly SignedRules bundle;
        readonly bool badRedirect;
        public FakeGithub(SignedRules value, bool bad = false) { bundle = value; badRedirect = bad; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            if (badRedirect)
            {
                var redirect = new HttpResponseMessage(HttpStatusCode.Redirect);
                redirect.Headers.Location = new Uri("https://example.com/not-github"); return Task.FromResult(redirect);
            }
            byte[] bytes;
            if (request.RequestUri.Host == "api.github.com")
            {
                bytes = Encoding.UTF8.GetBytes("{\"assets\":[{\"name\":\"studio-fix-rules.json\",\"browser_download_url\":\"https://github.com/example/studiofix/releases/download/v2/studio-fix-rules.json\"},{\"name\":\"studio-fix-rules.sig\",\"browser_download_url\":\"https://github.com/example/studiofix/releases/download/v2/studio-fix-rules.sig\"}]}");
            }
            else if (request.RequestUri.AbsolutePath.EndsWith(RuleUpdates.CatalogAsset)) bytes = bundle.Content;
            else bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(bundle.Signature));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    public static class TestProgram
    {
        static List<string> passed = new List<string>();
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        static void Refused(Action action, string message)
        { bool refused = false; try { action(); } catch { refused = true; } Check(refused, message); }
        static SignedRules Sign(RuleCatalog catalog, RSACryptoServiceProvider rsa)
        {
            byte[] data = Data.Utf8.GetBytes(Data.Json().Serialize(catalog));
            return new SignedRules { Content = data, Signature = rsa.SignData(data, CryptoConfig.MapNameToOID("SHA256")) };
        }
        static RuleCatalog Clone(RuleCatalog rules) { return Data.Json().Deserialize<RuleCatalog>(Data.Json().Serialize(rules)); }
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length == 2 && args[0] == "--app-updates-only")
            {
                try { AppUpdatesTests.Run(Path.GetFullPath(args[1])); Console.WriteLine("Signed application update tests passed."); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            }
            if (args.Length != 3) { Console.Error.WriteLine("Usage: StudioFix.Tests.exe <fixture-output> <original-js-paths.json> <FixtureStudio.exe>"); return 2; }
            string root = Path.GetFullPath(args[0]), sourcesFile = Path.GetFullPath(args[1]), stub = Path.GetFullPath(args[2]);
            Directory.CreateDirectory(root);
            try
            {
                var originals = Data.Read<Dictionary<string, string>>(sourcesFile);
                var store = new AppStore(Path.Combine(root, "state"));
                var rules = new RuleUpdates(store).Catalog;
                var engine = new PatchEngine(rules, () => false);
                foreach (var build in rules.Builds)
                {
                    string install = Path.Combine(root, "spaces & символы", "TikTok LIVE Studio", build.Version);
                    Directory.CreateDirectory(Path.Combine(install, "resources", "app", "electron"));
                    string main = Path.Combine(install, "resources", "app", "electron", "main.js");
                    File.Copy(originals[build.Version], main); File.Copy(stub, Path.Combine(install, Discovery.Executable));
                    Data.Save(Path.Combine(install, "ttnet.json"), new { ttnetParams = new { versionCode = build.Version } });
                    Check(Discovery.Resolve(install) == install, "Version directory not resolved.");
                    var before = engine.Inspect(install); Check(before.State == BuildState.Original && before.Verified, "Original not recognized.");
                    var fixedPlan = engine.Apply(install, before.CurrentHash);
                    Check(fixedPlan.State == BuildState.Patched && Data.Same(Data.HashFile(main), build.PatchedHash), "Patch hash differs from verified build.");
                    Check(Data.Same(Data.HashFile(main + ".network-fix.backup"), build.OriginalHash), "Backup differs.");
                    DateTime stamp = File.GetLastWriteTimeUtc(main);
                    engine.Apply(install, fixedPlan.CurrentHash); Check(File.GetLastWriteTimeUtc(main) == stamp, "Idempotent patch rewrote the file.");
                    using (var process = Discovery.Start(fixedPlan, engine, false, () => false)) process.WaitForExit();
                    Check(File.ReadAllText(Path.Combine(install, "fixture-started.txt")) == install, "Wrong Studio version or working directory launched.");
                    var restored = engine.Apply(install, fixedPlan.CurrentHash, true);
                    Check(restored.State == BuildState.Original && Data.Same(Data.HashFile(main), build.OriginalHash), "Exact rollback failed.");
                    Refused(() => engine.Apply(install, new string('0', 64)), "Stale inspection was applied.");
                    Refused(() => new PatchEngine(rules, () => true).Apply(install), "Running Studio was patched.");
                    Check(Data.Same(Data.HashFile(main), build.OriginalHash), "Rejected operation modified the file.");
                    // Recover a patch recorded by the previous CMD, without new metadata fields.
                    byte[] previousPatch = PatchEngine.Splice(File.ReadAllBytes(main), build); File.WriteAllBytes(main, previousPatch);
                    Data.Save(main + ".network-fix.json", new { Schema = 1, Version = build.Version, File = main, OriginalHash = build.OriginalHash, PatchedHash = build.PatchedHash, Created = "legacy-cmd" });
                    Check(engine.Inspect(install).State == BuildState.Patched, "Previous CMD fix not recognized.");
                    engine.Apply(install, null, true); Check(Data.Same(Data.HashFile(main), build.OriginalHash), "Legacy rollback failed.");
                    passed.Add("apply-repeat-launch-rollback-legacy-" + build.Version);
                }
                // An unknown implementation stays untouched and still offers ordinary launch.
                string unsupported = Path.Combine(root, "unsupported", "9.0.0"); Directory.CreateDirectory(Path.Combine(unsupported, "resources", "app", "electron"));
                string unknownMain = Path.Combine(unsupported, "resources", "app", "electron", "main.js"); File.WriteAllText(unknownMain, "console.log('unknown network implementation');");
                File.Copy(stub, Path.Combine(unsupported, Discovery.Executable)); Data.Save(Path.Combine(unsupported, "ttnet.json"), new { ttnetParams = new { versionCode = "9.0.0" } });
                var unknown = engine.Inspect(unsupported); string unknownHash = unknown.CurrentHash;
                Check(unknown.State == BuildState.Unsupported, "Unknown version incorrectly supported."); Refused(() => engine.Apply(unsupported), "Unknown code was patched.");
                using (var process = Discovery.Start(unknown, engine, true, () => false)) process.WaitForExit();
                Check(Data.Same(Data.HashFile(unknownMain), unknownHash) && File.Exists(Path.Combine(unsupported, "fixture-started.txt")), "Unsupported launch changed files or did not launch.");
                passed.Add("unsupported-launch-without-mutation");
                // Structural recognition is separate from curated/version-tested support.
                string renamed = File.ReadAllText(originals["1.37.4"]).Replace("LFi", "RenamedUrlPatterns");
                var auto = AutoRules.Analyze(Encoding.UTF8.GetBytes(renamed), "9.99.0"); Check(auto != null && auto.Mechanism == "WindowsTTNetBypass", "Renamed structure not recognized.");
                var oldAuto = AutoRules.Analyze(File.ReadAllBytes(originals["1.36.6"]), "9.99.1"); Check(oldAuto != null && oldAuto.Mechanism == "WindowsTNC", "TNC structure not recognized.");
                Check(AutoRules.Analyze(Encoding.UTF8.GetBytes(renamed.Replace("if(!Ln&&!At)return", "if(!Ln)return")), "9.99.2") == null, "Mac-only function was accepted.");
                passed.Add("structural-detection-and-windows-guard");
                // Installer changes must supersede a saved old version path.
                string activeRoot = Path.Combine(root, "spaces & символы", "TikTok LIVE Studio");
                Data.Save(Path.Combine(activeRoot, "launcher_config.json"), new { cur_path = "1.37.4" });
                Check(Discovery.Resolve(Path.Combine(activeRoot, "1.36.6")) == Path.Combine(activeRoot, "1.37.4"), "Saved old path did not follow installer update.");
                passed.Add("active-version-follows-installer");
                string knownDirectory = Path.Combine(activeRoot, "1.37.4");
                Check(Discovery.Resolve("\"" + Path.Combine(knownDirectory, Discovery.Executable) + "\" --argument") == knownDirectory, "Pasted quoted EXE was not resolved.");
                Check(Discovery.Resolve(Path.Combine(knownDirectory, "resources", "app", "electron", "main.js")) == knownDirectory, "Pasted internal file was not normalized.");
                string manualLink = Path.Combine(root, "manual-studio.lnk"); ShellLinks.Write(manualLink, Path.Combine(knownDirectory, Discovery.Executable));
                Check(Discovery.Resolve(manualLink) == knownDirectory, "Pasted ordinary shortcut was not resolved.");
                string packed = Path.Combine(root, "packed", "latest"); Directory.CreateDirectory(packed); File.Copy(stub, Path.Combine(packed, Discovery.Executable));
                Check(Discovery.Resolve(Path.GetDirectoryName(packed)) == packed, "Non-numeric runtime folder not found.");
                var packedPlan = engine.Inspect(packed); Check(packedPlan.State == BuildState.Unsupported && packedPlan.CurrentHash != null, "Different file layout was hidden as missing Studio.");
                using (var process = Discovery.Start(packedPlan, engine, true, () => false)) process.WaitForExit();
                passed.Add("manual-exe-shortcut-resource-path-and-packed-runtime");
                // Signed update download flow, with simulated GitHub HTTPS responses.
                using (var rsa = new RSACryptoServiceProvider(2048))
                {
                    rsa.PersistKeyInCsp = false;
                    var embedded = Sign(rules, rsa); var next = Clone(rules); next.CatalogVersion = 2; var bundle = Sign(next, rsa);
                    var updater = new RuleUpdates(new AppStore(Path.Combine(root, "updates")), rsa.ToXmlString(false), embedded, () => new FakeGithub(bundle));
                    Check(updater.UpdateAsync("https://github.com/example/studiofix", CancellationToken.None).GetAwaiter().GetResult(), "Signed GitHub update did not install.");
                    Check(updater.Catalog.CatalogVersion == 2, "Installed wrong rules version.");
                    string cache = Path.Combine(root, "updates", "rules-envelope.json"), cacheHash = Data.HashFile(cache);
                    var tampered = new SignedRules { Content = bundle.Content.ToArray(), Signature = bundle.Signature.ToArray() }; tampered.Content[10] ^= 1;
                    Refused(() => updater.Install(tampered), "Tampered update accepted."); Refused(() => updater.Install(embedded), "Rules downgrade accepted.");
                    Check(Data.Same(Data.HashFile(cache), cacheHash), "Rejected update overwrote last valid rules.");
                    var restarted = new RuleUpdates(new AppStore(Path.Combine(root, "updates")), rsa.ToXmlString(false), embedded);
                    Check(restarted.Catalog.CatalogVersion == 2, "Signed cached update was not loaded offline.");
                    File.WriteAllText(cache, "broken cache"); var fallback = new RuleUpdates(new AppStore(Path.Combine(root, "updates")), rsa.ToXmlString(false), embedded);
                    Check(fallback.Catalog.CatalogVersion == 1 && fallback.Warning != null, "Invalid cache did not fall back to embedded rules.");
                    var redirect = new RuleUpdates(new AppStore(Path.Combine(root, "redirect")), rsa.ToXmlString(false), embedded, () => new FakeGithub(bundle, true));
                    Refused(() => redirect.UpdateAsync("example/studiofix", CancellationToken.None).GetAwaiter().GetResult(), "Non-GitHub redirect accepted.");
                    passed.Add("signed-github-flow-tamper-downgrade-offline-redirect");
                }
                Refused(() => RuleUpdates.ParseRepository("http://github.com/example/studiofix"), "HTTP repository accepted.");
                Refused(() => RuleUpdates.ParseRepository("https://example.com/example/studiofix"), "Other host accepted.");
                passed.Add("repository-validation");
                AppUpdatesTests.Run(root);
                passed.Add("signed-app-update-current-downgrade-tamper-hash-tag-size-default-repository");
                // Migration is exercised only in a temporary desktop.
                string desktop = Path.Combine(root, "desktop"); Directory.CreateDirectory(desktop);
                string link = Path.Combine(desktop, "TikTok LIVE Studio.lnk"); ShellLinks.Write(link, stub, "--original-argument"); string linkHash = Data.HashFile(link);
                // The target basename is the vendor name so integration recognizes it.
                string studioStub = Path.Combine(root, "shortcut-target", Discovery.Executable); Directory.CreateDirectory(Path.GetDirectoryName(studioStub)); File.Copy(stub, studioStub);
                ShellLinks.Write(link, studioStub, "--original-argument"); linkHash = Data.HashFile(link);
                var shortcutStore = new AppStore(Path.Combine(root, "shortcut-state"));
                string backup = Path.Combine(shortcutStore.Root, "shortcut-backups", "original.lnk"); Directory.CreateDirectory(Path.GetDirectoryName(backup)); File.Copy(link, backup);
                string target = typeof(TestProgram).Assembly.Location; ShellLinks.Write(link, target);
                var record = new ShortcutRecord { Path = link, Backup = backup, BackupHash = linkHash, InstalledHash = Data.HashFile(link) };
                Data.Save(Path.Combine(shortcutStore.Root, "shortcuts.json"), new IntegrationState { Target = target, Shortcuts = new List<ShortcutRecord> { record } });
                var integration = new Integration(shortcutStore, target, new[] { desktop });
                Check(integration.Connected, "Legacy shortcut not detected.");
                Check(integration.Restore() == 1 && Data.Same(Data.HashFile(link), linkHash), "Shortcut not restored byte-for-byte.");
                Check(integration.Restore() == 0, "Repeated legacy cleanup changed a shortcut.");
                passed.Add("legacy-shortcut-exact-restore-idempotence");
                string common = Path.Combine(root, "public-desktop"); Directory.CreateDirectory(common);
                string commonLink = Path.Combine(common, "TikTok LIVE Studio.lnk"); ShellLinks.Write(commonLink, studioStub); string originalLinkHash = Data.HashFile(commonLink);
                var commonStore = new AppStore(Path.Combine(root, "common-shortcut-state"));
                var commonIntegration = new Integration(commonStore, target, new[] { common });
                Check(commonIntegration.Restore() == 0 && Data.Same(Data.HashFile(commonLink), originalLinkHash), "Original Studio shortcut was modified.");
                string created = Path.Combine(common, "old-studio.lnk"); ShellLinks.Write(created, target);
                Data.Save(Path.Combine(commonStore.Root, "shortcuts.json"), new IntegrationState { Target = target, Shortcuts = new List<ShortcutRecord> { new ShortcutRecord { Path = created, Created = true, InstalledHash = Data.HashFile(created) } } });
                Check(commonIntegration.Restore() == 1 && !File.Exists(created), "Obsolete created shortcut was not removed.");
                Data.Save(Path.Combine(shortcutStore.Root, "shortcuts.json"), new IntegrationState { Target = target, Shortcuts = new List<ShortcutRecord> { record } });
                ShellLinks.Write(link, target, "--manually-edited");
                string editedHash = Data.HashFile(link);
                Check(integration.Restore() == 0 && Data.Same(Data.HashFile(link), editedHash), "Manually changed shortcut was overwritten.");
                passed.Add("studio-shortcut-unchanged-legacy-created-cleanup-edited-link-preserved");
                Data.Save(Path.Combine(root, "test-results.json"), new { Success = true, Tests = passed, Runtime = Environment.Version.ToString(), FixtureDirectory = root });
                Console.WriteLine(Data.Json().Serialize(new { Success = true, Tests = passed })); return 0;
            }
            catch (Exception error)
            {
                Data.Save(Path.Combine(root, "test-results.json"), new { Success = false, Passed = passed, Error = error.ToString() }); Console.Error.WriteLine(error); return 1;
            }
        }
    }
}
