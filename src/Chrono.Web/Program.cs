using System.Text.Json;
using System.Text.Json.Serialization;
using Chrono.Core.Engine;
using Chrono.Core.Events;
using Chrono.Core.Materials;
using Chrono.Core.Units;
using Chrono.Storage;
using Chrono.Storage.Bundle;
using Chrono.Storage.CasePack;
using Chrono.Storage.Rendering;
using Chrono.Storage.Workflow;
using Chrono.Web.Api;

var builder = WebApplication.CreateBuilder(args);

var dbPath = Environment.GetEnvironmentVariable("CHRONO_DB")
             ?? Path.Combine(AppContext.BaseDirectory, "chrono-review.db");
var store = new ProjectStore(dbPath);
store.Initialize();

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<IChronoEngine, ChronoEngineService>();
builder.Services.AddSingleton<WorkflowService>();
builder.Services.AddSingleton<CasePackRunner>();
builder.Services.AddSingleton<ReplayReportHolder>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var jsonOptions = new JsonSerializerOptions
{
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = { new JsonStringEnumConverter() }
};

if (!store.ProjectExists())
{
    var workflow = app.Services.GetRequiredService<WorkflowService>();
    var runner = app.Services.GetRequiredService<CasePackRunner>();
    var report = runner.RunMainline();
    app.Services.GetRequiredService<ReplayReportHolder>().LastReport = report;
}

ApiEndpoints.Map(app, store, jsonOptions);

app.Run();

public partial class Program { }
