using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CheckDraw.App;
using CheckDraw.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

// Uso: CheckDraw.RenderTest <db> <arquivo.dxf> <saida.png>
//      CheckDraw.RenderTest scan <imagem> <marks.json> <saida.png>
// Abre a DrawingWindow real, roda a conferência instantânea e captura o canvas em PNG.
// A janela fica aberta (pressione ▶ Conferir nela para ver a animação ao vivo).

var scanMode = args[0] == "scan";
var dbPath = scanMode ? "" : args[0];
var fileName = args[1];
var pngPath = scanMode ? args[3] : args[2];

var t = new Thread(() =>
{
    var app = new Application { ShutdownMode = ShutdownMode.OnLastWindowClose };
    DrawingWindow win;
    if (scanMode)
    {
        var marks = System.Text.Json.JsonSerializer
            .Deserialize<List<DrawingWindow.MarkRegion>>(File.ReadAllText(args[2]),
                new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        win = new DrawingWindow(Path.GetFullPath(fileName), Path.GetFileName(fileName), marks);
    }
    else
    {
        List<CheckDraw.Engine.Domain.Finding> findings;
        CheckDraw.Engine.Domain.DrawingFile file;
        using (var db = new CheckDrawDbContext(dbPath))
        {
            file = db.Files.AsNoTracking().First(f => f.FileName == fileName);
            findings = db.Findings.AsNoTracking().Where(f => f.FileName == fileName).ToList();
        }
        win = new DrawingWindow(file.FullPath, file.FileName, findings);
    }
    win.Show();
    win.UpdateLayout();
    win.RunInstantCheck();
    win.UpdateLayout();

    var canvas = win.CanvasElement;
    var w = Math.Max((int)canvas.ActualWidth, 10);
    var h = Math.Max((int)canvas.ActualHeight, 10);
    var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(canvas);
    var enc = new PngBitmapEncoder();
    enc.Frames.Add(BitmapFrame.Create(rtb));
    using (var ms = new MemoryStream())
    {
        enc.Save(ms);
        File.WriteAllBytes(pngPath, ms.ToArray());
    }
    Console.WriteLine($"SNAPSHOT: {pngPath} ({w}x{h})");

    app.Run(); // mantém a janela viva até o usuário fechar
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();
