using Amuse.UI.Linux.ViewModels;
using Amuse.UI.Linux.Views;
using Avalonia.Controls;
using Avalonia.Input;
using System.Collections.Generic;
using System.IO;

namespace Amuse.UI.Linux
{
    public partial class MainWindow : Window
    {
        private readonly MainWindowViewModel _vm;

        public MainWindow()
        {
            _vm = new MainWindowViewModel();
            DataContext = _vm;
            InitializeComponent();
            UpdateSideMenu(_vm.ViewCategory);
            UpdateSelectedButton(_vm.CurrentView);
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.ViewCategory))
                    UpdateSideMenu(_vm.ViewCategory);
                if (e.PropertyName == nameof(MainWindowViewModel.CurrentView))
                    UpdateSelectedButton(_vm.CurrentView);
            };
            DetectGpu();
        }

        private void OnTitleBarDrag(object sender, PointerPressedEventArgs e)
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                BeginMoveDrag(e);
        }

        private void OnMinimize(object sender, Avalonia.Interactivity.RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void OnMaximize(object sender, Avalonia.Interactivity.RoutedEventArgs e)
            => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

        private void OnClose(object sender, Avalonia.Interactivity.RoutedEventArgs e)
            => Close();

        private void UpdateSideMenu(ViewCategory category)
        {
            ImageMenu.IsVisible    = category == ViewCategory.Image;
            VideoMenu.IsVisible    = category == ViewCategory.Video;
            AudioMenu.IsVisible    = category == ViewCategory.Audio;
            SettingsMenu.IsVisible = category == ViewCategory.Settings;
        }

        private void UpdateSelectedButton(View view)
        {
            foreach (var btn in GetSubNavButtons())
                btn.Classes.Remove("Selected");
            GetButtonForView(view)?.Classes.Add("Selected");
        }

        private Button GetButtonForView(View view) => view switch
        {
            View.TextToImage      => BtnTextToImage,
            View.ImageToImage     => BtnImageToImage,
            View.ImageEdit        => BtnImageEdit,
            View.ImageInpaint     => BtnImageInpaint,
            View.PaintToImage     => BtnPaintToImage,
            View.ImageUpscale     => BtnImageUpscale,
            View.ImageExtract     => BtnImageExtract,
            View.ImageCompose     => BtnImageCompose,
            View.TextToVideo      => BtnTextToVideo,
            View.ImageToVideo     => BtnImageToVideo,
            View.VideoToVideo     => BtnVideoToVideo,
            View.FrameToFrame     => BtnFrameToFrame,
            View.VideoUpscale     => BtnVideoUpscale,
            View.VideoExtract     => BtnVideoExtract,
            View.VideoInterpolate => BtnVideoInterpolate,
            View.VideoCompose     => BtnVideoCompose,
            View.TextToMusic      => BtnTextToMusic,
            View.TextToAudio      => BtnTextToAudio,
            View.AudioToText      => BtnAudioToText,
            View.General          => BtnGeneral,
            View.Environment      => BtnEnvironment,
            View.Diffusion        => BtnDiffusion,
            View.LoraAdapter      => BtnLoraAdapter,
            View.ControlNet       => BtnControlNet,
            View.Extract          => BtnExtract,
            View.Upscale          => BtnUpscale,
            View.Downloads        => BtnDownloads,
            View.Component        => BtnComponent,
            _                     => null
        };

        private IEnumerable<Button> GetSubNavButtons()
        {
            yield return BtnTextToImage;   yield return BtnImageToImage;
            yield return BtnImageEdit;     yield return BtnImageInpaint;
            yield return BtnPaintToImage;  yield return BtnImageUpscale;
            yield return BtnImageExtract;  yield return BtnImageCompose;
            yield return BtnTextToVideo;   yield return BtnImageToVideo;
            yield return BtnVideoToVideo;  yield return BtnFrameToFrame;
            yield return BtnVideoUpscale;  yield return BtnVideoExtract;
            yield return BtnVideoInterpolate; yield return BtnVideoCompose;
            yield return BtnTextToMusic;   yield return BtnTextToAudio;
            yield return BtnAudioToText;
            yield return BtnGeneral;       yield return BtnEnvironment;
            yield return BtnDiffusion;     yield return BtnLoraAdapter;
            yield return BtnControlNet;    yield return BtnExtract;
            yield return BtnUpscale;       yield return BtnDownloads;
            yield return BtnComponent;
        }

        private void DetectGpu()
        {
            try
            {
                foreach (var dir in Directory.GetDirectories("/sys/class/drm/", "card*"))
                {
                    var productPath = Path.Combine(dir, "device", "product_name");
                    if (File.Exists(productPath))
                    {
                        SystemInfoText.Text = File.ReadAllText(productPath).Trim();
                        return;
                    }
                    var ueventPath = Path.Combine(dir, "device", "uevent");
                    if (File.Exists(ueventPath))
                    {
                        foreach (var line in File.ReadAllLines(ueventPath))
                        {
                            if (line.StartsWith("DRIVER="))
                            {
                                SystemInfoText.Text = line.Replace("DRIVER=", "").Trim() + " GPU";
                                return;
                            }
                        }
                    }
                }
                SystemInfoText.Text = "GPU info unavailable";
            }
            catch
            {
                SystemInfoText.Text = "GPU info unavailable";
            }
        }
    }
}
