namespace Sharpshot.Tests;

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sharpshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // Best effort.
        }
    }
}

/// <summary> A test that only runs when an environment variable is set to 1, and shows as skipped otherwise.</summary>
internal sealed class OptInFactAttribute : FactAttribute
{
    public OptInFactAttribute(string variable)
    {
        if (Environment.GetEnvironmentVariable(variable) != "1")
        {
            Skip = $"Set {variable}=1 to run";
        }
    }
}

internal static class Sta
{
    /// <summary> WinForms controls need a single-threaded apartment; xUnit doesn't provide one.</summary>
    public static void Run(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}