using Avalonia.Controls;
using Avalonia.Interactivity;
using Moist.App.ViewModels;

namespace Moist.App.Views;

public partial class FilterTabView : UserControl
{
    public FilterTabView()
    {
        InitializeComponent();
    }

    private async void SavePreset_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not FilterTabViewModel vm) return;

            var dialog = new Window
            {
                Title = "Save Filter Preset",
                Width = 400,
                Height = 150,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                CanResize = false,
            };

            var textBox = new TextBox
            {
                Watermark = "Preset name...",
                Margin = new Avalonia.Thickness(16, 16, 16, 8),
            };

            var saveButton = new Button
            {
                Content = "Save",
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                Margin = new Avalonia.Thickness(16, 0, 16, 16),
            };

            saveButton.Click += (_, _) =>
            {
                if (!string.IsNullOrWhiteSpace(textBox.Text))
                {
                    vm.SaveCurrentAsPresetCommand.Execute(textBox.Text);
                    dialog.Close();
                }
            };

            dialog.Content = new StackPanel
            {
                Children = { textBox, saveButton }
            };

            var parent = TopLevel.GetTopLevel(this) as Window;
            if (parent is not null)
                await dialog.ShowDialog(parent);
        }
        catch { /* Dialog failure is non-critical */ }
    }
}
