namespace Ytdlp.Ui.Infrastructure.Storage;

public sealed class S3Options
{
    public string Endpoint { get; set; } = "";
    public string Region { get; set; } = "us-east-1";
    public string Bucket { get; set; } = "";
    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public bool ForcePathStyle { get; set; } = true;
}
