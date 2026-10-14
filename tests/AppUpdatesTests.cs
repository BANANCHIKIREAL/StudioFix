using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace StudioFix.Tests
{
    public sealed class FakeAppGithub : HttpMessageHandler
    {
        readonly SignedRules manifest;
        readonly byte[] installer;
        readonly string tag;
        public FakeAppGithub(SignedRules value, byte[] binary, string releaseTag = "v1.2.0") { manifest = value; installer = binary; tag = releaseTag; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            byte[] bytes;
            if (request.RequestUri.Host == "api.github.com")
            {
                var assets = new[] { AppUpdates.ManifestAsset, AppUpdates.SignatureAsset, BuildInfo.InstallerAsset };
                var entries = new System.Collections.Generic.List<object>();
                foreach (var name in assets) entries.Add(new { name = name, browser_download_url = "https://github.com/example/studiofix/releases/download/" + tag + "/" + name });
                bytes = Data.Utf8.GetBytes(Data.Json().Serialize(new { tag_name = tag, assets = entries }));
            }
            else if (request.RequestUri.AbsolutePath.EndsWith(AppUpdates.ManifestAsset)) bytes = manifest.Content;
            else if (request.RequestUri.AbsolutePath.EndsWith(AppUpdates.SignatureAsset)) bytes = Encoding.ASCII.GetBytes(Convert.ToBase64String(manifest.Signature));
            else bytes = installer;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
    public static class AppUpdatesTests
    {
        static void Check(bool value, string message) { if (!value) throw new Exception(message); }
        static void Refused(Action action) { bool refused = false; try { action(); } catch { refused = true; } Check(refused, "Unsafe app update was accepted."); }
        public static void Run(string root)
        {
            using (var rsa = new RSACryptoServiceProvider(2048))
            {
                rsa.PersistKeyInCsp = false;
                byte[] installer = Encoding.ASCII.GetBytes("fixture installer bytes, never executed");
                var release = new AppRelease { Schema = 1, Version = "1.2.0", Repository = "example/studiofix", Tag = "v1.2.0", Asset = BuildInfo.InstallerAsset, Sha256 = Data.Hash(installer), Size = installer.Length, Notes = "New version" };
                var signed = Sign(release, rsa);
                var updater = new AppUpdates(new AppStore(Path.Combine(root, "app-update")), rsa.ToXmlString(false), () => new FakeAppGithub(signed, installer), "1.1.0");
                var available = updater.CheckAsync("example/studiofix", CancellationToken.None).GetAwaiter().GetResult();
                Check(available != null && available.Release.Version == "1.2.0", "New app version not found.");
                string path = updater.DownloadAsync(available, CancellationToken.None).GetAwaiter().GetResult();
                Check(Data.Same(Data.HashFile(path), release.Sha256), "Verified app download was corrupted.");
                var current = new AppUpdates(new AppStore(root), rsa.ToXmlString(false), () => new FakeAppGithub(signed, installer), "1.2.0");
                Check(current.CheckAsync("example/studiofix", CancellationToken.None).GetAwaiter().GetResult() == null, "Current version prompted an update.");
                var newer = new AppUpdates(new AppStore(root), rsa.ToXmlString(false), () => new FakeAppGithub(signed, installer), "2.0.0");
                Check(newer.CheckAsync("example/studiofix", CancellationToken.None).GetAwaiter().GetResult() == null, "App downgrade was offered.");
                var tampered = new SignedRules { Content = (byte[])signed.Content.Clone(), Signature = signed.Signature }; tampered.Content[10] ^= 1;
                Refused(() => updater.Verify(tampered, "example/studiofix"));
                Refused(() => updater.Verify(signed, "other/repo"));
                var wrongTag = new AppUpdates(new AppStore(root), rsa.ToXmlString(false), () => new FakeAppGithub(signed, installer, "v9.0.0"));
                Refused(() => wrongTag.CheckAsync("example/studiofix", CancellationToken.None).GetAwaiter().GetResult());
                installer[0] ^= 1;
                Refused(() => updater.DownloadAsync(available, CancellationToken.None).GetAwaiter().GetResult());
                Check(Data.Same(Data.HashFile(path), release.Sha256), "Rejected app download overwrote a valid installer.");
                release.Size = 128 * 1024 * 1024;
                Refused(() => updater.Verify(Sign(release, rsa), "example/studiofix"));
                Check(new AppStore(Path.Combine(root, "new-user")).LoadSettings().Repository == BuildInfo.Repository, "Default repository missing for new users.");
            }
        }
        static SignedRules Sign(AppRelease release, RSACryptoServiceProvider rsa)
        {
            byte[] bytes = Data.Utf8.GetBytes(Data.Json().Serialize(release));
            return new SignedRules { Content = bytes, Signature = rsa.SignData(bytes, CryptoConfig.MapNameToOID("SHA256")) };
        }
    }
}
