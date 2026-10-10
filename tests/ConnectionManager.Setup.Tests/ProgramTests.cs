namespace ConnectionManager.Setup.Tests;

public sealed class ProgramTests
{
    [Fact]
    public void No_arguments_is_the_interactive_install() => Assert.Equal(SetupMode.Interactive, Program.ParseArguments([]));

    [Fact]
    public void Elevated_is_the_admin_half() => Assert.Equal(SetupMode.Elevated, Program.ParseArguments(["--elevated"]));

    [Fact]
    public void Unattended_is_the_scripted_install() => Assert.Equal(SetupMode.Unattended, Program.ParseArguments(["--unattended"]));

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--Elevated")] // exact matching
    [InlineData("--UNATTENDED")]
    [InlineData("/elevated")]
    [InlineData("-unattended")]
    [InlineData("--elevated ")]
    [InlineData("")]
    public void Anything_else_is_refused(string argument) => Assert.Null(Program.ParseArguments([argument]));

    [Theory]
    [InlineData("--elevated", "--elevated")]
    [InlineData("--unattended", "--unattended")]
    [InlineData("--elevated", "--unattended")]
    [InlineData("--unattended", "--bogus")]
    public void Two_arguments_are_refused(string first, string second) => Assert.Null(Program.ParseArguments([first, second]));

    [Fact]
    public void Program_Files_comes_from_the_registry_as_install_ps1_reads_it() =>
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Program.ProgramFiles(), ignoreCase: true);
}
