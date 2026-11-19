using System.Text.RegularExpressions;
using ReportDesigner.Core;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Web;

/// <summary>Stockage des modèles de rapport sous forme de fichiers JSON dans un répertoire.</summary>
public sealed partial class TemplateStore
{
    private readonly string _directory;

    public TemplateStore(string directory)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
    }

    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public IEnumerable<TemplateInfo> List() =>
        Directory.EnumerateFiles(_directory, "*.json")
            .Select(path => new FileInfo(path))
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => new TemplateInfo(Path.GetFileNameWithoutExtension(f.Name), TryReadTitle(f.FullName), f.LastWriteTimeUtc));

    public async Task<ReportTemplate?> LoadAsync(string name, CancellationToken ct = default)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return null;
        var template = ReportJson.Deserialize(await File.ReadAllTextAsync(path, ct));
        template.Name = name;
        return template;
    }

    public async Task SaveAsync(string name, ReportTemplate template, CancellationToken ct = default)
    {
        template.Name = name;
        template.Normalize();
        var path = PathFor(name);
        var temp = path + ".tmp";
        await File.WriteAllTextAsync(temp, ReportJson.Serialize(template), ct);
        File.Move(temp, path, overwrite: true);
    }

    public bool Delete(string name)
    {
        var path = PathFor(name);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    private string PathFor(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException($"Nom de modèle invalide : {name}", nameof(name));
        return Path.Combine(_directory, name + ".json");
    }

    private static string? TryReadTitle(string path)
    {
        try { return ReportJson.Deserialize(File.ReadAllText(path)).Title; }
        catch (Exception) { return null; }
    }

    [GeneratedRegex(@"^[\p{L}\p{N}_\-]{1,80}$")]
    private static partial Regex NamePattern();
}

public sealed record TemplateInfo(string Name, string? Title, DateTime ModifiedUtc);
