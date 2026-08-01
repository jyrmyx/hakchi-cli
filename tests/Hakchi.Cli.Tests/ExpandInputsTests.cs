using Xunit;

namespace Hakchi.Cli.Tests;

public class ExpandInputsTests
{
    [Fact]
    public void ExpandInputs_MultipleFiles()
    {
        using var tmp = new TempDir();
        var a = Path.Combine(tmp.Path, "a.nes");
        var b = Path.Combine(tmp.Path, "b.sfc");
        File.WriteAllText(a, "a");
        File.WriteAllText(b, "b");

        var result = AddGameCommand.ExpandInputs(new[] { a, b }).ToList();
        Assert.Equal(2, result.Count);
        Assert.Contains(Path.GetFullPath(a), result);
        Assert.Contains(Path.GetFullPath(b), result);
    }

    [Fact]
    public void ExpandInputs_Glob()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "one.zip"), "1");
        File.WriteAllText(Path.Combine(tmp.Path, "two.zip"), "2");
        File.WriteAllText(Path.Combine(tmp.Path, "skip.txt"), "x");

        var pattern = Path.Combine(tmp.Path, "*.zip");
        var result = AddGameCommand.ExpandInputs(new[] { pattern }).ToList();
        Assert.Equal(2, result.Count);
        Assert.All(result, p => Assert.EndsWith(".zip", p, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExpandInputs_Dedupes()
    {
        using var tmp = new TempDir();
        var a = Path.Combine(tmp.Path, "a.nes");
        File.WriteAllText(a, "a");

        var result = AddGameCommand.ExpandInputs(new[] { a, a }).ToList();
        Assert.Single(result);
    }

    [Fact]
    public void ExpandInputs_MissingFile_Throws()
    {
        Assert.Throws<FileNotFoundException>(() =>
            AddGameCommand.ExpandInputs(new[] { "/no/such/file.nes" }).ToList());
    }

    private sealed class TempDir : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "hakchi-expand-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { /* ignore */ }
        }
    }
}
