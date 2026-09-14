using Sharpshot.Links;
using Sharpshot.Storage;
using Sharpshot.Uploads;

namespace Sharpshot.Tests;

public class UploadQueueTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task SuccessfulUploadRemovesQueueFilesAndReportsCompletion()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore();
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero]);
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("a.png"), [1, 2, 3]);
        var job = await completed.Task.WaitAsync(Timeout);

        Assert.Equal("a.png", job.Key);
        Assert.Equal([1, 2, 3], store.Uploads["a.png"]);
        await WaitUntil(() => Directory.GetFiles(temp.Path).Length == 0);
        Assert.Equal(0, queue.PendingCount);
    }

    [Fact]
    public async Task EmbedJobsUploadTheImageThenThePage()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore();
        var builds = 0;
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero],
            pageBuilder: job => { Interlocked.Increment(ref builds); return "<html>page</html>"u8.ToArray(); });
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        var job = NewJob("abc.png");
        job.PageKey = "abc";
        queue.Enqueue(job, [1, 2]);
        await completed.Task.WaitAsync(Timeout);

        Assert.Equal([("abc.png", "image/png"), ("abc", EmbedPage.ContentType)], store.Puts.ToArray());
        Assert.Equal("<html>page</html>"u8.ToArray(), store.Uploads["abc"]);
        Assert.Equal(1, builds);
        await WaitUntil(() => Directory.GetFiles(temp.Path).Length == 0);
    }

    [Fact]
    public async Task RetryingAFailedPageDoesNotUploadTheImageAgain()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore
        {
            FailuresBeforeSuccess = 1,
            Failure = new StorageException(StorageErrorKind.Transient, "blip"),
            FailsFor = key => key == "page",
        };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero], pageBuilder: _ => [9]);
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        var job = NewJob("page.png");
        job.PageKey = "page";
        queue.Enqueue(job, [1]);
        await completed.Task.WaitAsync(Timeout);

        Assert.Equal(["page.png", "page", "page"], store.Puts.Select(p => p.Key).ToArray());
    }

    [Fact]
    public async Task TransientErrorsAreRetried()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore { FailuresBeforeSuccess = 2, Failure = new StorageException(StorageErrorKind.Transient, "blip") };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("retry.png"), [9]);
        var job = await completed.Task.WaitAsync(Timeout);

        Assert.Equal(3, job.Attempts);
        Assert.Equal(3, store.Calls);
    }

    [Fact]
    public async Task PermanentErrorsFailImmediatelyAndKeepTheScreenshot()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore { FailuresBeforeSuccess = int.MaxValue, Failure = new StorageException(StorageErrorKind.Authentication, "bad keys") };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero, TimeSpan.Zero]);
        var failed = Watch(queue, nameof(UploadQueue.Failed));
        queue.Start();

        var job = NewJob("denied.png");
        queue.Enqueue(job, [4, 5]);
        var result = await failed.Task.WaitAsync(Timeout);

        Assert.Equal("bad keys", result.LastError);
        Assert.Equal(1, store.Calls);
        Assert.Equal(1, queue.FailedCount);
        Assert.True(File.Exists(Path.Combine(temp.Path, job.Id + ".png")));
    }

    [Fact]
    public async Task RetryFailedUploadsAgain()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore { FailuresBeforeSuccess = 1, Failure = new StorageException(StorageErrorKind.Authentication, "bad keys") };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, []);
        var failed = Watch(queue, nameof(UploadQueue.Failed));
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("later.png"), [1]);
        await failed.Task.WaitAsync(Timeout);
        queue.RetryFailed();
        await completed.Task.WaitAsync(Timeout);

        Assert.Equal(0, queue.FailedCount);
    }

    [Fact]
    public async Task TakenNamesGetANewKeyInsteadOfOverwriting()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore { FailuresBeforeSuccess = 1, Failure = new StorageException(StorageErrorKind.AlreadyExists, "exists") };
        using var queue = new UploadQueue(temp.Path, () => store, _ => new ShotNames("renamed.png", null, "https://cdn.example.com/renamed.png"), [TimeSpan.Zero]);
        var renamed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        queue.Renamed += (_, previousUrl) => renamed.TrySetResult(previousUrl);
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("taken.png"), [7]);
        var job = await completed.Task.WaitAsync(Timeout);

        Assert.Equal("https://cdn.example.com/taken.png", await renamed.Task.WaitAsync(Timeout));
        Assert.Equal("renamed.png", job.Key);
        Assert.True(store.Uploads.ContainsKey("renamed.png"));
    }

    [Fact]
    public async Task NetworkProblemsAreRetriedUntilTheyClearAndWarnOnce()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore { FailuresBeforeSuccess = 6, Failure = new StorageException(StorageErrorKind.Transient, "offline") };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero]);
        var delayed = 0;
        queue.Delayed += _ => Interlocked.Increment(ref delayed);
        var failed = Watch(queue, nameof(UploadQueue.Failed));
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("patient.png"), [3]);
        var job = await completed.Task.WaitAsync(Timeout);

        Assert.Equal(7, job.Attempts);
        Assert.Equal(1, delayed);
        Assert.False(failed.Task.IsCompleted);
    }

    [Fact]
    public async Task RetryNowEndsTheBackoffWait()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore { FailuresBeforeSuccess = 1, Failure = new StorageException(StorageErrorKind.Transient, "offline") };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.FromHours(1)]);
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("impatient.png"), [3]);
        await WaitUntil(() => store.Calls == 1);
        queue.RetryNow();

        await completed.Task.WaitAsync(Timeout);
    }

    [Fact]
    public async Task FailuresFromSettingsReplacedMidUploadAreRetriedWithTheNewOnes()
    {
        using var temp = new TempDirectory();
        var fixedKeys = new FakeStore();
        IObjectStore current = null!;
        var wrongKeys = new FakeStore
        {
            FailuresBeforeSuccess = int.MaxValue,
            Failure = new StorageException(StorageErrorKind.Authentication, "bad keys"),
            FailsFor = _ =>
            {
                current = fixedKeys; // the user saves corrected keys while this request is in flight
                return true;
            },
        };
        current = wrongKeys;
        using var queue = new UploadQueue(temp.Path, () => current, Rename, [TimeSpan.Zero]);
        var failed = Watch(queue, nameof(UploadQueue.Failed));
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("fixed.png"), [1]);
        await completed.Task.WaitAsync(Timeout);

        Assert.False(failed.Task.IsCompleted);
        Assert.True(fixedKeys.Uploads.ContainsKey("fixed.png"));
    }

    [Fact]
    public async Task AnUploadWaitingToRetryDoesNotHoldUpNewerOnes()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore
        {
            FailuresBeforeSuccess = int.MaxValue,
            Failure = new StorageException(StorageErrorKind.Transient, "too big for this connection"),
            FailsFor = key => key == "huge.png",
        };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.FromHours(1)]);
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("huge.png"), [1]);
        await WaitUntil(() => store.Calls == 1);
        queue.Enqueue(NewJob("small.png"), [2]);

        Assert.Equal("small.png", (await completed.Task.WaitAsync(Timeout)).Key);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task WakeUpsDoNotCutTheBackoffShort()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore { FailuresBeforeSuccess = int.MaxValue, Failure = new StorageException(StorageErrorKind.Transient, "throttled") };
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.FromHours(1)]);
        queue.Start();

        queue.Enqueue(NewJob("throttled.png"), [1]);
        await WaitUntil(() => store.Calls == 1);
        for (var i = 0; i < 5; i++)
        {
            queue.RetryFailed(); // nothing has failed, but each call wakes the queue
        }

        await Task.Delay(300);
        Assert.Equal(1, store.Calls);
    }

    [Fact]
    public async Task UploadsAreRecordedBeforeTheyLeaveTheQueue()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore();
        var stillQueued = false;
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero],
            recordUpload: job => stillQueued = File.Exists(Path.Combine(temp.Path, job.Id + ".json")));
        var completed = Watch(queue, nameof(UploadQueue.Completed));
        queue.Start();

        queue.Enqueue(NewJob("recorded.png"), [1]);
        await completed.Task.WaitAsync(Timeout);

        Assert.True(stillQueued, "The record must be written while the job can still be recovered");
    }

    [Fact]
    public async Task UnusableQueueEntriesAreSetAsideOnStart()
    {
        using var temp = new TempDirectory();
        var store = new FakeStore();
        using var queue = new UploadQueue(temp.Path, () => store, Rename, [TimeSpan.Zero]);
        var job = NewJob("gone.png");

        // Simulate a job whose image vanished between runs.
        Directory.CreateDirectory(temp.Path);
        File.WriteAllText(Path.Combine(temp.Path, job.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(job, Settings.SettingsStore.JsonOptions));
        File.WriteAllText(Path.Combine(temp.Path, "junk.json"), "{ not json");
        queue.Start();

        await WaitUntil(() => File.Exists(Path.Combine(temp.Path, job.Id + ".json.broken")) && File.Exists(Path.Combine(temp.Path, "junk.json.broken")));
        Assert.Equal(0, queue.PendingCount);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task PendingUploadsSurviveARestart()
    {
        using var temp = new TempDirectory();
        var offline = new FakeStore { FailuresBeforeSuccess = int.MaxValue, Failure = new StorageException(StorageErrorKind.Transient, "offline") };
        using (var first = new UploadQueue(temp.Path, () => offline, Rename, [TimeSpan.FromMilliseconds(20)]))
        {
            first.Start();
            first.Enqueue(NewJob("survivor.png"), [42]);
            await WaitUntil(() => offline.Calls >= 2);
        }

        var online = new FakeStore();
        using var second = new UploadQueue(temp.Path, () => online, Rename, []);
        var completed = Watch(second, nameof(UploadQueue.Completed));
        second.Start();

        var job = await completed.Task.WaitAsync(Timeout);
        Assert.Equal("survivor.png", job.Key);
        Assert.Equal([42], online.Uploads["survivor.png"]);
    }

    [Fact]
    public async Task UnconfiguredStoreFailsWithAHelpfulMessage()
    {
        using var temp = new TempDirectory();
        using var queue = new UploadQueue(temp.Path, () => null, Rename, []);
        var failed = Watch(queue, nameof(UploadQueue.Failed));
        queue.Start();

        queue.Enqueue(NewJob("nowhere.png"), [1]);
        var job = await failed.Task.WaitAsync(Timeout);

        Assert.Contains("Settings", job.LastError);
    }

    private static ShotNames Rename(UploadJob job) => new("new-" + job.Key, null, "https://cdn.example.com/new-" + job.Key);

    private static UploadJob NewJob(string key) => new()
    {
        Id = UploadJob.NewId(),
        CreatedAt = DateTimeOffset.Now,
        Key = key,
        Url = "https://cdn.example.com/" + key,
        Width = 10,
        Height = 10,
    };

    private static TaskCompletionSource<UploadJob> Watch(UploadQueue queue, string eventName)
    {
        var tcs = new TaskCompletionSource<UploadJob>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (eventName == nameof(UploadQueue.Completed))
        {
            queue.Completed += job => tcs.TrySetResult(job);
        }
        else
        {
            queue.Failed += job => tcs.TrySetResult(job);
        }

        return tcs;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for condition");
            await Task.Delay(20);
        }
    }

    private sealed class FakeStore : IObjectStore
    {
        public int FailuresBeforeSuccess { get; init; }

        public StorageException? Failure { get; init; }

        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public System.Collections.Concurrent.ConcurrentDictionary<string, byte[]> Uploads { get; } = [];

        public System.Collections.Concurrent.ConcurrentQueue<(string Key, string ContentType)> Puts { get; } = [];

        /// <summary>Only uploads whose key passes this filter can fail.</summary>
        public Func<string, bool> FailsFor { get; init; } = _ => true;

        public Task PutNewObjectAsync(string key, byte[] data, string contentType, CancellationToken cancellationToken)
        {
            Puts.Enqueue((key, contentType));
            if (FailsFor(key) && Interlocked.Increment(ref _calls) <= FailuresBeforeSuccess)
            {
                throw Failure!;
            }

            Uploads[key] = data;
            return Task.CompletedTask;
        }
    }
}

