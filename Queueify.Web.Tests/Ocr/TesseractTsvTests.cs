using Queueify.Web.Ocr;

namespace Queueify.Web.Tests.Ocr;

public class TesseractTsvTests
{
    private const string Header = "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n";

    private static string Row(int level, int block, int par, int line, int word, int l, int t, int w, int h, double conf, string text) =>
        $"{level}\t1\t{block}\t{par}\t{line}\t{word}\t{l}\t{t}\t{w}\t{h}\t{conf.ToString(System.Globalization.CultureInfo.InvariantCulture)}\t{text}\n";

    [Fact]
    public void Lines_come_with_their_words_joined_and_their_boxes()
    {
        var tsv = Header
            + Row(1, 0, 0, 0, 0, 0, 0, 1600, 1000, -1, "")
            + Row(4, 1, 1, 1, 0, 72, 70, 505, 50, -1, "")
            + Row(5, 1, 1, 1, 1, 72, 80, 248, 40, 96.2, "CHARLY")
            + Row(5, 1, 1, 1, 2, 343, 70, 234, 50, 96.4, "GARCÍA")
            + Row(4, 1, 1, 2, 0, 69, 147, 593, 37, -1, "")
            + Row(5, 1, 1, 2, 1, 69, 147, 83, 30, 91.9, "Clics");

        var reading = TesseractOcr.ParseTsv(tsv, "Español + English");

        Assert.Equal((1600, 1000), (reading.Width, reading.Height));
        Assert.Equal("Español + English", reading.Language);
        Assert.Collection(reading.Lines,
            l => { Assert.Equal("CHARLY GARCÍA", l.Text); Assert.Equal(new(72, 70, 505, 50), l.Bounds); },
            l => { Assert.Equal("Clics", l.Text); Assert.Equal(new(69, 147, 593, 37), l.Bounds); });
    }

    [Fact]
    public void Blank_words_and_lines_without_words_are_dropped()
    {
        var tsv = Header
            + Row(1, 0, 0, 0, 0, 0, 0, 100, 100, -1, "")
            + Row(4, 1, 1, 1, 0, 5, 5, 50, 10, -1, "")
            + Row(5, 1, 1, 1, 1, 5, 5, 50, 10, 90, "   ")
            + Row(4, 1, 1, 2, 0, 5, 20, 50, 10, -1, "")
            + Row(5, 1, 1, 2, 1, 5, 20, 50, 10, -1, "ghost");   // conf -1: the engine isn't sure there's a word

        Assert.Empty(TesseractOcr.ParseTsv(tsv, "x").Lines);
    }

    [Fact]
    public void Garbage_output_gives_an_empty_reading_instead_of_throwing()
    {
        var reading = TesseractOcr.ParseTsv("not\ttsv\nat all\n1\t2", "x");
        Assert.Empty(reading.Lines);
    }

    [Fact]
    public void Text_with_odd_characters_survives()
    {
        var tsv = Header + Row(1, 0, 0, 0, 0, 0, 0, 10, 10, -1, "") + Row(4, 1, 1, 1, 0, 0, 0, 9, 9, -1, "")
            + Row(5, 1, 1, 1, 1, 0, 0, 9, 9, 80, "ñandú\"<b>");
        Assert.Equal("ñandú\"<b>", TesseractOcr.ParseTsv(tsv, "x").Lines.Single().Text);
    }

    [Theory]
    [InlineData("auto", "spa+eng")]
    [InlineData("es", "spa")]
    [InlineData("en", "eng")]
    public void The_language_choices_map_to_tesseract_languages(string choice, string expected) =>
        Assert.Equal(expected, TesseractOcr.LanguagesFor(choice));

    [Theory]
    [InlineData("spa+eng; rm -rf /")]
    [InlineData("../../etc")]
    [InlineData("fr")]
    [InlineData("")]
    public void Nothing_else_reaches_the_command_line(string choice) =>
        Assert.Throws<ArgumentException>(() => TesseractOcr.LanguagesFor(choice));

    [Fact]
    public void The_arguments_read_stdin_and_write_tsv_without_any_file()
    {
        var args = TesseractOcr.ArgumentsFor("spa", "/data/tessdata");
        Assert.Equal(["stdin", "stdout", "--tessdata-dir", "/data/tessdata", "-l", "spa", "-c", "tessedit_create_tsv=1"], args);
    }
}
