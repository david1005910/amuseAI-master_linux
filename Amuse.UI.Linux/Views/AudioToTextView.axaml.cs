using Amuse.UI.Linux.ViewModels;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using System;

namespace Amuse.UI.Linux.Views
{
    public partial class AudioToTextView : UserControl
    {
        public AudioToTextView()
        {
            InitializeComponent();
            DataContext = new AudioToTextViewModel();

            var btn = this.FindControl<Button>("SelectFileButton");
            if (btn != null)
                btn.Click += OnSelectFile;
        }

        private async void OnSelectFile(object sender, RoutedEventArgs e)
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Audio File",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Audio Files")
                    {
                        Patterns = new[] { "*.wav", "*.mp3", "*.ogg", "*.flac", "*.m4a", "*.aac" }
                    },
                    new FilePickerFileType("All Files")
                    {
                        Patterns = new[] { "*.*" }
                    },
                },
            });

            if (files.Count > 0 && DataContext is AudioToTextViewModel vm)
                vm.SelectedAudioPath = files[0].Path.LocalPath;
        }
    }
}
