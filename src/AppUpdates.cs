using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace StudioFix
{
    public sealed class AppRelease
    {
        public int Schema { get; set; }
        public string Version { get; set; }
        public string Repository { get; set; }
        public string Tag { get; set; }
        public string Asset { get; set; }
        public string Sha256 { get; set; }
        public int Size { get; set; }
        public string Notes { get; set; }
    }
    public sealed class AvailableUpdate
    {
        public AppRelease Release { get; internal set; }
        public Uri Download { get; internal set; }
    }
    public sealed class AppUpdates
    {
        public const string ManifestAsset = "studiofix-update.json";
        public const string SignatureAsset = "studiofix-update.sig";
        readonly AppStore store;
        readonly string publicKey;
        readonly Func<HttpMessageHandler> httpFactory;
        readonly Version currentVersion;
        public AppUpdates(AppStore storage, string key = null, Func<HttpMessageHandler> handlerFactory = null, string current = null)
        {
            store = storage; publicKey = key ?? Encoding.UTF8.GetString(Data.Resource("StudioFix.public-key.xml"));
            httpFactory = handlerFactory ?? (() => new HttpClientHandler { AllowAutoRedirect = false });
            currentVersion = Version.Parse(current ?? BuildInfo.AppVersion);
        }
        public AppRelease Verify(SignedRules signed, string repository)
        {
            if (signed == null || signed.Content == null || signed.Content.Length > 16384 || signed.Signature == null || signed.Signature.Length > 1024) throw new InvalidDataException("Некорректный размер описания обновления.");
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (!rsa.VerifyData(signed.Content, CryptoConfig.MapNameToOID("SHA256"), signed.Signature)) throw new InvalidDataException("Подпись обновления приложения не совпала.");
            }
            var release = Data.Json().Deserialize<AppRelease>(Encoding.UTF8.GetString(signed.Content));
            Version version;
            if (release == null || release.Schema != 1 || !Version.TryParse(release.Version, out version) || !Regex.IsMatch(release.Version, "^[0-9]+\\.[0-9]+\\.[0-9]+$") || !Data.Same(release.Repository, repository) || release.Tag != "v" + release.Version || release.Asset != BuildInfo.InstallerAsset || release.Size < 1 || release.Size > 64 * 1024 * 1024 || release.Sha256 == null || !Regex.IsMatch(release.Sha256, "^[a-fA-F0-9]{64}$") || (release.Notes ?? "").Length > 2000) throw new InvalidDataException("Описание обновления приложения не прошло проверку.");
            return release;
        }
        HttpClient Client()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            var client = new HttpClient(httpFactory()) { Timeout = TimeSpan.FromSeconds(120) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StudioFix/" + BuildInfo.AppVersion);
            return client;
        }
        public async Task<AvailableUpdate> CheckAsync(string repository, CancellationToken cancellation)
        {
            repository = RuleUpdates.ParseRepository(repository);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var client = Client())
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(20)); cancellation = deadline.Token;
                var bytes = await RuleUpdates.Fetch(client, new Uri("https://api.github.com/repos/" + repository + "/releases/latest"), 512 * 1024, cancellation).ConfigureAwait(false);
                var json = Data.Json().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(bytes));
                var values = json != null && json.ContainsKey("assets") ? json["assets"] as System.Collections.IEnumerable : null;
                var assets = values == null ? new Dictionary<string, object>[0] : values.Cast<object>().OfType<Dictionary<string, object>>().ToArray();
                // Older catalog-only releases remain valid for rule updates.
                if (!assets.Any(a => a.ContainsKey("name") && Convert.ToString(a["name"]) == ManifestAsset)) return null;
                Func<string, Uri> assetUrl = name =>
                {
                    var found = assets.Where(a => a.ContainsKey("name") && Convert.ToString(a["name"]) == name).ToArray();
                    if (found.Length != 1 || !found[0].ContainsKey("browser_download_url")) throw new InvalidDataException("В выпуске не найден " + name + ".");
                    var uri = new Uri(Convert.ToString(found[0]["browser_download_url"]));
                    if (!uri.AbsoluteUri.StartsWith("https://github.com/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Обновление относится к другому репозиторию.");
                    return uri;
                };
                var content = RuleUpdates.Fetch(client, assetUrl(ManifestAsset), 16384, cancellation);
                var signature = RuleUpdates.Fetch(client, assetUrl(SignatureAsset), 2048, cancellation);
                await Task.WhenAll(content, signature).ConfigureAwait(false);
                var release = Verify(new SignedRules { Content = content.Result, Signature = Convert.FromBase64String(Encoding.ASCII.GetString(signature.Result).Trim()) }, repository);
                if (!json.ContainsKey("tag_name") || Convert.ToString(json["tag_name"]) != release.Tag) throw new InvalidDataException("Версия подписанного обновления не совпадает с выпуском.");
                var installer = assetUrl(release.Asset);
                string expectedUrl = "https://github.com/" + repository + "/releases/download/" + release.Tag + "/" + release.Asset;
                if (!Data.Same(installer.AbsoluteUri, expectedUrl)) throw new InvalidDataException("Установщик относится к другой версии.");
                if (Version.Parse(release.Version) <= currentVersion) return null;
                return new AvailableUpdate { Release = release, Download = installer };
            }
        }
        public async Task<string> DownloadAsync(AvailableUpdate update, CancellationToken cancellation)
        {
            if (update == null || update.Release == null || Version.Parse(update.Release.Version) <= currentVersion) throw new InvalidDataException("Более новая версия не выбрана.");
            byte[] bytes;
            using (var client = Client()) bytes = await RuleUpdates.Fetch(client, update.Download, update.Release.Size, cancellation).ConfigureAwait(false);
            if (bytes.Length != update.Release.Size || !Data.Same(Data.Hash(bytes), update.Release.Sha256)) throw new InvalidDataException("Установщик не совпал с подписанной контрольной суммой. Обновление остановлено.");
            string path = Path.Combine(store.Root, "downloads", "StudioFix-" + update.Release.Version + "-Setup.exe");
            Data.SaveBytes(path, bytes); return path;
        }
        public static Process StartInstaller(string path)
        {
            if (!File.Exists(path)) throw new IOException("Установщик обновления не найден.");
            return Process.Start(new ProcessStartInfo { FileName = path, Arguments = "/SP- /NORESTART /LANG=russian", UseShellExecute = true });
        }
    }
}
