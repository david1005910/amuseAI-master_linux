using Amuse.UI.Linux.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Amuse.UI.Linux.Views
{
    public partial class TextToVideoView : UserControl
    {
        public TextToVideoView()
        {
            InitializeComponent();
            DataContext = new TextToVideoViewModel();
        }

        private async void OnPastePromptClick(object sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text) && DataContext is TextToVideoViewModel vm)
                vm.Prompt = text;
        }
    }
}
