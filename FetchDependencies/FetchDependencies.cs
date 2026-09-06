using System.IO.Compression;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FetchDependencies;

public class FetchDependencies
{
    private const string VersionUrlGlobal = "https://www.iinact.com/updater/version";
    private const string PluginUrlGlobal = "https://www.iinact.com/updater/download";
    private const string GlobalGitHubApiLatestUrl = "https://api.github.com/repos/ravahn/FFXIV_ACT_Plugin/releases/latest";
    // CN builds of FFXIV_ACT_Plugin (Chinese resources, rebased onto ravahn's 3.x line
    // with the .Models.Global API layout that IINACT targets) are published by
    // TundraWork/FFXIV_ACT_Plugin_CN. The old cninact.diemoe.net mirror is deliberately
    // NOT used: it still serves the pre-3.0 2.7.4.9 build whose type layout no longer
    // matches this codebase (TypeLoadException on load).
    private const string CnGitHubApiLatestUrl = "https://api.github.com/repos/TundraWork/FFXIV_ACT_Plugin_CN/releases/latest";

    private Version PluginVersion { get; }
    private string DependenciesDir { get; }
    private bool IsChinese { get; }
    private HttpClient HttpClient { get; }

    public FetchDependencies(Version version, string assemblyDir, bool isChinese, HttpClient httpClient)
    {
        PluginVersion = version;
        DependenciesDir = assemblyDir;
        IsChinese = isChinese;
        HttpClient = httpClient;
    }

    public void GetFfxivPlugin()
    {
        var pluginZipPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.zip");
        var pluginPath = Path.Combine(DependenciesDir, "FFXIV_ACT_Plugin.dll");

        if (!NeedsUpdate(pluginPath))
            return;

        // Both the global and the CN builds are distributed as zip archives containing
        // FFXIV_ACT_Plugin.dll plus deucalion DLLs; the Memory/Logfile satellites are
        // Costura-embedded in the main DLL and are unpacked by Patcher.MainPlugin.
        if (!File.Exists(pluginZipPath))
        {
            DownloadPlugin(pluginZipPath);
        }

        try
        {
            ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        }
        catch (InvalidDataException)
        {
            File.Delete(pluginZipPath);
            DownloadPlugin(pluginZipPath);
            ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        }
        File.Delete(pluginZipPath);

        foreach (var deucalionDll in Directory.GetFiles(DependenciesDir, "deucalion*.dll"))
            File.Delete(deucalionDll);

        var patcher = new Patcher(PluginVersion, DependenciesDir);
        patcher.MainPlugin();
        patcher.LogFilePlugin();
        patcher.MemoryPlugin();
    }

    private bool NeedsUpdate(string dllPath)
    {
        if (!File.Exists(dllPath)) return true;
        try
        {
            using var plugin = new TargetAssembly(dllPath);

            if (!plugin.ApiVersionMatches())
                return true;

            var remoteVersion = TryGetRemoteVersion();
            return remoteVersion != null && remoteVersion > plugin.Version;
        }
        catch
        {
            return false;
        }
    }

    private Version? TryGetRemoteVersion()
    {
        try
        {
            if (!IsChinese)
            {
                using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var remoteVersionString = HttpClient
                                          .GetStringAsync(VersionUrlGlobal, cancelAfterDelay.Token).Result;
                return new Version(remoteVersionString.Trim());
            }

            // CN: the TundraWork release asset name carries the plugin's assembly
            // version, e.g. FFXIV_ACT_Plugin_3.0.2.1_CN.zip -> 3.0.2.1.
            using var cnCancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var request = new HttpRequestMessage(HttpMethod.Get, CnGitHubApiLatestUrl);
            request.Headers.UserAgent.ParseAdd("IINACT/1.0");
            using var response = HttpClient.Send(request, cnCancelAfterDelay.Token);
            response.EnsureSuccessStatusCode();

            using var stream = response.Content.ReadAsStream(cnCancelAfterDelay.Token);
            var json = JsonNode.Parse(stream);
            var assets = json?["assets"]?.AsArray();
            if (assets == null)
                return null;

            foreach (var asset in assets)
            {
                var name = asset?["name"]?.ToString();
                if (string.IsNullOrEmpty(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;

                var match = Regex.Match(name, @"\d+\.\d+\.\d+(?:\.\d+)?");
                if (match.Success)
                    return new Version(match.Value);
            }

            return null;
        }
        catch
        {
            // Version feed unreachable — leave the installed plugin in place.
            return null;
        }
    }

    private void DownloadPlugin(string pluginZipPath)
    {
        if (IsChinese)
        {
            // Only a CN build can parse the CN client's combat data, so a global build
            // is never substituted here as a fallback.
            string? url;
            try
            {
                url = GetGitHubReleaseZipUrl(CnGitHubApiLatestUrl);
            }
            catch (Exception ex)
            {
                throw new Exception(
                    "Failed to download the CN FFXIV_ACT_Plugin: the TundraWork/FFXIV_ACT_Plugin_CN " +
                    $"GitHub release is unavailable ({ex.Message}).");
            }

            if (string.IsNullOrEmpty(url))
                throw new Exception(
                    "Failed to download the CN FFXIV_ACT_Plugin: the TundraWork/FFXIV_ACT_Plugin_CN " +
                    "latest release has no zip asset.");

            DownloadFile(url, pluginZipPath);
            return;
        }

        try
        {
            DownloadFile(PluginUrlGlobal, pluginZipPath);
        }
        catch
        {
            // Last resort: the official GitHub release. Its API may be blocked on some
            // networks, but the updater URL above normally succeeds before we get here.
            var githubUrl = GetGitHubReleaseZipUrl(GlobalGitHubApiLatestUrl);
            if (string.IsNullOrEmpty(githubUrl))
                throw new Exception("Could not find fallback download URL from GitHub API.");

            DownloadFile(githubUrl, pluginZipPath);
        }
    }

    private string? GetGitHubReleaseZipUrl(string githubApiUrl)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, githubApiUrl);
        request.Headers.UserAgent.ParseAdd("IINACT/1.0");
        using var response = HttpClient.Send(request);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream();
        var json = JsonNode.Parse(stream);
        var assets = json?["assets"]?.AsArray();
        if (assets == null)
            return null;

        foreach (var asset in assets)
        {
            var name = asset?["name"]?.ToString();
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset?["browser_download_url"]?.ToString();
            if (!string.IsNullOrEmpty(url))
                return url;
        }

        return null;
    }

    private void DownloadFile(string url, string path)
    {
        using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var downloadStream = HttpClient
                                   .GetStreamAsync(url,
                                                   cancelAfterDelay.Token).Result;
        using var zipFileStream = new FileStream(path, FileMode.Create);
        downloadStream.CopyTo(zipFileStream);
        zipFileStream.Close();
    }
}
