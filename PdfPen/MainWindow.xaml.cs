using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using PdfPen.Services.AnnotationService;
using PdfPen.Services.FileService;
using PdfPen.Services.PdfService;
using PdfPen.Services.PenService;

namespace PdfPen;

public partial class MainWindow : Window
{
    private const double PtToDip = 96.0 / 72.0;
    private const double MinZoom = 0.1, MaxZoom = 8.0;
    private enum FitMode { None, Page, Width }
    private enum Tool { Pen, Highlighter, Eraser }

    private static readonly Color[] Palette =
    {
        Colors.Black, Color.FromRgb(0xE5, 0x1C, 0x23), Color.FromRgb(0x1F, 0x4F, 0xD8),
        Color.FromRgb(0x1B, 0x8A, 0x3A), Color.FromRgb(0xF5, 0x7C, 0x00), Color.FromRgb(0x8E, 0x24, 0xAA),
    };
    private const byte HighlighterAlpha = 0x66;

    // 문서 상태
    private PdfDocument? _pdf;
    private int _page;                 // 0-based
    private double _zoom = 1.0;
    private FitMode _fit = FitMode.Page;
    private int _renderVersion;

    // 필기 상태
    private Dictionary<int, StrokeCollection> _strokes = new();
    private readonly InkHistory _history = new();
    private StrokeCollection? _hooked;
    private bool _suppressHistory;
    private bool _dirty;
    private Tool _tool = Tool.Pen;
    private Color _color = Colors.Black;
    private double _penWidth = 1.5, _highlighterWidth = 14;
    private readonly List<Button> _swatchButtons = new();

    // 창 상태
    private WindowState _prevState;
    private WindowStyle _prevStyle;
    private bool _fullscreen;

