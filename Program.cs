using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;
using Octokit;
using static System.Console;

const string LOCAL_PKG_LIST = "pkgs.json";

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

using var f = File.Open(LOCAL_PKG_LIST, System.IO.FileMode.OpenOrCreate);

var pkgs = (f.Length == 0 ? null : await JsonSerializer.DeserializeAsync<Dictionary<string, string>>(f)) ?? new Dictionary<string, string>();

using var h = new HttpClient();
var g = new GitHubClient(new ProductHeaderValue("apt-repo-action"));
g.Credentials = new Credentials(Token);

await Parallel.ForEachAsync(await g.Repository.GetAllForOrg(Org), async (repo, token) =>
{
    var release = await g.Repository.Release.GetLatest(repo.Id);
    var asset = release.Assets.Single(x => x.Name.Contains(".deb"));

    if (!pkgs.ContainsKey(repo.Name) || pkgs[repo.Name] != release.Name)
    {
        WriteLine($"Downloading {asset.Name}");
        var path = Path.Combine(Path.GetTempPath(), "apt-repo", asset.Name);
        using (var ds = await h.GetStreamAsync(asset.BrowserDownloadUrl))
        using (var df = File.OpenWrite(path))
        {
            await ds.CopyToAsync(df);
            await df.FlushAsync();
        }

        using var p = Process.Start(new ProcessStartInfo() {
            FileName = "/bin/bash",
            ArgumentList = {"-c", $"freight add -e {path} apt/stable"},
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
                pkgs[repo.Name] = release.Name;
            }
        }
    }
});

f.SetLength(0);
await JsonSerializer.SerializeAsync(f, pkgs, typeof(Dictionary<string, string>), new JsonSerializerOptions { WriteIndented = true });

return 0;