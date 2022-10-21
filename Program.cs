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
        using var f = File.Open(FilePath, System.IO.FileMode.Create);
        await SerializeJson(f, obj);
        await f.FlushAsync();
    }
}

class PkgOption
{
    public List<string>? Releases { get; set; }
    public string? Subpool { get; set; }
}

class PkgConfig : Dictionary<string, PkgOption>
{
    const string LOCAL_CONFIG = ".apt-repo.json";

    public static async Task<PkgConfig> GetConfig()
    {
        var conf = await JsonHelper.DeserializeJson<PkgConfig>(LOCAL_CONFIG);
        if (conf is null)
        {
            conf = new PkgConfig();
            var opts = new PkgOption();
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

    public PkgOption? GetOption(string name)
    {
        PkgOption? opts = null;
        this.TryGetValue(name, out opts);
        return opts;
    }

    public PkgOption? GetOption(ReleaseAsset asset)
    {
        return GetOption(asset.Name) ?? GetOption("*");
    }

    public PkgOption GetOption(ReleaseAsset asset, PkgConfig def)
    {
        
        return GetOption(asset) ?? def.GetOption(asset) ?? new PkgOption();
    }

    public List<string>? GetReleases(ReleaseAsset asset, PkgConfig def)
    {
        return GetOption(asset, def).Releases;
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
        try
        {
            return await _http.GetStreamAsync(url);
        }
        catch (System.Net.Http.HttpRequestException ex)
        {
            WriteLine($"Failed to download '{url}' with error message: {ex.Message}");
            throw ex;
        }
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

    static async Task<bool> DownloadAsset(ReleaseAsset asset, PkgConfig conf)
    {
        WriteLine($"Downloading {asset.Name}");
        var path = Path.Combine(Path.GetTempPath(), "apt-repo", asset.Name);
        await DownloadFile(asset.BrowserDownloadUrl, path);

        var flag = true;
        var opt = conf.GetOption(asset, _aptconf);
        await Parallel.ForEachAsync(opt.Releases ?? new List<string>(), async (release, token) =>
        {
            using var p = Process.Start(new ProcessStartInfo() {
                FileName = "/bin/bash",
                ArgumentList = {"-c", $"freight add -e {path} {(opt.Subpool is null ? string.Empty : $"-s {opt.Subpool}")} apt/{release}"},
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
                    if (!conf.IsExcluded(a, _aptconf) && !await DownloadAsset(a, conf))
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