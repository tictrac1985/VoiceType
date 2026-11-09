using VoiceType.Core.Speech;
using VoiceType.Text;
using Xunit;

namespace VoiceType.Tests.Text;

public class HallucinationsTests
{
    [Theory]
    [InlineData("السلام عليكم، النهارده هنتكلم عن المشروع. (ترجمة نانسي قنقر.)", "السلام عليكم، النهارده هنتكلم عن المشروع.")]
    [InlineData("السلام عليكم\n(ترجمة نانسي قنقر)", "السلام عليكم")]
    [InlineData("السلام عليكم ترجمة نانسي قنقر", "السلام عليكم")]
    [InlineData("ترجمة نانسي قنقر. السلام عليكم", "السلام عليكم")]
    [InlineData("أول جملة. ترجمة: نانسي قنقر. تاني جملة.", "أول جملة. تاني جملة.")]
    [InlineData("السلام عليكم «ترجمة نانسي قنقر»", "السلام عليكم")]
    public void Strips_the_subtitle_credit_wherever_it_appears(string input, string expected)
    {
        Assert.Equal(expected, Hallucinations.Clean(input));
    }

    [Theory]
    [InlineData("ترجمة نانسي قنقر")]
    [InlineData("(ترجمة نانسي قنقر.)")]
    [InlineData("اشتركوا في القناة")]
    [InlineData("Thanks for watching!")]
    [InlineData("  ")]
    public void A_hallucination_alone_becomes_empty(string input)
    {
        Assert.Equal(string.Empty, Hallucinations.Clean(input));
    }

    [Theory]
    [InlineData("شكرا للمشاهدة يا جماعة، ونتقابل الحلقة الجاية.")]
    [InlineData("اشتركوا في القناة وفعلوا الجرس")]
    [InlineData("السطر الأول\n\nالسطر التاني")]
    public void Leaves_normal_speech_alone(string input)
    {
        Assert.Equal(input, Hallucinations.Clean(input));
    }

    [Fact]
    public void Cleans_file_segments_and_drops_the_empty_ones()
    {
        var segments = new[]
        {
            new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(3), "أهلا بيكم", 1),
            new TranscriptSegment(TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(6), "كلام مهم. ترجمة نانسي قنقر", 2),
            new TranscriptSegment(TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(9), "(ترجمة نانسي قنقر)"),
        };

        var cleaned = Hallucinations.Clean(segments);

        Assert.Equal(2, cleaned.Count);
        Assert.Equal("أهلا بيكم", cleaned[0].Text);
        Assert.Equal("كلام مهم.", cleaned[1].Text);
        Assert.Equal(2, cleaned[1].Speaker);
    }

    [Fact]
    public void Returns_the_same_list_when_nothing_changes()
    {
        IReadOnlyList<TranscriptSegment> segments = [new TranscriptSegment(TimeSpan.Zero, TimeSpan.FromSeconds(1), "تمام")];
        Assert.Same(segments, Hallucinations.Clean(segments));
    }
}
