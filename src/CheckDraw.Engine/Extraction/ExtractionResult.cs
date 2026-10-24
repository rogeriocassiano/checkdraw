namespace CheckDraw.Engine.Extraction;

/// <summary>
/// Contrato de extração: qualquer fonte (DWG via plugin AutoCAD, DXF, PDF)
/// devolve este modelo neutro. O Engine nunca fala com a API do CAD.
/// </summary>
public interface IDocumentExtractor
{
    /// <summary>Formato que o extrator atende: "dxf", "dwg", "pdf".</summary>
    string FormatName { get; }

    bool CanRead(string path);

    /// <summary>Nunca lança para arquivo ilegível: devolve Success=false (CA-01).</summary>
    ExtractionResult Extract(string path);
}

public class ExtractionResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }

    /// <summary>Versão/formato interno detectado pelo extrator (ex.: "AC1032").</summary>
    public string? DetectedVersion { get; set; }

    public List<ExtractedText> Texts { get; set; } = new();
    public List<ExtractedBlock> Blocks { get; set; } = new();
    public List<ExtractedTable> Tables { get; set; } = new();
    public List<ExtractedDimension> Dimensions { get; set; } = new();
    public List<ExtractedGeometry> Geometry { get; set; } = new();
}

/// <summary>Geometria vetorial para visualização (linhas, polilinhas, círculos).</summary>
public class ExtractedGeometry
{
    public string Handle { get; set; } = string.Empty;
    /// <summary>"line" | "polyline" | "circle"</summary>
    public string Kind { get; set; } = string.Empty;
    /// <summary>Coordenadas achatadas [x1,y1,x2,y2,...]; para círculo = centro.</summary>
    public List<double> Points { get; set; } = new();
    public double? Radius { get; set; }
    /// <summary>Polilinha fechada.</summary>
    public bool Closed { get; set; }
    public string? Layer { get; set; }
}

public class ExtractedText
{
    public string Handle { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public string? Layer { get; set; }
}

public class ExtractedBlock
{
    public string Handle { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>Tag do atributo → valor (carimbos são normalmente blocos atributados).</summary>
    public Dictionary<string, string> Attributes { get; set; } = new();
}

public class ExtractedTable
{
    public string Handle { get; set; } = string.Empty;
    public string? Title { get; set; }
    public List<string> Columns { get; set; } = new();

    /// <summary>Linhas: coluna → valor bruto.</summary>
    public List<Dictionary<string, string>> Rows { get; set; } = new();
}

public class ExtractedDimension
{
    public string Handle { get; set; } = string.Empty;
    public string? StyleName { get; set; }

    /// <summary>Texto explícito; se não vazio e diferente da medida → cota sobrescrita (RF-02).</summary>
    public string? OverrideText { get; set; }
    public double? MeasuredValue { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
}
