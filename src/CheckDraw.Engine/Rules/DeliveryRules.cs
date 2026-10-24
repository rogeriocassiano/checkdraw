using CheckDraw.Engine.Domain;

namespace CheckDraw.Engine.Rules;

/// <summary>
/// PDF-001: contrato exige DWG+PDF → PDF ausente é não conformidade.
/// Contrato só-DWG → ausência não gera apontamento (CA-03).
/// </summary>
public class PdfRequiredRule : IRule
{
    public string RuleId => "PDF-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        if (ctx.Contract.Delivery != DeliveryScenario.DrawingPlusPdf)
            return Enumerable.Empty<Finding>();

        // Associação pelo código do documento (não pelo nome do arquivo:
        // "DWG-003_RevA.dxf" casa com "DWG-003.pdf" pelo código DWG-003).
        var pdfStems = ctx.Batch.Files
            .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileNameWithoutExtension(f.FileName))
            .ToList();

        return ctx.Batch.Files
            .Where(f => f.Extension is ".dxf" or ".dwg" &&
                        f.Identity.Status == IdentificationStatus.Identified &&
                        f.Identity.Code != null &&
                        !pdfStems.Any(p => p.StartsWith(f.Identity.Code!, StringComparison.OrdinalIgnoreCase)))
            .Select(f => new Finding
            {
                RuleId = RuleId,
                Description = $"Contrato exige PDF acompanhando o desenho; nenhum PDF associado a '{f.FileName}'",
                Evidence = $"Buscado PDF com base '{Path.GetFileNameWithoutExtension(f.FileName)}' no lote",
                Severity = FindingSeverity.Error,
                FileName = f.FileName,
                DocumentCode = f.Identity.Code,
                Revision = f.Identity.Revision
            });
    }
}

/// <summary>DIM-001: cota com texto sobrescrito — apontamento objetivo com entidade (RF-02).</summary>
public class OverriddenDimensionRule : IRule
{
    public string RuleId => "DIM-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx) =>
        ctx.Batch.Files
            .Where(f => ctx.Extractions.ContainsKey(f.Id))
            .SelectMany(f => ctx.Extractions[f.Id].Dimensions
                .Where(d => !string.IsNullOrWhiteSpace(d.OverrideText))
                .Select(d => new Finding
                {
                    RuleId = RuleId,
                    Description = $"Cota com texto sobrescrito: \"{d.OverrideText}\" (medida real: {d.MeasuredValue?.ToString() ?? "n/d"})",
                    Evidence = $"Entidade DIMENSION em ({d.X:0.##}, {d.Y:0.##}), estilo '{d.StyleName}'",
                    EntityRef = d.Handle,
                    Severity = FindingSeverity.Error,
                    FileName = f.FileName,
                    DocumentCode = f.Identity.Code,
                    Revision = f.Identity.Revision
                }));
}
