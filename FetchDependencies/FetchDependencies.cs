using System.IO.Compression;
using System.Text.Json.Nodes;

namespace FetchDependencies;

public class FetchDependencies
{
    private const string VersionUrlGlobal = "https://www.iinact.com/updater/version";
    private const string VersionUrlChinese = "https://cninact.diemoe.net/CN解析/版本.txt";
    private const string PluginUrlGlobal = "https://www.iinact.com/updater/download";
    private const string PluginUrlChinese = "https://cninact.diemoe.net/CN解析/FFXIV_ACT_Plugin.dll";
    private const string GitHubApiLatestUrl = "https://api.github.com/repos/ravahn/FFXIV_ACT_Plugin/releases/latest";

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

        // A stale or corrupted leftover zip must not short-circuit a fresh download.
        if (File.Exists(pluginZipPath))
            File.Delete(pluginZipPath);

        if (IsChinese)
        {
            // The CN mirror serves the CN-patched FFXIV_ACT_Plugin.dll as a raw PE file,
            // NOT a zip, so it must be written straight over FFXIV_ACT_Plugin.dll. Its
            // Logfile/Memory satellites are embedded in that single file as Costura
            // resources and are unpacked by Patcher.MainPlugin below. A global build
            // must never be substituted here — it cannot parse the CN client's data.
            if (!TryDownloadFrom(PluginUrlChinese, pluginPath, IsValidPe))
            {
                // CN mirror unreachable and no usable CN build on disk yet: fall back to
                // the global zip so the plugin still loads. Combat data will not parse
                // until the mirror is back and a CN dll has been installed.
                InstallGlobalZip(pluginZipPath);
            }
        }
        else
        {
            InstallGlobalZip(pluginZipPath);
        }

        foreach (var deucalionDll in Directory.GetFiles(DependenciesDir, "deucalion*.dll"))
            File.Delete(deucalionDll);

        var patcher = new Patcher(PluginVersion, DependenciesDir);
        patcher.MainPlugin();
        patcher.LogFilePlugin();
        patcher.MemoryPlugin();
    }

    private void InstallGlobalZip(string pluginZipPath)
    {
        DownloadGlobalPlugin(pluginZipPath);

        try
        {
            ZipFile.ExtractToDirectory(pluginZipPath, DependenciesDir, true);
        }
        finally
        {
            File.Delete(pluginZipPath);
        }
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
        // Only compare against the version feed of the running region: the CN mirror
        // and the official updater number their builds independently, so mixing the
        // two schemes would spuriously trigger or suppress updates.
        var url = IsChinese ? VersionUrlChinese : VersionUrlGlobal;

        try
        {
            using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var remoteVersionString = HttpClient
                                      .GetStringAsync(url, cancelAfterDelay.Token).Result;
            return new Version(remoteVersionString.Trim());
        }
        catch
        {
            // Region feed unreachable — leave the installed plugin in place.
            return null;
        }
    }

    private void DownloadGlobalPlugin(string pluginZipPath)
    {
        if (TryDownloadFrom(PluginUrlGlobal, pluginZipPath, IsValidZip))
            return;

        // Last resort: the official GitHub release. Its API may be blocked on some
        // networks, but the mirror URL above normally succeeds before we get here.
        string? githubUrl;
        try
        {
            githubUrl = GetGitHubReleaseDownloadUrl();
        }
        catch (Exception ex)
        {
            throw new Exception(
                $"Failed to download FFXIV_ACT_Plugin: the mirror sources were unavailable and the " +
                $"GitHub API fallback also failed ({ex.Message}). Tried: {PluginUrlGlobal}.");
        }

        if (githubUrl != null && TryDownloadFrom(githubUrl, pluginZipPath, IsValidZip))
            return;

        throw new Exception(
            $"Failed to download a valid FFXIV_ACT_Plugin archive from any source. " +
            $"Tried: {PluginUrlGlobal}, {githubUrl ?? "GitHub release (no zip asset)"}.");
    }

    private bool TryDownloadFrom(string url, string path, Func<string, bool> isValid)
    {
        try
        {
            DownloadFile(url, path);

            if (!isValid(path))
                throw new InvalidDataException(
                    $"The file downloaded from {url} is not a valid file (the server may have " +
                    $"returned an error page).");

            return true;
        }
        catch
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Ignore cleanup failures.
            }

            return false;
        }
    }

    private static bool IsValidZip(string path)
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            return archive.Entries.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidPe(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            // The CN build is a ~3 MB DLL; anything tiny is a server error page, and a
            // truncated transfer would only be caught later by the patcher.
            if (stream.Length < 1_000_000)
                return false;

            var magic = new byte[2];
            if (stream.Read(magic, 0, 2) != 2)
                return false;

            return magic[0] == (byte)'M' && magic[1] == (byte)'Z';
        }
        catch
        {
            return false;
        }
    }

    private string? GetGitHubReleaseDownloadUrl()
    {
        using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, GitHubApiLatestUrl);
        request.Headers.UserAgent.ParseAdd("IINACT/1.0");
        using var response = HttpClient.Send(request, cancelAfterDelay.Token);
        response.EnsureSuccessStatusCode();

        using var stream = response.Content.ReadAsStream(cancelAfterDelay.Token);
        var json = JsonNode.Parse(stream);
        var assets = json?["assets"]?.AsArray();
        if (assets == null)
            return null;

        // Prefer the FFXIV_ACT_Plugin zip asset; fall back to any zip, in case the
        // asset order/names change upstream.
        string? fallback = null;
        foreach (var asset in assets)
        {
            var name = asset?["name"]?.ToString();
            if (string.IsNullOrEmpty(name) || !name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset?["browser_download_url"]?.ToString();
            if (url == null)
                continue;

            if (name.Contains("FFXIV_ACT_Plugin", StringComparison.OrdinalIgnoreCase))
                return url;

            fallback ??= url;
        }

        return fallback;
    }

    private void DownloadFile(string url, string path)
    {
        using var cancelAfterDelay = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var downloadStream = HttpClient
                                   .GetStreamAsync(url,
                                                   cancelAfterDelay.Token).Result;
        using var zipFileStream = new FileStream(path, FileMode.Create);
        downloadStream.CopyTo(zipFileStream);
        zipFileStream.Close();
    }
}
