// Static file server for local preview.
//
// The site is plain files, but it fetches JSON at runtime, and fetch() is blocked under file://.
// This serves the repository root so index.html, ./site/, ./config/ and ./data/ all resolve exactly
// as they will on a static host.

using Microsoft.Extensions.FileProviders;

var root = FindRepoRoot();
var port = args.FirstOrDefault(a => int.TryParse(a, out _)) is { } p ? int.Parse(p) : 5080;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://localhost:{port}");
builder.Logging.ClearProviders();

var app = builder.Build();

var files = new PhysicalFileProvider(root);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    ServeUnknownFileTypes = true,
    // The data files change on every scrape, so never let a browser hold on to a stale copy.
    OnPrepareResponse = ctx => ctx.Context.Response.Headers.CacheControl = "no-cache, no-store"
});

Console.WriteLine($"OctoBiS preview → http://localhost:{port}/");
Console.WriteLine($"  serving {root}");
Console.WriteLine("  Ctrl+C to stop");

app.Run();

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "index.html"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new InvalidOperationException("Could not locate the repository root (no index.html found above the executable).");
}
