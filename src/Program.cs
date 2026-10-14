using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;

namespace StudioFix
{
    public static class Program
    {
        static string Value(string[] args, string key)
        { int index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null; }
        [STAThread]
        public static int Main(string[] args)
        {
            var store = new AppStore(Value(args, "--data-root"));
            try
            {
                var updates = new RuleUpdates(store);
                if (args.Contains("--patch-helper")) return PatchHelper(args, store, updates);
                if (args.Contains("--shortcut-helper"))
                {
                    string output = Path.GetFullPath(Value(args, "--result") ?? "");
                    if (!output.StartsWith(Path.Combine(store.Root, "sessions") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(output), "^[a-fA-F0-9]{32}\\.json$")) throw new InvalidDataException("Некорректный путь результата помощника ярлыка.");
                    try
                    {
                        if (!args.Contains("--restore")) throw new InvalidDataException("Подключение ярлыка Studio отключено. Используй собственный ярлык StudioFix.");
                        new Integration(store, typeof(Program).Assembly.Location).Restore();
                        Data.Save(output, new HelperResult { Success = true }); return 0;
                    }
                    catch (Exception error) { store.Log("shortcut-helper", error); Data.Save(output, new HelperResult { Success = false, Message = error.Message }); return 1; }
                }
                if (args.Contains("--export-rule"))
                {
                    string directory = Discovery.Resolve(Value(args, "--studio"));
                    if (directory == null) throw new InvalidDataException("Studio не найдена.");
                    var plan = new PatchEngine(updates.Catalog).Inspect(directory);
                    if (!plan.CanFix) throw new InvalidDataException("Для экспорта нужен исходный файл и распознанная структура сетевого кода.");
                    Data.Save(Value(args, "--output") ?? Path.Combine(store.Root, "build-rule.json"), plan.Spec); return 0;
                }
                if (args.Contains("--inspect"))
                {
                    string directory = args.Contains("--deep") ? Discovery.DeepFind(CancellationToken.None) : Discovery.Resolve(Value(args, "--studio")) ?? Discovery.Find(store.LoadSettings());
                    var plan = directory == null ? new Plan { State = BuildState.Missing } : new PatchEngine(updates.Catalog).Inspect(directory);
                    Data.Save(Value(args, "--output") ?? Path.Combine(store.Root, "inspection.json"), new { plan.Version, State = plan.State.ToString(), plan.Verified, plan.HasBackup, plan.Detail, Directory = plan.Directory, CatalogVersion = updates.Catalog.CatalogVersion });
                    return 0;
                }
                if (args.Contains("--integrate")) throw new InvalidDataException("Подключение ярлыка Studio отключено. Открой StudioFix через его собственный ярлык.");
                if (args.Contains("--restore-shortcuts")) { IntegrationService.Restore(store, typeof(Program).Assembly.Location); return 0; }
                if (args.Contains("--cleanup-legacy")) { new Integration(store, typeof(Program).Assembly.Location).Restore(); return 0; }
                if (args.Contains("--check-app-update"))
                {
                    var available = new AppUpdates(store).CheckAsync(store.LoadSettings().Repository, CancellationToken.None).GetAwaiter().GetResult();
                    Data.Save(Value(args, "--output") ?? Path.Combine(store.Root, "app-update-check.json"), new { Success = true, Current = BuildInfo.AppVersion, Available = available == null ? null : available.Release.Version }); return 0;
                }
                if (args.Contains("--preview"))
                {
                    var ui = new LauncherUi(store, updates, true);
                    string kind = Value(args, "--preview");
                    var known = updates.Catalog.Builds.Last();
                    ui.Present(new Plan { Version = kind == "unsupported" ? "9.0.0" : known.Version, State = kind == "patched" ? BuildState.Patched : kind == "unsupported" ? BuildState.Unsupported : kind == "missing" ? BuildState.Missing : BuildState.Original, Verified = kind != "unsupported", HasBackup = kind == "patched", Spec = known });
                    if (kind == "loading") ui.PreviewLoading();
                    if (kind == "update") { ui.PreviewAppUpdate(Value(args, "--output") ?? Path.Combine(store.Root, "update-preview.png")); return 0; }
                    ui.Render(Value(args, "--output") ?? Path.Combine(store.Root, "preview.png")); return 0;
                }
                string sid = WindowsIdentity.GetCurrent().User.Value;
                using (var mutex = new Mutex(false, "Local\\StudioFixUi-" + sid))
                {
                    bool locked; try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                    if (!locked) { MessageBox.Show("Лаунчер StudioFix уже открыт.", "StudioFix", MessageBoxButton.OK, MessageBoxImage.Information); return 0; }
                    try
                    {
                        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                        app.DispatcherUnhandledException += (s, e) => { store.Log("ui", e.Exception); MessageBox.Show(e.Exception.Message, "StudioFix", MessageBoxButton.OK, MessageBoxImage.Information); e.Handled = true; };
                        if (Value(args, "--studio") != null) { var settings = store.LoadSettings(); settings.StudioRoot = Value(args, "--studio"); store.SaveSettings(settings); }
                        var ui = new LauncherUi(store, updates, args.Contains("--hold")); return app.Run(ui.Window);
                    }
                    finally { mutex.ReleaseMutex(); }
                }
            }
            catch (Exception error)
            {
                store.Log("startup", error);
                if (args.Contains("--patch-helper") || args.Contains("--shortcut-helper") || args.Contains("--cleanup-legacy")) { }
                else if (args.Contains("--inspect") || args.Contains("--export-rule") || args.Contains("--preview") || args.Contains("--integrate") || args.Contains("--check-app-update"))
                { string output = Value(args, "--output"); if (output != null) { try { Data.Save(output, new HelperResult { Success = false, Message = error.Message }); } catch { } } }
                else MessageBox.Show(error.Message, "Не удалось открыть StudioFix", MessageBoxButton.OK, MessageBoxImage.Information);
                return 1;
            }
        }
        static int PatchHelper(string[] args, AppStore store, RuleUpdates updates)
        {
            string directory = Discovery.Resolve(Value(args, "--studio"));
            if (directory == null) throw new InvalidDataException("Выбранная папка Studio не найдена.");
            string output = Path.GetFullPath(Value(args, "--result") ?? "");
            string allowed = Path.Combine(store.Root, "sessions") + Path.DirectorySeparatorChar;
            if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase) || !Regex.IsMatch(Path.GetFileName(output), "^[a-fA-F0-9]{32}\\.json$")) throw new InvalidDataException("Некорректный путь результата помощника.");
            var expected = Value(args, "--expected");
            if (expected == null || !Regex.IsMatch(expected, "^[a-fA-F0-9]{64}$")) throw new InvalidDataException("Не указана контрольная сумма проверенной сборки.");
            try
            {
                new PatchEngine(updates.Catalog).Apply(directory, expected, args.Contains("--restore"));
                Data.Save(output, new HelperResult { Success = true }); return 0;
            }
            catch (Exception error) { store.Log("helper", error); Data.Save(output, new HelperResult { Success = false, Message = error.Message }); return 1; }
        }
    }
}
