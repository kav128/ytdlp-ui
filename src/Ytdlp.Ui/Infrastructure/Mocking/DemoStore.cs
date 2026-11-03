namespace Ytdlp.Ui.Infrastructure.Mocking;

public sealed record Attempt(int Number, string State, DateTimeOffset CreatedAt, string? Error);

public sealed record DemoDownload(
    Guid Id,
    string Url,
    string? Title,
    string State,
    string? ResumeState,
    double? Progress,
    DateTimeOffset CreatedAt,
    string? ErrorCode,
    string? Error,
    string[] AllowedActions,
    Attempt[] Attempts
);

public sealed record DemoResult(DemoDownload? Download = null, string? Code = null, int Status = 200);

public sealed class DemoStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DemoDownload> _downloads = new();

    public DemoStore()
    {
        var now = DateTimeOffset.UtcNow;
        Seed("https://example.com/videos/mountain-trails", "Mountain trails", "Downloading", null, now.AddMinutes(-12));
        Seed("https://example.com/videos/quiet-morning", "A quiet morning", "Queued", null, now.AddMinutes(-8));

        Seed(
            "https://example.com/videos/workshop",
            "Creative workshop",
            "Failed",
            "Uploading",
            now.AddHours(-1),
            "upload_failed",
            "Demonstration: the upload could not be completed."
        );

        Seed(
            "https://example.com/videos/field-notes",
            "Field notes",
            "Failed",
            "CleaningUp",
            now.AddDays(-1),
            "cleanup_failed",
            "Demonstration: local cleanup needs a retry."
        );

        Seed("https://example.com/videos/design-notes", "Design notes", "Completed", null, now.AddDays(-2));
    }

    public DemoDownload[] GetDownloads()
    {
        lock (_gate) return _downloads.Values.OrderByDescending(download => download.CreatedAt).ToArray();
    }

    public DemoDownload? GetDownload(Guid id)
    {
        lock (_gate) return _downloads.GetValueOrDefault(id);
    }

    public DemoResult Create(string? input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri)
         || uri.Scheme is not ("http" or "https")
         || uri.UserInfo.Length > 0)
            return new(Code: "invalid_url", Status: 400);

        var url = uri.AbsoluteUri;

        lock (_gate)
        {
            var existing = _downloads.Values.FirstOrDefault(download => download.Url == url);

            if (existing is not null)
                return existing.State == "Deleted"
                           ? Retry(existing.Id) with { Status = 202 }
                           : new(Code: "download_already_exists", Status: 409);

            var download = Seed(url, null, "Queued", null, DateTimeOffset.UtcNow);

            return new(download, Status: 201);
        }
    }

    public DemoResult Retry(Guid id)
    {
        lock (_gate)
        {
            if (!_downloads.TryGetValue(id, out var download)) return new(Code: "not_found", Status: 404);
            if (!download.AllowedActions.Contains("retry")) return new(Code: "invalid_state", Status: 409);

            var attempts = download.Attempts
                                   .Append(
                                        new Attempt(download.Attempts.Length + 1, "Queued", DateTimeOffset.UtcNow, null)
                                    )
                                   .ToArray();

            download = download with
            {
                State = "Queued",
                ResumeState = download.ResumeState ?? "Downloading",
                Error = null,
                ErrorCode = null,
                Attempts = attempts
            };

            _downloads[id] = WithActions(download);

            return new(_downloads[id], Status: 202);
        }
    }

    public DemoResult Cancel(Guid id)
    {
        lock (_gate)
        {
            if (!_downloads.TryGetValue(id, out var download)) return new(Code: "not_found", Status: 404);
            if (!download.AllowedActions.Contains("cancel")) return new(Code: "invalid_state", Status: 409);

            var attempts = download.State == "Failed"
                               ? download.Attempts.Append(
                                              new Attempt(
                                                  download.Attempts.Length + 1,
                                                  "Canceled",
                                                  DateTimeOffset.UtcNow,
                                                  null
                                              )
                                          )
                                         .ToArray()
                               : download.Attempts.ToArray();

            if (download.State != "Failed")
                attempts[^1] = attempts[^1] with
                {
                    State = "Canceled",
                    Error = null
                };

            _downloads[id] = WithActions(
                download with
                {
                    State = "Canceled",
                    ResumeState = null,
                    Error = null,
                    ErrorCode = null,
                    Attempts = attempts
                }
            );

            return new(_downloads[id], Status: 202);
        }
    }

    private DemoDownload Seed(
        string url,
        string? title,
        string state,
        string? resume,
        DateTimeOffset created,
        string? code = null,
        string? error = null
    )
    {
        var download = WithActions(
            new DemoDownload(
                Guid.NewGuid(),
                url,
                title,
                state,
                resume,
                null,
                created,
                code,
                error,
                [],
                [new(1, state, created, error)]
            )
        );

        _downloads[download.Id] = download;

        return download;
    }

    private static DemoDownload WithActions(DemoDownload download)
    {
        string[] actions = download.State switch
        {
            "Failed" when download.ResumeState == "CleaningUp"      => ["retry"],
            "Failed"                                                => ["retry", "cancel"],
            "Queued" when download.ResumeState == "CleaningUp"      => [],
            "Queued" or "Downloading" or "Preparing" or "Uploading" => ["cancel"],
            "Canceled" or "Deleted"                                 => ["retry"],
            _                                                       => []
        };

        return download with { AllowedActions = actions };
    }
}
