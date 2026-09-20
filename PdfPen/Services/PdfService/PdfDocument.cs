using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PDFiumCore;

namespace PdfPen.Services.PdfService;

/// <summary>
/// PDFium 문서 래퍼. 파일 전체를 메모리로 읽어 열기 때문에 디스크의 PDF를
/// 잠그지 않는다(나중에 임시 파일 → 원본 교체 방식의 Atomic Save가 가능).
/// PDFium은 스레드 안전하지 않으므로 모든 호출은 <see cref="Sync"/> 로 직렬화한다.
/// </summary>
public sealed class PdfDocument : IDisposable
{
    private const int RenderAnnotFlag = 0x01;   // FPDF_ANNOT
    private const int BitmapBgrx = 3;           // FPDFBitmap_BGRx

    private readonly byte[] _data;
    private GCHandle _pin;
    private FpdfDocumentT? _doc;

    public object Sync { get; } = new();
    public string FilePath { get; set; }
    internal FpdfDocumentT Handle => Doc;
    public int PageCount { get; }

    private PdfDocument(string path, byte[] data, GCHandle pin, FpdfDocumentT doc)
    {
        FilePath = path;
        _data = data;
        _pin = pin;
        _doc = doc;
        PageCount = fpdfview.FPDF_GetPageCount(doc);
    }

    public static PdfDocument Open(string path)
    {
        var data = File.ReadAllBytes(path);
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        var doc = fpdfview.FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), data.Length, null);
        if (doc == null)
        {
            var err = fpdfview.FPDF_GetLastError();
            pin.Free();
            throw new InvalidDataException(err switch
            {
                3 => "암호로 보호된 PDF는 아직 지원하지 않습니다.",
                2 => "PDF 형식이 올바르지 않습니다.",
                _ => $"PDF를 열 수 없습니다. (PDFium 오류 {err})"
            });
        }
        return new PdfDocument(path, data, pin, doc);
    }

    /// <summary>페이지 크기(PDF 포인트, 1pt = 1/72inch).</summary>
    public (double Width, double Height) GetPageSize(int index)
    {
        lock (Sync)
        {
            var size = new FS_SIZEF_();
            return fpdfview.FPDF_GetPageSizeByIndexF(Doc, index, size) != 0
                ? (size.Width, size.Height)
                : (595, 842);
        }
    }

    /// <summary>페이지를 pixelWidth x pixelHeight 로 렌더링한다. 어느 스레드에서든 호출 가능.</summary>
    public BitmapSource RenderPage(int index, int pixelWidth, int pixelHeight)
    {
        pixelWidth = Math.Clamp(pixelWidth, 1, 16384);
        pixelHeight = Math.Clamp(pixelHeight, 1, 16384);
        int stride = pixelWidth * 4;
        var pixels = new byte[stride * pixelHeight];
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            lock (Sync)
            {
                var page = fpdfview.FPDF_LoadPage(Doc, index);
                if (page == null) throw new InvalidOperationException($"{index + 1}페이지를 불러올 수 없습니다.");
                var bmp = fpdfview.FPDFBitmapCreateEx(pixelWidth, pixelHeight, BitmapBgrx, handle.AddrOfPinnedObject(), stride);
                try
                {
                    fpdfview.FPDFBitmapFillRect(bmp, 0, 0, pixelWidth, pixelHeight, 0xFFFFFFFF);
                    fpdfview.FPDF_RenderPageBitmap(bmp, page, 0, 0, pixelWidth, pixelHeight, 0, RenderAnnotFlag);
                }
                finally
                {
                    fpdfview.FPDFBitmapDestroy(bmp);
                    fpdfview.FPDF_ClosePage(page);
                }
            }
        }
        finally { handle.Free(); }

        var result = BitmapSource.Create(pixelWidth, pixelHeight, 96, 96, PixelFormats.Bgr32, null, pixels, stride);
        result.Freeze();
        return result;
    }

    private FpdfDocumentT Doc => _doc ?? throw new ObjectDisposedException(nameof(PdfDocument));

    public void Dispose()
    {
        lock (Sync)
        {
            if (_doc == null) return;
            fpdfview.FPDF_CloseDocument(_doc);
            _doc = null;
            _pin.Free();
        }
    }
}
