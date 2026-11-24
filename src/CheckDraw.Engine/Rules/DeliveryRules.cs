using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Identification;

namespace CheckDraw.Engine.Rules;

/// <summary>
/// PDF-001: contrato exige DWG+PDF → PDF ausente é não conformidade.
/// Contrato só-DWG → ausência não gera apontamento (CA-03).
/// Scans "-DC" (Desenho Conferido) são artefatos de conferência e não contam como PDF entregável.
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
            .Where(f => f.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase) &&
                        !TitleBlockIdentifier.IsCheckedScan(f.FileName))
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
                Description = $"Contrato exige PDF entregável acompanhando o desenho; nenhum PDF entregável associado a '{f.FileName}'",
                Evidence = $"Buscado PDF entregável (excluindo artefatos -DC) com base '{f.Identity.Code}' no lote",
                Severity = FindingSeverity.Error,
                FileName = f.FileName,
                DocumentCode = f.Identity.Code,
                Revision = f.Identity.Revision
            });
    }
}

/// <summary>DC-001: classifica scans '-DC' (Desenho Conferido) separados de entregáveis.</summary>
public class CheckedScanArtifactRule : IRule
{
    public string RuleId => "DC-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        var deliverables = new HashSet<string>(
            ctx.Batch.Files
                .Where(f => !TitleBlockIdentifier.IsCheckedScan(f.FileName))
                .Select(f => f.Identity.Code ?? TitleBlockIdentifier.ExtractCodeFromFileName(f.FileName))
                .Where(c => !string.IsNullOrEmpty(c))!,
            StringComparer.OrdinalIgnoreCase);

        foreach (var f in ctx.Batch.Files.Where(f => TitleBlockIdentifier.IsCheckedScan(f.FileName)))
        {
            var code = TitleBlockIdentifier.ExtractCodeFromFileName(f.FileName);
            var hasBase = !string.IsNullOrEmpty(code) && deliverables.Contains(code);
            yield return new Finding
            {
                RuleId = RuleId,
                Description = hasBase
                    ? $"Artefato de conferência (-DC) vinculado ao desenho '{code}' — abra no Viewer e clique em 'Detectar Marcas (-DC)' para ingerir marca-texto"
                    : $"Artefato de conferência (-DC) recebido sem desenho entregável correspondente ('{code}') neste lote",
                Evidence = $"Arquivo classificado por sufixo -DC: {f.FileName}",
                Severity = hasBase ? FindingSeverity.Info : FindingSeverity.Warning,
                FileName = f.FileName,
                DocumentCode = string.IsNullOrEmpty(code) ? null : code
            };
        }
    }
}

/// <summary>BOM-001: valida colunas obrigatórias e quantidades na lista de materiais extraída (RF-03).</summary>
public class BomIntegrityRule : IRule
{
    public string RuleId => "BOM-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        foreach (var f in ctx.Batch.Files.Where(x => ctx.Extractions.ContainsKey(x.Id)))
        {
            var tables = ctx.Extractions[f.Id].Tables;
            foreach (var t in tables)
            {
                var cols = new HashSet<string>(t.Columns.Select(c => c.Trim().ToLowerInvariant()));
                var missing = ctx.Contract.RequiredBomColumns
                    .Where(req => !cols.Contains(req.ToLowerInvariant()))
                    .ToList();
                if (missing.Count > 0 && cols.Count > 0)
                {
                    yield return new Finding
                    {
                        RuleId = RuleId,
                        Description = $"Tabela de materiais sem coluna(s) exigida(s) pelo contrato: {string.Join(", ", missing)}",
                        Evidence = $"Tabela '{t.Title ?? t.Handle}' possui: {string.Join(", ", t.Columns)}",
                        EntityRef = t.Handle,
                        Severity = FindingSeverity.Warning,
                        FileName = f.FileName,
                        DocumentCode = f.Identity.Code,
                        Revision = f.Identity.Revision
                    };
                }

                for (var r = 0; r < t.Rows.Count; r++)
                {
                    var row = t.Rows[r];
                    var qtyKv = row.FirstOrDefault(kv =>
                        kv.Key.Equals("qty", StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Equals("qt", StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Equals("quant", StringComparison.OrdinalIgnoreCase));
                    if (qtyKv.Key != null &&
                        (string.IsNullOrWhiteSpace(qtyKv.Value) ||
                         (double.TryParse(qtyKv.Value.Replace(',', '.'), System.Globalization.NumberStyles.Any,
                             System.Globalization.CultureInfo.InvariantCulture, out var q) && q <= 0)))
                    {
                        yield return new Finding
                        {
                            RuleId = RuleId,
                            Description = $"Linha {r + 1} da lista de materiais com quantidade inválida ou vazia ('{qtyKv.Value}')",
                            Evidence = $"Tabela '{t.Title ?? t.Handle}', linha {r + 1}",
                            EntityRef = t.Handle,
                            Severity = FindingSeverity.Error,
                            FileName = f.FileName,
                            DocumentCode = f.Identity.Code,
                            Revision = f.Identity.Revision
                        };
                    }
                }
            }
        }
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
