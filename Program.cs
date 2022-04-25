using System.Text.Json;
using System.Diagnostics;
using Octokit;
using static System.Console;

class JsonHelper
{
    public static async Task<T?> DeserializeJson<T>(string FilePath)
    {
        using var f = File.Open(FilePath, System.IO.FileMode.OpenOrCreate);
        return f.Length == 0 ? default(T) : await JsonSerializer.DeserializeAsync<T>(f);
    }

    public static async Task SerializeJson<T>(string FilePath, T Object)
    {
        using var f = File.Open(FilePath, System.IO.FileMode.OpenOrCreate);
        await JsonSerializer.SerializeAsync(f, Object, typeof(T), new JsonSerializerOptions { WriteIndented = true });
    }
}

class Config
{
    const string LOCAL_CONFIG = ".apt-repo.json";
    
    public Config()
    {
        Releases = new List<string>();
    }

    public static async Task<Config> GetConfig()
    {
        var conf = await JsonHelper.DeserializeJson<Config>(LOCAL_CONFIG);
        if (conf is null)
        {
            conf = new Config();
            conf.Releases.Add("stable");
            await JsonHelper.SerializeJson(LOCAL_CONFIG, conf);
            WriteLine($"Example config file has been generated. Please adapt it to your environment and try again.");
            Environment.Exit(-3);
        }
        
        return conf;
    }

    public List<string> Releases { get; set; }
}

class Program
{
    const string LOCAL_PKG_LIST = "pkgs.json";

    static HttpClient _http = new HttpClient();
    static Config _conf = new Config();
    static Dictionary<string, string> _pkgs = new Dictionary<string, string>();

    static async Task<bool> DownloadAsset(ReleaseAsset asset)
    {
        WriteLine($"Downloading {asset.Name}");
        var path = Path.Combine(Path.GetTempPath(), "apt-repo", asset.Name);
        using (var ds = await _http.GetStreamAsync(asset.BrowserDownloadUrl))
        using (var df = File.OpenWrite(path))
        {
            await ds.CopyToAsync(df);
            await df.FlushAsync();
        }

        var flag = true;
        await Parallel.ForEachAsync(_conf.Releases, async (release, token) => {
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

        _conf = await Config.GetConfig();

        _pkgs = await JsonHelper.DeserializeJson<Dictionary<string, string>>(LOCAL_PKG_LIST) ?? new Dictionary<string, string>();

        var g = new GitHubClient(new ProductHeaderValue("apt-repo-action"));
        g.Credentials = new Credentials(Token);

        await Parallel.ForEachAsync(await g.Repository.GetAllForOrg(Org), async (repo, token) =>
        {
            var release = await g.Repository.Release.GetLatest(repo.Id);
            var assets = release.Assets.Where(x => x.Name.Contains(".deb"));

            if (!_pkgs.ContainsKey(repo.Name) || _pkgs[repo.Name] != release.Name)
            {
                bool flag = true;
                await Parallel.ForEachAsync(assets, async (a, token) =>
                {
                    if (!await DownloadAsset(a))
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