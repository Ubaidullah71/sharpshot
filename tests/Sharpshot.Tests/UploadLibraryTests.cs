using System.Net;
using System.Text;
using Sharpshot.Storage;
using Sharpshot.Uploads;

namespace Sharpshot.Tests;

public class UploadLibraryTests
{
    [Fact]
    public void ListObjectsExampleMatchesAws()
    {
        // "GET Bucket (List Objects)" from AWS's Signature Version 4 examples.
        var headers = new Dictionary<string, string>
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-content-sha256"] = SigV4.EmptyPayloadHash,
            ["x-amz-date"] = "20130524T000000Z",
        };
        var query = SigV4.CanonicalQuery([new("prefix", "J"), new("max-keys", "2")]);

        var canonical = SigV4.CanonicalRequest("GET", "/", query, headers, SigV4.EmptyPayloadHash, out var signedHeaders);
        var authorization = SigV4.AuthorizationHeader("AKIAIOSFODNN7EXAMPLE", "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc), "us-east-1", "s3", canonical, signedHeaders);

        Assert.Equal("max-keys=2&prefix=J", query);
        Assert.EndsWith("Signature=34b48302e7b5fa45bde8084f4b7868a86f0a534bc59db6670ed5711ef69dc6f7", authorization);
    }

    [Fact]
    public async Task ListingFollowsContinuationTokens()
    {
        var pages = new Queue<string>(
        [
            Listing(truncated: true, next: "token/with+chars", ("shots/a.png", 10)),
            Listing(truncated: false, next: null, ("shots/b.png", 20), ("shots/b", 2)),
        ]);
        var handler = new R2ClientTests.RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(pages.Dequeue()) });
        var client = new R2Client(new R2Config("https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com", "key", "secret", "screenshots"), new HttpClient(handler));

        var objects = await client.ListObjectsAsync("shots/", CancellationToken.None);

        Assert.Equal(["shots/a.png", "shots/b.png", "shots/b"], objects.Select(o => o.Key));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal("https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com/screenshots?list-type=2&max-keys=1000&prefix=shots%2F", handler.Requests[0].Uri);
        Assert.Contains("continuation-token=token%2Fwith%2Bchars", handler.Requests[1].Uri);
        Assert.StartsWith("AWS4-HMAC-SHA256 ", handler.Requests[1].Headers["Authorization"]);
    }

    [Fact]
    public void ScreenshotsArePairedWithTheirEmbedPagesAndTestFilesAreHidden()
    {
        var older = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero);
        var newer = older.AddDays(3);
        StoredObject[] objects =
        [
            new("shots/old.png", 1_000, older),
            new("shots/new.png", 5_000, newer),
            new("shots/new", 2_000, newer),                    // its embed page
            new("shots/sharpshot-test-x.png", 100, newer),     // left over from a connection test
            new("shots/notes.txt", 300, older),                // not a screenshot
            new("shots/big", 900_000, older),                  // same name pattern, but far too big to be an embed page
            new("shots/big.png", 4_000, older.AddHours(1)),
        ];
        var history = new Dictionary<string, HistoryEntry>
        {
            ["shots/old.png"] = new(older.AddMinutes(-5), "https://cdn/old.png", "shots/old.png", 1920, 1080, 1_000, @"C:\Pictures\old.png"),
        };

        var contents = UploadLibrary.Build(objects, "shots/", history);

        Assert.Equal(["shots/new.png", "shots/big.png", "shots/old.png"], contents.Shots.Select(s => s.ImageKey));
        Assert.Equal("shots/new", contents.Shots[0].PageKey);
        Assert.Equal(7_000, contents.Shots[0].SizeBytes);
        Assert.Null(contents.Shots[1].PageKey);
        Assert.Equal((1920, 1080, @"C:\Pictures\old.png"), (contents.Shots[2].Width, contents.Shots[2].Height, contents.Shots[2].LocalCopyPath));
        Assert.Equal(objects.Sum(o => o.Size), contents.TotalBytes);
    }

    [Fact]
    public async Task DeletingRemovesTheImageAndPageFromR2ButNeverTheLocalCopy()
    {
        using var temp = new TempDirectory();
        var localCopy = temp.File("local.png");
        File.WriteAllBytes(localCopy, [1, 2, 3]);
        var history = new UploadHistory(temp.File("history.jsonl"));
        history.Append(new HistoryEntry(DateTimeOffset.Now, "https://cdn/a", "a.png", 10, 10, 3, localCopy));
        history.Append(new HistoryEntry(DateTimeOffset.Now, "https://cdn/keep", "keep.png", 10, 10, 3, null));

        var handler = new R2ClientTests.RecordingHandler(request => request.Method == HttpMethod.Head
            ? HeadResponse("text/html")
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var library = new UploadLibrary(history, temp.File("thumbs"), new HttpClient(handler));
        var shot = new StoredShot("a.png", "a", 5, DateTimeOffset.Now, 10, 10, localCopy);

        var result = await library.DeleteAsync(SettingsTests.ValidSettings(), [shot], progress: null, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal([shot], result.Deleted);
        Assert.Equal(
            [(HttpMethod.Head, "/screenshots/a"), (HttpMethod.Delete, "/screenshots/a"), (HttpMethod.Delete, "/screenshots/a.png")],
            handler.Requests.Select(r => (r.Method, new Uri(r.Uri).AbsolutePath)));
        Assert.True(File.Exists(localCopy), "The local copy must never be deleted");
        Assert.Equal(["keep.png"], history.ReadAll().Keys);
    }

    [Fact]
    public async Task AFileThatOnlySharesTheScreenshotsNameIsNotDeletedWithIt()
    {
        using var temp = new TempDirectory();
        var handler = new R2ClientTests.RecordingHandler(request => request.Method == HttpMethod.Head
            ? HeadResponse("application/pdf")
            : new HttpResponseMessage(HttpStatusCode.NoContent));
        var library = new UploadLibrary(new UploadHistory(temp.File("history.jsonl")), temp.File("thumbs"), new HttpClient(handler));

        await library.DeleteAsync(SettingsTests.ValidSettings(), [new StoredShot("docs/readme.png", "docs/readme", 5, DateTimeOffset.Now, 0, 0, null)],
            progress: null, CancellationToken.None);

        Assert.Equal(["/screenshots/docs/readme.png"], handler.Requests.Where(r => r.Method == HttpMethod.Delete).Select(r => new Uri(r.Uri).AbsolutePath));
    }

    [Fact]
    public async Task ARepeatingListingStopsInsteadOfLoopingForever()
    {
        var handler = new R2ClientTests.RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(Listing(truncated: true, next: "same-token", ("a.png", 1))),
        });
        var client = new R2Client(new R2Config("https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com", "key", "secret", "screenshots"), new HttpClient(handler));

        await Assert.ThrowsAsync<StorageException>(() => client.ListObjectsAsync("", CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void ListingsWithDtdsAreRejected()
    {
        const string bomb = "<?xml version=\"1.0\"?><!DOCTYPE r [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>" +
            "<ListBucketResult><Contents><Key>&b;</Key></Contents></ListBucketResult>";

        Assert.Throws<StorageException>(() => R2Client.ParseListing(bomb));
    }

    [Fact]
    public void OnlyReasonablySizedPngsAreDecodedForThumbnails()
    {
        using var png = new MemoryStream();
        using (var bitmap = new Bitmap(40, 30))
        {
            bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }

        png.Position = 0;
        Assert.True(UploadLibrary.IsSafeToDecode(png));
        Assert.Equal(0, png.Position);

        var huge = png.ToArray();
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(16), 30_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(huge.AsSpan(20), 30_000);
        Assert.False(UploadLibrary.IsSafeToDecode(new MemoryStream(huge)), "A tiny file claiming 900 megapixels must not be decoded");

        using var gif = new MemoryStream();
        using (var bitmap = new Bitmap(40, 30))
        {
            bitmap.Save(gif, System.Drawing.Imaging.ImageFormat.Gif);
        }

        gif.Position = 0;
        Assert.False(UploadLibrary.IsSafeToDecode(gif), "Only PNGs are decoded");
        Assert.False(UploadLibrary.IsSafeToDecode(new MemoryStream([0x89, 0x50])));
    }

    [Fact]
    public async Task AFailedDeleteStopsAndReportsWhatWasDeleted()
    {
        using var temp = new TempDirectory();
        var calls = 0;
        var handler = new R2ClientTests.RecordingHandler(_ => ++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.NoContent)
            : new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<Error><Code>AccessDenied</Code></Error>") });
        var library = new UploadLibrary(new UploadHistory(temp.File("history.jsonl")), temp.File("thumbs"), new HttpClient(handler));
        StoredShot[] shots =
        [
            new("first.png", null, 1, DateTimeOffset.Now, 0, 0, null),
            new("second.png", null, 1, DateTimeOffset.Now, 0, 0, null),
            new("third.png", null, 1, DateTimeOffset.Now, 0, 0, null),
        ];

        var result = await library.DeleteAsync(SettingsTests.ValidSettings(), shots, progress: null, CancellationToken.None);

        Assert.Equal(["first.png"], result.Deleted.Select(s => s.ImageKey));
        Assert.Contains("denied", result.Error);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public void ThumbnailsFillTheirBoxWithoutStretching()
    {
        using var wide = new Bitmap(400, 100);
        using (var g = Graphics.FromImage(wide))
        {
            g.Clear(Color.Red);
            g.FillRectangle(Brushes.Blue, 150, 0, 100, 100); // the middle, which should survive the crop
        }

        using var stream = new MemoryStream();
        wide.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;

        using var thumbnail = UploadLibrary.CreateThumbnail(stream, new Size(160, 100));

        Assert.Equal(new Size(160, 100), thumbnail.Size);
        Assert.True(thumbnail.GetPixel(80, 50).B > 200, "The centre of the image should be kept");
    }

    /// <summary>Read-only check against the bucket configured on this PC. It only lists objects.</summary>
    [OptInFact("SHARPSHOT_LIVE_R2")]
    public async Task LiveListingWorksAgainstTheConfiguredBucket()
    {
        var settings = new Settings.SettingsStore(AppPaths.Default.SettingsFile).Load();
        Assert.True(Settings.SettingsValidator.IsR2Ready(settings), "Sharpshot isn't configured on this PC");
        using var http = new HttpClient();
        var library = new UploadLibrary(new UploadHistory(AppPaths.Default.HistoryFile), Path.GetTempPath(), http);

        var contents = await library.LoadAsync(settings, CancellationToken.None);

        Console.WriteLine($"LIVE: {contents.Shots.Count} screenshots, {Sizes.Format(contents.TotalBytes)} total, " +
            $"{contents.Shots.Count(s => s.PageKey is not null)} with embed pages");

        // A thumbnail through the public domain (ignoring any local copy), which is how other PCs' uploads show up.
        if (contents.Shots.Count > 0)
        {
            var remote = contents.Shots[0] with { LocalCopyPath = null };
            using var thumbnail = await library.LoadThumbnailAsync(settings, remote, new Size(320, 200), CancellationToken.None);
            Assert.NotNull(thumbnail);
            Console.WriteLine($"LIVE: thumbnail {thumbnail!.Width}x{thumbnail.Height} for {remote.ImageKey.Length}-char key");
        }
    }

    [Theory]
    [InlineData(999, "999 B")]
    [InlineData(245_000, "245 KB")]
    [InlineData(2_350_000, "2.4 MB")]
    [InlineData(1_240_000_000, "1.24 GB")]
    public void SizesAreReadable(long bytes, string expected) => Assert.Equal(expected, Sizes.Format(bytes));

    private static HttpResponseMessage HeadResponse(string contentType) => new(HttpStatusCode.OK)
    {
        Content = new ByteArrayContent([]) { Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(contentType) } },
    };

    private static string Listing(bool truncated, string? next, params (string Key, long Size)[] items)
    {
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\">");
        foreach (var (key, size) in items)
        {
            xml.Append($"<Contents><Key>{key}</Key><LastModified>2026-09-14T12:00:00.000Z</LastModified><Size>{size}</Size></Contents>");
        }

        xml.Append($"<IsTruncated>{(truncated ? "true" : "false")}</IsTruncated>");
        if (next is not null)
        {
            xml.Append($"<NextContinuationToken>{next}</NextContinuationToken>");
        }

        return xml.Append("</ListBucketResult>").ToString();
    }
}