    public MainWindow()
    {
        InitializeComponent();
        BuildSwatches();
        Stylus.SetIsFlicksEnabled(Ink, false);
        Stylus.SetIsPressAndHoldEnabled(Ink, false);
        Stylus.SetIsTapFeedbackEnabled(Ink, false);
        Stylus.SetIsTouchFeedbackEnabled(Ink, false);
        SelectTool(Tool.Pen);

        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1])) Loaded += (_, _) => OpenFile(args[1]);
        UpdateUi();
    }

    // ================================================================ 파일

    private void Open_Click(object s, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "PDF 문서 (*.pdf)|*.pdf" };
        if (dlg.ShowDialog(this) == true) OpenFile(dlg.FileName);
    }

    private void Close_Click(object s, RoutedEventArgs e)
    {
        if (ConfirmDiscardOrSave()) CloseDocument();
    }

    private void Window_Drop(object s, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.FirstOrDefault(f => f.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) is { } pdf)
            OpenFile(pdf);
    }

    private void OpenFile(string path)
    {
        if (!ConfirmDiscardOrSave()) return;
        try
        {
            var doc = PdfDocument.Open(path);
            var strokes = InkAnnotationStore.Import(doc);   // 기존 Ink 주석을 편집 가능한 필기로
            CloseDocument();
            _pdf = doc;
            _strokes = strokes;
            _history.Clear();
            _dirty = false;
            _page = 0;
            _fit = FitMode.Page;
            Hint.Visibility = Visibility.Collapsed;
            Ink.Visibility = Visibility.Visible;
            RenderCurrent();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "PDF 열기 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void CloseDocument()
    {
        _renderVersion++;
        HookStrokes(null);
        Ink.Strokes = new StrokeCollection();
        Ink.Visibility = Visibility.Collapsed;
        _pdf?.Dispose();
        _pdf = null;
        _strokes = new();
        _history.Clear();
        _dirty = false;
        PageImage.Source = null;
        Hint.Visibility = Visibility.Visible;
        UpdateUi();
    }

    private void Save_Click(object s, RoutedEventArgs e) => Save();

    private void SaveAs_Click(object s, RoutedEventArgs e)
    {
        if (_pdf == null) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF 문서 (*.pdf)|*.pdf",
            FileName = Path.GetFileName(_pdf.FilePath),
            InitialDirectory = Path.GetDirectoryName(_pdf.FilePath),
        };
        if (dlg.ShowDialog(this) == true) Save(dlg.FileName);
    }

    private bool Save(string? path = null)
    {
        if (_pdf == null) return true;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            var dest = path ?? _pdf.FilePath;
            AtomicPdfSaver.Save(_pdf, _strokes, dest);
            _pdf.FilePath = dest;
            _dirty = false;
            UpdateUi();
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"저장하지 못했습니다.\n{ex.Message}\n\n작업 내용은 아직 프로그램에 남아 있습니다.",
                            "저장 실패", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
        finally { Mouse.OverrideCursor = null; }
    }

    /// <summary>저장하지 않은 변경이 있으면 물어본다. false 면 동작을 취소해야 한다.</summary>
    private bool ConfirmDiscardOrSave()
    {
        if (_pdf == null || !_dirty) return true;
        var r = MessageBox.Show(this, $"'{Path.GetFileName(_pdf.FilePath)}' 의 변경 내용을 저장할까요?",
                                "PDF Pen", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return r switch
        {
            MessageBoxResult.Yes => Save(),
            MessageBoxResult.No => true,
            _ => false,
        };
    }

    private void Window_Closing(object? s, CancelEventArgs e)
    {
        if (!ConfirmDiscardOrSave()) e.Cancel = true;
    }

    // ================================================================ 페이지 이동

    private void GoTo(int page)
    {
        if (_pdf == null) return;
        page = Math.Clamp(page, 0, _pdf.PageCount - 1);
        if (page == _page && PageImage.Source != null) { UpdateUi(); return; }
        _page = page;
        RenderCurrent();
        Scroller.ScrollToTop();
    }

    private void Prev_Click(object s, RoutedEventArgs e) => GoTo(_page - 1);
    private void Next_Click(object s, RoutedEventArgs e) => GoTo(_page + 1);

    private void PageBox_KeyDown(object s, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (int.TryParse(PageBox.Text, out var n)) GoTo(n - 1);
        UpdateUi();
        Scroller.Focus();
        e.Handled = true;
    }

    private void PageBox_GotFocus(object s, KeyboardFocusChangedEventArgs e) => PageBox.SelectAll();

    // ================================================================ 확대/축소

    private void ZoomIn_Click(object s, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void ZoomOut_Click(object s, RoutedEventArgs e) => SetZoom(_zoom / 1.25);
    private void FitPage_Click(object s, RoutedEventArgs e) { _fit = FitMode.Page; RenderCurrent(); }
    private void FitWidth_Click(object s, RoutedEventArgs e) { _fit = FitMode.Width; RenderCurrent(); }

    private void SetZoom(double zoom)
    {
        _fit = FitMode.None;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        RenderCurrent();
    }

    private void Scroller_PreviewMouseWheel(object s, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetZoom(e.Delta > 0 ? _zoom * 1.1 : _zoom / 1.1);
        e.Handled = true;
    }

    private void Window_SizeChanged(object s, SizeChangedEventArgs e)
    {
        if (_pdf != null && _fit != FitMode.None) RenderCurrent();
    }

    // ================================================================ 전체화면 / 단축키

    private void Fullscreen_Click(object s, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _prevState = WindowState; _prevStyle = WindowStyle;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Normal;   // 작업표시줄까지 덮도록 한 번 리셋
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = _prevStyle;
            WindowState = _prevState;
        }
        _fullscreen = !_fullscreen;
    }

    private void Window_PreviewKeyDown(object s, KeyEventArgs e)
    {
        if (PageBox.IsKeyboardFocusWithin) return;
        var mods = Keyboard.Modifiers;
        var ctrl = mods == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.O when ctrl: Open_Click(this, e); break;
            case Key.S when ctrl: Save(); break;
            case Key.S when mods == (ModifierKeys.Control | ModifierKeys.Shift): SaveAs_Click(this, e); break;
            case Key.Z when ctrl: Undo(); break;
            case Key.Y when ctrl: Redo(); break;
            case Key.OemPlus or Key.Add when ctrl: SetZoom(_zoom * 1.25); break;
            case Key.OemMinus or Key.Subtract when ctrl: SetZoom(_zoom / 1.25); break;
            case Key.PageDown or Key.Right: GoTo(_page + 1); break;
            case Key.PageUp or Key.Left: GoTo(_page - 1); break;
            case Key.Home: GoTo(0); break;
            case Key.End when _pdf != null: GoTo(_pdf.PageCount - 1); break;
            default: return;
        }
        e.Handled = true;
    }

    // ================================================================ 도구

    private void PenTool_Click(object s, RoutedEventArgs e) => SelectTool(Tool.Pen);
    private void HighlighterTool_Click(object s, RoutedEventArgs e) => SelectTool(Tool.Highlighter);
    private void EraserTool_Click(object s, RoutedEventArgs e) => SelectTool(Tool.Eraser);

    private void BuildSwatches()
    {
        foreach (var c in Palette)
        {
            var b = new Button { Style = (Style)FindResource("Swatch"), Background = new SolidColorBrush(c), Tag = c };
            b.Click += (_, _) => { _color = c; if (_tool == Tool.Eraser) _tool = Tool.Pen; SelectTool(_tool); };
            _swatchButtons.Add(b);
            Swatches.Children.Add(b);
        }
    }

    private void SelectTool(Tool tool)
    {
        _tool = tool;
        PenTool.IsChecked = tool == Tool.Pen;
        HighlighterTool.IsChecked = tool == Tool.Highlighter;
        EraserTool.IsChecked = tool == Tool.Eraser;

        foreach (var b in _swatchButtons)
            b.BorderBrush = (Color)b.Tag == _color ? Brushes.DodgerBlue : Brushes.Transparent;

        WidthSlider.IsEnabled = tool != Tool.Eraser;
        if (tool != Tool.Eraser)
        {
            _suppressSlider = true;
            WidthSlider.Value = tool == Tool.Pen ? _penWidth : _highlighterWidth;
            _suppressSlider = false;
        }
        ApplyDrawingAttributes();
        ApplyEditingMode(true);
    }

    private bool _suppressSlider;

    private void WidthSlider_ValueChanged(object s, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressSlider || Ink == null) return;   // InitializeComponent 중에는 Ink 가 아직 없다
        if (_tool == Tool.Pen) _penWidth = e.NewValue;
        else if (_tool == Tool.Highlighter) _highlighterWidth = e.NewValue;
        ApplyDrawingAttributes();
    }

    private void ApplyDrawingAttributes()
    {
        bool hl = _tool == Tool.Highlighter;
        var color = hl ? Color.FromArgb(HighlighterAlpha, _color.R, _color.G, _color.B) : _color;
        Ink.DefaultDrawingAttributes = InkAnnotationStore.MakeAttributes(color, hl ? _highlighterWidth : _penWidth, hl);
    }

    private void ApplyEditingMode(bool inputAllowed)
    {
        Ink.EditingMode = !inputAllowed ? InkCanvasEditingMode.None
            : _tool == Tool.Eraser ? InkCanvasEditingMode.EraseByStroke
            : InkCanvasEditingMode.Ink;
    }

    // 펜 → 필기, 손가락 → 필기 안 함(스크롤), 마우스 → 설정에 따름.
    // 손바닥 무시(Palm rejection)의 1차 구현: 터치 입력은 필기 레이어가 받지 않는다.
    private void Ink_PreviewStylusDown(object s, StylusDownEventArgs e)
    {
        bool touch = e.StylusDevice?.TabletDevice?.Type == TabletDeviceType.Touch;
        ApplyEditingMode(!touch);
    }

    private void Ink_PreviewMouseDown(object s, MouseButtonEventArgs e)
    {
        if (e.StylusDevice == null) ApplyEditingMode(MouseInkBox.IsChecked == true);   // 진짜 마우스만
    }

    private void ClearPage_Click(object s, RoutedEventArgs e)
    {
        if (_pdf != null && Ink.Strokes.Count > 0) Ink.Strokes.Clear();   // StrokesChanged → 기록됨(Undo 가능)
    }

    // ================================================================ 필기 기록 / Undo / Redo

    private StrokeCollection StrokesFor(int page)
    {
        if (!_strokes.TryGetValue(page, out var c)) _strokes[page] = c = new StrokeCollection();
        return c;
    }

    private void HookStrokes(StrokeCollection? c)
    {
        if (_hooked != null) _hooked.StrokesChanged -= OnStrokesChanged;
        _hooked = c;
        if (c != null) c.StrokesChanged += OnStrokesChanged;
    }

    private void OnStrokesChanged(object? s, StrokeCollectionChangedEventArgs e)
    {
        if (_suppressHistory) return;
        _history.Record(_page, e.Added, e.Removed);
        _dirty = true;
        UpdateUi();
    }

    private void Undo() => Apply(_history.PopUndo(), undo: true);
    private void Redo() => Apply(_history.PopRedo(), undo: false);
    private void Undo_Click(object s, RoutedEventArgs e) => Undo();
    private void Redo_Click(object s, RoutedEventArgs e) => Redo();

    private void Apply(InkHistory.Entry? entry, bool undo)
    {
        if (entry == null || _pdf == null) return;
        if (entry.Page != _page) { _page = entry.Page; RenderCurrent(); }   // 변경이 일어난 페이지로 이동
        var target = StrokesFor(entry.Page);
        var toRemove = undo ? entry.Added : entry.Removed;
        var toAdd = undo ? entry.Removed : entry.Added;

        _suppressHistory = true;
        try
        {
            foreach (var st in toRemove) target.Remove(st);
            foreach (var st in toAdd) target.Add(st);
        }
        finally { _suppressHistory = false; }
        _dirty = true;
        UpdateUi();
    }

    // ================================================================ 렌더링 / UI 갱신

    private void UpdateUi()
    {
        PageBox.Text = _pdf == null ? "" : (_page + 1).ToString();
        PageCountText.Text = $"/ {_pdf?.PageCount ?? 0}";
        ZoomText.Text = $"{_zoom * 100:0}%";
        UndoButton.IsEnabled = _history.CanUndo;
        RedoButton.IsEnabled = _history.CanRedo;
        SaveButton.IsEnabled = _pdf != null;
        Title = _pdf == null ? "PDF Pen" : $"{(_dirty ? "● " : "")}{Path.GetFileName(_pdf.FilePath)} - PDF Pen";
    }

    private async void RenderCurrent()
    {
        if (_pdf == null) return;
        var pdf = _pdf;
        int page = _page;
        int version = ++_renderVersion;

        var (wPt, hPt) = pdf.GetPageSize(page);
        double wDip = wPt * PtToDip, hDip = hPt * PtToDip;

        if (_fit != FitMode.None)
        {
            double availW = Scroller.ViewportWidth - 32, availH = Scroller.ViewportHeight - 32;
            // 첫 레이아웃 전에는 Viewport 가 0 → 창 크기로 대체
            if (Scroller.ViewportWidth <= 0) { availW = ActualWidth - 48; availH = ActualHeight - 160; }
            availW = Math.Max(50, availW); availH = Math.Max(50, availH);
            _zoom = _fit == FitMode.Page ? Math.Min(availW / wDip, availH / hDip) : availW / wDip;
            _zoom = Math.Clamp(_zoom, MinZoom, MaxZoom);
        }

        double dispW = wDip * _zoom, dispH = hDip * _zoom;
        PageImage.Width = dispW;
        PageImage.Height = dispH;

        // 필기 레이어: 좌표계는 PDF pt. 확대/축소는 재렌더링 없이 변환만 바꾼다.
        double scale = _zoom * PtToDip;
        Ink.Width = wPt;
        Ink.Height = hPt;
        Ink.LayoutTransform = new ScaleTransform(scale, scale);
        var strokes = StrokesFor(page);
        if (!ReferenceEquals(Ink.Strokes, strokes)) Ink.Strokes = strokes;
        HookStrokes(strokes);
        UpdateUi();

        var dpi = VisualTreeHelper.GetDpi(this);
        int pxW = (int)Math.Round(dispW * dpi.DpiScaleX);
        int pxH = (int)Math.Round(dispH * dpi.DpiScaleY);

        try
        {
            var bmp = await Task.Run(() => pdf.RenderPage(page, pxW, pxH));
            if (version == _renderVersion) PageImage.Source = bmp;   // 오래된 결과는 버림
        }
        catch (ObjectDisposedException) { }
        catch (Exception ex)
        {
            if (version == _renderVersion)
                MessageBox.Show(this, ex.Message, "렌더링 실패", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
