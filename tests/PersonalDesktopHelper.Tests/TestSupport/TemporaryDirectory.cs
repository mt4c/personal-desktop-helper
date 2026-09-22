namespace PersonalDesktopHelper.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PersonalDesktopHelper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }
    public string StatePath => System.IO.Path.Combine(Path, "state.json");

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
