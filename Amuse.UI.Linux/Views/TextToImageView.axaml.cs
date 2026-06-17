using Amuse.UI.Linux.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Amuse.UI.Linux.Views
{
    public partial class TextToImageView : UserControl
    {
        public TextToImageView()
        {
            DataContext = new TextToImageViewModel();
            InitializeComponent();
        }

        private async void OnPastePromptClick(object sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text) && DataContext is TextToImageViewModel vm)
                vm.Prompt = text;
        }
    }
}