public class UploadHistoryTests
{
    [Fact]
    public void RecentEntriesComeBackNewestFirst()
    {
        using var temp = new TempDirectory();
        var history = new UploadHistory(temp.File("history.jsonl"));
        for (var i = 0; i < 15; i++)
        {
            history.Append(Entry(i));
        }

        var recent = history.ReadRecent(10);

        Assert.Equal(10, recent.Count);
        Assert.Equal("https://cdn.example.com/14.png", recent[0].Url);
        Assert.Equal("https://cdn.example.com/5.png", recent[^1].Url);
    }

    [Fact]
    public void LargeHistoryOnlyReadsTheEndAndSkipsDamagedLines()
    {
        using var temp = new TempDirectory();
        var path = temp.File("history.jsonl");
        var history = new UploadHistory(path);
        for (var i = 0; i < 3000; i++)
        {
            history.Append(Entry(i));
        }

        File.AppendAllText(path, "{ broken line\n");
        history.Append(Entry(3000));

        var recent = history.ReadRecent(3);

        Assert.Equal(["https://cdn.example.com/3000.png", "https://cdn.example.com/2999.png", "https://cdn.example.com/2998.png"], recent.Select(e => e.Url));
    }

    [Fact]
    public void MissingHistoryIsEmpty()
    {
        using var temp = new TempDirectory();
        Assert.Empty(new UploadHistory(temp.File("none.jsonl")).ReadRecent(10));
    }

