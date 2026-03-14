using Avalonia.Controls;
using Moist.App.ViewModels;

namespace Moist.App.Views;

public partial class QueueTabView : UserControl
{
    public QueueTabView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is QueueTabViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.LogText))
                    ScrollLogToEnd();
            };
        }
    }

    private void ScrollLogToEnd()
    {
        var textBox = this.FindControl<TextBox>("LogTextBox");
        if (textBox is null || string.IsNullOrEmpty(textBox.Text)) return;
        textBox.CaretIndex = textBox.Text.Length;
    }
}
