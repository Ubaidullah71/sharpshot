using System.Globalization;

namespace Sharpshot.Uploads;

internal static class LocalCopies
{
    private const int FileExistsHResult = unchecked((int)0x80070050); // ERROR_FILE_EXISTS

    public static string Save(string rootFolder, DateTimeOffset takenAt, byte[] png)
    {
        var folder = Path.Combine(rootFolder, takenAt.ToString("yyyy-MM", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);

        var baseName = $"{AppInfo.Name} {takenAt.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)}";
        for (var n = 1; ; n++)
        {
            var path = Path.Combine(folder, n == 1 ? $"{baseName}.png" : $"{baseName} ({n}).png");
            FileStream file;
            try
            {
                file = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            }
            catch (IOException ex) when (ex.HResult == FileExistsHResult && n < 1000)
            {
                continue; // two screenshots in the same second: try the next number
            }

            try
            {
                using (file)
                {
                    file.Write(png);
                }

                return path;
            }
            catch
            {
                // A full disk or a dropped network folder: don't leave a broken file behind.
                try
                {
                    File.Delete(path);
                }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
                {
                    Log.Warn($"Couldn't remove a partly written local copy: {cleanup.Message}");
                }

                throw;
            }
        }
    }
}