using Avalonia.Controls;
using Moist.App.ViewModels;

namespace Moist.App.Views;

/// <summary>
/// A resizable window that previews an encode: renders a short snippet through the real pipeline
/// and shows it scrubbable and/or side-by-side against the source. Disposes its view-model on close
/// so preview bitmaps and any in-flight render are released.
/// </summary>
public partial class PreviewWindow : Window
{
    public PreviewWindow()
    {
        InitializeComponent();
    }

    public PreviewWindow(PreviewViewModel viewModel) : this()
    {
        DataContext = viewModel;
        Closed += (_, _) => viewModel.Dispose();
    }
}
