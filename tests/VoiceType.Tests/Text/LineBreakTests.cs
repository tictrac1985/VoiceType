using VoiceType.Providers.Engines;
using VoiceType.Text;
using Xunit;

namespace VoiceType.Tests.Text;

public class LineBreakTests
{
    private readonly TextProcessor _processor = new(technical: TermDictionary.Empty);

    [Fact]
    public void Voice_command_starts_new_line_and_removes_its_punctuation()
    {
        var result = _processor.Process("أهلا يا محمد، سطر جديد، عامل ايه النهارده", TextMode.General, LineBreakMode.None);
        Assert.Equal("أهلا يا محمد.\nعامل ايه النهارده.", result);
    }

    [Fact]
    public void New_paragraph_leaves_empty_line()
    {
        var result = _processor.Process("الموضوع الأول خلص خلاص. فقرة جديدة. نتكلم في الميزانية بقى", TextMode.General, LineBreakMode.None);
        Assert.Equal("الموضوع الأول خلص خلاص.\n\nنتكلم في الميزانية بقى.", result);
    }

    [Fact]
    public void Command_glued_to_previous_sentence_without_space()
    {
        // نص حقيقي من Gemini: النقطة ملتصقة بالأمر
        var result = _processor.Process("واشكرك بشدة.سطر جديد. انا دلوقتي بجرب سطر جديد لان الكلام ده ما اشتغلش. سطر جديد. دلوقتي التجربة انتهت", TextMode.General, LineBreakMode.Sentences);
        Assert.Equal("واشكرك بشدة.\nانا دلوقتي بجرب.\nلان الكلام ده ما اشتغلش.\nدلوقتي التجربة انتهت.", result);
    }

    [Fact]
    public void English_command_works()
    {
        var result = _processor.Process("first point new line second point", TextMode.Technical, LineBreakMode.None);
        Assert.Equal("first point\nsecond point", result);
    }

    [Fact]
    public void Sentences_mode_puts_each_sentence_on_its_own_line()
    {
        var result = _processor.Process("السلام عليكم يا أحمد. هل خلصت الملف؟ ابعته النهارده لو سمحت.", TextMode.General, LineBreakMode.Sentences);
        Assert.Equal("السلام عليكم يا أحمد.\nهل خلصت الملف؟\nابعته النهارده لو سمحت.", result);
    }

    [Fact]
    public void Sentences_mode_keeps_numbers_and_links_intact()
    {
        var result = _processor.Process("الإصدار 3.5 موجود على example.com/v3.5 وهو أسرع", TextMode.General, LineBreakMode.Sentences);
        Assert.Equal("الإصدار 3.5 موجود على example.com/v3.5 وهو أسرع.", result);
    }

    [Fact]
    public void Default_overload_keeps_single_paragraph()
    {
        var result = _processor.Process("جملة أولى هنا. جملة تانية هنا", TextMode.General);
        Assert.DoesNotContain('\n', result);
    }

    [Fact]
    public void Raw_mode_does_not_apply_commands()
    {
        Assert.Equal("كلمة سطر جديد كلمة", _processor.Process("كلمة سطر جديد كلمة", TextMode.Raw, LineBreakMode.Sentences));
    }

    [Fact]
    public void Windows_line_endings_for_pasting()
    {
        Assert.Equal("a\r\nb\r\n\r\nc", LineBreaks.ToWindows("a\nb\r\n\nc"));
    }

    [Theory]
    [InlineData("<think>let me listen</think>أهلا بيك", "أهلا بيك")]
    [InlineData("Transcription: أهلا بيك", "أهلا بيك")]
    [InlineData("النص: أهلا بيك", "أهلا بيك")]
    [InlineData("\"أهلا بيك\"", "أهلا بيك")]
    [InlineData("```\nأهلا بيك\n```", "أهلا بيك")]
    [InlineData("أهلا بيك", "أهلا بيك")]
    public void Chat_model_output_is_cleaned(string input, string expected)
    {
        Assert.Equal(expected, ChatTextCleanup.Clean(input));
    }
}
