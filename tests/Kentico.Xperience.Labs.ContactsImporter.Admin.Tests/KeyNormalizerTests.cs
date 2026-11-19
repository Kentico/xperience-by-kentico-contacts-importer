using System.Text.Json;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// Runs the shared TypeScript/C# parity fixture (<c>tests/fixtures/normalization.json</c>) and
/// checks the places where JavaScript and .NET string handling differ.
/// </summary>
public class KeyNormalizerTests
{
    private const char CapitalSigma = (char)0x03A3;
    private const char SmallSigma = (char)0x03C3;
    private const char FinalSigma = (char)0x03C2;

    private static readonly JsonElement fixture = LoadFixture();

    private static JsonElement LoadFixture()
    {
        string path = Path.Combine(TestContext.CurrentContext.TestDirectory, "fixtures", "normalization.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));

        return document.RootElement.Clone();
    }

    private static IEnumerable<TestCaseData> KeyCases() =>
        fixture.GetProperty("keyCases").EnumerateArray().Select((c, i) =>
            new TestCaseData(c.GetProperty("keyField").GetString(), c.GetProperty("input").GetString(), c.GetProperty("normalizedKey").GetString())
                .SetName($"Fixture_KeyCase_{i:00}"));

    private static IEnumerable<TestCaseData> EmailCases() =>
        fixture.GetProperty("emailCases").EnumerateArray().Select((c, i) =>
            new TestCaseData(c.GetProperty("input").GetString(), c.GetProperty("valid").GetBoolean())
                .SetName($"Fixture_EmailCase_{i:00}"));

    private static IEnumerable<TestCaseData> LengthCases() =>
        fixture.GetProperty("lengthCases").EnumerateArray().Select((c, i) =>
            new TestCaseData(
                c.GetProperty("keyField").GetString(),
                c.GetProperty("repeatChar").GetString(),
                c.TryGetProperty("domain", out var domain) ? domain.GetString() : "",
                c.GetProperty("validLength").GetInt32(),
                c.GetProperty("invalidLength").GetInt32())
                .SetName($"Fixture_LengthCase_{i:00}"));

    [Test]
    public void Fixture_Has_Cases() => Assert.Multiple(() =>
                                            {
                                                Assert.That(fixture.GetProperty("keyCases").GetArrayLength(), Is.GreaterThan(0));
                                                Assert.That(fixture.GetProperty("emailCases").GetArrayLength(), Is.GreaterThan(0));
                                                Assert.That(fixture.GetProperty("lengthCases").GetArrayLength(), Is.GreaterThan(0));
                                            });

    [TestCaseSource(nameof(KeyCases))]
    public void NormalizeKey_Matches_Fixture(string keyField, string input, string? expected) =>
        Assert.That(KeyNormalizer.NormalizeKey(keyField, input), Is.EqualTo(expected));

    [TestCaseSource(nameof(EmailCases))]
    public void IsValidEmail_Matches_Fixture(string input, bool expected) =>
        Assert.That(KeyNormalizer.IsValidEmail(input), Is.EqualTo(expected));

    [TestCaseSource(nameof(LengthCases))]
    public void NormalizeKey_Length_Limits_Match_Fixture(string keyField, string repeatChar, string domain, int validLength, int invalidLength)
    {
        string valid = string.Concat(Enumerable.Repeat(repeatChar, validLength - domain.Length)) + domain;
        string invalid = string.Concat(Enumerable.Repeat(repeatChar, invalidLength - domain.Length)) + domain;

        Assert.Multiple(() =>
        {
            Assert.That(valid, Has.Length.EqualTo(validLength));
            Assert.That(KeyNormalizer.NormalizeKey(keyField, valid), Is.EqualTo(valid));
            Assert.That(invalid, Has.Length.EqualTo(invalidLength));
            Assert.That(KeyNormalizer.NormalizeKey(keyField, invalid), Is.Null);
        });
    }

    [Test]
    public void NormalizeKey_Null_Input_Is_Invalid() =>
        Assert.That(KeyNormalizer.NormalizeKey("CustomerID", null), Is.Null);

    [Test]
    public void IsJsWhiteSpace_Matches_JavaScript_Whitespace_Set()
    {
        // Code points matched by /\s/ (and removed by trim) in V8, BMP only. Generated with Node 26.
        int[] expected =
        [
            0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x20, 0xA0, 0x1680,
            0x2000, 0x2001, 0x2002, 0x2003, 0x2004, 0x2005, 0x2006, 0x2007, 0x2008, 0x2009, 0x200A,
            0x2028, 0x2029, 0x202F, 0x205F, 0x3000, 0xFEFF,
        ];

        int[] actual = [.. Enumerable.Range(0, 0x10000).Where(c => KeyNormalizer.IsJsWhiteSpace((char)c))];

        Assert.That(actual, Is.EqualTo(expected));
    }

    [Test]
    public void Trim_Removes_Bom_But_Not_Next_Line_Character()
    {
        string bom = ((char)0xFEFF).ToString();
        string nextLine = ((char)0x85).ToString();

        Assert.Multiple(() =>
        {
            Assert.That(KeyNormalizer.Trim(bom + " a " + bom), Is.EqualTo("a"));
            Assert.That(KeyNormalizer.Trim(nextLine + "a" + nextLine), Is.EqualTo(nextLine + "a" + nextLine));
            Assert.That(KeyNormalizer.Trim("a b"), Is.EqualTo("a b"));
            Assert.That(KeyNormalizer.Trim(" \t\r\n"), Is.Empty);
        });
    }

    [Test]
    public void ToLowerCase_Maps_Capital_I_With_Dot_To_Two_Characters()
    {
        string input = (char)0x0130 + "A";

        Assert.That(KeyNormalizer.ToLowerCase(input), Is.EqualTo("i" + (char)0x0307 + "a"));
    }

    // Expected values produced by String.prototype.toLowerCase in Node 26 (Unicode 17).
    [TestCase("A{S}", "a{f}", Description = "final after a cased letter")]
    [TestCase("A{S}B", "a{s}b", Description = "not final before a cased letter")]
    [TestCase("{S}", "{s}", Description = "not final without a preceding cased letter")]
    [TestCase("{S}@EXAMPLE.COM", "{s}@example.com")]
    [TestCase("OD{S}@EXAMPLE.COM", "od{f}@example.com", Description = "'@' is not case-ignorable")]
    [TestCase("OD{S}.GR@EXAMPLE.COM", "od{s}.gr@example.com", Description = "'.' is case-ignorable")]
    [TestCase("A.{S}", "a.{f}", Description = "case-ignorable between cased letter and sigma")]
    [TestCase("A{S}'B", "a{s}'b")]
    [TestCase("A{S} B", "a{f} b")]
    [TestCase("A{S}{S}", "a{s}{f}")]
    public void ToLowerCase_Applies_Final_Sigma_Rule(string input, string expected)
    {
        input = input.Replace("{S}", CapitalSigma.ToString());
        expected = expected.Replace("{s}", SmallSigma.ToString()).Replace("{f}", FinalSigma.ToString());

        Assert.That(KeyNormalizer.ToLowerCase(input), Is.EqualTo(expected));
    }

    [TestCase("a@b.c", true)]
    [TestCase("a@b.c.", true)]
    [TestCase("a@.b.c", true)]
    [TestCase("a@b..c", true)]
    [TestCase("a@b.", false)]
    [TestCase("a.b@c", false)]
    [TestCase("a@b\t.c", false)]
    [TestCase("@", false)]
    public void IsValidEmail_Follows_The_Contract_Pattern(string input, bool expected) =>
        Assert.That(KeyNormalizer.IsValidEmail(input), Is.EqualTo(expected));

    [Test]
    public void IsValidEmail_Rejects_JavaScript_Only_Whitespace()
    {
        string email = "a" + (char)0xFEFF + "@b.c";

        Assert.That(KeyNormalizer.IsValidEmail(email), Is.False);
    }

    [Test]
    public void NormalizeKey_Email_Key_Is_Case_Insensitive_Other_Keys_Are_Not() => Assert.Multiple(() =>
                                                                                        {
                                                                                            Assert.That(KeyNormalizer.NormalizeKey("ContactEmail", "A@B.C"), Is.EqualTo("a@b.c"));
                                                                                            Assert.That(KeyNormalizer.NormalizeKey("CustomerID", "A@B.C"), Is.EqualTo("A@B.C"));
                                                                                            Assert.That(KeyNormalizer.NormalizeKey("contactemail", "A@B.C"), Is.EqualTo("A@B.C"));
                                                                                        });
}
