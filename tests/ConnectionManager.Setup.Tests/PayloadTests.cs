using System.IO.Compression;

namespace ConnectionManager.Setup.Tests;

public sealed class PayloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cm-setup-test-").FullName;

    [Fact]
    public void A_zip_like_the_release_is_accepted() // Windows PowerShell's Compress-Archive writes backslashes
    {
        var zip = Zip(("install.ps1", "x"), (@"app\ConnectionManager.exe", "x"), (@"app\runtimes\", ""), ("app/ui/index.html", "x"));

        Assert.Null(Problem(zip));
    }

    [Theory]
    [InlineData("../x")]
    [InlineData(@"..\x")]
    [InlineData(@"app\..\..\x")]
    [InlineData("app/..")]
    [InlineData(@"C:\x")]
    [InlineData("C:x")]
    [InlineData(@"\x")]
    [InlineData("/x")]
    [InlineData(@"\\server\share\x")]
    [InlineData("app/install.ps1:stream")]
    public void An_entry_that_is_not_a_plain_relative_path_is_refused(string name)
    {
        var zip = Zip(("install.ps1", "x"), (name, "x"));

        Assert.Contains(name, Problem(zip));
    }

    [Fact]
    public void Five_thousand_entries_are_accepted_and_one_more_is_refused()
    {
        var names = Enumerable.Range(0, Payload.MaxEntries + 1).Select(i => ($"app/{i}.dll", "")).ToArray();

        Assert.Null(Problem(Zip(names[..Payload.MaxEntries])));
        Assert.Contains("more than 5000", Problem(Zip(names)));
    }

    [Fact]
    public void The_uncompressed_total_counts_not_each_entry()
    {
        var zip = Zip(("a", "123456"), ("b", "123456"));

        Assert.Null(Problem(zip, maxBytes: 12));
        Assert.Contains("more than 11 bytes", Problem(zip, maxBytes: 11));
    }

    [Fact]
    public void The_real_limit_is_1_GB() => Assert.Equal(1024L * 1024 * 1024, Payload.MaxBytes);

    [Fact]
    public void A_file_that_is_not_a_zip_is_refused()
    {
        var path = Path.Combine(_root, "not.zip");
        File.WriteAllText(path, "not a zip");

        Assert.NotNull(Problem(path));
    }

    private string Zip(params (string Name, string Content)[] entries)
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid()}.zip");
        using var archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(content);
        }
        return path;
    }

    private static string? Problem(string zip, long maxBytes = Payload.MaxBytes)
    {
        using var stream = File.OpenRead(zip);
        return Payload.Problem(stream, maxBytes);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
