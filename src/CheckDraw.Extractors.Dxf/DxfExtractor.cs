using CheckDraw.Engine.Extraction;

namespace CheckDraw.Extractors.Dxf;

/// <summary>
/// Extrator DXF: mesmo modelo neutro que o futuro plugin DWG entregará.
/// TEXT/MTEXT → textos; INSERT+ATTRIB → blocos atributados (carimbos);
/// DIMENSION c/ grupo 1 não vazio → cota sobrescrita.
/// </summary>
public class DxfExtractor : IDocumentExtractor
{
    public string FormatName => "dxf";

    public bool CanRead(string path) =>
        Path.GetExtension(path).Equals(".dxf", StringComparison.OrdinalIgnoreCase);

    public ExtractionResult Extract(string path)
    {
        var result = new ExtractionResult();
        try
        {
            if (!File.Exists(path) || !DxfParser.IsDxf(path))
            {
                result.Error = "Arquivo não é um DXF válido ou está corrompido";
                return result;
            }

            var doc = DxfParser.Parse(path);
            doc.HeaderVariables.TryGetValue("$ACADVER", out var ver);
            result.DetectedVersion = ver;

            for (var i = 0; i < doc.Entities.Count; i++)
            {
                var e = doc.Entities[i];
                switch (e.Type)
                {
                    case "TEXT":
                    case "MTEXT":
                        result.Texts.Add(new ExtractedText
                        {
                            Handle = e.Str(5) ?? string.Empty,
                            Value = string.Concat(e.All(3).Concat(e.All(1))),
                            X = e.Num(10), Y = e.Num(20),
                            Layer = e.Str(8)
                        });
                        break;

                    case "INSERT":
                        var block = new ExtractedBlock
                        {
                            Handle = e.Str(5) ?? string.Empty,
                            Name = e.Str(2) ?? string.Empty,
                            X = e.Num(10), Y = e.Num(20)
                        };
                        // ATTRIBs seguem o INSERT quando o código 66 = 1, até SEQEND.
                        if (e.Str(66) == "1")
                        {
                            for (var j = i + 1; j < doc.Entities.Count; j++)
                            {
                                var next = doc.Entities[j];
                                if (next.Type == "SEQEND") { i = j; break; }
                                if (next.Type != "ATTRIB") break;
                                var tag = next.Str(2);
                                var val = next.Str(1);
                                if (tag != null && val != null)
                                {
                                    block.Attributes[tag] = val;
                                    // ATTRIB é texto posicionado: renderizável no viewer
                                    result.Texts.Add(new ExtractedText
                                    {
                                        Handle = next.Str(5) ?? string.Empty,
                                        Value = val,
                                        X = next.Num(10), Y = next.Num(20),
                                        Layer = next.Str(8)
                                    });
                                }
                            }
                        }
                        result.Blocks.Add(block);
                        break;

                    case "LINE":
                        result.Geometry.Add(new ExtractedGeometry
                        {
                            Handle = e.Str(5) ?? string.Empty,
                            Kind = "line",
                            Points = { e.Num(10), e.Num(20), e.Num(11), e.Num(21) },
                            Layer = e.Str(8)
                        });
                        break;

                    case "LWPOLYLINE":
                    case "POLYLINE":
                        var pts = e.All(10).Zip(e.All(20))
                            .SelectMany(p => new[] { double.Parse(p.First, System.Globalization.CultureInfo.InvariantCulture),
                                                     double.Parse(p.Second, System.Globalization.CultureInfo.InvariantCulture) })
                            .ToList();
                        result.Geometry.Add(new ExtractedGeometry
                        {
                            Handle = e.Str(5) ?? string.Empty,
                            Kind = "polyline",
                            Points = pts,
                            Closed = e.Num(70) > 0 && ((int)e.Num(70) & 1) == 1,
                            Layer = e.Str(8)
                        });
                        break;

                    case "CIRCLE":
                        result.Geometry.Add(new ExtractedGeometry
                        {
                            Handle = e.Str(5) ?? string.Empty,
                            Kind = "circle",
                            Points = { e.Num(10), e.Num(20) },
                            Radius = e.Num(40),
                            Layer = e.Str(8)
                        });
                        break;

                    case "DIMENSION":
                        var overrideText = e.Str(1);
                        result.Dimensions.Add(new ExtractedDimension
                        {
                            Handle = e.Str(5) ?? string.Empty,
                            StyleName = e.Str(3),
                            OverrideText = string.IsNullOrWhiteSpace(overrideText) ? null : overrideText,
                            MeasuredValue = double.TryParse(e.Str(42),
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : null,
                            X = e.Num(10), Y = e.Num(20)
                        });
                        break;
                }
            }

            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = $"Falha ao ler DXF: {ex.Message}";
        }
        return result;
    }
}
