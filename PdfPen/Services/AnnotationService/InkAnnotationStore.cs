using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using PDFiumCore;
using PdfPen.Services.PdfService;
using static PdfPen.Services.PdfService.PdfiumNative;

namespace PdfPen.Services.AnnotationService;

/// <summary>
/// 필기(Stroke) ↔ PDF Ink Annotation 변환.
///  · Import: 문서를 열 때 기존 Ink 주석을 편집 가능한 Stroke 로 옮기고, 메모리 상의 문서에서는 제거한다
///            (그래야 화면에 이중으로 그려지지 않는다. 디스크의 파일은 저장 전까지 그대로다).
///  · Export: 저장할 때 Stroke 를 Ink 주석으로 붙여 임시 파일로 저장한 뒤 다시 제거한다.
/// 좌표는 Stroke 쪽이 "페이지 좌상단 원점, pt", PDF 쪽이 사용자 공간이다.
/// </summary>
public static unsafe class InkAnnotationStore
{
    private const string HighlighterSubject = "PdfPen Highlighter";
    private const int FlagPrint = 4;

    public static Dictionary<int, StrokeCollection> Import(PdfDocument doc)
    {
        var result = new Dictionary<int, StrokeCollection>();
        lock (doc.Sync)
        {
            for (int p = 0; p < doc.PageCount; p++)
            {
                var page = fpdfview.FPDF_LoadPage(doc.Handle, p);
                if (page == null) continue;
                try
                {
                    var ph = page.__Instance;
                    var geo = GetGeometry(ph);
                    var strokes = new StrokeCollection();
                    for (int i = FPDFPage_GetAnnotCount(ph) - 1; i >= 0; i--)
                    {
                        var annot = FPDFPage_GetAnnot(ph, i);
                        if (annot == IntPtr.Zero) continue;
                        bool isInk = FPDFAnnot_GetSubtype(annot) == AnnotSubtypeInk;
                        if (isInk) ReadInk(annot, geo, strokes);
                        FPDFPage_CloseAnnot(annot);
                        if (isInk) FPDFPage_RemoveAnnot(ph, i);
                    }
                    if (strokes.Count > 0) result[p] = strokes;
                }
                finally { fpdfview.FPDF_ClosePage(page); }
            }
        }
        return result;
    }

    /// <summary>Stroke 를 주석으로 붙여 destPath 에 저장한다. 메모리 문서는 저장 후 원래 상태로 되돌린다.</summary>
    public static void SaveWithInk(PdfDocument doc, IReadOnlyDictionary<int, StrokeCollection> strokesByPage,
                                   string destPath, bool incremental)
    {
        var added = new List<(int Page, int Count)>();
        lock (doc.Sync)
        {
            try
            {
                foreach (var (p, strokes) in strokesByPage)
                {
                    if (strokes.Count == 0 || p < 0 || p >= doc.PageCount) continue;
                    var page = fpdfview.FPDF_LoadPage(doc.Handle, p);
                    if (page == null) continue;
                    try
                    {
                        int n = WriteInk(page.__Instance, strokes);
                        if (n > 0) { added.Add((p, n)); GenerateAppearances(page); }
                    }
                    finally { fpdfview.FPDF_ClosePage(page); }
                }
                WriteDocument(doc, destPath, incremental);
            }
            finally
            {
                foreach (var (p, n) in added)
                {
                    var page = fpdfview.FPDF_LoadPage(doc.Handle, p);
                    if (page == null) continue;
                    try
                    {
                        var ph = page.__Instance;
                        for (int k = 0; k < n; k++)
                            FPDFPage_RemoveAnnot(ph, FPDFPage_GetAnnotCount(ph) - 1);
                    }
                    finally { fpdfview.FPDF_ClosePage(page); }
                }
            }
        }
    }

