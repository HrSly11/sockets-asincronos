using ChatCliente.Network;
using Xunit;

namespace Chat.FunctionalTests;

public sealed class FileNameSanitizationTests
{
    [Theory]
    [InlineData("CON.txt", "_CON.txt")]
    [InlineData("NUL", "_NUL")]
    [InlineData("com1.JSON", "_com1.JSON")]
    [InlineData("Lpt9.log", "_Lpt9.log")]
    [InlineData("PRN .txt", "_PRN .txt")]
    [InlineData("COM¹.txt", "_COM¹.txt")]
    [InlineData("lpt².JSON", "_lpt².JSON")]
    [InlineData("Com³", "_Com³")]
    [InlineData("LPT³.log", "_LPT³.log")]
    public void Windows_device_names_are_prefixed_deterministically(
        string input,
        string expected)
    {
        Assert.Equal(expected, ChatClient.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("report. ", "report")]
    [InlineData(".", "archivo")]
    [InlineData("..", "archivo")]
    [InlineData("   ", "archivo")]
    [InlineData("AUX...   ", "_AUX")]
    public void Trailing_dot_space_and_empty_aliases_become_safe(
        string input,
        string expected)
    {
        Assert.Equal(expected, ChatClient.SanitizeFileName(input));
    }

    [Theory]
    [InlineData("résumé-😀-漢字-العربية.txt")]
    [InlineData("данные-日本語.bin")]
    [InlineData("report¹.txt")]
    public void Valid_unicode_file_names_remain_exact(string input)
    {
        Assert.Equal(input, ChatClient.SanitizeFileName(input));
    }
}
