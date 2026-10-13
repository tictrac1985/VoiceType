using VoiceType.Text;
using Xunit;

namespace VoiceType.Tests.Text;

public class BidiTests
{
    [Fact]
    public void Mixed_line_is_wrapped_as_right_to_left()
    {
        var result = BidiFormatter.Apply("افتح الـ Workspace بسرعة.");
        Assert.Equal("‏‫افتح الـ Workspace بسرعة.‬‏", result);
    }

    [Fact]
    public void Line_starting_with_english_still_becomes_arabic_direction()
    {
        var result = BidiFormatter.Apply("Python لغة جميلة.");
        Assert.StartsWith("‏‫Python", result);
    }

    [Fact]
    public void Each_line_is_handled_and_english_lines_are_untouched()
    {
        var result = BidiFormatter.Apply("السلام عليكم.\nHello world.\nهل خلصت الـ API؟");
        var lines = result.Split('\n');
        Assert.Equal("‏السلام عليكم.‏", lines[0]);
        Assert.Equal("Hello world.", lines[1]);
        Assert.Equal("‏‫هل خلصت الـ API؟‬‏", lines[2]);
    }

    [Fact]
    public void Applying_twice_does_not_add_marks_twice_and_strip_removes_them()
    {
        var once = BidiFormatter.Apply("افتح الـ API.");
        Assert.Equal(once, BidiFormatter.Apply(once));
        Assert.Equal("افتح الـ API.", BidiFormatter.Strip(once));
    }

    [Fact]
    public void Pure_english_text_is_unchanged()
    {
        Assert.Equal("git commit -m \"fix\"", BidiFormatter.Apply("git commit -m \"fix\""));
    }

    [Theory]
    [InlineData("النهارده هنراجع الـ API", true)]
    [InlineData("  ١٢٣ - افتح البرنامج", true)]
    [InlineData("\u200Fمرحبا", true)]
    [InlineData("API جديد النهارده", false)]
    [InlineData("Hello world", false)]
    [InlineData("123 456", false)]
    [InlineData("", false)]
    public void Detects_text_that_should_start_from_the_right(string text, bool expected)
    {
        Assert.Equal(expected, BidiFormatter.StartsRightToLeft(text));
    }
}
