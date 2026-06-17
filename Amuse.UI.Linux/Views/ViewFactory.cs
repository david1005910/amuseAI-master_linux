using Avalonia.Controls;

namespace Amuse.UI.Linux.Views
{
    public static class ViewFactory
    {
        public static Control Create(View view) => view switch
        {
            View.TextToImage    => new TextToImageView(),
            View.ImageToImage   => new StubView("Image to Image"),
            View.ImageEdit      => new StubView("Image Edit"),
            View.ImageInpaint   => new StubView("Image Inpaint"),
            View.PaintToImage   => new StubView("Paint to Image"),
            View.ImageUpscale   => new StubView("Image Upscale"),
            View.ImageExtract   => new StubView("Image Extract"),
            View.ImageCompose   => new StubView("Image Compose"),
            View.TextToVideo    => new TextToVideoView(),
            View.ImageToVideo   => new StubView("Image to Video"),
            View.VideoToVideo   => new StubView("Video to Video"),
            View.FrameToFrame   => new StubView("Frame to Frame"),
            View.VideoUpscale   => new StubView("Video Upscale"),
            View.VideoExtract   => new StubView("Video Extract"),
            View.VideoInterpolate => new StubView("Video Interpolate"),
            View.VideoCompose   => new StubView("Video Compose"),
            View.TextToAudio    => new TextToAudioView(),
            View.AudioToText    => new AudioToTextView(),
            View.TextToMusic    => new TextToAudioView(),
            View.Gallery        => new StubView("Gallery"),
            View.General        => new StubView("General Settings"),
            View.Environment    => new StubView("Environment Settings"),
            View.Diffusion      => new StubView("Diffusion Settings"),
            View.LoraAdapter    => new StubView("Lora Adapter Settings"),
            View.ControlNet     => new StubView("ControlNet Settings"),
            View.Extract        => new StubView("Extractor Settings"),
            View.Upscale        => new StubView("Upscaler Settings"),
            View.Downloads      => new StubView("Downloads"),
            View.Component      => new StubView("Components"),
            _                   => new StubView("Unknown View")
        };
    }
}
