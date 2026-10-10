using ConnectionManager.Core.Steam;

namespace ConnectionManager.Core.Tests;

public class VdfTests
{
    [Fact]
    public void Parses_nested_blocks_comments_and_escapes()
    {
        var root = Vdf.Parse("""
            "libraryfolders"
            {
                // a comment
                "0"
                {
                    "path"    "C:\\Program Files (x86)\\Steam"
                    "label"   "say \"hi\""
                }
            }
            """);

        var library = root["libraryfolders"]!["0"]!;
        Assert.Equal(@"C:\Program Files (x86)\Steam", library["path"]!.Value);
        Assert.Equal("say \"hi\"", library["label"]!.Value);
    }

    [Fact]
    public void Keys_are_case_insensitive() =>
        Assert.Equal("1", Vdf.Parse("\"A\" { \"Key\" \"1\" }")["a"]!["KEY"]!.Value);

    [Theory]
    [InlineData("\"a\" {")]
    [InlineData("\"a\" }")]
    [InlineData("}")]
    [InlineData("\"a\" \"unterminated")]
    [InlineData("\"lonely\"")]
    public void Rejects_broken_files(string text) => Assert.Throws<FormatException>(() => Vdf.Parse(text));

    [Fact]
    public void Reads_unquoted_tokens()
    {
        var root = Vdf.Parse("key value\nblock { inner 42 }");

        Assert.Equal("value", root["key"]!.Value);
        Assert.Equal("42", root["block"]!["inner"]!.Value);
    }

    [Fact]
    public void Rejects_absurdly_deep_nesting()
    {
        var text = string.Concat(Enumerable.Repeat("\"a\" { ", 100)) + string.Concat(Enumerable.Repeat("} ", 100));
        Assert.Throws<FormatException>(() => Vdf.Parse(text));
    }
}
