using System.Text;
using System.Text.Json;
using Sharpshot.Settings;

namespace Sharpshot.Uploads;

/// <summary>One JSON object per line.</summary>
internal sealed class UploadHistory(string path)
{
    private static readonly JsonSerializerOptions LineOptions = new(SettingsStore.JsonOptions) { WriteIndented = false };

    private readonly Lock _gate = new();

    public void Append(HistoryEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, LineOptions) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line, new UTF8Encoding(false));
        }
    }

    /// <summary>Keyed by object key; the latest entry wins.</summary>
    public IReadOnlyDictionary<string, HistoryEntry> ReadAll()
    {
        var entries = new Dictionary<string, HistoryEntry>(StringComparer.Ordinal);
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return entries;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (TryParse(line) is { } entry)
                {
                    entries[entry.Key] = entry;
                }
            }
        }

        return entries;
    }

    /// <summary>Rewrites the file atomically. Local copies aren't touched.</summary>
    public void Remove(IReadOnlySet<string> keys)
    {
        if (keys.Count == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return;
            }

            var kept = File.ReadLines(path).Where(line => TryParse(line) is not { } entry || !keys.Contains(entry.Key)).ToList();
            var temp = path + ".tmp";
            File.WriteAllLines(temp, kept, new UTF8Encoding(false));
            File.Move(temp, path, overwrite: true);
        }
    }

    /// <returns>Null for a blank or damaged line, which is skipped rather than losing the whole history.</returns>
    private static HistoryEntry? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<HistoryEntry>(line, LineOptions) is { Key: not null, Url: not null } entry ? entry : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Newest first. Only reads the end of the file, so it stays fast as history grows.</summary>
    public IReadOnlyList<HistoryEntry> ReadRecent(int count)
    {
        const int TailBytes = 64 * 1024;
        string text;
        lock (_gate)
        {
            if (!File.Exists(path))
            {
                return [];
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(start, SeekOrigin.Begin);
            var buffer = new byte[stream.Length - start];
            stream.ReadExactly(buffer);
            text = Encoding.UTF8.GetString(buffer);
            if (start > 0)
            {
                var firstNewline = text.IndexOf('\n');
                text = firstNewline < 0 ? "" : text[(firstNewline + 1)..]; // drop the partial first line
            }
        }

        var entries = new List<HistoryEntry>(count);
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (TryParse(line) is { } entry)
            {
                entries.Add(entry);
            }

            if (entries.Count == count)
            {
                break;
            }
        }

        return entries;
    }
}

internal sealed record HistoryEntry(
    DateTimeOffset CreatedAt,
    string Url,
    string Key,
    int Width,
    int Height,
    long SizeBytes,
    string? LocalCopyPath);