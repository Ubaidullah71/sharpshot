using System.Net;
using Sharpshot.Storage;

namespace Sharpshot.Tests;

public class R2ClientTests
{
    private static readonly R2Config Config = new(
        "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com",
        "access-key",
        "secret-key",
        "screenshots");

    [Fact]
    public async Task PutSendsExactEncodedPathAndRequiredHeaders()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new R2Client(Config, new HttpClient(handler));

        await client.PutNewObjectAsync("shots/⠓⠕⠍⠑⠎.png", [1, 2, 3], "image/png", CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal(
            "https://0123456789abcdef0123456789abcdef.r2.cloudflarestorage.com/screenshots/shots/%E2%A0%93%E2%A0%95%E2%A0%8D%E2%A0%91%E2%A0%8E.png",
            request.Uri);
        Assert.Equal("image/png", request.ContentType);
        Assert.Equal("*", request.Headers["If-None-Match"]);
        Assert.Equal(R2Client.CacheControl, request.Headers["Cache-Control"]);
        Assert.Equal(SigV4.HashHex([1, 2, 3]), request.Headers["x-amz-content-sha256"]);
        Assert.Matches(@"^AWS4-HMAC-SHA256 Credential=access-key/\d{8}/auto/s3/aws4_request, SignedHeaders=host;if-none-match;x-amz-content-sha256;x-amz-date, Signature=[0-9a-f]{64}$",
            request.Headers["Authorization"]);
        Assert.Equal(new byte[] { 1, 2, 3 }, request.Body);
    }

    [Fact]
    public async Task InvisibleCharactersSurviveTheTripToTheWire()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new R2Client(Config, new HttpClient(handler));

        await client.PutNewObjectAsync("\u200B\u200C\u200D\u2060.png", [0], "image/png", CancellationToken.None);

        Assert.EndsWith("/screenshots/%E2%80%8B%E2%80%8C%E2%80%8D%E2%81%A0.png", handler.Requests[0].Uri);
    }

    [Theory]
    [InlineData(HttpStatusCode.PreconditionFailed, "PreconditionFailed", StorageErrorKind.AlreadyExists)]
    [InlineData(HttpStatusCode.Forbidden, "SignatureDoesNotMatch", StorageErrorKind.Authentication)]
    [InlineData(HttpStatusCode.Unauthorized, null, StorageErrorKind.Authentication)]
    [InlineData(HttpStatusCode.NotFound, "NoSuchBucket", StorageErrorKind.BucketNotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable, null, StorageErrorKind.Transient)]
    [InlineData(HttpStatusCode.TooManyRequests, null, StorageErrorKind.Transient)]
    [InlineData(HttpStatusCode.BadRequest, "InvalidArgument", StorageErrorKind.Unknown)]
    public async Task ErrorResponsesAreClassified(HttpStatusCode status, string? code, StorageErrorKind expected)
    {
        var body = code is null ? "" : $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>nope</Message></Error>";
        var handler = new RecordingHandler(_ => new HttpResponseMessage(status) { Content = new StringContent(body) });
        var client = new R2Client(Config, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<StorageException>(() => client.PutNewObjectAsync("a.png", [0], "image/png", CancellationToken.None));

        Assert.Equal(expected, error.Kind);
        Assert.False(string.IsNullOrWhiteSpace(error.Message));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NameTakenChecksWhetherAnEarlierAttemptAlreadyStoredTheSameFile(bool sameFile)
    {
        byte[] data = [1, 2, 3, 4];
        var storedMd5 = sameFile
            ? Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(data))
            : "00000000000000000000000000000000";
        var handler = new RecordingHandler(request =>
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK);
                head.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue($"\"{storedMd5}\"");
                return head;
            }

            return new HttpResponseMessage(HttpStatusCode.PreconditionFailed)
            {
                Content = new StringContent("<Error><Code>PreconditionFailed</Code></Error>"),
            };
        });
        var client = new R2Client(Config, new HttpClient(handler));

        var put = client.PutNewObjectAsync("maybe.png", data, "image/png", CancellationToken.None);

        if (sameFile)
        {
            await put;
        }
        else
        {
            var error = await Assert.ThrowsAsync<StorageException>(() => put);
            Assert.Equal(StorageErrorKind.AlreadyExists, error.Kind);
        }

        Assert.Equal([HttpMethod.Put, HttpMethod.Head], handler.Requests.Select(r => r.Method));
    }

    [Theory]
    [InlineData(0, 30)]
    [InlineData(10_000_000, 342)]
    [InlineData(10_000_000_000, 1800)]
    public void TimeoutGrowsWithUploadSize(long bytes, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), R2Client.TimeoutFor(bytes));

    [Fact]
    public async Task NetworkFailuresAreTransient()
    {
        var handler = new RecordingHandler(_ => throw new HttpRequestException("no route to host"));
        var client = new R2Client(Config, new HttpClient(handler));

        var error = await Assert.ThrowsAsync<StorageException>(() => client.PutNewObjectAsync("a.png", [0], "image/png", CancellationToken.None));

        Assert.True(error.IsTransient);
    }

    [Fact]
    public async Task ClockSkewIsCorrectedOnceUsingTheServerTime()
    {
        var serverTime = DateTimeOffset.UtcNow.AddHours(3);
        var calls = 0;
        var handler = new RecordingHandler(_ =>
        {
            if (calls++ == 0)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden)
                {
                    Content = new StringContent("<Error><Code>RequestTimeTooSkewed</Code></Error>"),
                };
                response.Headers.Date = serverTime;
                return response;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var client = new R2Client(Config, new HttpClient(handler));

        await client.PutNewObjectAsync("a.png", [0], "image/png", CancellationToken.None);

        Assert.Equal(2, handler.Requests.Count);
        var retriedDate = DateTime.ParseExact(handler.Requests[1].Headers["x-amz-date"], "yyyyMMdd'T'HHmmss'Z'", null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        Assert.InRange(retriedDate, serverTime.UtcDateTime.AddMinutes(-1), serverTime.UtcDateTime.AddMinutes(1));
    }

    internal sealed record RecordedRequest(HttpMethod Method, string Uri, Dictionary<string, string> Headers, string? ContentType, byte[]? Body);

    internal sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content is null ? null : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new RecordedRequest(request.Method, request.RequestUri!.AbsoluteUri, headers, request.Content?.Headers.ContentType?.MediaType, body));
            return respond(request);
        }
    }
}