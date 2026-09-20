using System.IO;
using System.Windows.Ink;
using PdfPen.Services.AnnotationService;
using PdfPen.Services.PdfService;

namespace PdfPen.Services.FileService;

/// <summary>
/// 안전한 저장: 같은 폴더의 임시 파일에 먼저 쓰고 → 다시 열어 검증하고 → File.Replace 로 원본을 교체한다.
/// 어느 단계에서 실패/종료돼도 원본 PDF 는 손상되지 않는다. 백업(.bak)은 교체 중에만 존재하고 성공하면 지운다.
/// </summary>
public static class AtomicPdfSaver
{
    public static void Save(PdfDocument doc, IReadOnlyDictionary<int, StrokeCollection> strokes, string destPath)
    {
        int expected = strokes.Values.Sum(CountPaths);
        string dir = Path.GetDirectoryName(Path.GetFullPath(destPath))!;
        string tmp = Path.Combine(dir, $".{Path.GetFileName(destPath)}.{Guid.NewGuid():N}.pdfpen.tmp");
        string bak = destPath + ".pdfpen.bak";

        try
        {
            // 전체 재작성이 기본이다: 결과 크기 ≈ 원본 + 필기 분량.
            // (PDFium 의 증분 저장은 메모리에 로드된 객체를 모두 덧붙여 파일이 2배가 되므로 예비 수단으로만 쓴다.)
            if (!TryWriteAndVerify(doc, strokes, tmp, incremental: false, expected, doc.PageCount) &&
                !TryWriteAndVerify(doc, strokes, tmp, incremental: true, expected, doc.PageCount))
                throw new IOException("저장한 PDF를 검증하지 못했습니다. 원본 파일은 변경되지 않았습니다.");

            if (File.Exists(destPath))
            {
                File.Replace(tmp, destPath, bak, ignoreMetadataErrors: true);
                try { File.Delete(bak); } catch { /* 남아 있어도 무해 */ }
            }
            else
            {
                File.Move(tmp, destPath);
            }
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static bool TryWriteAndVerify(PdfDocument doc, IReadOnlyDictionary<int, StrokeCollection> strokes,
                                          string tmp, bool incremental, int expectedPaths, int expectedPages)
    {
        try
        {
            InkAnnotationStore.SaveWithInk(doc, strokes, tmp, incremental);
            using var check = PdfDocument.Open(tmp);
            return check.PageCount == expectedPages && InkAnnotationStore.CountInkPaths(check) == expectedPaths;
        }
        catch
        {
            return false;
        }
    }

    // 점이 하나뿐인 획도 경로 1개로 저장되므로 그대로 센다
    private static int CountPaths(StrokeCollection c) => c.Count(s => s.StylusPoints.Count > 0);
}
