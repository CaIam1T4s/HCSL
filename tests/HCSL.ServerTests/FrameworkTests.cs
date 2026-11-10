using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using HCSL.Application.Abstractions;
using HCSL.Core;
using HCSL.Infrastructure.Frameworks;
using HCSL.Infrastructure.Storage;
namespace HCSL.ServerTests;

public static class FrameworkTests
{
    public static async Task RunAsync()
    {
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        async Task Reject(Task action, string code)
        {
            try { await action; throw new Exception("Expected " + code); }
            catch (InstanceException e) when (e.Code == code) { }
        }

        // Tag and asset rules are pure; the catalog must stay closed.
        Check(FrameworkRules.VersionFromTag("metamod", "2.0.0.1490") == "2.0.0-dev+1490", "Metamod tag mapping.");
        Check(FrameworkRules.VersionFromTag("metamod", "1.12.0.1229") is null, "Legacy Metamod tag accepted.");
        Check(FrameworkRules.VersionFromTag("counterstrikesharp", "v1.0.379") == "1.0.379", "CSSharp tag mapping.");
        Check(FrameworkRules.VersionFromTag("source2mod", "v1.0.46") is null, "Unsupported adapter guessed a version.");
        string[] mm = ["mmsource-2.0.0-git1489-linux.tar.gz", "mmsource-2.0.0-git1489-windows.zip"];
        Check(FrameworkRules.SelectAsset("metamod", mm, windows: false, withRuntime: false) == mm[0], "Metamod Linux asset.");
        Check(FrameworkRules.SelectAsset("metamod", mm, windows: true, withRuntime: false) == mm[1], "Metamod Windows asset.");
        string[] css = ["counterstrikesharp-linux-1.0.379.zip", "counterstrikesharp-windows-1.0.379.zip",
            "counterstrikesharp-with-runtime-linux-1.0.379.zip", "counterstrikesharp-with-runtime-windows-1.0.379.zip"];
        Check(FrameworkRules.SelectAsset("counterstrikesharp", css, false, true) == css[2], "CSSharp runtime asset.");
        Check(FrameworkRules.SelectAsset("counterstrikesharp", css, true, false) == css[1], "CSSharp Windows asset.");
        Check(FrameworkRules.CompareVersions("metamod", "2.0.0-dev+1490", "2.0.0-dev+999") > 0, "Build numbers compared as text.");
        Check(FrameworkRules.CompareVersions("counterstrikesharp", "1.0.379", "1.0.9") > 0, "Version numbers compared as text.");
        Check(FrameworkRules.Conflicts("counterstrikesharp", "source2mod"), "Scripting frameworks not exclusive.");
        Check(!FrameworkRules.Conflicts("metamod", "counterstrikesharp"), "Metamod marked exclusive.");
        Check(FrameworkRules.Find("unknown") is null, "Unknown framework accepted.");

        var root = Path.Combine(Path.GetTempPath(), "hcsl-frameworks-" + Guid.NewGuid().ToString("N"));
        try
        {
            var workspace = new WorkspaceRequest(Path.Combine(root, "settings"), Path.Combine(root, "data"), Path.Combine(root, "library"));
            using (new LocalWorkspaceManager().Acquire(workspace)) { }
            var archive = BuildCsSharpZip();
            var digest = "sha256:" + Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            var json = Encoding.UTF8.GetBytes($$"""
                [{"tag_name":"v1.0.379","draft":false,"assets":[
                  {"name":"counterstrikesharp-linux-1.0.379.zip","browser_download_url":"https://github.com/example/plain.zip","size":{{archive.Length}},"digest":"{{digest}}"},
                  {"name":"counterstrikesharp-with-runtime-linux-1.0.379.zip","browser_download_url":"https://github.com/example/runtime.zip","size":{{archive.Length}},"digest":"{{digest}}"}
                ]}]
                """);
            var handler = new FakeHandler(json, archive);
            var manager = new FrameworkManager(handler);
            var check = await manager.CheckAsync(workspace);
            var cssCheck = check.Frameworks.Single(f => f.Id == "counterstrikesharp");
            Check(cssCheck.InstalledVersion is null && cssCheck.LatestVersion == "1.0.379" && cssCheck.UpdateAvailable == false, "Initial check state.");
            Check(!check.Frameworks.Single(f => f.Id == "source2mod").Installable, "Source2Mod reported installable.");
            var installed = await manager.InstallAsync(workspace, new("counterstrikesharp"));
            Check(installed.Version == "1.0.379" && installed.DigestVerified, "Install did not verify the digest.");
            Check(File.Exists(Path.Combine(installed.PackagePath, "files", "csgo", "addons", "counterstrikesharp", "bin", "linuxsteamrt64", "counterstrikesharp.so")),
                "Framework binary was not imported.");
            Check(File.Exists(Path.Combine(installed.PackagePath, "files", "csgo", "addons", "metamod", "counterstrikesharp.vdf")), "Framework VDF missing.");
            Check(!File.Exists(Path.Combine(installed.PackagePath, "files", "csgo", "gameinfo.gi")), "A sub-plugin package generated a gameinfo overlay.");
            Check(installed.Warnings.Any(w => w.Contains("metamod", StringComparison.OrdinalIgnoreCase)), "Missing Metamod dependency was not reported.");
            var after = await manager.CheckAsync(workspace);
            Check(after.Frameworks.Single(f => f.Id == "counterstrikesharp").InstalledVersion == "1.0.379", "Installed version not detected.");

            var bad = new FrameworkManager(new FakeHandler(json, archive, mutateDigest: true));
            await Reject(bad.InstallAsync(workspace, new("counterstrikesharp")), "HCSL-FRAMEWORK-HASH");
            await Reject(manager.InstallAsync(workspace, new("source2mod")), "HCSL-FRAMEWORK-ID");
            await Reject(manager.InstallAsync(workspace, new("unknown")), "HCSL-FRAMEWORK-ID");
            var offline = await new FrameworkManager(new ThrowingHandler()).CheckAsync(workspace);
            Check(offline.Frameworks.Where(f => f.Installable).All(f => f.Error == "HCSL-FRAMEWORK-NET"), "Offline check did not report a stable code.");
            var noAsset = new FrameworkManager(new FakeHandler(json, archive, dropAssets: true));
            await Reject(noAsset.InstallAsync(workspace, new("counterstrikesharp")), "HCSL-FRAMEWORK-RELEASE");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static byte[] BuildCsSharpZip()
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write(zip, "addons/counterstrikesharp/bin/linuxsteamrt64/counterstrikesharp.so", "binary");
            Write(zip, "addons/counterstrikesharp/configs/core.example.json", "{}");
            Write(zip, "addons/metamod/counterstrikesharp.vdf", "\"Plugin\"{}");
        }
        return memory.ToArray();
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(content);
        stream.Write(bytes);
    }

    private sealed class FakeHandler(byte[] json, byte[] archive, bool mutateDigest = false, bool dropAssets = false) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!;
            if (url.Host == "api.github.com")
            {
                var text = Encoding.UTF8.GetString(json);
                if (dropAssets)
                    text = text.Replace("\"assets\":[", "\"assets\":[] ,\"ignored\":[", StringComparison.Ordinal);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(text)) });
            }
            var bytes = archive;
            if (mutateDigest) { bytes = (byte[])archive.Clone(); bytes[0] ^= 0xFF; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("offline");
    }
}
