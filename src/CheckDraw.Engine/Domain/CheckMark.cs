namespace CheckDraw.Engine.Domain;

/// <summary>
/// Marca de conferência do profissional sobre uma região do desenho (RF-05 por
/// item): a versão digital do marca-texto. Vinculada a arquivo/revisão, usuário e data.
/// </summary>
public class CheckMark
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string FileName { get; set; } = string.Empty;
    public string? DocumentCode { get; set; }
    public string? Revision { get; set; }

    /// <summary>Região marcada em unidades do desenho (mm no DXF/DWG, px no scan).</summary>
    public double X { get; set; }
    public double Y { get; set; }
    public double W { get; set; }
    public double H { get; set; }

    /// <summary>CheckedOk (amarelo) | NeedsCorrection (vermelho) | NeedsRevalidation (laranja).</summary>
    public CheckState State { get; set; } = CheckState.CheckedOk;

    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string? User { get; set; }
}
