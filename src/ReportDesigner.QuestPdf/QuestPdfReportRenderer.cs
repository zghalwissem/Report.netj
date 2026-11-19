using QuestPDF.Fluent;
using QuestPDF.Infrastructure;
using ReportDesigner.Core;
using ReportDesigner.Core.Data;
using ReportDesigner.Core.Model;

namespace ReportDesigner.QuestPdf;

/// <summary>
/// Moteur historique basé sur QuestPDF, conservé en option (comparaison, migration).
/// Licence QuestPDF : Community gratuite sous 1 M$ de chiffre d'affaires annuel ; sinon, définissez
/// QuestPDF.Settings.License avant le premier rendu.
/// </summary>
public sealed class QuestPdfReportRenderer : IReportRenderer
{
    public QuestPdfReportRenderer()
    {
        QuestPDF.Settings.License ??= LicenseType.Community;
    }

    public void Render(ReportTemplate template, ReportData data, Stream output, DateTime? now = null) =>
        new ReportDocument(template, data, now).GeneratePdf(output);
}
