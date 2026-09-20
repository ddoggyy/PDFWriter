namespace PdfPen.Services.PdfService;

/// <summary>
/// 화면(좌상단 원점, 회전 반영, 단위 pt) 좌표 ↔ PDF 사용자 공간 좌표 변환.
/// 필기는 이 화면 pt 좌표로 저장하고, PDF 에 쓸 때만 PDF 좌표로 바꾼다.
/// </summary>
internal readonly struct PageGeometry
{
    private readonly double _left, _bottom, _bw, _bh;
    private readonly int _rotation;   // 0,1,2,3 = 0,90,180,270 (시계방향)

    public PageGeometry(double left, double bottom, double width, double height, int rotation)
    {
        _left = left; _bottom = bottom; _bw = width; _bh = height;
        _rotation = ((rotation % 4) + 4) % 4;
    }

    public double DisplayWidth => _rotation % 2 == 0 ? _bw : _bh;
    public double DisplayHeight => _rotation % 2 == 0 ? _bh : _bw;

    public (double X, double Y) PdfToDisplay(double px, double py)
    {
        double u = px - _left, v = py - _bottom;
        return _rotation switch
        {
            0 => (u, _bh - v),
            1 => (v, u),
            2 => (_bw - u, v),
            _ => (_bh - v, _bw - u),
        };
    }

    public (double X, double Y) DisplayToPdf(double x, double y)
    {
        double u, v;
        switch (_rotation)
        {
            case 0: u = x; v = _bh - y; break;
            case 1: v = x; u = y; break;
            case 2: u = _bw - x; v = y; break;
            default: v = _bh - x; u = _bw - y; break;
        }
        return (u + _left, v + _bottom);
    }
}
