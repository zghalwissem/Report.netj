using System.Text.Json.Nodes;
using QuestPDF.Drawing;
using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Model;
using ReportDesigner.Core.Rendering;
using ReportDesigner.Web;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o => ReportJson.Configure(o.SerializerOptions));
builder.Services.AddProblemDetails();

var templatesPath = Path.Combine(builder.Environment.ContentRootPath, builder.Configuration["ReportDesigner:TemplatesPath"] ?? "templates");
builder.Services.AddSingleton(new TemplateStore(templatesPath));

var app = builder.Build();

// Licence QuestPDF : voir README (Community gratuite sous 1 M$ de CA annuel).
ReportRenderer.EnsureLicense();

// Polices supplémentaires : déposez des fichiers .ttf/.otf dans le dossier « fonts ».
var fontsPath = Path.Combine(app.Environment.ContentRootPath, app.Configuration["ReportDesigner:FontsPath"] ?? "fonts");
if (Directory.Exists(fontsPath))
    FontManager.RegisterFontsFromDirectory(fontsPath);

app.UseExceptionHandler();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------- Modèles ----------
api.MapGet("/templates", (TemplateStore store) => store.List());

api.MapGet("/templates/{name}", async (string name, TemplateStore store, CancellationToken ct) =>
    !TemplateStore.IsValidName(name) ? Results.BadRequest()
    : await store.LoadAsync(name, ct) is { } template ? Results.Ok(template) : Results.NotFound());

api.MapPut("/templates/{name}", async (string name, ReportTemplate template, TemplateStore store, CancellationToken ct) =>
{
    if (!TemplateStore.IsValidName(name))
        return Results.Problem("Nom invalide : lettres, chiffres, « - » et « _ » uniquement.", statusCode: 400);
    await store.SaveAsync(name, template, ct);
    return Results.Ok(new { name });
});

api.MapDelete("/templates/{name}", (string name, TemplateStore store) =>
    !TemplateStore.IsValidName(name) ? Results.BadRequest()
    : store.Delete(name) ? Results.NoContent() : Results.NotFound());

// ---------- Rendu ----------

// Aperçu depuis le designer : modèle (non enregistré) + données.
api.MapPost("/render", (RenderRequest request) =>
    Render(request.Template, request.Data ?? request.Template.SampleData));

// Génération d'un rapport enregistré, à appeler depuis vos applications :
//   POST /api/reports/facture   corps = [ {...}, ... ]  ou  { "parameters": {...}, "rows": [...] }
api.MapPost("/reports/{name}", async (string name, HttpRequest http, TemplateStore store, CancellationToken ct) =>
{
    if (!TemplateStore.IsValidName(name) || await store.LoadAsync(name, ct) is not { } template) return Results.NotFound();
    var body = await new StreamReader(http.Body).ReadToEndAsync(ct);
    JsonNode? data;
    try { data = string.IsNullOrWhiteSpace(body) ? template.SampleData : JsonNode.Parse(body); }
    catch (Exception ex) { return Results.Problem($"JSON invalide : {ex.Message}", statusCode: 400); }
    return Render(template, data, download: http.Query.ContainsKey("download"));
});

// Même chose avec les données d'exemple du modèle (pratique pour un lien direct).
api.MapGet("/reports/{name}", async (string name, bool? download, TemplateStore store, CancellationToken ct) =>
    !TemplateStore.IsValidName(name) || await store.LoadAsync(name, ct) is not { } template
        ? Results.NotFound()
        : Render(template, template.SampleData, download ?? false));

app.Run();

static IResult Render(ReportTemplate template, JsonNode? data, bool download = false)
{
    try
    {
        var pdf = ReportRenderer.RenderPdf(template, ReportData.FromNode(data));
        return Results.File(pdf, "application/pdf", download ? $"{template.Name}.pdf" : null);
    }
    catch (Exception ex) when (ex is InvalidDataException or ReportDesigner.Core.Expressions.ExpressionException or QuestPDF.Drawing.Exceptions.DocumentLayoutException or System.Text.Json.JsonException)
    {
        return Results.Problem(ex.Message, title: "Impossible de générer le rapport", statusCode: 422);
    }
}

public sealed record RenderRequest(ReportTemplate Template, JsonNode? Data);

public partial class Program;
