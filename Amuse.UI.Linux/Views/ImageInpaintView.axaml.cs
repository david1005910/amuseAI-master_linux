using Amuse.UI.Linux.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Amuse.UI.Linux.Views
{
    public partial class ImageInpaintView : UserControl
    {
        public ImageInpaintView()
        {
            DataContext = new ImageInpaintViewModel();
            InitializeComponent();
        }

        private async void OnBrowseInputImageClick(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Source Image",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Image Files")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp" }
                    },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } },
                },
            });

            if (files.Count > 0 && DataContext is ImageInpaintViewModel vm)
                vm.SetInputImage(files[0].Path.LocalPath);
        }

        private async void OnBrowseMaskImageClick(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Mask Image (white = fill area)",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Image Files")
                    {
                        Patterns = new[] { "*.png", "*.jpg", "*.jpeg", "*.bmp" }
                    },
                    new FilePickerFileType("All Files") { Patterns = new[] { "*.*" } },
                },
            });

            if (files.Count > 0 && DataContext is ImageInpaintViewModel vm)
                vm.SetMaskImage(files[0].Path.LocalPath);
        }

        private async void OnPastePromptClick(object sender, RoutedEventArgs e)
        {
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard == null) return;
            var text = await clipboard.GetTextAsync();
            if (!string.IsNullOrEmpty(text) && DataContext is ImageInpaintViewModel vm)
                vm.Prompt = text;
        }
    }
}
