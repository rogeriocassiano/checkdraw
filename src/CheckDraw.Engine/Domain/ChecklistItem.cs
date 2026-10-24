namespace CheckDraw.Engine.Domain;

/// <summary>
/// Item do checklist de conferência (evolução do ContractProfile — inspirado
/// no modelo rev_cad): cada item é um critério verificável com severidade.
/// Regras determinísticas e IA reportam apontamentos contra esses códigos.
/// </summary>
public class ChecklistItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ContractProfileId { get; set; }

    /// <summary>Código estável do critério (ex.: CAR-01, REV-01, BOM-02) — casa com Finding.RuleId.</summary>
    public string Code { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;      // carimbo | revisao | bom | cotas | entrega
    public string Description { get; set; } = string.Empty;  // "revisão bate com a lista mestra"
    public string? Expected { get; set; }                    // valor/padrão esperado, quando aplicável
    public FindingSeverity Severity { get; set; } = FindingSeverity.Warning;
    public bool AiCheckable { get; set; }                    // se a IA pode avaliar (senão, só regra)
}
