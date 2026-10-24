using System.Text.Json;
using CheckDraw.Engine.Domain;

namespace CheckDraw.Cli;

/// <summary>
/// Gera corpus sintético de teste enquanto os DWGs reais não chegam.
/// Cobre os casos dos critérios de aceite: válidos, corrompido, fora de padrão,
/// duas revisões do mesmo código, duplicado por conteúdo, cota sobrescrita,
/// PDFs e lista mestra com divergências.
/// </summary>
public static class CorpusGenerator
{
    public static void Generate(string dir)
    {
        Directory.CreateDirectory(dir);
        var files = new Dictionary<string, string>();

        // Padrão A: bloco TITULO_A c/ atributos CODIGO/REV/TITULO/DISCIPLINA/FORMATO
        for (var i = 1; i <= 16; i++)
        {
            var code = $"DWG-{i:000}";
            var rev = code == "DWG-012" ? "E" : "A";
            files[$"{code}_Rev{rev}.dxf"] = StandardA(code, rev, $"Conjunto soldado {i:000}", "ESTR", "A1");
        }
        files["DWG-012_RevF.dxf"] = StandardA("DWG-012", "F", "Conjunto soldado 012", "ESTR", "A1");

        // DWG-005 com cota sobrescrita (DIM-001)
        files["DWG-005_RevA.dxf"] = StandardA("DWG-005", "A", "Conjunto soldado 005", "ESTR", "A1",
            extraEntities: OverriddenDimension());

        // Padrão B: bloco TITULO_B c/ tags diferentes (NUMERO/REVISAO/DESCRICAO)
        files["DWG-101_Rev0.dxf"] = StandardB("DWG-101", "0", "Suporte tubulação", "TUB");
        files["DWG-102_Rev0.dxf"] = StandardB("DWG-102", "0", "Base fixação", "TUB");

        // Fora de padrão: textos soltos, sem bloco atributado → não identificado (CA-02)
        files["DWG-200_RevA.dxf"] = LooseTexts("DWG-200", "A");

        // Corrompido: conteúdo que não é DXF (CA-01)
        files["DWG-300_RevA.dxf"] = "ISTO NAO E UM ARQUIVO DXF %$#@! binario corrompido";

        // Duplicado por conteúdo: cópia idêntica do DWG-007 com outro nome
        files["COPIA-DWG-007.dxf"] = files["DWG-007_RevA.dxf"];

        // PDFs associados por nome (cenário B)
        files["DWG-003.pdf"] = MinimalPdf("DWG-003 Rev A");
        files["DWG-004.pdf"] = MinimalPdf("DWG-004 Rev A");

        foreach (var (name, content) in files)
            File.WriteAllText(Path.Combine(dir, name), content);

        // Lista mestra: espera DWG-099 (ausente → ML-001) e DWG-008 Rev B (virá A → ML-002)
        var masterList = Enumerable.Range(1, 16)
            .Select(i => new MasterListEntry
            {
                Code = $"DWG-{i:000}",
                ExpectedRevision = i == 8 ? "B" : i == 12 ? "F" : "A",
                Title = $"Conjunto soldado {i:000}"
            })
            .Concat(new[]
            {
                new MasterListEntry { Code = "DWG-101", ExpectedRevision = "0" },
                new MasterListEntry { Code = "DWG-102", ExpectedRevision = "0" },
                new MasterListEntry { Code = "DWG-099", ExpectedRevision = "C", Title = "Plataforma acesso" }
            }).ToList();
        File.WriteAllText(Path.Combine(dir, "lista-mestra.json"),
            JsonSerializer.Serialize(masterList, new JsonSerializerOptions { WriteIndented = true }));

        // Contrato de teste: cenário A (só desenho) + padrões de carimbo A e B
        var contract = new ContractProfile
        {
            Name = "Contrato teste — Fornecedor X",
            Delivery = DeliveryScenario.DrawingOnly,
            AllowsExternalAi = false,
            RequiredTitleFields = new List<string> { "code", "revision" },
            TitleBlocks =
            {
                new TitleBlockProfile
                {
                    Name = "Carimbo padrão A",
                    BlockName = "TITULO_A",
                    AttributeMap = new Dictionary<string, string>
                    {
                        ["code"] = "CODIGO", ["revision"] = "REV", ["title"] = "TITULO",
                        ["discipline"] = "DISCIPLINA", ["format"] = "FORMATO"
                    }
                },
                new TitleBlockProfile
                {
                    Name = "Carimbo padrão B",
                    BlockName = "TITULO_B",
                    AttributeMap = new Dictionary<string, string>
                    {
                        ["code"] = "NUMERO", ["revision"] = "REVISAO", ["title"] = "DESCRICAO"
                    }
                }
            }
        };
        File.WriteAllText(Path.Combine(dir, "contrato.json"),
            JsonSerializer.Serialize(contract, new JsonSerializerOptions { WriteIndented = true }));

        Console.WriteLine($"Corpus gerado em {dir}: {files.Count} arquivos + lista-mestra.json + contrato.json");
    }

