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

    static HttpClient http = new HttpClient();
    static Config conf = new Config();
    static Dictionary<string, string> pkgs = new Dictionary<string, string>();

    static async Task<bool> DownloadAsset(ReleaseAsset asset)
    {
        WriteLine($"Downloading {asset.Name}");
        var path = Path.Combine(Path.GetTempPath(), "apt-repo", asset.Name);
        using (var ds = await http.GetStreamAsync(asset.BrowserDownloadUrl))
        using (var df = File.OpenWrite(path))
        {
            await ds.CopyToAsync(df);
            await df.FlushAsync();
        }

        var command = "true";
        conf.Releases.ForEach(i => {
            command += $" && freight add -e {path} apt/{i}";
        });

        using var p = Process.Start(new ProcessStartInfo() {
            FileName = "/bin/bash",
            ArgumentList = {"-c", command},
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
                return true;
            }
        }

        return false;
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

        var conf = await Config.GetConfig();

        var pkgs = await JsonHelper.DeserializeJson<Dictionary<string, string>>(LOCAL_PKG_LIST) ?? new Dictionary<string, string>();

        var g = new GitHubClient(new ProductHeaderValue("apt-repo-action"));
        g.Credentials = new Credentials(Token);

        await Parallel.ForEachAsync(await g.Repository.GetAllForOrg(Org), async (repo, token) =>
        {
            var release = await g.Repository.Release.GetLatest(repo.Id);
            var assets = release.Assets.Where(x => x.Name.Contains(".deb"));

            if (!pkgs.ContainsKey(repo.Name) || pkgs[repo.Name] != release.Name)
            {
                await Parallel.ForEachAsync(assets, async (a, token) =>
                {
                    if (await DownloadAsset(a))
                    {
                        pkgs[repo.Name] = release.Name;
                    }
                });
            }
        });

        await JsonHelper.SerializeJson(LOCAL_PKG_LIST, pkgs);

        return 0;
    }
}