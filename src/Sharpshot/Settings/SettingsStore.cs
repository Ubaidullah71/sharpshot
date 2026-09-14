using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Sharpshot.Settings;

internal sealed class SettingsStore(string path)
{
    private static readonly byte[] Entropy = "Sharpshot.R2.SecretAccessKey"u8.ToArray();

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // The default encoder escapes '+' and non-ASCII (braille) as \uXXXX; this file isn't HTML.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new LenientEnumConverterFactory() },
    };

    public string Path { get; } = path;

    public bool Exists => File.Exists(Path);

    /// <summary>A missing file gives defaults; a broken file is set aside rather than silently lost.</summary>
    public AppSettings Load()
    {
        if (!File.Exists(Path))
        {
            return new AppSettings();
        }

        AppSettings settings;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(Path), JsonOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            var backup = $"{Path}.broken-{DateTime.Now:yyyyMMdd-HHmmss}";
            Log.Error($"Couldn't read settings.json; moving it to {backup} and starting with defaults", ex);
            try
            {
                File.Move(Path, backup, overwrite: true);
            }
            catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
            {
                Log.Error("Couldn't set the broken settings file aside", moveError);
            }

            return new AppSettings();
        }

        FillMissingValues(settings);
        settings.R2.SecretAccessKey = Unprotect(settings.R2.EncryptedSecretAccessKey);
        return settings;
    }

    /// <summary>A hand-edited file may contain nulls (like "bucket": null); treat them as empty rather than crash later.</summary>
    private static void FillMissingValues(AppSettings settings)
    {
        settings.R2 ??= new R2Settings();
        settings.Links ??= new LinkSettings();
        settings.Capture ??= new CaptureSettings();
        settings.General ??= new GeneralSettings();
        settings.Embed ??= new EmbedSettings();

        var r2 = settings.R2;
        r2.AccountId ??= "";
        r2.AccessKeyId ??= "";
        r2.EncryptedSecretAccessKey ??= "";
        r2.Bucket ??= "";
        r2.PublicUrl ??= "";
        r2.PathPrefix ??= "";
        r2.Endpoint ??= "";

        settings.Capture.LocalFolder ??= AppPaths.DefaultScreenshotFolder;

        var embed = settings.Embed;
        embed.Color ??= EmbedSettings.DefaultColor;
        embed.SiteName ??= "";
        embed.Title ??= "";
        embed.Description ??= "";
    }

    /// <summary>Writes atomically, so a crash mid-save can't corrupt the file.</summary>
    public void Save(AppSettings settings)
    {
        var copy = settings.Clone();
        copy.Version = AppSettings.CurrentVersion;
        copy.R2.EncryptedSecretAccessKey = Protect(copy.R2.SecretAccessKey);

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(copy, JsonOptions), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    internal static string Protect(string secret) => string.IsNullOrEmpty(secret)
        ? ""
        : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    internal static string Unprotect(string encrypted)
    {
        if (string.IsNullOrEmpty(encrypted))
        {
            return "";
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(encrypted), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            Log.Warn("Couldn't decrypt the saved Secret Access Key (settings from another PC or user?)");
            return "";
        }
    }
}

/// <summary>An unknown enum name, say from a newer version, falls back to the default instead of breaking the whole file.</summary>
internal sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;

    private sealed class LenientEnumConverter<T> : JsonConverter<T>
        where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.String && Enum.TryParse<T>(reader.GetString(), ignoreCase: true, out var value) && Enum.IsDefined(value))
            {
                return value;
            }

            reader.Skip();
            Log.Warn($"Unknown {typeof(T).Name} value in settings; using the default");
            return default;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}