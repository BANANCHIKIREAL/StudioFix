using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace StudioFix
{
    public sealed class LauncherUi
    {
        public Window Window { get; private set; }
        readonly AppStore store;
        readonly Settings settings;
        readonly RuleUpdates updates;
        readonly AppUpdates appUpdates;
        readonly Integration integration;
        AvailableUpdate availableUpdate;
        readonly CancellationTokenSource cancel = new CancellationTokenSource();
        Plan plan;
        bool busy, writing, checkingUpdates;
        public LauncherUi(AppStore storage, RuleUpdates rules, bool hold = false)
        {
            store = storage; updates = rules; settings = store.LoadSettings();
            appUpdates = new AppUpdates(store);
            integration = new Integration(store, typeof(LauncherUi).Assembly.Location);
            Window = (Window)XamlReader.Parse(Encoding.UTF8.GetString(Data.Resource("StudioFix.MainWindow.xaml")));
            Window.Resources.MergedDictionaries.Add((ResourceDictionary)XamlReader.Parse(Encoding.UTF8.GetString(Data.Resource("StudioFix.Icons.xaml"))));
            Icon("BrandIcon", "ScanIcon"); Icon("CloseIcon", "CloseIcon"); Icon("UpdateIcon", "UpdateIcon"); Icon("SettingsIcon", "SettingsIcon"); Icon("HeroIcon", "SearchIcon");
            Window.Icon = BitmapFrame.Create(new MemoryStream(Data.Resource("StudioFix.icon.ico")));
            Get<FrameworkElement>("TitleBar").MouseLeftButtonDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) Window.DragMove(); };
            Get<Button>("CloseButton").Click += (s, e) => Window.Close();
            Get<Button>("PrimaryButton").Click += async (s, e) => { await Primary(); };
            Get<Button>("SecondaryButton").Click += async (s, e) => { if (plan != null && plan.State == BuildState.Patched) await Fix(true, true); else await Launch(true); };
            Get<Button>("UpdateButton").Click += async (s, e) => { if (availableUpdate != null) ShowAppUpdate(); else await CheckUpdates(false); };
            Get<Button>("SettingsButton").Click += (s, e) => { ShowSettings(); };
            Window.Loaded += async (s, e) => await Initialize();
            Window.Closing += (s, e) =>
            {
                if (writing) { e.Cancel = true; Notice("Подожди завершения проверки файлов."); return; }
                cancel.Cancel();
            };
        }
        T Get<T>(string name) where T : FrameworkElement { return (T)Window.FindName(name); }
        static Brush Brush(string hex) { return (Brush)new BrushConverter().ConvertFromString(hex); }
        void Text(string name, string value) { Get<TextBlock>(name).Text = value ?? ""; }
        void Icon(string name, string key) { Get<Image>(name).Source = (ImageSource)Window.Resources[key]; }
        void Loading(bool on)
        {
            Get<System.Windows.Shapes.Ellipse>("LoadingRing").Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            var transform = (RotateTransform)Get<System.Windows.Shapes.Ellipse>("LoadingRing").RenderTransform;
            transform.BeginAnimation(RotateTransform.AngleProperty, on ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(2)) { RepeatBehavior = RepeatBehavior.Forever } : null);
        }
        void Notice(string value) { Text("NoticeText", value); Get<TextBlock>("NoticeText").ToolTip = value; }
        void SetBusy(string heading, string description)
        {
            busy = true;
            Loading(true); Icon("HeroIcon", "SearchIcon");
            Text("StatusText", "ПРОВЕРКА"); Text("HeadingText", heading); Text("DescriptionText", description);
            Get<Button>("PrimaryButton").IsEnabled = false; Get<Button>("SecondaryButton").IsEnabled = false;
            Get<Button>("PrimaryButton").Content = "Подождите…";
            Get<Button>("UpdateButton").IsEnabled = false; Get<Button>("SettingsButton").IsEnabled = false;
        }
        public void PreviewLoading() { SetBusy("Ищем версию и фикс", "Проверяем Studio и возможные правки."); Text("VersionText", "…"); Text("FixText", "Анализируем сетевой код"); }
        async Task Initialize()
        {
            try { integration.Restore(); }
            catch (Exception error) { store.Log("legacy-shortcut", error); Notice("Старую связь с ярлыком Studio можно отключить в настройках."); }
            if (!String.IsNullOrEmpty(updates.Warning)) Notice(updates.Warning);
            await Scan(true);
            if (cancel.IsCancellationRequested) return;
            if (settings.AutoCheckUpdates && !String.IsNullOrEmpty(settings.Repository))
            {
                await CheckUpdates(true);
            }
        }
        async Task Scan(bool auto = false)
        {
            SetBusy("Готовим Studio", "Найдём установленную версию и проверим фикс перед запуском.");
            try
            {
                var scanTask = Task.Run(() =>
                {
                    string root = Discovery.Find(settings);
                    return root == null ? new Plan { State = BuildState.Missing } : new PatchEngine(updates.Catalog).Inspect(root);
                });
                if (auto) await Task.WhenAll(scanTask, Task.Delay(650, cancel.Token));
                plan = await scanTask;
                if (cancel.IsCancellationRequested) return;
                Present(plan);
                if (Discovery.IsRunning())
                {
                    Text("HeadingText", "Studio уже открыта"); Text("DescriptionText", "Полностью закрой её перед установкой или откатом фикса.");
                    Get<Button>("PrimaryButton").Content = "Проверить снова";
                    Get<Button>("SecondaryButton").IsEnabled = false;
                }
            }
            catch (Exception error)
            {
                if (cancel.IsCancellationRequested) return;
                store.Log("scan", error); plan = new Plan { State = BuildState.Missing }; Present(plan); Notice(Friendly(error));
            }
        }
        public void Present(Plan value)
        {
            plan = value; busy = false;
            Loading(false);
            Get<Button>("UpdateButton").IsEnabled = true; Get<Button>("SettingsButton").IsEnabled = true;
            Get<Button>("PrimaryButton").IsEnabled = true; Get<Button>("SecondaryButton").Visibility = Visibility.Visible;
            Get<Button>("SecondaryButton").Content = "Запустить без фикса";
            Get<Button>("SecondaryButton").IsEnabled = value.State != BuildState.Missing;
            Text("CatalogText", BuildInfo.AppVersion + " · r" + updates.Catalog.CatalogVersion);
            Text("VersionText", value.Version ?? "не найдена");
            string accent = "#A0A0A0", status, heading, description, fix, button;
            switch (value.State)
            {
                case BuildState.Patched:
                    status = "ГОТОВО"; heading = "Фикс установлен";
                    description = "Всё готово. Теперь можно запустить Studio.";
                    fix = "Правка применена"; button = "Запустить";
                    Get<Button>("SecondaryButton").IsEnabled = value.HasBackup;
                    Icon("HeroIcon", "CheckIcon");
                    break;
                case BuildState.Original:
                    if (value.Verified)
                    { status = "ПОДДЕРЖИВАЕТСЯ"; heading = "Фикс найден"; description = "Эта версия поддерживается. Применим правку с резервной копией."; fix = "Резервная копия перед заменой"; button = "Пофиксить"; }
                    else
                    { status = "КОД РАСПОЗНАН"; heading = "Возможный фикс"; description = "Найден знакомый сетевой код. Эту сборку ещё нужно проверить в эфире."; fix = "Пробная правка с резервной копией"; button = "Пофиксить"; }
                    Icon("HeroIcon", "WrenchIcon");
                    break;
                case BuildState.Missing:
                    status = "НЕ НАЙДЕНА"; heading = "Где твоя Studio?";
                    description = "Не удалось найти установку. Укажи папку TikTok LIVE Studio.";
                    fix = "Ожидаем выбранную папку"; button = "Выбрать папку";
                    Get<Button>("SecondaryButton").Visibility = Visibility.Collapsed; Icon("HeroIcon", "FolderIcon"); break;
                case BuildState.Conflict:
                    status = "НУЖНА ПРОВЕРКА"; heading = "Файл отличается";
                    description = value.Detail ?? "Сборка отличается от ожидаемой. Можно открыть её как есть.";
                    fix = "Автоматическая замена остановлена"; button = "Запустить как есть";
                    Icon("HeroIcon", "HelpIcon");
                    Get<Button>("SecondaryButton").Visibility = Visibility.Collapsed; break;
                default:
                    status = "ПОКА НЕ ПОДДЕРЖИВАЕТСЯ"; heading = "Фикс не найден";
                    description = "Для этой версии пока нет правки. Проверь обновления или запусти без фикса.";
                    fix = "Файлы не изменены"; button = "Обновить фикс";
                    Icon("HeroIcon", "HelpIcon"); break;
            }
            Text("StatusText", status); Get<TextBlock>("StatusText").Foreground = Brush(accent); Get<System.Windows.Shapes.Ellipse>("StatusDot").Fill = Brush(accent);
            Text("HeadingText", heading); Text("DescriptionText", description); Text("FixText", fix);
            Get<Button>("PrimaryButton").Content = button;
            Get<Button>("PrimaryButton").Background = Brush("#F3F3F3");
            Get<TextBlock>("VersionText").ToolTip = value.Directory;
        }
        async Task Primary()
        {
            if (busy) return;
            if (plan == null || plan.State == BuildState.Missing) { ChooseFolder(); return; }
            if (Discovery.IsRunning()) { await Scan(); return; }
            if (plan.CanFix) await Fix(false, false);
            else if (plan.State == BuildState.Unsupported) await CheckUpdates(false);
            else await Launch(plan.State != BuildState.Patched);
        }
        async Task Fix(bool restore, bool launch)
        {
            if (busy || plan == null) return;
            SetBusy(restore ? "Возвращаем оригинал" : "Применяем фикс", "Проверяем контрольные суммы и сохраняем резервную копию.");
            writing = true;
            try
            {
                var current = plan;
                try { plan = await Task.Run(() => new PatchEngine(updates.Catalog).Apply(current.Directory, current.CurrentHash, restore)); }
                catch (UnauthorizedAccessException) { plan = await ElevatedPatch(current, restore); }
                writing = false; Present(plan);
                if (launch) await Launch(restore);
                else Notice(restore ? "Исходный файл восстановлен." : "");
            }
            catch (Exception error)
            { writing = false; store.Log("patch", error); await Scan(); Notice(Friendly(error)); }
        }
        async Task<Plan> ElevatedPatch(Plan current, bool restore)
        {
            string sessions = Path.Combine(store.Root, "sessions"); Directory.CreateDirectory(sessions);
            string result = Path.Combine(sessions, Guid.NewGuid().ToString("N") + ".json");
            string args = "--patch-helper --studio " + Data.Quote(current.Directory) + " --expected " + Data.Quote(current.CurrentHash) + " --data-root " + Data.Quote(store.Root) + " --result " + Data.Quote(result) + (restore ? " --restore" : "");
            Notice("Windows запросит права для записи файла программы.");
            try
            {
                using (var process = Process.Start(new ProcessStartInfo { FileName = typeof(LauncherUi).Assembly.Location, Arguments = args, Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden }))
                    await Task.Run(() => process.WaitForExit());
                if (!File.Exists(result)) throw new IOException("Помощник не вернул результат. Повтори проверку.");
                var response = Data.Read<HelperResult>(result);
                if (!response.Success) throw new IOException(response.Message ?? "Не удалось применить правку.");
                return await Task.Run(() => new PatchEngine(updates.Catalog).Inspect(current.Directory));
            }
            finally { if (File.Exists(result)) File.Delete(result); }
        }
        async Task Launch(bool withoutFix)
        {
            if (busy || plan == null || plan.State == BuildState.Missing) return;
            SetBusy("Открываем Studio", withoutFix ? "Запускаем выбранную версию без новой правки." : "Фикс проверен. Запускаем выбранную версию.");
            try
            {
                var current = plan;
                await Task.Run(() => { using (var process = Discovery.Start(current, new PatchEngine(updates.Catalog), withoutFix)) { } });
                Window.Close();
            }
            catch (Exception error) { store.Log("launch", error); await Scan(); Notice(Friendly(error)); }
        }
        async Task CheckUpdates(bool quiet)
        {
            if (checkingUpdates) { if (!quiet) Notice("Проверка обновлений уже идёт."); return; }
            if (busy) return;
            checkingUpdates = true;
            if (!quiet) SetBusy("Проверяем обновления", "Ищем новые правила и версию приложения на GitHub.");
            bool changed = false; Exception rulesError = null, appError = null;
            // Failure of one asset never prevents checking the other kind of update.
            var rulesTask = CheckRules(); var appTask = CheckApplication();
            await Task.WhenAll(rulesTask, appTask);
            checkingUpdates = false;
            if (cancel.IsCancellationRequested) return;
            if (!quiet || (changed && !busy)) await Scan();
            if (availableUpdate != null)
            {
                Text("UpdateLabel", "Доступно обновление");
                Get<Button>("UpdateButton").Foreground = Brush("#D0D0D0");
                Get<Button>("UpdateButton").ToolTip = "StudioFix " + availableUpdate.Release.Version;
                if (!busy) ShowAppUpdate();
            }
            else if (!quiet) Notice(rulesError != null || appError != null ? "Не удалось проверить все обновления. Попробуй позже." : changed ? "Правила обновлены." : "Приложение и правила уже обновлены.");

            async Task CheckRules()
            {
                try { changed = await updates.UpdateAsync(settings.Repository, cancel.Token); }
                catch (Exception error) { rulesError = error; store.Log("rule-update", error); }
            }
            async Task CheckApplication()
            {
                try { availableUpdate = await appUpdates.CheckAsync(settings.Repository, cancel.Token); }
                catch (Exception error) { appError = error; store.Log("app-update", error); }
            }
        }
        void ShowAppUpdate(string previewOutput = null)
        {
            if (busy || availableUpdate == null || cancel.IsCancellationRequested) return;
            var update = availableUpdate;
            var dialog = new Window { Title = "Обновление StudioFix", Owner = previewOutput == null ? Window : null, Width = 354, Height = 308, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Foreground = Brush("#EEEEEE"), FontFamily = new FontFamily("Segoe UI") };
            var panel = new StackPanel { Margin = new Thickness(23, 24, 23, 20) };
            panel.Children.Add(new Image { Source = (ImageSource)Window.Resources["UpdateIcon"], Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 16) });
            panel.Children.Add(new TextBlock { Text = "Новая версия StudioFix", FontSize = 19, FontWeight = FontWeights.Medium });
            panel.Children.Add(new TextBlock { Text = BuildInfo.AppVersion + " → " + update.Release.Version, Foreground = Brush("#777777"), FontSize = 11, Margin = new Thickness(0, 8, 0, 12) });
            var detail = new TextBlock { Text = String.IsNullOrWhiteSpace(update.Release.Notes) ? "Обновление готово. Настройки и резервные копии сохранятся." : update.Release.Notes, Foreground = Brush("#A0A0A0"), FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxHeight = 48, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 18) };
            panel.Children.Add(detail);
            var install = new Button { Content = "Обновить приложение", Style = (Style)Window.Resources["Button"], Background = Brush("#F3F3F3"), Foreground = Brush("#0B0B0B"), Height = 40 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(install, "InstallAppUpdate"); panel.Children.Add(install);
            var later = new Button { Content = "Позже", Style = (Style)Window.Resources["Button"], Background = Brushes.Transparent, Foreground = Brush("#777777"), FontSize = 10, Padding = new Thickness(5, 8, 5, 8) }; panel.Children.Add(later);
            later.Click += (s, e) => dialog.Close();
            bool downloading = false;
            install.Click += async (s, e) =>
            {
                if (downloading) return;
                downloading = true; install.IsEnabled = false; later.IsEnabled = false; install.Content = "Загружаем и проверяем…";
                try
                {
                    string path = await appUpdates.DownloadAsync(update, cancel.Token);
                    if (!Data.Same(Data.HashFile(path), update.Release.Sha256)) throw new InvalidDataException("Скачанный файл изменился. Обновление остановлено.");
                    using (var process = AppUpdates.StartInstaller(path)) { }
                    downloading = false; dialog.Close(); Window.Close();
                }
                catch (Exception error) { store.Log("install-update", error); detail.Text = Friendly(error); downloading = false; install.IsEnabled = true; later.IsEnabled = true; install.Content = "Повторить загрузку"; }
            };
            dialog.Content = new Border { Background = Brush("#080808"), BorderBrush = Brush("#292929"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Margin = new Thickness(7), Child = panel };
            dialog.MouseLeftButtonDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) dialog.DragMove(); };
            dialog.Closing += (s, e) => { if (downloading) e.Cancel = true; };
            dialog.KeyDown += (s, e) => { if (e.Key == Key.Escape && !downloading) dialog.Close(); };
            if (previewOutput != null) RenderWindow(dialog, previewOutput); else dialog.ShowDialog();
        }
        public void PreviewAppUpdate(string output)
        {
            availableUpdate = new AvailableUpdate { Release = new AppRelease { Version = "1.2.0", Notes = "Новые правила и улучшения поиска Studio. Настройки и резервные копии сохранятся." } };
            ShowAppUpdate(output); Window.Close();
        }
        void ChooseFolder()
        {
            var dialog = new Window { Title = "Найти Studio", Owner = Window, Width = 426, Height = 430, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Foreground = Brush("#EEEEEE"), FontFamily = new FontFamily("Segoe UI") };
            var searchCancel = new CancellationTokenSource();
            var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 17) };
            var close = new Button { Content = new Image { Source = (ImageSource)Window.Resources["CloseIcon"], Width = 12, Height = 12 }, Style = (Style)Window.Resources["Button"], Background = Brushes.Transparent, Padding = new Thickness(5) };
            DockPanel.SetDock(close, Dock.Right); close.Click += (s, e) => dialog.Close(); header.Children.Add(close);
            header.Children.Add(new TextBlock { Text = "Найти Studio", FontSize = 18 }); header.MouseLeftButtonDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) dialog.DragMove(); }; panel.Children.Add(header);
            panel.Children.Add(new TextBlock { Text = "Путь к папке, EXE или обычному ярлыку", FontSize = 11, Foreground = Brush("#929292"), Margin = new Thickness(0, 0, 0, 8) });
            var pathBox = new TextBox { Text = settings.StudioRoot ?? "", Background = Brush("#111111"), Foreground = Brush("#DDDDDD"), BorderBrush = Brush("#292929"), Padding = new Thickness(9), FontSize = 11 };
            System.Windows.Automation.AutomationProperties.SetAutomationId(pathBox, "StudioPathInput");
            panel.Children.Add(pathBox);
            var hint = new TextBlock { Text = "", Foreground = Brush("#A0A0A0"), TextWrapping = TextWrapping.Wrap, FontSize = 10, MinHeight = 25, Margin = new Thickness(0, 7, 0, 10) }; panel.Children.Add(hint);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) }); actions.ColumnDefinitions.Add(new ColumnDefinition());
            var browse = new Button { Content = "Выбрать папку", Style = (Style)Window.Resources["Button"], FontSize = 11 };
            browse.Click += (s, e) => { using (var folder = new System.Windows.Forms.FolderBrowserDialog { Description = "Выбери папку TikTok LIVE Studio", ShowNewFolderButton = false }) if (folder.ShowDialog() == System.Windows.Forms.DialogResult.OK) pathBox.Text = folder.SelectedPath; };
            var apply = new Button { Content = "Проверить путь", Style = (Style)Window.Resources["Button"], FontSize = 11, Background = Brush("#F3F3F3"), Foreground = Brush("#0B0B0B") }; Grid.SetColumn(apply, 2); actions.Children.Add(browse); actions.Children.Add(apply); panel.Children.Add(actions);
            System.Windows.Automation.AutomationProperties.SetAutomationId(apply, "CheckStudioPath");
            apply.Click += (s, e) =>
            {
                try
                {
                    string resolved = Discovery.Resolve(pathBox.Text);
                    if (resolved == null) { hint.Text = "По этому пути Studio не найдена. Укажи её папку или EXE из свойства «Объект» ярлыка."; return; }
                    var parent = Directory.GetParent(resolved); settings.StudioRoot = parent != null && File.Exists(Path.Combine(parent.FullName, "launcher_config.json")) ? parent.FullName : resolved;
                    store.SaveSettings(settings); dialog.Close(); _ = Scan();
                }
                catch (Exception error) { hint.Text = Friendly(error); }
            };
            var deep = new Button { Content = "Расширенный поиск по дискам", Style = (Style)Window.Resources["Button"], Background = Brushes.Transparent, Foreground = Brush("#747474"), FontSize = 10, Margin = new Thickness(0, 5, 0, 10), Padding = new Thickness(4, 7, 4, 7) }; panel.Children.Add(deep);
            deep.Click += async (s, e) =>
            {
                deep.IsEnabled = false; apply.IsEnabled = false; hint.Text = "Ищем в папках приложений и на локальных дисках…";
                try
                {
                    string found = await Task.Run(() => Discovery.DeepFind(searchCancel.Token));
                    if (searchCancel.IsCancellationRequested) return;
                    if (found != null) { pathBox.Text = found; hint.Text = "Studio найдена. Нажми «Проверить путь»."; } else hint.Text = "Не нашли за время поиска. Скопируй путь из свойств обычного ярлыка.";
                }
                catch (OperationCanceledException) { }
                catch (Exception error) { hint.Text = Friendly(error); }
                finally { deep.IsEnabled = true; apply.IsEnabled = true; }
            };
            panel.Children.Add(new TextBlock { Text = "Где взять путь", FontSize = 11, Foreground = Brush("#B0B0B0"), Margin = new Thickness(0, 0, 0, 7) });
            panel.Children.Add(new TextBlock { Text = "Правой кнопкой по обычному ярлыку Studio → Свойства → Объект. Скопируй весь путь и вставь выше. Или нажми «Расположение файла» и скопируй адрес открытой папки.", FontSize = 11, Foreground = Brush("#929292"), TextWrapping = TextWrapping.Wrap, LineHeight = 16 });
            panel.Children.Add(new TextBlock { Text = "Также проверь: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "TikTok LIVE Studio"), FontSize = 10, Foreground = Brush("#505050"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 9, 0, 0) });
            dialog.Content = new Border { Background = Brush("#080808"), BorderBrush = Brush("#292929"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Margin = new Thickness(7), Child = panel };
            dialog.Closed += (s, e) => searchCancel.Cancel(); dialog.KeyDown += (s, e) => { if (e.Key == Key.Escape) dialog.Close(); }; dialog.ShowDialog();
        }
        void ShowSettings()
        {
            if (busy) return;
            var dialog = new Window { Title = "Настройки StudioFix", Owner = Window, Width = 352, Height = 356, WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Foreground = Brush("#EEEEEE"), FontFamily = new FontFamily("Segoe UI") };
            var panel = new StackPanel { Margin = new Thickness(22, 18, 22, 20) };
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            var close = new Button { Content = new Image { Source = (ImageSource)Window.Resources["CloseIcon"], Width = 12, Height = 12 }, Style = (Style)Window.Resources["Button"], Padding = new Thickness(5), Background = Brushes.Transparent };
            DockPanel.SetDock(close, Dock.Right); close.Click += (s, e) => dialog.Close(); header.Children.Add(close);
            header.Children.Add(new TextBlock { Text = "Настройки", FontSize = 18, FontWeight = FontWeights.Medium });
            header.MouseLeftButtonDown += (s, e) => { if (e.ChangedButton == MouseButton.Left) dialog.DragMove(); };
            panel.Children.Add(header);
            panel.Children.Add(new TextBlock { Text = "Репозиторий обновлений", Foreground = Brush("#888888"), FontSize = 11, Margin = new Thickness(0, 0, 0, 8) });
            var repository = new TextBox { Text = settings.Repository ?? "", Background = Brush("#111111"), Foreground = Brush("#DDDDDD"), BorderBrush = Brush("#2A2A2A"), BorderThickness = new Thickness(1), Padding = new Thickness(9), FontSize = 11, ToolTip = "https://github.com/владелец/репозиторий" };
            panel.Children.Add(repository);
            panel.Children.Add(new TextBlock { Text = "GitHub-ссылка или владелец/репозиторий. Обновления берутся из подписанных GitHub Releases.", Foreground = Brush("#5F5F5F"), TextWrapping = TextWrapping.Wrap, FontSize = 10, Margin = new Thickness(0, 8, 0, 14) });
            var autoUpdate = new CheckBox { Content = "Проверять обновления при открытии", IsChecked = settings.AutoCheckUpdates, Foreground = Brush("#999999"), FontSize = 11, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(autoUpdate);
            if (integration.Connected)
            {
            var links = new Button { Content = "Восстановить старый ярлык Studio", Style = (Style)Window.Resources["Button"], FontSize = 11, Margin = new Thickness(0, 0, 0, 9) };
            links.Click += async (s, e) =>
            {
                dialog.Close();
                SetBusy("Настраиваем ярлык", "Для общего ярлыка Windows может запросить права."); writing = true;
                try { await Task.Run(() => IntegrationService.Restore(store, typeof(LauncherUi).Assembly.Location)); writing = false; await Scan(); Notice("Оригинальный ярлык Studio восстановлен."); }
                catch (Exception error) { writing = false; store.Log("shortcuts", error); await Scan(); Notice(Friendly(error)); }
            };
            panel.Children.Add(links);
            dialog.Height += 43;
            }
            panel.Children.Add(new TextBlock { Text = "StudioFix открывается через собственный ярлык.", Foreground = Brush("#606060"), FontSize = 10, Margin = new Thickness(0, 0, 0, 12) });
            var row = new Grid(); row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            var folder = new Button { Content = "Папка Studio", Style = (Style)Window.Resources["Button"], FontSize = 11 };
            folder.Click += (s, e) => { dialog.Close(); ChooseFolder(); }; row.Children.Add(folder);
            var save = new Button { Content = "Сохранить", Style = (Style)Window.Resources["Button"], Background = Brush("#F3F3F3"), Foreground = Brush("#0B0B0B"), FontSize = 11 };
            save.Click += (s, e) =>
            {
                try { string repo = String.IsNullOrWhiteSpace(repository.Text) ? BuildInfo.Repository : RuleUpdates.ParseRepository(repository.Text); settings.Repository = repo; settings.AutoCheckUpdates = autoUpdate.IsChecked == true; store.SaveSettings(settings); dialog.Close(); Notice("Настройки сохранены."); }
                catch (Exception error) { MessageBox.Show(dialog, Friendly(error), "Настройки", MessageBoxButton.OK, MessageBoxImage.Information); }
            };
            Grid.SetColumn(save, 2); row.Children.Add(save); panel.Children.Add(row);
            if (plan != null && plan.State == BuildState.Patched && plan.HasBackup)
            {
                var restore = new Button { Content = "Вернуть исходный файл Studio", Style = (Style)Window.Resources["Button"], FontSize = 11, Margin = new Thickness(0, 10, 0, 0) };
                restore.Click += async (s, e) => { dialog.Close(); await Fix(true, false); }; panel.Children.Add(restore); dialog.Height += 43;
            }
            dialog.Content = new Border { Background = Brush("#080808"), BorderBrush = Brush("#292929"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(15), Margin = new Thickness(7), Child = panel };
            dialog.KeyDown += (s, e) => { if (e.Key == Key.Escape) dialog.Close(); }; dialog.ShowDialog();
        }
        static string Friendly(Exception error)
        {
            if (error is Win32Exception && ((Win32Exception)error).NativeErrorCode == 1223) return "Запрос прав Windows отменён. Действие не завершено.";
            if (error is TaskCanceledException) return "Сервер не ответил вовремя. Сохранённые правила остаются доступны.";
            return error.Message;
        }
        public void Render(string output)
        {
            RenderWindow(Window, output);
        }
        static void RenderWindow(Window target, string output)
        {
            var visual = (FrameworkElement)target.Content;
            visual.Measure(new Size(target.Width, target.Height)); visual.Arrange(new Rect(0, 0, target.Width, target.Height)); visual.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)target.Width, (int)target.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));
            using (var file = File.Create(output)) encoder.Save(file);
            target.Close();
        }
    }
    public sealed class HelperResult { public bool Success { get; set; } public string Message { get; set; } }
}