    [Fact]
    public void UnicodeLinksArePreserved()
    {
        using var temp = new TempDirectory();
        var history = new UploadHistory(temp.File("history.jsonl"));
        history.Append(Entry(0) with { Url = "https://cdn.example.com/⠓⠕⠍⠑⠎.png" });

        Assert.Equal("https://cdn.example.com/⠓⠕⠍⠑⠎.png", history.ReadRecent(1)[0].Url);
    }

    private static HistoryEntry Entry(int i) =>
        new(DateTimeOffset.Now.AddMinutes(i), $"https://cdn.example.com/{i}.png", $"{i}.png", 100, 50, 1234, null);
}

public class LocalCopiesTests
{
    [Fact]
    public void CopiesAreGroupedByMonthAndNeverOverwritten()
    {
        using var temp = new TempDirectory();
        var takenAt = new DateTimeOffset(2026, 9, 14, 15, 30, 12, TimeSpan.Zero);

        var first = LocalCopies.Save(temp.Path, takenAt, [1]);
        var second = LocalCopies.Save(temp.Path, takenAt, [2]);

        Assert.Equal(Path.Combine(temp.Path, "2026-09", "Sharpshot 2026-09-14 15-30-12.png"), first);
        Assert.Equal(Path.Combine(temp.Path, "2026-09", "Sharpshot 2026-09-14 15-30-12 (2).png"), second);
        Assert.Equal([1], File.ReadAllBytes(first));
        Assert.Equal([2], File.ReadAllBytes(second));
    }
}