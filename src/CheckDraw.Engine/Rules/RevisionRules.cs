using CheckDraw.Engine.Domain;

namespace CheckDraw.Engine.Rules;

/// <summary>REV-001: mesmo código recebido em mais de uma revisão no lote.</summary>
public class MultipleRevisionsRule : IRule
{
    public string RuleId => "REV-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx) =>
        ctx.Batch.Files
            .Where(f => f.Identity.Status == IdentificationStatus.Identified && f.Identity.Code != null)
            .GroupBy(f => f.Identity.Code!)
            .Where(g => g.Select(f => f.Identity.Revision).Distinct().Count() > 1)
            .SelectMany(g => g.Select(f => new Finding
            {
                RuleId = RuleId,
                Description = $"Documento '{g.Key}' recebido nas revisões {string.Join(", ", g.Select(x => x.Identity.Revision).OrderBy(r => r))} — verificar qual está autorizada",
                Evidence = $"Arquivos: {string.Join(", ", g.Select(x => $"{x.FileName} (Rev. {x.Identity.Revision})"))}",
                Severity = FindingSeverity.Warning,
                FileName = f.FileName,
                DocumentCode = g.Key,
                Revision = f.Identity.Revision
            }));
}

/// <summary>ML-001: documento esperado na lista mestra não foi recebido.</summary>
public class MasterListMissingRule : IRule
{
    public string RuleId => "ML-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx)
    {
        var received = new HashSet<string>(
            ctx.Batch.Files
                .Where(f => f.Identity.Status == IdentificationStatus.Identified && f.Identity.Code != null)
                .Select(f => f.Identity.Code!),
            StringComparer.OrdinalIgnoreCase);

        return ctx.MasterList
            .Where(m => !received.Contains(m.Code))
            .Select(m => new Finding
            {
                RuleId = RuleId,
                Description = $"Documento esperado '{m.Code}' (Rev. {m.ExpectedRevision}) não consta no lote recebido",
                Evidence = $"Lista mestra: {m.Code} — {m.Title}",
                Severity = FindingSeverity.Error,
                DocumentCode = m.Code,
                Revision = m.ExpectedRevision
            });
    }
}

/// <summary>ML-002: revisão recebida diverge da esperada na lista mestra.</summary>
public class MasterListRevisionMismatchRule : IRule
{
    public string RuleId => "ML-002";

    public IEnumerable<Finding> Evaluate(RuleContext ctx) =>
        ctx.Batch.Files
            .Where(f => f.Identity.Status == IdentificationStatus.Identified && f.Identity.Code != null)
            .Join(ctx.MasterList,
                f => f.Identity.Code!.ToUpperInvariant(),
                m => m.Code.ToUpperInvariant(),
                (f, m) => (f, m))
            .Where(x => !string.Equals(x.f.Identity.Revision, x.m.ExpectedRevision, StringComparison.OrdinalIgnoreCase))
            .Select(x => new Finding
            {
                RuleId = RuleId,
                Description = $"Revisão recebida '{x.f.Identity.Revision}' diverge da lista mestra ('{x.m.ExpectedRevision}') para '{x.m.Code}'",
                Evidence = $"Arquivo {x.f.FileName}; lista mestra espera Rev. {x.m.ExpectedRevision}",
                Severity = FindingSeverity.Warning,
                FileName = x.f.FileName,
                DocumentCode = x.m.Code,
                Revision = x.f.Identity.Revision
            });
}
