namespace Amuse.UI.Linux.Services
{
    public class GenerationRequest
    {
        public string Mode           { get; set; } = "image"; // "image" | "video" | "audio"
        public string ModelId        { get; set; } = "runwayml/stable-diffusion-v1-5";
        public string Prompt         { get; set; } = "";
        public string NegativePrompt { get; set; } = "";
        // image
        public int    Width          { get; set; } = 512;
        public int    Height         { get; set; } = 512;
        public int    Steps          { get; set; } = 20;
        public float  GuidanceScale  { get; set; } = 7.5f;
        public int    Seed           { get; set; } = -1;
        public string Scheduler      { get; set; } = "DPMSolverMultistep";
        public bool   IsXL           { get; set; } = false;
        // video
        public int    NumFrames      { get; set; } = 16;
        public int    Fps            { get; set; } = 8;
        // audio
        public float  Duration       { get; set; } = 10f;
        // audio_to_text
        public string AudioPath      { get; set; } = "";
        public string Language       { get; set; } = "";   // empty = auto-detect
    }

    public class GenerationProgress
    {
        public int    Step    { get; set; }
        public int    Total   { get; set; }
        public string Message { get; set; }
        public bool   Done    { get; set; }
    }
}
