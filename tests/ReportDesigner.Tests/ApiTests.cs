using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;
using ReportDesigner.Core;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Tests;

public sealed class ApiTests : IClassFixture<WebApplicationFactory<Program>>, IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "rd-tests-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.WithWebHostBuilder(b => b.UseSetting("ReportDesigner:TemplatesPath", _directory)).CreateClient();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static ReportTemplate Sample() => new()
    {
        Title = "Test API",
        Bands = [new() { Type = BandType.Detail, Height = 20, Elements = [new() { Text = "{nom}", Width = 200, Height = 18 }] }],
        SampleData = System.Text.Json.Nodes.JsonNode.Parse("""[ { "nom": "Alice" } ]"""),
    };

    private static StringContent Json(object value) =>
        new(System.Text.Json.JsonSerializer.Serialize(value, ReportJson.Options), Encoding.UTF8, "application/json");

    [Fact]
    public async Task Save_list_load_render_and_delete_template()
    {
        var put = await _client.PutAsync("/api/templates/essai", Json(Sample()));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        var list = await _client.GetFromJsonAsync<List<TemplateInfoDto>>("/api/templates");
        Assert.Contains(list!, t => t.Name == "essai" && t.Title == "Test API");

        var loaded = ReportJson.Deserialize(await _client.GetStringAsync("/api/templates/essai"));
        Assert.Equal(BandType.Detail, loaded.Bands.Single().Type);

        var pdf = await _client.PostAsync("/api/reports/essai", new StringContent("""[ { "nom": "Bob" } ]""", Encoding.UTF8, "application/json"));
        Assert.Equal("application/pdf", pdf.Content.Headers.ContentType?.MediaType);
        Assert.StartsWith("%PDF", Encoding.ASCII.GetString(await pdf.Content.ReadAsByteArrayAsync())[..4]);

        var sample = await _client.GetAsync("/api/reports/essai?download=true");
        Assert.Equal("essai.pdf", sample.Content.Headers.ContentDisposition?.FileName);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync("/api/templates/essai")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/templates/essai")).StatusCode);
    }

    [Fact]
    public async Task Render_endpoint_returns_pdf_for_unsaved_template()
    {
        var response = await _client.PostAsync("/api/render", Json(new { template = Sample(), data = (object?)null }));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Render_reports_template_errors_as_problem_details()
    {
        var template = Sample();
        template.Bands[0].Elements[0].Text = "{$INCONNU}";
        var response = await _client.PostAsync("/api/render", Json(new { template, data = (object?)null }));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("$INCONNU", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("..%2F..%2Fetc")]
    [InlineData("nom avec espaces")]
    public async Task Rejects_invalid_template_names(string name)
    {
        var response = await _client.PutAsync($"/api/templates/{name}", Json(Sample()));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Lists_fonts_and_uses_new_engine()
    {
        Assert.Contains("Lato", await _client.GetStringAsync("/api/fonts"));
        var response = await _client.PostAsync("/api/render", Json(new { template = Sample(), data = (object?)null }));
        using var pdf = UglyToad.PdfPig.PdfDocument.Open(await response.Content.ReadAsByteArrayAsync());
        Assert.Contains("ReportDesigner.Pdf", pdf.Information.Producer);
    }

    [Fact]
    public async Task Serves_designer_page()
    {
        var html = await _client.GetStringAsync("/");
        Assert.Contains("Report Designer", html);
    }

    private sealed record TemplateInfoDto(string Name, string? Title);
}
