namespace Sharpshot.Uploads;

internal sealed class UploadJob
{
    public required string Id { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>The PNG's object key.</summary>
    public required string Key { get; set; }

    /// <summary>Only set when sharing as an embed.</summary>
    public string? PageKey { get; set; }

    /// <summary>The link that was shared: the embed page if there is one, otherwise the image.</summary>
    public required string Url { get; set; }

    /// <summary>The image is up; only the embed page is left (so a retry doesn't send the image again).</summary>
    public bool ImageUploaded { get; set; }

    public int Width { get; init; }

    public int Height { get; init; }

    public long SizeBytes { get; init; }

    public string? LocalCopyPath { get; init; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    /// <summary>True when a retry can't help (e.g. wrong keys); waits for a manual retry or the next app start.</summary>
    public bool Failed { get; set; }

    /// <summary>The job was dropped from the queue and can never be uploaded.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Discarded { get; set; }

    /// <summary>When the next attempt may start, after a failed one (not saved: a restart retries straight away).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public DateTimeOffset RetryAt { get; set; }

    public static string NewId() => Guid.NewGuid().ToString("N");
}