    private static string DxfWrap(string inner, string blocks) => $"""
          0
        SECTION
          2
        HEADER
          9
        $ACADVER
          1
        AC1032
          0
        ENDSEC
          0
        SECTION
          2
        BLOCKS
        {blocks}          0
        ENDSEC
          0
        SECTION
          2
        ENTITIES
        {inner}          0
        ENDSEC
          0
        EOF

        """;

    private static string BlockDef(string name, string attribs) => $"""
          0
        BLOCK
          8
        0
          2
        {name}
         70
        0
         10
        0.0
         20
        0.0
         30
        0.0
          3
        {name}
          1

        {attribs}          0
        ENDBLK
          8
        0

        """;

    private static string Attrib(string handle, string tag, string value, double y) => $"""
          0
        ATTRIB
          5
        {handle}
          8
        0
         10
        10.0
         20
        {y:0.0}
         30
        0.0
         40
        5.0
          1
        {value}
          2
        {tag}
         70
        0

        """;

    private static string StandardA(string code, string rev, string title, string disc, string fmt, string extraEntities = "")
    {
        var attribs =
            Attrib("A1", "CODIGO", code, 30) +
            Attrib("A2", "REV", rev, 24) +
            Attrib("A3", "TITULO", title, 18) +
            Attrib("A4", "DISCIPLINA", disc, 12) +
            Attrib("A5", "FORMATO", fmt, 6);

        var insert = $"""
              0
            INSERT
              5
            10
              8
            0
              2
            TITULO_A
             10
            420.0
             20
            0.0
             30
            0.0
             66
            1
            {attribs}              0
            SEQEND
              5
            1F

            """;

        return DxfWrap(insert + extraEntities, BlockDef("TITULO_A", attribs));
    }

    private static string StandardB(string code, string rev, string title, string disc)
    {
        var attribs =
            Attrib("B1", "NUMERO", code, 30) +
            Attrib("B2", "REVISAO", rev, 24) +
            Attrib("B3", "DESCRICAO", title, 18) +
            Attrib("B4", "DISCIPLINA", disc, 12);

        var insert = $"""
              0
            INSERT
              5
            20
              8
            0
              2
            TITULO_B
             10
            420.0
             20
            0.0
             30
            0.0
             66
            1
            {attribs}              0
            SEQEND
              5
            2F

            """;

        return DxfWrap(insert, BlockDef("TITULO_B", attribs));
    }

    private static string LooseTexts(string code, string rev)
    {
        string Text(string handle, string value, double y) => $"""
              0
            TEXT
              5
            {handle}
              8
            0
             10
            300.0
             20
            {y:0.0}
             30
            0.0
             40
            4.0
              1
            {value}
              7
            STANDARD

            """;

        var entities =
            Text("C1", $"DESENHO {code}", 40) +
            Text("C2", $"REV {rev}", 30) +
            Text("C3", "PECA FORA DO PADRAO DE CARIMBO", 20);
        return DxfWrap(entities, "");
    }

    private static string OverriddenDimension() => """
          0
        DIMENSION
          5
        99
          8
        0
          2
        *D0
         10
        50.0
         20
        80.0
         30
        0.0
          1
        999
         42
        250.0
          3
        STANDARD
         70
        0

        """;

    private static string MinimalPdf(string label) => $"""
        %PDF-1.4
        1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
        2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
        3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >> endobj
        trailer << /Root 1 0 R >>
        %%EOF
        % {label}
        """;
}
