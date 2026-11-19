using System.Text;
using System.Text.Json;

using static Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing.ProcessingTestData;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

public class FieldValueConverterTests
{
    private static ImportField Field(ImportValueKind kind, int? maxLength = null, int digits = 10, int scale = 2) => new(0, "F", kind, maxLength, digits, scale);

    private static object? Convert(ImportValueKind kind, string raw, int? maxLength = null) =>
        FieldValueConverter.TryConvert(Field(kind, maxLength), raw, out object value) ? value : null;

    [Test]
    public void Text_Is_Kept_And_Checked_Against_The_Field_Length() => Assert.Multiple(() =>
                                                                            {
                                                                                Assert.That(Convert(ImportValueKind.Text, "abc", 3), Is.EqualTo("abc"));
                                                                                Assert.That(Convert(ImportValueKind.Text, "abcd", 3), Is.Null);
                                                                                Assert.That(Convert(ImportValueKind.Text, new string('x', 100_000)), Has.Length.EqualTo(100_000));
                                                                            });

    [TestCase("12", 12)]
    [TestCase("-12", -12)]
    [TestCase("+12", 12)]
    [TestCase("2147483647", int.MaxValue)]
    public void Integer_Accepts_Whole_Numbers(string raw, int expected) => Assert.That(Convert(ImportValueKind.Integer, raw), Is.EqualTo(expected));

    [TestCase("2147483648")]
    [TestCase("1.0")]
    [TestCase("1,000")]
    [TestCase("12a")]
    [TestCase("0x10")]
    [TestCase("1e3")]
    public void Integer_Rejects_Everything_Else(string raw) => Assert.That(Convert(ImportValueKind.Integer, raw), Is.Null);

    [Test]
    public void Long_Integer_Accepts_64_Bit_Numbers() => Assert.Multiple(() =>
                                                              {
                                                                  Assert.That(Convert(ImportValueKind.LongInteger, "9223372036854775807"), Is.EqualTo(long.MaxValue));
                                                                  Assert.That(Convert(ImportValueKind.LongInteger, "9223372036854775808"), Is.Null);
                                                              });

    [TestCase("1.5", 1.5)]
    [TestCase("-1e3", -1000d)]
    [TestCase(".5", 0.5)]
    public void Double_Accepts_Invariant_Numbers(string raw, double expected) => Assert.That(Convert(ImportValueKind.Double, raw), Is.EqualTo(expected));

    [TestCase("NaN")]
    [TestCase("Infinity")]
    [TestCase("1e999")]
    [TestCase("1,5")]
    public void Double_Rejects_Non_Finite_And_Localized_Numbers(string raw) => Assert.That(Convert(ImportValueKind.Double, raw), Is.Null);

