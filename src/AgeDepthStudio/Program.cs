using AgeDepthStudio.CasePackages;
using AgeDepthStudio.Engine;
using AgeDepthStudio.Store;
using AgeDepthStudio.Web;

var builder = WebApplication.CreateBuilder(args);

var dataDir = Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "agedepth.db");
var packageDir = Path.Combine(builder.Environment.ContentRootPath, "CasePackages");

builder.Services.AddSingleton(_ => new EventStore(dbPath));
builder.Services.AddSingleton<StudioService>();

var app = builder.Build();

var svc = app.Services.GetRequiredService<StudioService>();
var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

// 首次启动：自带离线案例包 -> 带样例审阅页，无需账号、云服务或额外配置。
foreach (var file in Directory.GetFiles(packageDir, "*.json").OrderBy(f => f))
{
    var pkg = JsonSerializer.Deserialize<CasePackage>(File.ReadAllText(file), jsonOpts)!;
    if (!svc.Store.WorkspaceExists(pkg.WorkspaceId))
        Replayer.Replay(svc, pkg);
}

Api.Map(app, svc, dbPath, _ => packageDir);
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program { }
