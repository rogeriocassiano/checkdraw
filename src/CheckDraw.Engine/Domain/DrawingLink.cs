namespace CheckDraw.Engine.Domain;

/// <summary>
/// Vínculo conjunto→detalhe: item do conjunto (marca/balão) aponta pro desenho
/// de detalhe. RF-01 — descoberto no quadro de aplicação ou vinculado manual.
/// </summary>
public class DrawingLink
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Arquivo do conjunto (arranjo geral/isométrico).</summary>
    public string AssemblyFile { get; set; } = string.Empty;

    /// <summary>Marca/posição do item no conjunto (ex.: "07-516-001").</summary>
    public string Marca { get; set; } = string.Empty;

    /// <summary>Quantidade do item.</summary>
    public int? Qty { get; set; }

    /// <summary>Código do desenho de detalhe declarado no conjunto (ex.: AA3112M0003270).</summary>
    public string DetailCode { get; set; } = string.Empty;

    /// <summary>Arquivo detalhe encontrado no banco (null se não importado).</summary>
    public string? DetailFileName { get; set; }

    /// <summary>proposed (auto) | confirmed (humano) | ambiguous | missing</summary>
    public string Status { get; set; } = "proposed";

    /// <summary>Peso unitário declarado (kg), quando presente no quadro.</summary>
    public string? UnitWeight { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
