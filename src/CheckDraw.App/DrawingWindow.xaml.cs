using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using CheckDraw.Engine.Domain;
using CheckDraw.Engine.Extraction;
using CheckDraw.Extractors.Dxf;
using CheckDraw.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace CheckDraw.App;

/// <summary>
/// Visualizador do desenho + "conferência ao vivo": varre as entidades
/// acumulando marcações amarelas (verificado) e vermelhas (apontamento),
/// reproduzindo digitalmente o marca-texto do conferente.
/// </summary>
public partial class DrawingWindow : Window
{
    private readonly string _filePath;
    private readonly List<Finding> _findings;
    private ExtractionResult _ex = new();
    private readonly List<Shape> _marks = new();
    private readonly List<CheckStep> _steps = new();
    private Ellipse? _cursor;
    private bool _running;
    private bool _needsFit;
    private double _minX, _maxX, _minY, _maxY;
    private double _scale = 1, _tx, _ty;
    private Point _dragStart;
    private bool _dragging;

    private record CheckStep(Rect Bounds, string Handle, string Label, Finding? Finding, string Mark = "yellow");

    /// <summary>Região de marcação detectada num scan "-DC" (ingestão por imagem).</summary>
    public class MarkRegion
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double W { get; set; }
        public double H { get; set; }
        public string Color { get; set; } = "yellow";
    }

    private readonly string? _scanImage;
    private readonly List<MarkRegion> _scanMarks = new();
    private readonly string _fileName;
    private readonly string? _dbPath;
    private bool _marking;
    private CheckState _markState = CheckState.CheckedOk;
    private Point _markOrigin;
    private Rectangle? _selRect;
    private bool _markDragging;
    private readonly List<(CheckMark Mark, Shape Shape)> _userMarks = new();

    public DrawingWindow(string filePath, string fileName, IEnumerable<Finding> findings, string? dbPath = null)
    {
        InitializeComponent();
        _filePath = filePath;
        _fileName = fileName;
        _dbPath = dbPath;
        _findings = findings.ToList();
        TitleText.Text = fileName;
        FindingsList.ItemsSource = _findings;
        Loaded += (_, _) => LoadAndRender();
        DrawCanvas.SizeChanged += (_, _) => { if (_needsFit) FitView(); };
    }

    /// <summary>Modo scan "-DC": exibe o desenho conferido digitalizado e ingere as marcas detectadas.</summary>
    public DrawingWindow(string imagePath, string fileName, List<MarkRegion> marks, string? dbPath = null)
        : this(imagePath, fileName,
            marks.Where(m => m.Color is "red" or "orange" or "green").Select(m => new Finding
            {
                RuleId = "SCAN-" + m.Color.ToUpperInvariant(),
                Description = m.Color switch
                {
                    "red" => "Marcação de correção pendente (não alterada na nova revisão)",
                    "orange" => "Região antes verificada mudou — revalidar",
                    "green" => "Correção aparente aplicada — confirmar",
                    _ => "Marcação"
                },
                Source = FindingSource.Rule,
                Kind = FindingKind.Objective,
                Severity = m.Color == "red" ? FindingSeverity.Warning : FindingSeverity.Info,
                FileName = fileName
            }), dbPath)
    {
        _scanImage = imagePath;
        _scanMarks = marks;
        TitleText.Text = $"{fileName}  (scan conferido — ingestão de marcas)";
    }

    private void LoadAndRender()
    {
        if (_scanImage != null)
        {
            RenderScan();
            BuildScanSteps();
            FitView();
            LoadUserMarks();
            StepText.Text = $"Scan ingerido: {_scanMarks.Count(m => m.Color == "yellow")} verificadas, " +
                            $"{_scanMarks.Count(m => m.Color == "orange")} a revalidar, " +
                            $"{_scanMarks.Count(m => m.Color == "green")} correções aparentes, " +
                            $"{_scanMarks.Count(m => m.Color == "red")} correções pendentes. ▶ para replay.";
            return;
        }

        IDocumentExtractor extractor = System.IO.Path.GetExtension(_filePath).Equals(".dwg", StringComparison.OrdinalIgnoreCase)
            ? new OdaDwgExtractor()
            : new DxfExtractor();

        _ex = extractor.Extract(_filePath);
        if (!_ex.Success) { StepText.Text = $"Falha ao ler: {_ex.Error}"; return; }

        ComputeBounds();
        RenderEntities();
        BuildSteps();
        FitView();
        LoadUserMarks();
        StepText.Text = $"{_ex.Texts.Count + _ex.Geometry.Count + _ex.Blocks.Count + _ex.Dimensions.Count} entidades extraídas. Pronto para conferir.";
    }

    // ---- modo scan "-DC" ----
    private void RenderScan()
    {
        DrawCanvas.Children.Clear();
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(_scanImage!, UriKind.Absolute);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.EndInit();

        _minX = 0; _minY = 0; _maxX = bmp.PixelWidth; _maxY = bmp.PixelHeight;
        var img = new Image { Source = bmp, Width = bmp.PixelWidth, Height = bmp.PixelHeight };
        Canvas.SetLeft(img, 0); Canvas.SetTop(img, 0);
        DrawCanvas.Children.Add(img);
    }

    private void BuildScanSteps()
    {
        _steps.Clear();
        var pseudoFindings = _findings.Where(f => f.RuleId.StartsWith("SCAN-")).ToList();
        var idx = 0;
        foreach (var m in _scanMarks.OrderBy(m => m.Y).ThenBy(m => m.X))
        {
            var bounds = new Rect(m.X, m.Y, m.W, m.H);
            var finding = m.Color is "red" or "orange" or "green" && idx < pseudoFindings.Count
                ? pseudoFindings[idx++] : null;
            var label = m.Color switch
            {
                "orange" => "Conteúdo mudou desde a conferência anterior — revalidar",
                "green" => "Correção aparente aplicada — confirmar",
                "red" => "Correção pendente — não alterada",
                _ => "Região marcada como verificada — preservada"
            };
            _steps.Add(new CheckStep(bounds, "", label, finding, m.Color));
        }
    }

    // ---- coordenadas: DXF tem Y para cima; WPF para baixo ----
    private double Fy(double y) => _maxY - y;
    private Rect BoundsOf(IEnumerable<double> pts)
    {
        var p = pts.ToList();
        if (p.Count < 2) return new Rect(0, 0, 10, 10);
        var xs = Enumerable.Range(0, p.Count / 2).Select(i => p[i * 2]);
        var ys = Enumerable.Range(0, p.Count / 2).Select(i => Fy(p[i * 2 + 1]));
        var x0 = xs.Min(); var x1 = xs.Max(); var y0 = ys.Min(); var y1 = ys.Max();
        return new Rect(x0, y0, Math.Max(x1 - x0, 4), Math.Max(y1 - y0, 4));
    }

    private void ComputeBounds()
    {
        var xs = new List<double>(); var ys = new List<double>();
        void Add(double x, double y) { xs.Add(x); ys.Add(y); }
        foreach (var g in _ex.Geometry) for (var i = 0; i + 1 < g.Points.Count; i += 2) Add(g.Points[i], g.Points[i + 1]);
        foreach (var t in _ex.Texts) Add(t.X, t.Y);
        foreach (var b in _ex.Blocks) Add(b.X, b.Y);
        foreach (var d in _ex.Dimensions) Add(d.X, d.Y);
        _minX = xs.DefaultIfEmpty(0).Min() - 10; _maxX = xs.DefaultIfEmpty(100).Max() + 10;
        _minY = ys.DefaultIfEmpty(0).Min() - 10; _maxY = ys.DefaultIfEmpty(100).Max() + 10;
    }

    private void RenderEntities()
    {
        DrawCanvas.Children.Clear();
        foreach (var g in _ex.Geometry)
        {
            switch (g.Kind)
            {
                case "line" or "polyline" when g.Points.Count >= 4:
                    var poly = new Polyline
                    {
                        Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                        StrokeThickness = 0.8,
                        Points = new PointCollection(
                            Enumerable.Range(0, g.Points.Count / 2)
                                .Select(i => new Point(g.Points[i * 2], Fy(g.Points[i * 2 + 1]))))
                    };
                    if (g.Closed && g.Points.Count >= 6)
                        poly.Points.Add(new Point(g.Points[0], Fy(g.Points[1])));
                    DrawCanvas.Children.Add(poly);
                    break;

                case "circle" when g.Points.Count >= 2:
                    var r = g.Radius ?? 4;
                    var el = new Ellipse
                    {
                        Width = r * 2, Height = r * 2,
                        Stroke = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                        StrokeThickness = 0.8
                    };
                    Canvas.SetLeft(el, g.Points[0] - r);
                    Canvas.SetTop(el, Fy(g.Points[1]) - r);
                    DrawCanvas.Children.Add(el);
                    break;
            }
        }

        foreach (var t in _ex.Texts)
            AddText(t.Value, t.X, t.Y, "#FF1A237E");
        foreach (var b in _ex.Blocks)
            AddText(b.Name, b.X, b.Y, "#FF6A1B9A", size: 4);
        foreach (var d in _ex.Dimensions)
        {
            var mark = new Ellipse { Width = 5, Height = 5, Fill = Brushes.DarkOrange };
            Canvas.SetLeft(mark, d.X - 2.5);
            Canvas.SetTop(mark, Fy(d.Y) - 2.5);
            DrawCanvas.Children.Add(mark);
            AddText(d.OverrideText ?? d.MeasuredValue?.ToString("0.#") ?? "dim", d.X + 4, d.Y,
                d.OverrideText != null ? "#FFD32F2F" : "#FFEF6C00");
        }
    }

    private void AddText(string value, double x, double y, string color, double size = 5)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var tb = new TextBlock
        {
            Text = value,
            FontSize = size,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color))
        };
        Canvas.SetLeft(tb, x);
        Canvas.SetTop(tb, Fy(y) - size);
        DrawCanvas.Children.Add(tb);
    }

    // ---- passos da conferência ----
    private void BuildSteps()
    {
        _steps.Clear();
        foreach (var b in _ex.Blocks)
        {
            var bnds = new Rect(b.X - 2, Fy(b.Y) - 90, 130, 95);
            _steps.Add(new CheckStep(bnds, b.Handle, $"Verificando carimbo '{b.Name}'…", Find(b.Handle)));
        }
        foreach (var t in _ex.Texts)
        {
            var bnds = new Rect(t.X - 1, Fy(t.Y) - 6, Math.Max(t.Value.Length * 2.6, 8), 8);
            _steps.Add(new CheckStep(bnds, t.Handle, $"Verificando texto \"{Trim(t.Value)}\"…", Find(t.Handle)));
        }
        foreach (var d in _ex.Dimensions)
            _steps.Add(new CheckStep(new Rect(d.X - 4, Fy(d.Y) - 8, 40, 16), d.Handle,
                $"Conferindo cota (estilo '{d.StyleName}')…", Find(d.Handle)));
        // Geometria estrutural não recebe marca-texto: só entra como passo quando
        // há apontamento vinculado à entidade (marca vermelha).
        foreach (var g in _ex.Geometry)
        {
            var finding = Find(g.Handle);
            if (finding != null)
                _steps.Add(new CheckStep(BoundsOf(g.Points), g.Handle,
                    $"Verificando geometria {g.Kind}…", finding));
        }

        Finding? Find(string h) => _findings.FirstOrDefault(f => f.EntityRef == h);
        static string Trim(string v) => v.Length > 24 ? v[..24] + "…" : v;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (_running) return;
        _running = true; PlayBtn.IsEnabled = false;
        ClearMarks();
        EnsureCursor();
        CheckProgress.Maximum = _steps.Count;
        CheckProgress.Value = 0;

        var ok = 0; var flags = 0;
        foreach (var step in _steps)
        {
            var delay = (int)SpeedSlider.Value;
            var isFinding = step.Finding != null;

            ApplyStep(step);

            if (isFinding)
            {
                StepText.Text = $"⚠ {step.Finding!.RuleId}: {step.Finding.Description}";
                StepText.Foreground = Brushes.Firebrick;
                flags++;
                await Task.Delay(Math.Max(delay, 700));
            }
            else
            {
                StepText.Text = step.Label;
                StepText.Foreground = Brushes.DarkOliveGreen;
                ok++;
                await Task.Delay(delay);
            }
            CheckProgress.Value++;
        }

        StepText.Text = $"Conferência simulada: {ok} elementos marcados como verificados, {flags} apontamentos. Sem marcação = não verificado.";
        StepText.Foreground = Brushes.Black;
        PlayBtn.IsEnabled = true;
        _running = false;
    }

    /// <summary>Aplica todas as marcas instantaneamente (demo/teste headless).</summary>
    public void RunInstantCheck()
    {
        ClearMarks();
        EnsureCursor();
        var flags = 0;
        foreach (var step in _steps)
        {
            ApplyStep(step, instant: true);
            if (step.Finding != null) flags++;
        }
        CheckProgress.Maximum = _steps.Count;
        CheckProgress.Value = _steps.Count;
        StepText.Text = $"Conferência simulada: {_steps.Count - flags} elementos marcados como verificados, {flags} apontamentos. Sem marcação = não verificado.";
        StepText.Foreground = Brushes.Black;
    }

    /// <summary>Canvas para snapshot/renderização externa.</summary>
    public Canvas CanvasElement => DrawCanvas;

    private void ApplyStep(CheckStep step, bool instant = false)
    {
        MoveCursorTo(step.Bounds);
        var (fill, stroke) = step.Mark switch
        {
            "red" => ("#8CF44336", "#FFB71C1C"),
            "orange" => ("#8CFF9800", "#FFE65100"),
            "green" => ("#8C66BB6A", "#FF2E7D32"),
            _ => step.Finding != null ? ("#8CF44336", "#FFB71C1C") : ("#8CFFEB3B", "#FFBDBB28")
        };
        AddMark(step.Bounds, fill, stroke, instant, dashed: _scanImage != null);
    }

    private void AddMark(Rect b, string fill, string stroke, bool instant = false, bool dashed = false)
    {
        var r = new Rectangle
        {
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)),
            StrokeThickness = dashed ? 1.6 : 0.4,
            StrokeDashArray = dashed ? new DoubleCollection { 4, 2 } : null,
            RadiusX = 1.5, RadiusY = 1.5,
            Opacity = 0
        };
        Canvas.SetLeft(r, b.X); Canvas.SetTop(r, b.Y);
        r.Width = b.Width; r.Height = b.Height;
        DrawCanvas.Children.Add(r);
        _marks.Add(r);
        if (instant) r.Opacity = 1;
        else r.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)));
    }

    private void EnsureCursor()
    {
        if (_cursor != null) return;
        _cursor = new Ellipse { Width = 6, Height = 6, Fill = Brushes.DodgerBlue, Opacity = 0.75 };
        DrawCanvas.Children.Add(_cursor);
    }

    private void MoveCursorTo(Rect b)
    {
        if (_cursor == null) return;
        Canvas.SetLeft(_cursor, b.X + b.Width / 2 - 3);
        Canvas.SetTop(_cursor, b.Y + b.Height / 2 - 3);
        Panel.SetZIndex(_cursor, int.MaxValue);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => ClearMarks();
    private void ClearMarks()
    {
        foreach (var m in _marks) DrawCanvas.Children.Remove(m);
        _marks.Clear();
        CheckProgress.Value = 0;
        StepText.Text = "Marcas limpas.";
    }

    // ---- marcação manual (marca-texto digital, RF-05 por região) ----
    private void MarkToggle_Changed(object s, RoutedEventArgs e) =>
        _marking = MarkToggle.IsChecked == true;

    private void MarkState_Picked(object s, RoutedEventArgs e)
    {
        if (s is RadioButton { Tag: string tag })
            _markState = Enum.Parse<CheckState>(tag);
    }

    private static (string fill, string stroke) MarkColors(CheckState st) => st switch
    {
        CheckState.NeedsCorrection => ("#8CF44336", "#FFB71C1C"),
        CheckState.NeedsRevalidation => ("#8CFF9800", "#FFE65100"),
        _ => ("#8CFFEB3B", "#FFBDBB28")
    };

    private void LoadUserMarks()
    {
        if (_dbPath == null || !File.Exists(_dbPath)) return;
        using var db = new CheckDrawDbContext(_dbPath);
        foreach (var m in db.CheckMarks.Where(c => c.FileName == _fileName).ToList())
            AddUserMarkShape(m);
    }

    private void AddUserMarkShape(CheckMark m)
    {
        var (fill, stroke) = MarkColors(m.State);
        var r = new Rectangle
        {
            Width = m.W, Height = m.H,
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)),
            StrokeThickness = 1.2,
            RadiusX = 1.5, RadiusY = 1.5,
            ToolTip = $"{m.State} — {m.User} {m.CreatedAt:dd/MM HH:mm}"
        };
        Canvas.SetLeft(r, m.X);
        // DXF: Y pra cima → SetTop na coordenada invertida; scan: Y já é tela
        Canvas.SetTop(r, _scanImage != null ? m.Y : Fy(m.Y + m.H));
        DrawCanvas.Children.Add(r);
        _userMarks.Add((m, r));
    }

    private void SaveUserMark(Rect b)
    {
        var mark = new CheckMark
        {
            FileName = _fileName,
            X = b.X,
            Y = _scanImage != null ? b.Y : _maxY - (b.Y + b.Height),
            W = b.Width, H = b.Height,
            State = _markState,
            User = Environment.UserName
        };
        if (_dbPath != null)
        {
            using var db = new CheckDrawDbContext(_dbPath);
            db.CheckMarks.Add(mark);
            db.SaveChanges();
        }
        AddUserMarkShape(mark);
        StepText.Text = $"Marca {_markState} salva em ({b.X:0}, {b.Y:0})" + (_dbPath == null ? " (sem banco — efêmera)" : "");
    }

    private void DeleteUserMarkAt(Point p)
    {
        var py = _scanImage != null ? p.Y : _maxY - p.Y;
        var hit = _userMarks.LastOrDefault(um =>
            p.X >= um.Mark.X && p.X <= um.Mark.X + um.Mark.W &&
            py >= um.Mark.Y && py <= um.Mark.Y + um.Mark.H);
        if (hit.Shape == null) { StepText.Text = "Nenhuma marca do usuário nesse ponto"; return; }
        if (_dbPath != null)
        {
            using var db = new CheckDrawDbContext(_dbPath);
            db.CheckMarks.Where(c => c.Id == hit.Mark.Id).ExecuteDelete();
        }
        DrawCanvas.Children.Remove(hit.Shape);
        _userMarks.Remove(hit);
        StepText.Text = "Marca removida";
    }

    // ---- zoom / pan ----
    private void FitView()
    {
        var vw = DrawCanvas.ActualWidth; var vh = DrawCanvas.ActualHeight;
        if (vw <= 0 || vh <= 0) { _needsFit = true; return; }
        _needsFit = false;
        var bw = Math.Max(_maxX - _minX, 1); var bh = Math.Max(_maxY - _minY, 1);
        _scale = Math.Min(vw / bw, vh / bh) * 0.96;
        _tx = (vw - bw * _scale) / 2 - _minX * _scale;
        _ty = (vh - bh * _scale) / 2 - 0 * _scale;
        ApplyTransform();
    }

    private void ApplyTransform() =>
        DrawCanvas.RenderTransform = new TransformGroup
        {
            Children = { new ScaleTransform(_scale, _scale), new TranslateTransform(_tx, _ty) }
        };

    private void ZoomBy(double factor)
    {
        var cx = DrawCanvas.ActualWidth / 2;
        var cy = DrawCanvas.ActualHeight / 2;
        _tx = cx - (cx - _tx) * factor;
        _ty = cy - (cy - _ty) * factor;
        _scale *= factor;
        ApplyTransform();
    }

    private void ZoomIn_Click(object s, RoutedEventArgs e) => ZoomBy(1.25);
    private void ZoomOut_Click(object s, RoutedEventArgs e) => ZoomBy(1 / 1.25);
    private void Fit_Click(object s, RoutedEventArgs e) => FitView();

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(DrawCanvas);
        var factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        _tx = pos.X - (pos.X - _tx) * factor;
        _ty = pos.Y - (pos.Y - _ty) * factor;
        _scale *= factor;
        ApplyTransform();
    }

    private void Canvas_MouseDown(object s, MouseButtonEventArgs e)
    {
        if (_marking && e.ChangedButton == MouseButton.Left)
        {
            _markDragging = true;
            _markOrigin = e.GetPosition(DrawCanvas);
            _selRect = new Rectangle
            {
                Stroke = Brushes.DodgerBlue, StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(0x20, 0x1E, 0x90, 0xFF))
            };
            Canvas.SetLeft(_selRect, _markOrigin.X); Canvas.SetTop(_selRect, _markOrigin.Y);
            DrawCanvas.Children.Add(_selRect);
            DrawCanvas.CaptureMouse();
            return;
        }
        _dragging = true; _dragStart = e.GetPosition(this); DrawCanvas.CaptureMouse();
    }

    private void Canvas_MouseUp(object s, MouseButtonEventArgs e)
    {
        if (_markDragging && _selRect != null)
        {
            var p = e.GetPosition(DrawCanvas);
            var b = new Rect(_markOrigin, p);
            DrawCanvas.Children.Remove(_selRect);
            _selRect = null;
            _markDragging = false;
            DrawCanvas.ReleaseMouseCapture();
            if (b.Width > 2 && b.Height > 2) SaveUserMark(b);
            return;
        }
        _dragging = false; DrawCanvas.ReleaseMouseCapture();
    }

    private void Canvas_MouseRightDown(object s, MouseButtonEventArgs e)
    {
        if (_marking) DeleteUserMarkAt(e.GetPosition(DrawCanvas));
    }

    private void Canvas_MouseMove(object s, MouseEventArgs e)
    {
        if (_markDragging && _selRect != null)
        {
            var p = e.GetPosition(DrawCanvas);
            var b = new Rect(_markOrigin, p);
            Canvas.SetLeft(_selRect, b.X); Canvas.SetTop(_selRect, b.Y);
            _selRect.Width = b.Width; _selRect.Height = b.Height;
            return;
        }
        if (!_dragging) return;
        var pt = e.GetPosition(this);
        _tx += pt.X - _dragStart.X; _ty += pt.Y - _dragStart.Y;
        _dragStart = pt;
        ApplyTransform();
    }
}
