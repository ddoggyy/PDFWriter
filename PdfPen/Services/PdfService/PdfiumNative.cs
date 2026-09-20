using System.Runtime.InteropServices;

namespace PdfPen.Services.PdfService;

/// <summary>
/// PDFiumCore 래퍼가 다루기 불편한 함수(배열 인자, 콜백)만 pdfium 을 직접 호출한다.
/// 같은 pdfium 모듈을 공유하므로 PDFiumCore 가 만든 핸들(__Instance)을 그대로 넘길 수 있다.
/// </summary>
internal static unsafe class PdfiumNative
{
    private const string Lib = "pdfium";

    public const int AnnotSubtypeInk = 15;
    public const int SaveIncremental = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PointF { public float X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RectF { public float Left, Top, Right, Bottom; }   // PDFium 의 FS_RECTF 필드 순서 (top > bottom)

    [StructLayout(LayoutKind.Sequential)]
    public struct FileWrite { public int Version; public IntPtr WriteBlock; }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int WriteBlockFn(IntPtr self, IntPtr data, uint size);

    [DllImport(Lib)] public static extern int FPDFPage_GetRotation(IntPtr page);
    [DllImport(Lib)] public static extern int FPDF_GetPageBoundingBox(IntPtr page, RectF* rect);

    [DllImport(Lib)] public static extern int FPDFPage_GetAnnotCount(IntPtr page);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_GetAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern IntPtr FPDFPage_CreateAnnot(IntPtr page, int subtype);
    [DllImport(Lib)] public static extern int FPDFPage_RemoveAnnot(IntPtr page, int index);
    [DllImport(Lib)] public static extern void FPDFPage_CloseAnnot(IntPtr annot);

    [DllImport(Lib)] public static extern int FPDFAnnot_GetSubtype(IntPtr annot);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetRect(IntPtr annot, RectF* rect);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetAP(IntPtr annot, int apMode, ushort* value);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetFlags(IntPtr annot, int flags);
    [DllImport(Lib)] public static extern int FPDFAnnot_AddInkStroke(IntPtr annot, PointF* points, nuint count);
    [DllImport(Lib)] public static extern nuint FPDFAnnot_GetInkListCount(IntPtr annot);
    [DllImport(Lib)] public static extern nuint FPDFAnnot_GetInkListPath(IntPtr annot, nuint pathIndex, PointF* buffer, nuint length);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetColor(IntPtr annot, int colorType, uint r, uint g, uint b, uint a);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetColor(IntPtr annot, int colorType, uint* r, uint* g, uint* b, uint* a);
    [DllImport(Lib)] public static extern int FPDFAnnot_SetBorder(IntPtr annot, float horizontalRadius, float verticalRadius, float borderWidth);
    [DllImport(Lib)] public static extern int FPDFAnnot_GetBorder(IntPtr annot, float* horizontalRadius, float* verticalRadius, float* borderWidth);
    [DllImport(Lib, CharSet = CharSet.Unicode)]
    public static extern int FPDFAnnot_SetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, string value);
    [DllImport(Lib)]
    public static extern nuint FPDFAnnot_GetStringValue(IntPtr annot, [MarshalAs(UnmanagedType.LPStr)] string key, ushort* buffer, nuint buflen);

    [DllImport(Lib)] public static extern int FPDF_SaveAsCopy(IntPtr document, FileWrite* writer, uint flags);
}
