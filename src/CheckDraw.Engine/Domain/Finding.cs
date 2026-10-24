namespace CheckDraw.Engine.Domain;

/// <summary>
/// Apontamento. Regra de qualidade do produto: toda detecção diz o que verificou,
/// onde está a evidência, qual regra aplicou e se é objetiva ou suspeita (humano decide).
/// </summary>
public class Finding
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Identificador da regra que gerou (ex.: "DUP-001") ou "AI-..." para IA.</summary>
    public string RuleId { get; set; } = string.Empty;

    /// <summary>O que foi verificado / o que foi encontrado.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Onde está a evidência (entidade, região, campo, arquivo).</summary>
    public string Evidence { get; set; } = string.Empty;

    /// <summary>Referência à entidade CAD (handle) para localizar a origem no desenho (RF-02).</summary>
    public string? EntityRef { get; set; }

    public FindingSeverity Severity { get; set; }
    public FindingSource Source { get; set; } = FindingSource.Rule;
    public FindingKind Kind { get; set; } = FindingKind.Objective;

    /// <summary>Confiança quando a origem é IA; nulo para regras.</summary>
    public double? Confidence { get; set; }

    /// <summary>Código do documento afetado (quando identificado).</summary>
    public string? DocumentCode { get; set; }
    public string? Revision { get; set; }

    /// <summary>Arquivo de origem no lote.</summary>
    public string? FileName { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? CreatedBy { get; set; }

    /// <summary>Veredito do revisor humano: Open → Confirmed/Dismissed/Corrected.</summary>
    public ReviewStatus ReviewStatus { get; set; } = ReviewStatus.Open;

    /// <summary>Região do problema no desenho (unidades do desenho ou px do scan).</summary>
    public double? BBoxX { get; set; }
    public double? BBoxY { get; set; }
    public double? BBoxW { get; set; }
    public double? BBoxH { get; set; }

    /// <summary>Correção proposta (pela IA ou regra) — humano aprova antes de aplicar.</summary>
    public string? SuggestedFix { get; set; }
}
