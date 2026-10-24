namespace CheckDraw.Extractors.Dxf;

/// <summary>Par (código de grupo, valor) lido de um DXF.</summary>
internal readonly struct DxfPair
{
    public int Code { get; }
    public string Value { get; }
    public DxfPair(int code, string value) { Code = code; Value = value; }
}

/// <summary>Entidade DXF crua: tipo + pares código/valor.</summary>
internal class DxfEntity
{
    public string Type { get; set; } = string.Empty;
    public List<DxfPair> Pairs { get; } = new();

    public string? Str(int code) =>
        Pairs.Where(p => p.Code == code).Select(p => p.Value).FirstOrDefault();

    public IEnumerable<string> All(int code) =>
        Pairs.Where(p => p.Code == code).Select(p => p.Value);

    public double Num(int code) =>
        double.TryParse(Str(code), System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}

/// <summary>
/// Parser mínimo de DXF: lê pares código/valor e agrupa entidades por seção.
/// Suficiente para TEXT/MTEXT/INSERT/ATTRIB/DIMENSION em ENTITIES e BLOCKS.
/// </summary>
internal static class DxfParser
{
    public static bool IsDxf(string path)
    {
        using var reader = new StreamReader(path);
        for (var i = 0; i < 8; i++)
        {
            var line = reader.ReadLine();
            if (line == null) return false;
            if (line.Trim() == "SECTION") return true;
        }
        return false;
    }

    /// <summary>Devolve entidades agrupadas por seção (HEADER vira dicionário de variáveis).</summary>
    public static DxfDocument Parse(string path)
    {
        var doc = new DxfDocument();
        using var reader = new StreamReader(path);

        var stack = new List<DxfPair>();
        while (ReadPair(reader) is { } pair) stack.Add(pair);

        string? section = null;
        for (var i = 0; i < stack.Count; i++)
        {
            var p = stack[i];
            if (p.Code == 0 && p.Value == "SECTION" && i + 1 < stack.Count && stack[i + 1].Code == 2)
            {
                section = stack[++i].Value;
                continue;
            }
            if (p.Code == 0 && (p.Value == "ENDSEC" || p.Value == "EOF")) { section = null; continue; }
            if (section == null) continue;

            if (section == "HEADER" && p.Code == 9)
            {
                // variável $NOME seguida do(s) valor(es)
                if (i + 1 < stack.Count) doc.HeaderVariables[p.Value] = stack[++i].Value;
                continue;
            }

            if (p.Code == 0)
            {
                var entity = new DxfEntity { Type = p.Value };
                for (var j = i + 1; j < stack.Count && stack[j].Code != 0; j++)
                {
                    entity.Pairs.Add(stack[j]);
                    i = j;
                }
                doc.Add(section, entity);
            }
        }
        return doc;
    }

    private static DxfPair? ReadPair(StreamReader reader)
    {
        var codeLine = reader.ReadLine();
        if (codeLine == null) return null;
        var value = reader.ReadLine() ?? string.Empty;
        return int.TryParse(codeLine.Trim(), out var code) ? new DxfPair(code, value) : null;
    }
}

internal class DxfDocument
{
    public Dictionary<string, string> HeaderVariables { get; } = new();
    public List<DxfEntity> Entities { get; } = new();
    public List<DxfEntity> BlockEntities { get; } = new();

    public void Add(string section, DxfEntity e)
    {
        if (section == "ENTITIES") Entities.Add(e);
        else if (section == "BLOCKS") BlockEntities.Add(e);
    }
}
