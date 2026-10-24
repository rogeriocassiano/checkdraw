using CheckDraw.Engine.Domain;

namespace CheckDraw.Engine.Rules;

/// <summary>FILE-001: arquivo ilegível sinalizado sem interromper o lote (CA-01).</summary>
public class CorruptFileRule : IRule
{
    public string RuleId => "FILE-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx) =>
        ctx.Batch.Files.Where(f => f.Health == FileHealth.Corrupt).Select(f => new Finding
        {
            RuleId = RuleId,
            Description = $"Arquivo '{f.FileName}' ilegível ou corrompido: {f.HealthDetail}",
            Evidence = f.FullPath,
            Severity = FindingSeverity.Error,
            FileName = f.FileName,
            DocumentCode = f.Identity.Code,
            Revision = f.Identity.Revision
        });
}

/// <summary>FILE-002: arquivos com conteúdo idêntico (mesmo hash SHA-256).</summary>
public class DuplicateHashRule : IRule
{
    public string RuleId => "FILE-002";

    public IEnumerable<Finding> Evaluate(RuleContext ctx) =>
        ctx.Batch.Files
            .Where(f => !string.IsNullOrEmpty(f.Sha256))
            .GroupBy(f => f.Sha256)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g.Select(f => new Finding
            {
                RuleId = RuleId,
                Description = $"Conteúdo idêntico a outro(s) {g.Count() - 1} arquivo(s) do lote",
                Evidence = $"SHA-256: {g.Key.Substring(0, 16)}…; arquivos: {string.Join(", ", g.Select(x => x.FileName))}",
                Severity = FindingSeverity.Warning,
                FileName = f.FileName,
                DocumentCode = f.Identity.Code,
                Revision = f.Identity.Revision
            }));
}

/// <summary>ID-001: carimbo fora dos padrões configurados → incerteza declarada (CA-02).</summary>
public class UnidentifiedFileRule : IRule
{
    public string RuleId => "ID-001";

    public IEnumerable<Finding> Evaluate(RuleContext ctx) =>
        ctx.Batch.Files
            .Where(f => f.Extension is ".dxf" or ".dwg" &&
                        f.Health == FileHealth.Ok &&
                        f.Identity.Status == IdentificationStatus.Unidentified)
            .Select(f => new Finding
            {
                RuleId = RuleId,
                Description = $"Carimbo de '{f.FileName}' fora dos padrões do contrato — identificação incerta, exige conferência manual",
                Evidence = f.Identity.EvidenceText is { Length: > 0 } e
                    ? e
                    : "Nenhum bloco de carimbo compatível encontrado",
                Severity = FindingSeverity.Warning,
                FileName = f.FileName
            });
}
