using Autodesk.AutoCAD.DatabaseServices;
using CheckDraw.Engine.Extraction;

namespace CheckDraw.Acad;

/// <summary>
/// Extrai entidades de um DWG para o modelo neutro do CheckDraw — a mesma
/// saída do DxfExtractor. Lê via Database lateral (sem abrir documento),
/// cobrindo ModelSpace e todos os layouts (carimbos vivem no paper space).
/// </summary>
public static class DwgExtractor
{
    public static ExtractionResult Extract(string dwgPath)
    {
        var result = new ExtractionResult();
        try
        {
            using var db = new Database(false, true);
            db.ReadDwgFile(dwgPath, FileOpenMode.OpenForReadAndAllShare, false, null);
            db.CloseInput(true);
            result.DetectedVersion = db.OriginalFileVersion.ToString();

            using var tr = db.TransactionManager.StartTransaction();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (btr.IsLayout) ReadLayoutEntities(btr, tr, result);
            }
            tr.Commit();
            result.Success = true;
        }
        catch (Exception ex)
        {
            result.Error = $"Falha ao ler DWG: {ex.Message}";
        }
        return result;
    }

    private static void ReadLayoutEntities(BlockTableRecord btr, Transaction tr, ExtractionResult result)
    {
        foreach (ObjectId id in btr)
        {
            Entity e;
            try { e = (Entity)tr.GetObject(id, OpenMode.ForRead); }
            catch { continue; } // proxy/objetos de terceiros não devem derrubar a leitura

            switch (e)
            {
                case DBText t:
                    result.Texts.Add(new ExtractedText
                    {
                        Handle = t.Handle.ToString(),
                        Value = t.TextString,
                        X = t.Position.X, Y = t.Position.Y,
                        Layer = t.Layer
                    });
                    break;

                case MText t:
                    result.Texts.Add(new ExtractedText
                    {
                        Handle = t.Handle.ToString(),
                        Value = t.Text,
                        X = t.Location.X, Y = t.Location.Y,
                        Layer = t.Layer
                    });
                    break;

                // Table herda de BlockReference: precisa vir antes
                case Table tb:
                    result.Tables.Add(ReadTable(tb));
                    break;

                case BlockReference br:
                    var block = new ExtractedBlock
                    {
                        Handle = br.Handle.ToString(),
                        Name = br.Name,
                        X = br.Position.X, Y = br.Position.Y
                    };
                    foreach (ObjectId attId in br.AttributeCollection)
                    {
                        var att = (AttributeReference)tr.GetObject(attId, OpenMode.ForRead);
                        block.Attributes[att.Tag] = att.TextString;
                    }
                    result.Blocks.Add(block);
                    break;

                case Dimension d:
                    var text = d.DimensionText;
                    result.Dimensions.Add(new ExtractedDimension
                    {
                        Handle = d.Handle.ToString(),
                        StyleName = d.DimensionStyleName,
                        // "<>" ou vazio = texto medido; qualquer outro = sobrescrito (RF-02)
                        OverrideText = string.IsNullOrWhiteSpace(text) || text == "<>" ? null : text,
                        MeasuredValue = d.Measurement,
                        X = d.TextPosition.X, Y = d.TextPosition.Y
                    });
                    break;
            }
        }
    }

    private static ExtractedTable ReadTable(Table tb)
    {
        var table = new ExtractedTable { Handle = tb.Handle.ToString() };
        if (tb.Rows.Count == 0 || tb.Columns.Count == 0) return table;

        for (var c = 0; c < tb.Columns.Count; c++)
            table.Columns.Add(CellText(tb, 0, c));

        for (var r = 1; r < tb.Rows.Count; r++)
        {
            var row = new Dictionary<string, string>();
            for (var c = 0; c < tb.Columns.Count; c++)
                row[table.Columns[c] is { Length: > 0 } col ? col : $"col{c}"] = CellText(tb, r, c);
            table.Rows.Add(row);
        }
        return table;
    }

    private static string CellText(Table tb, int r, int c)
    {
        try { return tb.Cells[r, c].TextString ?? string.Empty; }
        catch { return string.Empty; }
    }
}
