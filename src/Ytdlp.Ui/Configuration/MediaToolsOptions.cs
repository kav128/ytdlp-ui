namespace Ytdlp.Ui.Configuration;

public sealed class MediaToolsOptions
{
    public string YtDlpPath { get; set; } = "yt-dlp";
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";
    public string NodePath { get; set; } = "node";
}

public sealed class WorkerOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalMilliseconds { get; set; } = 1000;
}