    /// <summary>
    /// PDFium 은 주석의 외관 스트림(/AP)을 "렌더링할 때" 만들어 문서 객체에 기록한다.
    /// 저장 전에 페이지를 아주 작게 한 번 렌더링해 /AP 가 파일에 들어가게 한다
    /// (외관이 없으면 Acrobat 등 다른 뷰어에서 필기가 보이지 않을 수 있다).
    /// </summary>
    private static void GenerateAppearances(FpdfPageT page)
    {
        var pixels = new byte[8 * 8 * 4];
        fixed (byte* px = pixels)
        {
            var bmp = fpdfview.FPDFBitmapCreateEx(8, 8, 3, (IntPtr)px, 32);
            try { fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, 8, 8, 0, 0x01); }
            finally { fpdfview.FPDFBitmapDestroy(bmp); }
        }
    }

    /// <summary>문서 전체의 Ink 경로 개수(저장 결과 검증용).</summary>
    public static int CountInkPaths(PdfDocument doc)
    {
        int total = 0;
        lock (doc.Sync)
        {
            for (int p = 0; p < doc.PageCount; p++)
            {
                var page = fpdfview.FPDF_LoadPage(doc.Handle, p);
                if (page == null) continue;
                try
                {
                    var ph = page.__Instance;
                    for (int i = 0; i < FPDFPage_GetAnnotCount(ph); i++)
                    {
                        var annot = FPDFPage_GetAnnot(ph, i);
                        if (annot == IntPtr.Zero) continue;
                        if (FPDFAnnot_GetSubtype(annot) == AnnotSubtypeInk)
                            total += (int)FPDFAnnot_GetInkListCount(annot);
                        FPDFPage_CloseAnnot(annot);
                    }
                }
                finally { fpdfview.FPDF_ClosePage(page); }
            }
        }
        return total;
    }

    // ---------------------------------------------------------------- 읽기

    private static void ReadInk(IntPtr annot, PageGeometry geo, StrokeCollection into)
    {
        // PDFium 은 외관 스트림(/AP)이 있는 주석의 색을 돌려주지 않는다. 이 주석은 곧 메모리 문서에서 제거되므로 AP 를 먼저 뗀다.
        FPDFAnnot_SetAP(annot, 0, null);
        uint r = 0, g = 0, b = 0, a = 255;
        if (FPDFAnnot_GetColor(annot, 0, &r, &g, &b, &a) == 0) { r = g = b = 0; a = 255; }
        float h = 0, v = 0, w = 1;
        if (FPDFAnnot_GetBorder(annot, &h, &v, &w) == 0 || w <= 0) w = 1;

        bool highlighter = a < 250 || GetString(annot, "Subj") == HighlighterSubject;
        var color = Color.FromArgb((byte)a, (byte)r, (byte)g, (byte)b);

        int count = (int)FPDFAnnot_GetInkListCount(annot);
        for (int i = 0; i < count; i++)
        {
            int len = (int)FPDFAnnot_GetInkListPath(annot, (nuint)i, null, 0);
            if (len < 1) continue;
            var pts = new PointF[len];
            fixed (PointF* pp = pts) FPDFAnnot_GetInkListPath(annot, (nuint)i, pp, (nuint)len);

            var spc = new StylusPointCollection(len);
            foreach (var pt in pts)
            {
                var (x, y) = geo.PdfToDisplay(pt.X, pt.Y);
                spc.Add(new StylusPoint(x, y));
            }
            if (spc.Count == 1) spc.Add(new StylusPoint(spc[0].X + 0.01, spc[0].Y));

            into.Add(new Stroke(spc, MakeAttributes(color, w, highlighter, ignorePressure: true)));
        }
    }

    private static string GetString(IntPtr annot, string key)
    {
        var needed = (int)FPDFAnnot_GetStringValue(annot, key, null, 0);
        if (needed <= 2) return "";
        var buf = new ushort[needed / 2];
        fixed (ushort* p = buf) FPDFAnnot_GetStringValue(annot, key, p, (nuint)needed);
        return new string(MemoryMarshal.Cast<ushort, char>(buf)).TrimEnd('\0');
    }

    public static DrawingAttributes MakeAttributes(Color color, double width, bool highlighter, bool ignorePressure = false) => new()
    {
        Color = color,
        Width = width,
        Height = width,
        FitToCurve = true,
        IgnorePressure = ignorePressure || highlighter,
        IsHighlighter = highlighter,
        StylusTip = highlighter ? StylusTip.Rectangle : StylusTip.Ellipse,
    };

    // ---------------------------------------------------------------- 쓰기

    private static int WriteInk(IntPtr page, StrokeCollection strokes)
    {
        var geo = GetGeometry(page);
        int created = 0;

        // 같은 색/굵기/종류의 획은 하나의 Ink 주석(InkList 여러 개)으로 묶는다 → 객체 수 감소
        var groups = strokes
            .Where(s => s.StylusPoints.Count > 0)
            .GroupBy(s => (s.DrawingAttributes.Color, W: Math.Round(s.DrawingAttributes.Width, 2), s.DrawingAttributes.IsHighlighter));

        foreach (var group in groups)
        {
            var annot = FPDFPage_CreateAnnot(page, AnnotSubtypeInk);
            if (annot == IntPtr.Zero) continue;

            double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
            foreach (var stroke in group)
            {
                var pts = ToPdfPoints(stroke, geo);
                fixed (PointF* pp = pts) FPDFAnnot_AddInkStroke(annot, pp, (nuint)pts.Length);
                foreach (var p in pts)
                {
                    minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                    minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
                }
            }

            var (color, width, highlighter) = group.Key;
            float pad = (float)(width / 2 + 1);
            var rect = new RectF
            {
                Left = (float)minX - pad, Bottom = (float)minY - pad,
                Right = (float)maxX + pad, Top = (float)maxY + pad   // PDF 좌표: Top 이 더 큰 y
            };
            FPDFAnnot_SetRect(annot, &rect);
            FPDFAnnot_SetColor(annot, 0, color.R, color.G, color.B, color.A);
            FPDFAnnot_SetBorder(annot, 0, 0, (float)width);
            FPDFAnnot_SetFlags(annot, FlagPrint);
            if (highlighter) FPDFAnnot_SetStringValue(annot, "Subj", HighlighterSubject);
            FPDFPage_CloseAnnot(annot);
            created++;
        }
        return created;
    }

    private static PointF[] ToPdfPoints(Stroke stroke, PageGeometry geo)
    {
        var list = new List<PointF>(stroke.StylusPoints.Count);
        double lx = double.NaN, ly = double.NaN;
        foreach (var sp in stroke.StylusPoints)
        {
            // 0.1pt 미만의 미세 이동은 버려 파일 크기를 줄인다
            if (!double.IsNaN(lx) && Math.Abs(sp.X - lx) < 0.1 && Math.Abs(sp.Y - ly) < 0.1) continue;
            lx = sp.X; ly = sp.Y;
            var (px, py) = geo.DisplayToPdf(sp.X, sp.Y);
            list.Add(new PointF { X = (float)px, Y = (float)py });
        }
        if (list.Count == 1) list.Add(list[0]);
        return list.ToArray();
    }

    private static PageGeometry GetGeometry(IntPtr page)
    {
        RectF box;
        FPDF_GetPageBoundingBox(page, &box);
        return new PageGeometry(box.Left, box.Bottom, box.Right - box.Left, box.Top - box.Bottom,
                                FPDFPage_GetRotation(page));
    }

    private static void WriteDocument(PdfDocument doc, string path, bool incremental)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        Exception? failure = null;
        WriteBlockFn callback = (_, data, size) =>
        {
            try
            {
                fs.Write(new ReadOnlySpan<byte>((void*)data, (int)size));
                return 1;
            }
            catch (Exception ex) { failure = ex; return 0; }
        };

        var writer = (FileWrite*)Marshal.AllocHGlobal(sizeof(FileWrite));
        try
        {
            writer->Version = 1;
            writer->WriteBlock = Marshal.GetFunctionPointerForDelegate(callback);
            int ok = FPDF_SaveAsCopy(doc.Handle.__Instance, writer, incremental ? (uint)SaveIncremental : 0u);
            GC.KeepAlive(callback);
            if (ok == 0) throw new IOException("PDF 저장에 실패했습니다.", failure);
            fs.Flush(true);
        }
        finally { Marshal.FreeHGlobal((IntPtr)writer); }
    }
}
