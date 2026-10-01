using System.Text;

namespace Ytdlp.Ui.Infrastructure.Mocking;

public sealed record LibraryFile(string Id, string Name, long Size, DateTimeOffset LastModified, string ContentUrl);
public sealed record Attempt(int Number, string State, DateTimeOffset CreatedAt, string? Error);
public sealed record DemoDownload(Guid Id, string Url, string? Title, string State, string? ResumeState,
    double? Progress, DateTimeOffset CreatedAt, string? ErrorCode, string? Error, string[] AllowedActions, Attempt[] Attempts);
public sealed record DemoResult(DemoDownload? Download = null, string? Code = null, int Status = 200);

public sealed class DemoStore
{
    private readonly object gate = new();
    private readonly Dictionary<string, (LibraryFile File, byte[] Content, Guid? DownloadId)> files = new();
    private readonly Dictionary<Guid, DemoDownload> downloads = new();

    public DemoStore()
    {
        var now = DateTimeOffset.UtcNow;
        AddFile("field-notes", "Field notes.txt", "Demo file: field notes.\n", now.AddDays(-1));
        AddFile("design-notes", "Design notes.txt", "Demo file: design notes.\n", now.AddDays(-2));
        AddFile("weekend", "Weekend itinerary.txt", "Demo file: weekend itinerary.\n", now.AddDays(-3));
        Seed("https://example.com/videos/mountain-trails", "Mountain trails", "Downloading", null, now.AddMinutes(-12));
        Seed("https://example.com/videos/quiet-morning", "A quiet morning", "Queued", null, now.AddMinutes(-8));
        Seed("https://example.com/videos/workshop", "Creative workshop", "Failed", "Uploading", now.AddHours(-1), "upload_failed", "Demonstration: the upload could not be completed.");
        var cleanup = Seed("https://example.com/videos/field-notes", "Field notes", "Failed", "CleaningUp", now.AddDays(-1), "cleanup_failed", "Demonstration: local cleanup needs a retry.");
        files["field-notes"] = (files["field-notes"].File, files["field-notes"].Content, cleanup.Id);
        var completed = Seed("https://example.com/videos/design-notes", "Design notes", "Completed", null, now.AddDays(-2));
        files["design-notes"] = (files["design-notes"].File, files["design-notes"].Content, completed.Id);
    }

    public LibraryFile[] GetFiles() { lock (gate) return files.Values.Select(value => value.File).OrderByDescending(file => file.LastModified).ToArray(); }
    public byte[]? GetContent(string id) { lock (gate) return files.TryGetValue(id, out var value) ? value.Content : null; }
    public LibraryFile? GetFile(string id) { lock (gate) return files.TryGetValue(id, out var value) ? value.File : null; }
    public DemoDownload[] GetDownloads() { lock (gate) return downloads.Values.OrderByDescending(download => download.CreatedAt).ToArray(); }
    public DemoDownload? GetDownload(Guid id) { lock (gate) return downloads.GetValueOrDefault(id); }

    public DemoResult Create(string? input)
    {
        if (!Uri.TryCreate(input?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0)
            return new(Code: "invalid_url", Status: 400);
        var url = uri.AbsoluteUri;
        lock (gate)
        {
            var existing = downloads.Values.FirstOrDefault(download => download.Url == url);
            if (existing is not null)
                return existing.State == "Deleted" ? Retry(existing.Id) with { Status = 202 } : new(Code: "download_already_exists", Status: 409);
            var download = Seed(url, null, "Queued", null, DateTimeOffset.UtcNow);
            return new(download, Status: 201);
        }
    }

    public DemoResult Retry(Guid id)
    {
        lock (gate)
        {
            if (!downloads.TryGetValue(id, out var download)) return new(Code: "not_found", Status: 404);
            if (!download.AllowedActions.Contains("retry")) return new(Code: "invalid_state", Status: 409);
            var attempts = download.Attempts.Append(new Attempt(download.Attempts.Length + 1, "Queued", DateTimeOffset.UtcNow, null)).ToArray();
            download = download with { State = "Queued", ResumeState = download.ResumeState ?? "Downloading", Error = null, ErrorCode = null, Attempts = attempts };
            downloads[id] = WithActions(download);
            return new(downloads[id], Status: 202);
        }
    }

    public DemoResult Cancel(Guid id)
    {
        lock (gate)
        {
            if (!downloads.TryGetValue(id, out var download)) return new(Code: "not_found", Status: 404);
            if (!download.AllowedActions.Contains("cancel")) return new(Code: "invalid_state", Status: 409);
            var attempts = download.State == "Failed"
                ? download.Attempts.Append(new Attempt(download.Attempts.Length + 1, "Canceled", DateTimeOffset.UtcNow, null)).ToArray()
                : download.Attempts.ToArray();
            if (download.State != "Failed") attempts[^1] = attempts[^1] with { State = "Canceled", Error = null };
            downloads[id] = WithActions(download with { State = "Canceled", ResumeState = null, Error = null, ErrorCode = null, Attempts = attempts });
            return new(downloads[id], Status: 202);
        }
    }

    public DemoResult DeleteFile(string id)
    {
        lock (gate)
        {
            if (!files.TryGetValue(id, out var file)) return new(Status: 204);
            if (file.DownloadId is { } downloadId)
            {
                var download = downloads[downloadId];
                if (download.State != "Completed") return new(Code: "invalid_state", Status: 409);
                var attempts = download.Attempts.Append(new Attempt(download.Attempts.Length + 1, "Deleted", DateTimeOffset.UtcNow, null)).ToArray();
                downloads[downloadId] = WithActions(download with { State = "Deleted", Attempts = attempts });
            }
            files.Remove(id);
            return new(Status: 204);
        }
    }

    private void AddFile(string id, string name, string content, DateTimeOffset modified)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        files[id] = (new LibraryFile(id, name, bytes.Length, modified, $"/api/library/{id}/content"), bytes, null);
    }

    private DemoDownload Seed(string url, string? title, string state, string? resume, DateTimeOffset created, string? code = null, string? error = null)
    {
        var download = WithActions(new DemoDownload(Guid.NewGuid(), url, title, state, resume, null, created, code, error, [], [new(1, state, created, error)]));
        downloads[download.Id] = download;
        return download;
    }

    private static DemoDownload WithActions(DemoDownload download)
    {
        string[] actions = download.State switch
        {
            "Failed" when download.ResumeState == "CleaningUp" => ["retry"],
            "Failed" => ["retry", "cancel"],
            "Queued" when download.ResumeState == "CleaningUp" => [],
            "Queued" or "Downloading" or "Preparing" or "Uploading" => ["cancel"],
            "Canceled" or "Deleted" => ["retry"],
            _ => []
        };
        return download with { AllowedActions = actions };
    }
}
