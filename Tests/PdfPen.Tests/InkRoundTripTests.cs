using System.IO;
using System.Text;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using PDFiumCore;
using PdfPen.Services.AnnotationService;
using PdfPen.Services.FileService;
using PdfPen.Services.PdfService;

namespace PdfPen.Tests;

public class InkRoundTripTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pdfpen-tests-" + Guid.NewGuid().ToString("N"));

    static InkRoundTripTests() => fpdfview.FPDF_InitLibrary();

    public InkRoundTripTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { if (Environment.GetEnvironmentVariable("PDFPEN_KEEP") == "1") return; try { Directory.Delete(_dir, true); } catch { } }

    // 최소한의 PDF (xref 오프셋 없음: PDFium 이 복구한다). 1페이지 = 정상, 2페이지 = 90도 회전.
    private string MakePdf(string name = "sample.pdf")
    {
        const string body =
            "%PDF-1.4\n" +
            "1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n" +
            "2 0 obj<</Type/Pages/Kids[3 0 R 4 0 R]/Count 2>>endobj\n" +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 600]/Contents 5 0 R>>endobj\n" +
            "4 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 400 600]/Rotate 90/Contents 5 0 R>>endobj\n" +
            "5 0 obj<</Length 44>>stream\n0.9 g 50 50 300 500 re f 0 g 60 60 20 20 re f\nendstream endobj\n" +
            "trailer<</Root 1 0 R/Size 6>>\n%%EOF\n";
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(body));
        return path;
    }

    private static Stroke MakeStroke(double x0, double y0, Color color, double width, bool hl = false)
    {
        var pts = new StylusPointCollection();
        for (int i = 0; i < 30; i++) pts.Add(new StylusPoint(x0 + i * 3, y0 + Math.Sin(i / 4.0) * 20));
        return new Stroke(pts, InkAnnotationStore.MakeAttributes(color, width, hl));
    }

    private static Dictionary<int, StrokeCollection> Sample() => new()
    {
        [0] = new StrokeCollection
        {
            MakeStroke(50, 100, Colors.Red, 2),
            MakeStroke(50, 200, Colors.Red, 2),                                   // 같은 속성 → 한 주석으로 묶임
            MakeStroke(50, 300, Colors.Blue, 4),
            MakeStroke(50, 400, Color.FromArgb(0x66, 255, 255, 0), 14, hl: true),
        },
        [1] = new StrokeCollection { MakeStroke(100, 150, Colors.Green, 3) },
    };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Strokes_survive_save_and_reload(bool incremental)
    {
        var src = MakePdf();
        var original = Sample();
        var dest = Path.Combine(_dir, "out.pdf");

        using (var doc = PdfDocument.Open(src))
            InkAnnotationStore.SaveWithInk(doc, original, dest, incremental);

        using var reopened = PdfDocument.Open(dest);
        Assert.Equal(2, reopened.PageCount);
        Assert.Equal(5, InkAnnotationStore.CountInkPaths(reopened));

        var loaded = InkAnnotationStore.Import(reopened);
        Assert.Equal(4, loaded[0].Count);
        Assert.Single(loaded[1]);

        // 좌표(회전 페이지 포함)가 0.1pt 이내로 복원되는지
        foreach (var page in new[] { 0, 1 })
        {
            foreach (var o in original[page])
            {
                var match = loaded[page].FirstOrDefault(l =>
                    Math.Abs(l.StylusPoints[0].X - o.StylusPoints[0].X) < 0.1 &&
                    Math.Abs(l.StylusPoints[0].Y - o.StylusPoints[0].Y) < 0.1);
                Assert.NotNull(match);
                Assert.Equal(o.StylusPoints.Count, match!.StylusPoints.Count);
                Assert.Equal(o.DrawingAttributes.Width, match.DrawingAttributes.Width, 2);
                Assert.Equal(o.DrawingAttributes.Color.R, match.DrawingAttributes.Color.R);
                Assert.Equal(o.DrawingAttributes.Color.B, match.DrawingAttributes.Color.B);
                Assert.Equal(o.DrawingAttributes.IsHighlighter, match.DrawingAttributes.IsHighlighter);
                var last = o.StylusPoints.Count - 1;
                Assert.Equal(o.StylusPoints[last].X, match.StylusPoints[last].X, 1);
                Assert.Equal(o.StylusPoints[last].Y, match.StylusPoints[last].Y, 1);
            }
        }
    }

    [Fact]
    public void Import_removes_ink_from_working_document_so_it_is_not_drawn_twice()
    {
        var dest = Path.Combine(_dir, "out.pdf");
        using (var doc = PdfDocument.Open(MakePdf()))
            InkAnnotationStore.SaveWithInk(doc, Sample(), dest, true);

        using var reopened = PdfDocument.Open(dest);
        Assert.NotEmpty(InkAnnotationStore.Import(reopened));
        Assert.Equal(0, InkAnnotationStore.CountInkPaths(reopened));
    }

    [Fact]
    public void Saving_does_not_leave_annotations_in_working_document()
    {
        using var doc = PdfDocument.Open(MakePdf());
        InkAnnotationStore.SaveWithInk(doc, Sample(), Path.Combine(_dir, "a.pdf"), true);
        Assert.Equal(0, InkAnnotationStore.CountInkPaths(doc));
        // 두 번 저장해도 결과가 같아야 한다(주석이 누적되지 않음)
        InkAnnotationStore.SaveWithInk(doc, Sample(), Path.Combine(_dir, "b.pdf"), true);
        using var b = PdfDocument.Open(Path.Combine(_dir, "b.pdf"));
        Assert.Equal(5, InkAnnotationStore.CountInkPaths(b));
    }

    [Fact]
    public void Incremental_save_keeps_original_bytes_untouched()
    {
        var src = MakePdf();
        var before = File.ReadAllBytes(src);
        var dest = Path.Combine(_dir, "out.pdf");
        using (var doc = PdfDocument.Open(src))
            InkAnnotationStore.SaveWithInk(doc, Sample(), dest, true);
        var after = File.ReadAllBytes(dest);
        Assert.True(after.Length > before.Length);
        Assert.True(after.AsSpan(0, before.Length).SequenceEqual(before), "원본 바이트가 그대로 앞부분에 보존되어야 함");
    }

    [Fact]
    public void Saved_annotation_carries_appearance_stream_for_other_viewers()
    {
        var dest = Path.Combine(_dir, "out.pdf");
        using (var doc = PdfDocument.Open(MakePdf()))
            InkAnnotationStore.SaveWithInk(doc, Sample(), dest, true);
        var text = Encoding.Latin1.GetString(File.ReadAllBytes(dest));
        Assert.Contains("/Subtype/Ink", text.Replace(" ", ""));
        Assert.Contains("/InkList", text);
        Assert.Contains("/AP", text);   // 외관 스트림이 있어야 Acrobat 등에서도 보인다
    }

    [Fact]
    public void Atomic_save_replaces_original_and_leaves_no_temp_files()
    {
        var path = MakePdf();
        using (var doc = PdfDocument.Open(path))
            AtomicPdfSaver.Save(doc, Sample(), path);

        Assert.Equal(new[] { "sample.pdf" }, Directory.GetFiles(_dir).Select(Path.GetFileName).ToArray());
        using var reopened = PdfDocument.Open(path);
        Assert.Equal(5, InkAnnotationStore.CountInkPaths(reopened));
    }

    [Fact]
    public void Erasing_all_strokes_and_saving_removes_previous_ink()
    {
        var path = MakePdf();
        using (var doc = PdfDocument.Open(path)) AtomicPdfSaver.Save(doc, Sample(), path);

        using (var doc = PdfDocument.Open(path))
        {
            var loaded = InkAnnotationStore.Import(doc);   // 사용자가 전부 지운 상황
            loaded.Clear();
            AtomicPdfSaver.Save(doc, loaded, path);
        }
        using var final = PdfDocument.Open(path);
        Assert.Equal(0, InkAnnotationStore.CountInkPaths(final));
    }

    // 저장된 주석이 "PDF 안의 올바른 위치"에 그려지는지(내부 좌표 왕복이 아니라 렌더링 결과로 확인)
    [Theory]
    [InlineData(0, 400, 600)]   // 정상 페이지
    [InlineData(1, 600, 400)]   // 90도 회전 페이지 (표시 크기가 바뀜)
    public void Saved_ink_renders_at_the_correct_position(int page, int w, int h)
    {
        // 본문의 검은 사각형(PDF 좌표 60..80, 60..80)을 파란 굵은 선으로 덮는다.
        // 표시 좌표(좌상단 원점, 회전 반영)에서의 위치: 정상 페이지 (70,530), 90도 회전 페이지 (70,70)
        double cx = page == 0 ? 70 : 70, cy = page == 0 ? 530 : 70;
        var pts = new StylusPointCollection();
        for (int i = -8; i <= 8; i++) pts.Add(new StylusPoint(cx + i, cy));
        var strokes = new Dictionary<int, StrokeCollection>
        {
            [page] = new StrokeCollection { new Stroke(pts, InkAnnotationStore.MakeAttributes(Colors.Blue, 12, false)) }
        };

        var dest = Path.Combine(_dir, "out.pdf");
        using (var doc = PdfDocument.Open(MakePdf()))
            InkAnnotationStore.SaveWithInk(doc, strokes, dest, true);

        using var reopened = PdfDocument.Open(dest);
        var bmp = reopened.RenderPage(page, w, h);      // 1픽셀 = 1pt
        var px = new byte[4];
        bmp.CopyPixels(new System.Windows.Int32Rect((int)cx, (int)cy, 1, 1), px, 4, 0);
        Assert.True(px[0] > 200 && px[1] < 60 && px[2] < 60, $"기대: 파랑, 실제 BGR=({px[0]},{px[1]},{px[2]})");
    }
}
