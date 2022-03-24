using System.Text.Json;
using System.Text.Json.Serialization;
using Octokit;

const string LOCAL_PKG_LIST = "pkgs.json";

if (args.Count() != 3)
{
    Console.WriteLine("Usage: apt-repo <token> <org name> <output dir>");
    return -1;
}
else
{
    return await QueryRepos(args[0], args[1], args[2]);
}

async Task<int> QueryRepos(string Token, string Org, string Output)
{
    try
    {
        Directory.CreateDirectory(Path.Combine(Output, "pool"));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Failed to set up output folder, reason: {ex.Message}");
        return -2;
    }

    using var f = File.Open(Path.Combine(Output, LOCAL_PKG_LIST), System.IO.FileMode.OpenOrCreate);
    
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
            Console.WriteLine($"Downloading {asset.Name}");
            using var ds = await h.GetStreamAsync(asset.BrowserDownloadUrl);
            using var df = File.OpenWrite(Path.Combine(Output, "pool", asset.Name));
            await ds.CopyToAsync(df);
            pkgs[repo.Name] = release.Name;
        }
    });

    f.SetLength(0);
    await JsonSerializer.SerializeAsync(f, pkgs, typeof(Dictionary<string, string>), new JsonSerializerOptions { WriteIndented = true });

    return 0;
}