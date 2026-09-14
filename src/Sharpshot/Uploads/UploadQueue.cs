using System.Text.Json;
using Sharpshot.Links;
using Sharpshot.Settings;
using Sharpshot.Storage;

namespace Sharpshot.Uploads;

/// <summary>
/// Every screenshot is written to disk before uploading, so pending uploads survive a crash, a reboot or a dropped
/// connection. Network problems are retried indefinitely with backoff; problems a retry can't fix, like wrong keys,
/// park the job as failed until <see cref="RetryFailed"/>. Events are raised on a background thread.
/// </summary>
internal sealed class UploadQueue : IDisposable
{
    private const string ContentType = "image/png";
    private const int MaxRenames = 5;

    /// <summary>After this many failed attempts the <see cref="Delayed"/> event fires (once per job).</summary>
    private const int DelayedNoticeAttempts = 3;

    private static readonly TimeSpan[] DefaultRetryDelays =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
    ];

    /// <summary>Files a crash left without their job entry are cleaned up once they're this old.</summary>
    private static readonly TimeSpan OrphanAge = TimeSpan.FromDays(1);

    private readonly string _directory;
    private readonly Func<IObjectStore?> _storeProvider;
    private readonly Func<UploadJob, ShotNames> _rename;
    private readonly Func<UploadJob, byte[]>? _pageBuilder;
    private readonly Action<UploadJob>? _recordUpload;
    private readonly IReadOnlyList<TimeSpan> _retryDelays;
    private readonly Lock _gate = new();
    private readonly List<UploadJob> _jobs = [];
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _stopping = new();
    private Task? _worker;
    private bool _disposed;

    /// <param name="storeProvider">Returns null if R2 isn't configured.</param>
    /// <param name="rename">Picks new names for a job whose key belongs to a different file.</param>
    /// <param name="retryDelays">Wait before each retry; the last value repeats.</param>
    /// <param name="pageBuilder">Called once per job shared as an embed; the result is kept in the queue.</param>
    /// <param name="recordUpload">
    /// Runs on the upload thread before the job leaves the queue, so an exit right after an upload can't lose the record.
    /// </param>
    public UploadQueue(
        string directory,
        Func<IObjectStore?> storeProvider,
        Func<UploadJob, ShotNames> rename,
        IReadOnlyList<TimeSpan>? retryDelays = null,
        Func<UploadJob, byte[]>? pageBuilder = null,
        Action<UploadJob>? recordUpload = null)
    {
        _directory = directory;
        _storeProvider = storeProvider;
        _rename = rename;
        _pageBuilder = pageBuilder;
        _recordUpload = recordUpload;
        _retryDelays = retryDelays is { Count: > 0 } ? retryDelays : DefaultRetryDelays;
    }

    public event Action<UploadJob>? Completed;

    /// <summary>The job can't be uploaded without the user's help (it stays queued unless <see cref="UploadJob.Discarded"/>).</summary>
    public event Action<UploadJob>? Failed;

    /// <summary>A network problem has lasted a few attempts. The queue keeps retrying.</summary>
    public event Action<UploadJob>? Delayed;

    /// <summary>The job's link changed because the name was taken by a different file. Args: job, previous URL.</summary>
    public event Action<UploadJob, string>? Renamed;

    /// <summary>Pending or failed counts changed.</summary>
    public event Action? Changed;

    public int PendingCount
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count(j => !j.Failed);
            }
        }
    }

    public int FailedCount
    {
        get
        {
            lock (_gate)
            {
                return _jobs.Count(j => j.Failed);
            }
        }
    }

    /// <summary>Picks up jobs left over from a previous run.</summary>
    public void Start()
    {
        Directory.CreateDirectory(_directory);
        foreach (var leftover in Directory.EnumerateFiles(_directory, "*.tmp"))
        {
            TryDelete(leftover); // half-written by a crash; the real file was never replaced
        }

        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            UploadJob? job;
            try
            {
                job = JsonSerializer.Deserialize<UploadJob>(File.ReadAllText(file), SettingsStore.JsonOptions);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Probably held open by antivirus or backup software: leave it for the next start.
                Log.Error($"Couldn't read queue entry {Path.GetFileName(file)}; it will be tried again next time", ex);
                continue;
            }
            catch (JsonException ex)
            {
                Log.Error($"Couldn't load queue entry {Path.GetFileName(file)}", ex);
                job = null;
            }

            if (job is null || !File.Exists(ImagePath(job)))
            {
                Log.Warn($"Setting aside unusable queue entry {Path.GetFileName(file)}");
                TryMove(file, file + ".broken");
                continue;
            }

            job.Failed = false; // retry failed jobs on startup
            job.Attempts = 0;
            lock (_gate)
            {
                _jobs.Add(job);
            }
        }

        lock (_gate)
        {
            _jobs.Sort((a, b) => a.CreatedAt.CompareTo(b.CreatedAt));
        }

        RemoveOrphans();
        _worker = Task.Run(() => RunAsync(_stopping.Token));
        _wake.Release();
        Changed?.Invoke();
    }

    /// <summary>Deletes old images and pages whose job entry is gone (a crash between writing and removing files).</summary>
    private void RemoveOrphans()
    {
        // Entries set aside as .json.broken keep their screenshot too, in case they can be recovered by hand.
        var jobIds = Directory.EnumerateFiles(_directory, "*.json*").Select(f => Path.GetFileName(f).Split('.')[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cutoff = DateTime.UtcNow - OrphanAge;
        foreach (var file in Directory.EnumerateFiles(_directory))
        {
            var name = Path.GetFileName(file);
            var orphan = Path.GetExtension(name) is ".png" or ".html" && !jobIds.Contains(Path.GetFileNameWithoutExtension(name));
            if (orphan && File.GetLastWriteTimeUtc(file) < cutoff)
            {
                TryDelete(file);
            }
        }
    }

    /// <summary>Persists the image and job, then queues the upload.</summary>
    public void Enqueue(UploadJob job, byte[] png)
    {
        Directory.CreateDirectory(_directory);
        WriteAtomically(ImagePath(job), png);
        SaveJob(job);
        lock (_gate)
        {
            _jobs.Add(job);
        }

        _wake.Release();
        Changed?.Invoke();
    }

    public void RetryFailed()
    {
        lock (_gate)
        {
            foreach (var job in _jobs.Where(j => j.Failed))
            {
                job.Failed = false;
                job.Attempts = 0;
                job.RetryAt = default;
            }
        }

        _wake.Release();
        Changed?.Invoke();
    }

    /// <summary>Cuts retry waits short (e.g. the network just came back).</summary>
    public void RetryNow()
    {
        lock (_gate)
        {
            foreach (var job in _jobs)
            {
                job.RetryAt = default;
            }
        }

        _wake.Release();
    }

    private async Task RunAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            // The oldest job that's due; a job waiting to retry doesn't hold up the others.
            UploadJob? job;
            DateTimeOffset retryAt;
            lock (_gate)
            {
                job = _jobs.Where(j => !j.Failed).MinBy(j => j.RetryAt);
                retryAt = job?.RetryAt ?? default;
            }

            try
            {
                if (job is null)
                {
                    await _wake.WaitAsync(stopping).ConfigureAwait(false);
                    continue;
                }

                var wait = retryAt - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    // Enqueue, RetryFailed and RetryNow wake this early.
                    await _wake.WaitAsync(wait, stopping).ConfigureAwait(false);
                    continue;
                }

                await ProcessAsync(job, stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Error($"Unexpected error uploading {job?.Id}", ex);
                if (job is not null)
                {
                    MarkFailed(job, $"Unexpected error: {ex.Message}");
                }
            }
        }
    }

    private async Task ProcessAsync(UploadJob job, CancellationToken stopping)
    {
        byte[] data;
        try
        {
            data = await File.ReadAllBytesAsync(ImagePath(job), stopping).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Log.Error($"Queued image for {job.Id} is missing; dropping it", ex);
            Remove(job);
            job.Discarded = true;
            job.LastError = "The screenshot waiting to upload went missing from Sharpshot's queue folder.";
            Failed?.Invoke(job);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            job.Attempts++;
            job.LastError = $"The queued screenshot couldn't be read: {ex.Message}";
            Log.Warn($"Queued image for {job.Id} couldn't be read, will retry: {ex.Message}");
            ScheduleRetry(job);
            return;
        }

        var renames = 0;
        while (true)
        {
            var store = _storeProvider();
            if (store is null)
            {
                MarkFailed(job, "Sharpshot isn't connected to Cloudflare R2. Open Settings to set it up.");
                return;
            }

            try
            {
                job.Attempts++;
                if (!job.ImageUploaded)
                {
                    await store.PutNewObjectAsync(job.Key, data, ContentType, stopping).ConfigureAwait(false);
                    job.ImageUploaded = true;
                    TrySaveJob(job);
                }

                if (job.PageKey is not null)
                {
                    var page = GetPage(job);
                    await store.PutNewObjectAsync(job.PageKey, page, EmbedPage.ContentType, stopping).ConfigureAwait(false);
                }

                Log.Info($"Uploaded {job.Id} ({data.Length:N0} bytes{(job.PageKey is null ? "" : " + embed page")}) on attempt {job.Attempts}");
                RecordUpload(job);
                Remove(job);
                Completed?.Invoke(job);
                return;
            }
            catch (StorageException ex) when (ex.Kind == StorageErrorKind.AlreadyExists && renames < MaxRenames)
            {
                renames++;
                var previousUrl = job.Url;
                var names = _rename(job);
                job.Key = names.ImageKey;
                job.PageKey = names.PageKey;
                job.Url = names.Link;
                job.ImageUploaded = false;
                job.Attempts = 0;
                TryDelete(PagePath(job)); // it links to the old image name
                SaveJob(job);
                Log.Warn($"Key for {job.Id} belongs to a different file; renamed");
                Renamed?.Invoke(job, previousUrl);
            }
            catch (StorageException ex) when (ex.IsTransient)
            {
                job.LastError = ex.Message;
                TrySaveJob(job);
                Log.Warn($"Upload {job.Id} attempt {job.Attempts} failed, will retry: {ex.Message}");
                ScheduleRetry(job);
                return;
            }
            catch (StorageException ex) when (!ReferenceEquals(store, _storeProvider()))
            {
                // The settings changed while this attempt was running; try again with the new ones.
                Log.Warn($"Upload {job.Id} failed with the old settings, retrying: {ex.Message}");
                job.Attempts = 0;
            }
            catch (StorageException ex)
            {
                Log.Warn($"Upload {job.Id} failed: {ex.Message}");
                MarkFailed(job, ex.Message);
                return;
            }
        }
    }

    private void RecordUpload(UploadJob job)
    {
        try
        {
            _recordUpload?.Invoke(job);
        }
        catch (Exception ex)
        {
            // History is best-effort; the upload already succeeded.
            Log.Error($"Couldn't record upload {job.Id}", ex);
        }
    }

    /// <summary>Other jobs can upload in the meantime.</summary>
    private void ScheduleRetry(UploadJob job)
    {
        var delay = _retryDelays[Math.Clamp(job.Attempts, 1, _retryDelays.Count) - 1];
        lock (_gate)
        {
            job.RetryAt = DateTimeOffset.UtcNow + delay;
        }

        if (job.Attempts == DelayedNoticeAttempts)
        {
            Delayed?.Invoke(job);
        }
    }

    private void MarkFailed(UploadJob job, string error)
    {
        lock (_gate)
        {
            job.Failed = true;
            job.LastError = error;
        }

        TrySaveJob(job);
        Failed?.Invoke(job);
        Changed?.Invoke();
    }

    private void Remove(UploadJob job)
    {
        lock (_gate)
        {
            _jobs.Remove(job);
        }

        TryDelete(JobPath(job));
        TryDelete(ImagePath(job));
        TryDelete(PagePath(job));
        Changed?.Invoke();
    }

    /// <summary>Builds the embed page once and keeps it, so every retry uploads exactly the same bytes.</summary>
    private byte[] GetPage(UploadJob job)
    {
        var path = PagePath(job);
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }

        var page = _pageBuilder?.Invoke(job) ?? throw new InvalidOperationException("No embed page builder was provided.");
        WriteAtomically(path, page);
        return page;
    }

    private string JobPath(UploadJob job) => Path.Combine(_directory, job.Id + ".json");

    private string ImagePath(UploadJob job) => Path.Combine(_directory, job.Id + ".png");

    private string PagePath(UploadJob job) => Path.Combine(_directory, job.Id + ".html");

    private void SaveJob(UploadJob job) =>
        WriteAtomically(JobPath(job), JsonSerializer.SerializeToUtf8Bytes(job, SettingsStore.JsonOptions));

    private void TrySaveJob(UploadJob job)
    {
        try
        {
            SaveJob(job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Couldn't update queue entry {job.Id}", ex);
        }
    }

    private static void WriteAtomically(string path, byte[] bytes)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, bytes);
        File.Move(temp, path, overwrite: true);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't delete {path}: {ex.Message}");
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Couldn't move {from}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stopping.Cancel();
        try
        {
            _worker?.Wait(TimeSpan.FromSeconds(3));
        }
        catch (AggregateException)
        {
            // Shutting down; any failure is already logged.
        }

        _stopping.Dispose();
        _wake.Dispose();
    }
}