    [TestCase("1234.565", "1234.57")]
    [TestCase("-0.005", "-0.01")]
    [TestCase("0.004", "0.00")]
    [TestCase("99999999.994", "99999999.99")]
    public void Decimal_Rounds_To_The_Field_Scale_Like_Sql_Server(string raw, string expected) =>
        Assert.That(Convert(ImportValueKind.Decimal, raw), Is.EqualTo(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture)));

    [TestCase("100000000")]
    [TestCase("99999999.995")]
    [TestCase("1e3")]
    [TestCase("1,000.00")]
    [TestCase("abc")]
    public void Decimal_Rejects_Values_Outside_The_Field_Precision_And_Other_Formats(string raw) => Assert.That(Convert(ImportValueKind.Decimal, raw), Is.Null);

    [TestCase("2024-02-29", 2024, 2, 29, 0, 0, 0)]
    [TestCase("2024-02-29T13:45:10", 2024, 2, 29, 13, 45, 10)]
    [TestCase("2024-02-29T13:45:10Z", 2024, 2, 29, 13, 45, 10)]
    [TestCase("2024-02-29T13:45:10+05:30", 2024, 2, 29, 13, 45, 10)]
    [TestCase("2024-02-29T13:45:10.5-08:00", 2024, 2, 29, 13, 45, 10)]
    public void DateTime_Keeps_The_Wall_Clock_Time_Of_The_File(string raw, int year, int month, int day, int hour, int minute, int second)
    {
        var value = (DateTime)Convert(ImportValueKind.DateTime, raw)!;

        Assert.Multiple(() =>
        {
            Assert.That(value.AddTicks(-(value.Ticks % TimeSpan.TicksPerSecond)), Is.EqualTo(new DateTime(year, month, day, hour, minute, second, DateTimeKind.Unspecified)));
            Assert.That(value.Kind, Is.EqualTo(DateTimeKind.Unspecified));
        });
    }

    [Test]
    public void DateTime_Keeps_Seven_Fraction_Digits_And_Date_Drops_The_Time() => Assert.Multiple(() =>
                                                                                       {
                                                                                           Assert.That(((DateTime)Convert(ImportValueKind.DateTime, "2001-12-31T23:59:59.1234567")!).Ticks % TimeSpan.TicksPerSecond, Is.EqualTo(1234567));
                                                                                           Assert.That(Convert(ImportValueKind.Date, "2001-12-31T23:59:59"), Is.EqualTo(new DateTime(2001, 12, 31, 0, 0, 0, DateTimeKind.Unspecified)));
                                                                                       });

    [TestCase("2024-02-30")]
    [TestCase("29.02.2024")]
    [TestCase("2024-02-29 13:45:10")]
    [TestCase("yesterday")]
    public void DateTime_Rejects_Anything_But_The_Iso_Format_The_Browser_Sends(string raw) => Assert.That(Convert(ImportValueKind.DateTime, raw), Is.Null);

    [TestCase("true", true)]
    [TestCase("TRUE", true)]
    [TestCase("1", true)]
    [TestCase("yes", true)]
    [TestCase("false", false)]
    [TestCase("0", false)]
    [TestCase("No", false)]
    public void Boolean_Accepts_Common_Spellings(string raw, bool expected) => Assert.That(Convert(ImportValueKind.Boolean, raw), Is.EqualTo(expected));

    [Test]
    public void Boolean_And_Guid_Reject_Other_Values() => Assert.Multiple(() =>
                                                               {
                                                                   Assert.That(Convert(ImportValueKind.Boolean, "2"), Is.Null);
                                                                   Assert.That(Convert(ImportValueKind.Boolean, "y"), Is.Null);
                                                                   Assert.That(Convert(ImportValueKind.Guid, "6f9619ff-8b86-d011-b42d-00c04fc964ff"), Is.EqualTo(new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff")));
                                                                   Assert.That(Convert(ImportValueKind.Guid, "6f9619ff"), Is.Null);
                                                               });

    [Test]
    public void Database_Values_Are_Compared_By_Value_Per_Kind()
    {
        var date = new DateTime(2024, 2, 29, 1, 2, 3, DateTimeKind.Unspecified);
        var guid = Guid.NewGuid();

        Assert.Multiple(() =>
        {
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Text), "Ann", "Ann"), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Text), "ann", "Ann"), Is.False);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Text), null, "Ann"), Is.False);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Text), DBNull.Value, "Ann"), Is.False);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Integer), 5, 5), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Integer), 5L, 5), Is.True, "A wider column type still compares by value.");
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.LongInteger), 5L, 6L), Is.False);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Double), 0.1d, 0.1d), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Decimal), 1.50m, 1.5m), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.DateTime), date, date), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.DateTime), date.AddTicks(1), date), Is.False);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Boolean), true, true), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Guid), guid, guid), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Guid), guid.ToString(), guid), Is.True);
            Assert.That(FieldValueConverter.AreEqual(Field(ImportValueKind.Integer), "not a number", 5), Is.False);

            Assert.That(FieldValueConverter.IsEmpty(null), Is.True);
            Assert.That(FieldValueConverter.IsEmpty(DBNull.Value), Is.True);
            Assert.That(FieldValueConverter.IsEmpty(""), Is.True);
            Assert.That(FieldValueConverter.IsEmpty(0), Is.False);
            Assert.That(FieldValueConverter.IsEmpty(false), Is.False);
        });
    }

    [Test]
    public void Values_Are_Written_As_Json_Sql_Server_Converts_To_The_Declared_Type()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            FieldValueConverter.WriteJson(writer, "text", Field(ImportValueKind.Text), "a\"b");
            FieldValueConverter.WriteJson(writer, "int", Field(ImportValueKind.Integer), 5);
            FieldValueConverter.WriteJson(writer, "long", Field(ImportValueKind.LongInteger), 9000000000L);
            FieldValueConverter.WriteJson(writer, "double", Field(ImportValueKind.Double), 0.1d);
            FieldValueConverter.WriteJson(writer, "decimal", Field(ImportValueKind.Decimal), 1234.50m);
            FieldValueConverter.WriteJson(writer, "datetime", Field(ImportValueKind.DateTime), new DateTime(2024, 2, 29, 13, 45, 10, DateTimeKind.Unspecified).AddTicks(1));
            FieldValueConverter.WriteJson(writer, "date", Field(ImportValueKind.Date), new DateTime(2024, 2, 29, 0, 0, 0, DateTimeKind.Unspecified));
            FieldValueConverter.WriteJson(writer, "true", Field(ImportValueKind.Boolean), true);
            FieldValueConverter.WriteJson(writer, "false", Field(ImportValueKind.Boolean), false);
            FieldValueConverter.WriteJson(writer, "guid", Field(ImportValueKind.Guid), new Guid("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
            writer.WriteEndObject();
        }

        Assert.That(
            Encoding.UTF8.GetString(buffer.ToArray()),
            Is.EqualTo("{\"text\":\"a\\u0022b\",\"int\":5,\"long\":9000000000,\"double\":0.1,\"decimal\":\"1234.50\",\"datetime\":\"2024-02-29T13:45:10.0000001\",\"date\":\"2024-02-29\",\"true\":1,\"false\":0,\"guid\":\"6f9619ff-8b86-d011-b42d-00c04fc964ff\"}"));
    }

    [Test]
    public void Sql_Types_Come_From_The_Field_Definition() => Assert.Multiple(() =>
                                                                   {
                                                                       Assert.That(Field(ImportValueKind.Text, 100).SqlType, Is.EqualTo("nvarchar(100)"));
                                                                       Assert.That(Field(ImportValueKind.Text).SqlType, Is.EqualTo("nvarchar(max)"));
                                                                       Assert.That(Field(ImportValueKind.Text, 5000).SqlType, Is.EqualTo("nvarchar(max)"));
                                                                       Assert.That(Field(ImportValueKind.Integer).SqlType, Is.EqualTo("int"));
                                                                       Assert.That(Field(ImportValueKind.LongInteger).SqlType, Is.EqualTo("bigint"));
                                                                       Assert.That(Field(ImportValueKind.Double).SqlType, Is.EqualTo("float"));
                                                                       Assert.That(Field(ImportValueKind.Decimal).SqlType, Is.EqualTo("decimal(10,2)"));
                                                                       Assert.That(Field(ImportValueKind.DateTime).SqlType, Is.EqualTo("datetime2(7)"));
                                                                       Assert.That(Field(ImportValueKind.Date).SqlType, Is.EqualTo("date"));
                                                                       Assert.That(Field(ImportValueKind.Boolean).SqlType, Is.EqualTo("bit"));
                                                                       Assert.That(Field(ImportValueKind.Guid).SqlType, Is.EqualTo("uniqueidentifier"));
                                                                   });

    [Test]
    public void Context_Maps_Field_Definitions_To_Kinds_And_Refuses_What_It_Cannot_Process()
    {
        var context = Context(Email, [Email, "ContactNotes", "ContactBirthday", "Age", "Big", "Total", "Score", "Day", "Active", "Uid"]);
        var job = TestData.Job(null, Email, Email, FirstName);

        Assert.Multiple(() =>
        {
            Assert.That(context.Fields.Select(f => f.Kind), Is.EqualTo(new[]
            {
                ImportValueKind.Text, ImportValueKind.Text, ImportValueKind.DateTime, ImportValueKind.Integer, ImportValueKind.LongInteger,
                ImportValueKind.Decimal, ImportValueKind.Double, ImportValueKind.Date, ImportValueKind.Boolean, ImportValueKind.Guid,
            }));
            Assert.That(context.Fields[0].MaxLength, Is.EqualTo(254));
            Assert.That(context.Fields[5].SqlType, Is.EqualTo("decimal(10,2)"));
            Assert.That(context.KeyIndex, Is.Zero);
            Assert.That(context.EmailIndex, Is.EqualTo(-1), "Email as the key needs no email check.");
            Assert.That(Context(CustomerId, [Email, CustomerId]).EmailIndex, Is.Zero);

            AssertRefused(TestData.Job(null, Email, Email, "Picture"), "data type");
            AssertRefused(TestData.Job(null, Email, Email, "Gone"), "no longer exists");
            AssertRefused(TestData.Job(null, "Total", Email, "Total"), "cannot be the key field");
            AssertRefused(TestData.Job(null, FirstName, Email), "key field");
            AssertRefused(job with { Options = new ImportOptions { SourceField = "Age", SourceValue = "x" } }, "source field");
            AssertRefused(job with { Options = new ImportOptions { SourceField = Source } }, "source value");
            AssertRefused(job with { Mapping = [.. job.Mapping, job.Mapping[0]] }, "more than once");
        });
    }

    private static void AssertRefused(ImportJob job, string expectedMessagePart)
    {
        bool created = ImportProcessingContext.TryCreate(job, Fields, out var context, out var error);

        Assert.That(created, Is.False);
        Assert.That(context, Is.Null);
        Assert.That(error?.ToString(), Does.Contain(expectedMessagePart));
    }
}
