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
    private double _minX, _maxX, _minY, _maxY;
    private double _scale = 1, _tx, _ty;
    private Point _dragStart;
    private bool _dragging;

    private record CheckStep(Rect Bounds, string Handle, string Label, Finding? Finding);

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

    public DrawingWindow(string filePath, string fileName, IEnumerable<Finding> findings)
    {
        InitializeComponent();
        _filePath = filePath;
        _findings = findings.ToList();
        TitleText.Text = fileName;
        FindingsList.ItemsSource = _findings;
        Loaded += (_, _) => LoadAndRender();
    }

    /// <summary>Modo scan "-DC": exibe o desenho conferido digitalizado e ingere as marcas detectadas.</summary>
    public DrawingWindow(string imagePath, string fileName, List<MarkRegion> marks)
        : this(imagePath, fileName,
            marks.Where(m => m.Color == "red").Select(m => new Finding
            {
                RuleId = "SCAN-RED",
                Description = "Marcação de correção detectada no scan conferido",
                Source = FindingSource.Rule,
                Kind = FindingKind.Objective,
                Severity = FindingSeverity.Warning,
                FileName = fileName
            }))
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
            StepText.Text = $"Scan ingerido: {_scanMarks.Count(m => m.Color == "yellow")} marcas de verificado, " +
                            $"{_scanMarks.Count(m => m.Color == "red")} de correção. ▶ para replay.";
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
        var redFindings = _findings.Where(f => f.RuleId == "SCAN-RED").ToList();
        var redIndex = 0;
        foreach (var m in _scanMarks.OrderBy(m => m.Y).ThenBy(m => m.X))
        {
            var bounds = new Rect(m.X, m.Y, m.W, m.H);
            _steps.Add(m.Color == "red"
                ? new CheckStep(bounds, "", "Marcação de correção detectada", redIndex < redFindings.Count ? redFindings[redIndex++] : null)
                : new CheckStep(bounds, "", "Região marcada como verificada no papel", null));
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
        if (step.Finding != null) AddMark(step.Bounds, "#8CF44336", "#FFB71C1C", instant);
        else AddMark(step.Bounds, "#8CFFEB3B", "#FFBDBB28", instant);
    }

    private void AddMark(Rect b, string fill, string stroke, bool instant = false)
    {
        var r = new Rectangle
        {
            Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(fill)),
            Stroke = new SolidColorBrush((Color)ColorConverter.ConvertFromString(stroke)),
            StrokeThickness = 0.4,
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

    // ---- zoom / pan ----
    private void FitView()
    {
        var vw = DrawCanvas.ActualWidth; var vh = DrawCanvas.ActualHeight;
        if (vw <= 0) { vw = 800; vh = 600; }
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
    { _dragging = true; _dragStart = e.GetPosition(this); DrawCanvas.CaptureMouse(); }
    private void Canvas_MouseUp(object s, MouseButtonEventArgs e)
    { _dragging = false; DrawCanvas.ReleaseMouseCapture(); }
    private void Canvas_MouseMove(object s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(this);
        _tx += p.X - _dragStart.X; _ty += p.Y - _dragStart.Y;
        _dragStart = p;
        ApplyTransform();
    }
}
