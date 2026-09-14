namespace Sharpshot.Storage;

public enum StorageErrorKind
{
    /// <summary>Wrong keys, or the token can't access this bucket. Retrying won't help.</summary>
    Authentication,

    BucketNotFound,

    AlreadyExists,

    /// <summary>Network trouble, timeouts, throttling or a 5xx. Worth retrying.</summary>
    Transient,

    Unknown,
}

internal sealed class StorageException(StorageErrorKind kind, string message, int? statusCode = null, string? code = null, Exception? inner = null)
    : Exception(message, inner)
{
    public StorageErrorKind Kind { get; } = kind;

    public int? StatusCode { get; } = statusCode;

    public string? Code { get; } = code;

    public bool IsTransient => Kind == StorageErrorKind.Transient;

    public static StorageException FromResponse(int statusCode, string? body, string bucket)
    {
        var (code, detail) = ParseError(body);
        var kind = Classify(statusCode, code);
        var suffix = code is null ? $"HTTP {statusCode}" : $"{code}, HTTP {statusCode}";

        var message = kind switch
        {
            StorageErrorKind.Authentication when code == "SignatureDoesNotMatch" =>
                $"Cloudflare R2 rejected the Secret Access Key ({suffix}). Double-check it in Settings.",
            StorageErrorKind.Authentication when code == "InvalidAccessKeyId" =>
                $"Cloudflare R2 doesn't recognise the Access Key ID ({suffix}). Double-check it in Settings.",
            StorageErrorKind.Authentication =>
                $"Cloudflare R2 denied access ({suffix}). Check the keys and that the API token can write to the \"{bucket}\" bucket.",
            StorageErrorKind.BucketNotFound =>
                $"The bucket \"{bucket}\" doesn't exist in this Cloudflare account ({suffix}).",
            StorageErrorKind.AlreadyExists =>
                $"A file with this name already exists ({suffix}).",
            StorageErrorKind.Transient =>
                $"Cloudflare R2 is temporarily unavailable ({suffix}).",
            _ => string.IsNullOrWhiteSpace(detail)
                ? $"Cloudflare R2 returned an error ({suffix})."
                : $"Cloudflare R2 returned an error ({suffix}): {detail}",
        };

        return new StorageException(kind, message, statusCode, code);
    }

    internal static StorageErrorKind Classify(int statusCode, string? code) => (statusCode, code) switch
    {
        (412, _) or (_, "PreconditionFailed") => StorageErrorKind.AlreadyExists,
        (_, "NoSuchBucket") => StorageErrorKind.BucketNotFound,
        (_, "InvalidAccessKeyId" or "SignatureDoesNotMatch" or "AccessDenied" or "Unauthorized") => StorageErrorKind.Authentication,
        (_, "RequestTimeout" or "SlowDown" or "InternalError" or "ServiceUnavailable") => StorageErrorKind.Transient,
        (401 or 403, _) => StorageErrorKind.Authentication,
        (408 or 429, _) or (>= 500, _) => StorageErrorKind.Transient,
        _ => StorageErrorKind.Unknown,
    };

    internal static (string? Code, string? Message) ParseError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return (null, null);
        }

        try
        {
            var root = R2Client.ParseXml(body).Root;
            string? Element(string name) => root?.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;
            return (Element("Code"), Element("Message"));
        }
        catch (System.Xml.XmlException)
        {
            return (null, null);
        }
    }
}