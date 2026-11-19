using ReportDesigner.Core.Data;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Core;

/// <summary>Moteur capable de produire un PDF à partir d'un modèle et de données.</summary>
public interface IReportRenderer
{
    /// <param name="now">Date utilisée par {$DATE} / {$NOW} (par défaut : maintenant).</param>
    void Render(ReportTemplate template, ReportData data, Stream output, DateTime? now = null);
}

public static class ReportRendererExtensions
{
    /// <summary>Génère le PDF en mémoire. Sans données, utilise les données d'exemple du modèle.</summary>
    public static byte[] RenderPdf(this IReportRenderer renderer, ReportTemplate template, ReportData? data = null, DateTime? now = null)
    {
        using var stream = new MemoryStream();
        renderer.Render(template, data ?? ReportData.FromNode(template.SampleData), stream, now);
        return stream.ToArray();
    }
}
