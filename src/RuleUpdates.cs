using System;
using System.Collections.Generic;
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
    public sealed class SignedRules
    {
        public byte[] Content { get; set; }
        public byte[] Signature { get; set; }
    }
    public sealed class RuleUpdates
    {
        public const string CatalogAsset = "studio-fix-rules.json";
        public const string SignatureAsset = "studio-fix-rules.sig";
        readonly AppStore store;
        readonly string publicKey;
        readonly Func<HttpMessageHandler> httpFactory;
        public RuleCatalog Catalog { get; private set; }
        public string Warning { get; private set; }
        public RuleUpdates(AppStore storage, string key = null, SignedRules embedded = null, Func<HttpMessageHandler> handlerFactory = null)
        {
            store = storage; publicKey = key ?? Encoding.UTF8.GetString(Data.Resource("StudioFix.public-key.xml"));
            httpFactory = handlerFactory ?? (() => new HttpClientHandler { AllowAutoRedirect = false });
            embedded = embedded ?? new SignedRules { Content = Data.Resource("StudioFix.rules.json"), Signature = Data.Resource("StudioFix.rules.sig") };
            Catalog = Verify(embedded);
            string cached = Path.Combine(store.Root, "rules-envelope.json");
            if (File.Exists(cached))
            {
                try
                {
                    var envelope = Data.Read<SignedRules>(cached); var next = Verify(envelope);
                    if (next.CatalogVersion < Catalog.CatalogVersion) throw new InvalidDataException("Каталог устарел.");
                    Catalog = next;
                }
                catch (Exception error) { store.Log("rules-cache", error); Warning = "Сохранённое обновление не прошло проверку. Используются встроенные правила."; }
            }
        }
        public RuleCatalog Verify(SignedRules envelope)
        {
            if (envelope == null || envelope.Content == null || envelope.Content.Length > 256 * 1024 || envelope.Signature == null || envelope.Signature.Length > 1024) throw new InvalidDataException("Некорректный размер обновления.");
            using (var rsa = new RSACryptoServiceProvider())
            {
                rsa.PersistKeyInCsp = false; rsa.FromXmlString(publicKey);
                if (!rsa.VerifyData(envelope.Content, CryptoConfig.MapNameToOID("SHA256"), envelope.Signature)) throw new InvalidDataException("Подпись обновления не совпала. Правки не загружены.");
            }
            var catalog = Data.Json().Deserialize<RuleCatalog>(Encoding.UTF8.GetString(envelope.Content));
            if (catalog == null || catalog.Schema != 1 || catalog.CatalogVersion < 1 || catalog.Builds == null || catalog.Builds.Count < 1 || catalog.Builds.Count > 128) throw new InvalidDataException("Формат каталога не поддерживается.");
            var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var build in catalog.Builds)
            {
                Version version;
                if (!Version.TryParse(build.Version, out version) || !IsHash(build.OriginalHash) || !IsHash(build.PatchedHash) || Data.Same(build.OriginalHash, build.PatchedHash) || String.IsNullOrEmpty(build.Needle) || String.IsNullOrEmpty(build.Replacement) || build.Needle.Length > 8192 || build.Replacement.Length > 16384 || build.Needle.Any(c => c > 127) || build.Replacement.Any(c => c > 127) || !pairs.Add(build.Version + ":" + build.OriginalHash)) throw new InvalidDataException("В каталоге найдена некорректная правка.");
            }
            return catalog;
        }
        static bool IsHash(string value) { return value != null && Regex.IsMatch(value, "^[A-Fa-f0-9]{64}$"); }
        public bool Install(SignedRules envelope)
        {
            var next = Verify(envelope);
            if (next.CatalogVersion < Catalog.CatalogVersion) throw new InvalidDataException("Более старое обновление не устанавливается.");
            if (next.CatalogVersion == Catalog.CatalogVersion)
            {
                if (Data.Json().Serialize(next) != Data.Json().Serialize(Catalog)) throw new InvalidDataException("Каталог с этим номером уже существует. Для новой правки нужен новый номер каталога.");
                return false;
            }
            Data.Save(Path.Combine(store.Root, "rules-envelope.json"), envelope); Catalog = next; return true;
        }
        public static string ParseRepository(string input)
        {
            string value = (input ?? "").Trim().TrimEnd('/');
            if (value.StartsWith("https://github.com/", StringComparison.OrdinalIgnoreCase)) value = value.Substring("https://github.com/".Length);
            if (value.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) value = value.Substring(0, value.Length - 4);
            if (!Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}/[A-Za-z0-9_.-]{1,100}$")) throw new ArgumentException("Укажи ссылку https://github.com/владелец/репозиторий или владелец/репозиторий.");
            return value;
        }
        static bool GithubDownload(Uri uri)
        {
            if (uri == null || uri.Scheme != "https" || !String.IsNullOrEmpty(uri.UserInfo)) return false;
            return uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        }
        public async Task<bool> UpdateAsync(string repository, CancellationToken cancellation)
        {
            repository = ParseRepository(repository);
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            using (var handler = httpFactory())
            using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) })
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(20)); cancellation = deadline.Token;
                client.DefaultRequestHeaders.UserAgent.ParseAdd("StudioFix/" + BuildInfo.AppVersion);
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                var releaseBytes = await Fetch(client, new Uri("https://api.github.com/repos/" + repository + "/releases/latest"), 512 * 1024, cancellation).ConfigureAwait(false);
                var release = Data.Json().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(releaseBytes));
                var assetValues = release.ContainsKey("assets") ? release["assets"] as System.Collections.IEnumerable : null;
                var assets = assetValues == null ? null : assetValues.Cast<object>().OfType<Dictionary<string, object>>().ToArray();
                if (assets == null) throw new InvalidDataException("В последнем GitHub Release нет файлов обновления.");
                Func<string, Uri> assetUrl = name =>
                {
                    var found = assets.Cast<Dictionary<string, object>>().Where(a => Convert.ToString(a["name"]) == name).ToArray();
                    if (found.Length != 1) throw new InvalidDataException("В GitHub Release не найден " + name + ".");
                    var uri = new Uri(Convert.ToString(found[0]["browser_download_url"]));
                    if (!uri.AbsoluteUri.StartsWith("https://github.com/" + repository + "/releases/download/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Адрес файла обновления не совпадает с репозиторием.");
                    return uri;
                };
                var contentTask = Fetch(client, assetUrl(CatalogAsset), 256 * 1024, cancellation);
                var signatureTask = Fetch(client, assetUrl(SignatureAsset), 2048, cancellation);
                await Task.WhenAll(contentTask, signatureTask).ConfigureAwait(false);
                byte[] signature;
                try { signature = Convert.FromBase64String(Encoding.ASCII.GetString(signatureTask.Result).Trim()); }
                catch { throw new InvalidDataException("Файл подписи обновления повреждён."); }
                return Install(new SignedRules { Content = contentTask.Result, Signature = signature });
            }
        }
        internal static async Task<byte[]> Fetch(HttpClient client, Uri uri, int limit, CancellationToken cancellation)
        {
            for (int redirect = 0; redirect <= 4; redirect++)
            {
                if (!GithubDownload(uri)) throw new InvalidDataException("Адрес обновления не является HTTPS-адресом GitHub.");
                using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status <= 399)
                    { var location = response.Headers.Location; if (location == null) throw new IOException("GitHub вернул пустой адрес перенаправления."); uri = location.IsAbsoluteUri ? location : new Uri(uri, location); continue; }
                    if (status == 404) throw new IOException("Репозиторий или опубликованный GitHub Release не найден.");
                    if (status == 403 || status == 429) throw new IOException("GitHub временно ограничил запросы. Попробуй позже.");
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Файл обновления слишком большой.");
                    using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new MemoryStream())
                    {
                        byte[] buffer = new byte[8192]; int count;
                        while ((count = await stream.ReadAsync(buffer, 0, buffer.Length, cancellation).ConfigureAwait(false)) > 0)
                        { if (output.Length + count > limit) throw new InvalidDataException("Файл обновления слишком большой."); output.Write(buffer, 0, count); }
                        return output.ToArray();
                    }
                }
            }
            throw new IOException("GitHub вернул слишком много перенаправлений.");
        }
    }
}
