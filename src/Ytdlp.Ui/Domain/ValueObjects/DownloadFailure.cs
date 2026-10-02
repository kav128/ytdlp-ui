namespace Ytdlp.Ui.Domain.ValueObjects;

public sealed record DownloadFailure(string Code, string? Details = null);
