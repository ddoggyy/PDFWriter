using System.Windows;
using PDFiumCore;

namespace PdfPen;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        fpdfview.FPDF_InitLibrary();
        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        fpdfview.FPDF_DestroyLibrary();
        base.OnExit(e);
    }
}
