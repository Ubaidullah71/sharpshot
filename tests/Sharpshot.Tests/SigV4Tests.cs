using System.Text;
using Sharpshot.Storage;

namespace Sharpshot.Tests;

/// <summary>Uses the worked examples from AWS's "Signature Version 4 for S3" documentation.</summary>
public class SigV4Tests
{
    private const string AccessKey = "AKIAIOSFODNN7EXAMPLE";
    private const string SecretKey = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private static readonly DateTime Date = new(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void GetObjectExampleMatchesAws()
    {
        var headers = new Dictionary<string, string>
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["Range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = SigV4.EmptyPayloadHash,
            ["x-amz-date"] = "20130524T000000Z",
        };

        var canonical = SigV4.CanonicalRequest("GET", "/test.txt", "", headers, SigV4.EmptyPayloadHash, out var signedHeaders);

        Assert.Equal("host;range;x-amz-content-sha256;x-amz-date", signedHeaders);
        Assert.Equal("7344ae5b7ee6c3e7e6b0fe0640412a37625d1fbfff95c48bbb2dc43964946972", SigV4.HashHex(Encoding.UTF8.GetBytes(canonical)));

        var authorization = SigV4.AuthorizationHeader(AccessKey, SecretKey, Date, "us-east-1", "s3", canonical, signedHeaders);
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, " +
            "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
            "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            authorization);
    }

    [Fact]
    public void PutObjectExampleMatchesAws()
    {
        var body = Encoding.UTF8.GetBytes("Welcome to Amazon S3.");
        var payloadHash = SigV4.HashHex(body);
        Assert.Equal("44ce7dd67c959e0d3524ffac1771dfbba87d2b6b4b4e99e42034a8b803f8b072", payloadHash);

        var headers = new Dictionary<string, string>
        {
            ["Date"] = "Fri, 24 May 2013 00:00:00 GMT",
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["x-amz-date"] = "20130524T000000Z",
            ["x-amz-storage-class"] = "REDUCED_REDUNDANCY",
            ["x-amz-content-sha256"] = payloadHash,
        };

        var canonical = SigV4.CanonicalRequest("PUT", SigV4.EncodePath("/test$file.text"), "", headers, payloadHash, out var signedHeaders);
        var authorization = SigV4.AuthorizationHeader(AccessKey, SecretKey, Date, "us-east-1", "s3", canonical, signedHeaders);

        Assert.StartsWith("PUT\n/test%24file.text\n\n", canonical);
        Assert.EndsWith("Signature=98ad721746da40c64f1a55b78f14c238d841ea1380cd77a1b5971af0ece108bd", authorization);
    }

    [Theory]
    [InlineData("shots/abc-_.~123.png", "shots/abc-_.~123.png")]
    [InlineData("a b+c.png", "a%20b%2Bc.png")]
    [InlineData("⠓⠕.png", "%E2%A0%93%E2%A0%95.png")]
    [InlineData("\u200B\u2060.png", "%E2%80%8B%E2%81%A0.png")]
    [InlineData("😀.png", "%F0%9F%98%80.png")]
    public void EncodePathEncodesEverythingButUnreservedCharacters(string key, string expected) =>
        Assert.Equal(expected, SigV4.EncodePath(key));
}