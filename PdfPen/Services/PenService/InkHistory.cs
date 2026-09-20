using System.Windows.Ink;

namespace PdfPen.Services.PenService;

/// <summary>문서 전체 Undo/Redo. 각 항목은 "어느 페이지에서 어떤 획이 추가/삭제됐는가"만 기록한다.</summary>
public sealed class InkHistory
{
    public sealed record Entry(int Page, Stroke[] Added, Stroke[] Removed);

    private readonly Stack<Entry> _undo = new();
    private readonly Stack<Entry> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Record(int page, IEnumerable<Stroke> added, IEnumerable<Stroke> removed)
    {
        var e = new Entry(page, added.ToArray(), removed.ToArray());
        if (e.Added.Length == 0 && e.Removed.Length == 0) return;
        _undo.Push(e);
        _redo.Clear();
    }

    public Entry? PopUndo()
    {
        if (!_undo.TryPop(out var e)) return null;
        _redo.Push(e);
        return e;
    }

    public Entry? PopRedo()
    {
        if (!_redo.TryPop(out var e)) return null;
        _undo.Push(e);
        return e;
    }

    public void Clear() { _undo.Clear(); _redo.Clear(); }
}
