using System.Text.Json;
using System.Diagnostics;
using Octokit;
using static System.Console;
using System.Text.RegularExpressions;

class JsonHelper
{
    public static async Task<T?> DeserializeJson<T>(Stream? s)
    {
        if (s is null)
        {
            return default(T);
        }

        try
        {
            if (s.Length == 0)
            {
                return default(T);
            }
        }
        catch (NotSupportedException)
        {

        }
        
        return await JsonSerializer.DeserializeAsync<T>(s);
    }

    public static async Task<T?> DeserializeJson<T>(string FilePath)
    {
        using var f = File.Open(FilePath, System.IO.FileMode.OpenOrCreate);
        return await DeserializeJson<T>(f);
    }

    public static async Task SerializeJson<T>(Stream s, T obj)
    {
        await JsonSerializer.SerializeAsync(s, obj, typeof(T), new JsonSerializerOptions { WriteIndented = true });
    }

    public static async Task SerializeJson<T>(string FilePath, T obj)
    {
        using var f = File.Open(FilePath, System.IO.FileMode.OpenOrCreate);
        await SerializeJson(f, obj);
    }
}

class PkgOptions
{
    public List<string>? Releases { get; set; }
}

class PkgConfig : Dictionary<string, PkgOptions>
{
    const string LOCAL_CONFIG = ".apt-repo.json";

    public static async Task<PkgConfig> GetConfig()
    {
        var conf = await JsonHelper.DeserializeJson<PkgConfig>(LOCAL_CONFIG);
        if (conf is null)
        {
            conf = new PkgConfig();
            var opts = new PkgOptions();
            opts.Releases = new List<string>();
            opts.Releases.Add("bullseye");
            opts.Releases.Add("jammy");
            conf.Add("*", opts);
            await JsonHelper.SerializeJson(LOCAL_CONFIG, conf);
            WriteLine($"Example config file has been generated. Please adapt it to your environment and try again.");
            Environment.Exit(-3);
        }
        
        return conf;
    }

    public static async Task<PkgConfig> GetConfig(ReleaseAsset a)
    {
        using var s = await Program.DownloadFile(a.BrowserDownloadUrl);

        var conf = await JsonHelper.DeserializeJson<PkgConfig>(s);
        if (conf is null)
        {
            conf = new PkgConfig();
        }
        
        return conf;
    }

    public PkgOptions? GetOptions(string name)
    {
        PkgOptions? opts = null;
        this.TryGetValue(name, out opts);
        return opts;
    }

    public PkgOptions? GetOptions(ReleaseAsset asset)
    {
        return GetOptions(asset.Name);
    }

    public PkgOptions GetOptions(ReleaseAsset asset, PkgConfig def)
    {
        
        return GetOptions(asset) ?? def.GetOptions("*") ?? new PkgOptions();
    }

    public List<string>? GetReleases(ReleaseAsset asset, PkgConfig def)
    {
        return GetOptions(asset, def).Releases;
    }

    public bool IsExcluded(ReleaseAsset asset, PkgConfig def)
    {
        return GetReleases(asset, def)?.Count == 0;
    }
}

class Program
{
    const string LOCAL_PKG_LIST = "pkgs.json";

    static HttpClient _http = new HttpClient();
    static PkgConfig _aptconf = new PkgConfig();
    static Dictionary<string, string> _pkgs = new Dictionary<string, string>();

    public static async Task<Stream> DownloadFile(string url)
    {
        return await _http.GetStreamAsync(url);
    }

    public static async Task DownloadFile(string url, string path)
    {
        using (var ds = await DownloadFile(url))
        using (var df = File.OpenWrite(path))
        {
            await ds.CopyToAsync(df);
            await df.FlushAsync();
        }
    }

    static async Task<bool> DownloadAsset(ReleaseAsset asset)
    {
        WriteLine($"Downloading {asset.Name}");
        var path = Path.Combine(Path.GetTempPath(), "apt-repo", asset.Name);
        await DownloadFile(asset.BrowserDownloadUrl, path);

        var flag = true;
        await Parallel.ForEachAsync(_aptconf["*"].Releases ?? new List<string>(), async (release, token) => {
            using var p = Process.Start(new ProcessStartInfo() {
                FileName = "/bin/bash",
                ArgumentList = {"-c", $"freight add -e {path} apt/{release}"},
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (p is null)
            {
                WriteLine($"sh failed for {asset.Name}!");
            }
            else
            {
                await p.WaitForExitAsync();
                if (p.ExitCode != 0)
                {
                    WriteLine($"freight add failed for {asset.Name}: {(await p.StandardError.ReadToEndAsync()).TrimEnd()}");
                }
                else
                {
                    WriteLine((await p.StandardError.ReadToEndAsync()).TrimEnd());
                    return;
                }
            }

            flag = false;
        });

        return flag;
    }

    static async Task<int> Main(string[] args)
    {
        if (args.Count() != 2)
        {
            WriteLine("Usage: apt-repo <token> <org name>");
            return -1;
        }

        var Token = args[0];
        var Org = args[1];

        try
        {
            Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "apt-repo"));
        }
        catch (Exception ex)
        {
            WriteLine($"Failed to set up output folder, reason: {ex.Message}");
            return -2;
        }

        _aptconf = await PkgConfig.GetConfig();

        _pkgs = await JsonHelper.DeserializeJson<Dictionary<string, string>>(LOCAL_PKG_LIST) ?? new Dictionary<string, string>();

        var g = new GitHubClient(new ProductHeaderValue("apt-repo-action"));
        g.Credentials = new Credentials(Token);

        await Parallel.ForEachAsync(await g.Repository.GetAllForOrg(Org), async (repo, token) =>
        {
            Release release;
            try
            {
                release = await g.Repository.Release.GetLatest(repo.Id);
            }
            catch (Octokit.NotFoundException)
            {
                WriteLine($"No release was found in {repo.Name}.");
                return;
            }

            var assets = release.Assets.Where(x => x.Name.Contains(".deb"));
            var conf = new PkgConfig();
            if (release.Assets.SingleOrDefault(i => i.Name.Equals("pkg.conf")) is ReleaseAsset a)
            {
                conf = await PkgConfig.GetConfig(a);
            }

            if (!_pkgs.ContainsKey(repo.Name) || _pkgs[repo.Name] != release.Name)
            {
                bool flag = true;
                await Parallel.ForEachAsync(assets, async (a, token) =>
                {
                    if (!conf.IsExcluded(a, _aptconf) && !await DownloadAsset(a))
                    {
                        flag = false;
                    }
                });

                if (flag)
                {
                    _pkgs[repo.Name] = release.Name;
                }
            }
        });

        await JsonHelper.SerializeJson(LOCAL_PKG_LIST, _pkgs);

        return 0;
    }
}