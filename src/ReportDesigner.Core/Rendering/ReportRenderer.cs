using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Model;

namespace ReportDesigner.Core.Rendering;

/// <summary>Point d'entrée : génère un PDF à partir d'un modèle et de données.</summary>
public static class ReportRenderer
{
    /// <summary>
    /// Licence QuestPDF. Community est gratuite pour les organisations dont le chiffre d'affaires
    /// annuel est inférieur à 1 M$ ; sinon, définissez QuestPDF.Settings.License avant le premier rendu.
    /// </summary>
    public static void EnsureLicense()
    {
        QuestPDF.Settings.License ??= LicenseType.Community;
    }

    public static byte[] RenderPdf(ReportTemplate template, ReportData? data = null, DateTime? now = null)
    {
        EnsureLicense();
        data ??= ReportData.FromNode(template.SampleData);
        return new ReportDocument(template, data, now).GeneratePdf();
    }

    public static void RenderPdf(ReportTemplate template, ReportData data, Stream output, DateTime? now = null)
    {
        EnsureLicense();
        new ReportDocument(template, data, now).GeneratePdf(output);
    }
}
