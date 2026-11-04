namespace Ytdlp.Ui.IntegrationTests.Data;

public static class MediaMetadata
{
    public const string Combined =
        """{"_type":"video","title":"test clip","formats":[{"format_id":"mp4","ext":"mp4","vcodec":"h264","acodec":"aac","height":720,"filesize":100}]}""";

    public const string UnknownCombined =
        """{"_type":"video","title":"direct clip","formats":[{"format_id":"mp4","ext":"mp4","vcodec":null,"acodec":null,"height":null}]}""";

    public const string Separate =
        """{"_type":"video","title":"separate clip","formats":[{"format_id":"0","ext":"mp4","vcodec":"avc1.64000c","acodec":"none","height":720,"filesize":100},{"format_id":"1","ext":"m4a","vcodec":"none","acodec":"mp4a.40.2","filesize":100}]}""";
}
