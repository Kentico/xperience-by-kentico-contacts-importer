using System.Text;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

public class ChunkValidatorTests
{
    private const long MaxBytes = 10 * 1024 * 1024;

    private static ChunkValidationContext Context(string keyField = "ContactEmail", params string[] targets)
    {
        bool created = ChunkValidationContext.TryCreate(TestData.Job(keyField: keyField, targets: targets), TestData.Fields, out var context, out string? error);
        Assert.That(created, Is.True, error);

        return context!;
    }

    private static ChunkValidationResult Validate(ChunkValidationContext context, params string[] lines) =>
        Validate(context, lines.Length, string.Join('\n', lines));

    private static ChunkValidationResult Validate(ChunkValidationContext context, int expectedRows, string content, long maxBytes = MaxBytes)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        return ChunkValidator.Validate(stream, context, maxBytes, expectedRows);
    }

    [Test]
    public void Valid_Records_Are_Accepted_With_Row_Range()
    {
        var result = Validate(
            Context(),
            TestData.Record(3, "jane@example.com"),
            TestData.Record(4, "joe@example.com", ""),
            """{"v":{"ContactFirstName":"Ann","ContactEmail":"Ann@Example.com"},"k":"ann@example.com","r":9}""");

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True, result.Message);
            Assert.That(result.Rows, Is.EqualTo(3));
            Assert.That(result.FirstRow, Is.EqualTo(3));
            Assert.That(result.LastRow, Is.EqualTo(9));
        });
    }

    [Test]
    public void Trailing_Line_Feed_Is_Accepted()
    {
        var result = Validate(Context(), 1, TestData.Record(1, "jane@example.com") + "\n");

        Assert.That(result.IsValid, Is.True, result.Message);
    }

    [Test]
    public void Record_May_Omit_Mapped_Fields_Other_Than_The_Key()
    {
        var result = Validate(Context(), """{"r":1,"k":"jane@example.com","v":{"ContactEmail":"jane@example.com"}}""");

        Assert.That(result.IsValid, Is.True, result.Message);
    }

    [Test]
    public void Custom_Key_Is_Case_Sensitive_And_Allows_InvalidEmail_Flag()
    {
        var context = Context("CustomerID", "CustomerID", "ContactEmail");

        var result = Validate(
            context,
            """{"r":1,"k":"AbC-1","v":{"CustomerID":"AbC-1","ContactEmail":"not-an-email"},"f":["invalidEmail"]}""",
            """{"r":2,"k":"AbC-2","v":{"CustomerID":"AbC-2","ContactEmail":"jane@example.com"}}""",
            """{"r":3,"k":"AbC-3","v":{"CustomerID":"AbC-3","ContactEmail":""},"f":[]}""");

        Assert.That(result.IsValid, Is.True, result.Message);
    }

    [Test]
    public void Long_Text_Field_Has_No_Field_Limit()
    {
        var context = Context("ContactEmail", "ContactEmail", "ContactNotes");
        string notes = new('n', 100_000);

        var result = Validate(context, "{\"r\":1,\"k\":\"a@b.c\",\"v\":{\"ContactEmail\":\"a@b.c\",\"ContactNotes\":\"" + notes + "\"}}");

        Assert.That(result.IsValid, Is.True, result.Message);
    }

    // Each case is one record that must reject the chunk.
    [TestCase("""[1]""", "not a JSON object", TestName = "Reject_Array")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}""", "not a valid JSON", TestName = "Reject_Truncated_Json")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}} {}""", "not a valid JSON", TestName = "Reject_Trailing_Content")]
    [TestCase("", "not a valid JSON", TestName = "Reject_Empty_Line")]
    [TestCase("""{"k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "no row number", TestName = "Reject_Missing_R")]
    [TestCase("""{"r":0,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "invalid row number", TestName = "Reject_R_Zero")]
    [TestCase("""{"r":-4,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "invalid row number", TestName = "Reject_R_Negative")]
    [TestCase("""{"r":1.5,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "invalid row number", TestName = "Reject_R_Fraction")]
    [TestCase("""{"r":"1","k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "invalid row number", TestName = "Reject_R_String")]
    [TestCase("""{"r":99999999999999999999,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "invalid row number", TestName = "Reject_R_Overflow")]
    [TestCase("""{"r":1,"r":2,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "duplicate 'r'", TestName = "Reject_Duplicate_R")]
    [TestCase("""{"r":1,"v":{"ContactEmail":"a@b.c"}}""", "no key", TestName = "Reject_Missing_K")]
    [TestCase("""{"r":1,"k":5,"v":{"ContactEmail":"a@b.c"}}""", "not a string", TestName = "Reject_K_Number")]
    [TestCase("""{"r":1,"k":null,"v":{"ContactEmail":"a@b.c"}}""", "not a string", TestName = "Reject_K_Null")]
    [TestCase("""{"r":1,"k":"","v":{"ContactEmail":""}}""", "empty or longer", TestName = "Reject_K_Empty")]
    [TestCase("""{"r":1,"k":"a@b.c","k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "duplicate 'k'", TestName = "Reject_Duplicate_K")]
    [TestCase("""{"r":1,"k":"a@b.c"}""", "no values", TestName = "Reject_Missing_V")]
    [TestCase("""{"r":1,"k":"a@b.c","v":["a@b.c"]}""", "not an object", TestName = "Reject_V_Array")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"},"v":{"ContactEmail":"a@b.c"}}""", "duplicate 'v'", TestName = "Reject_Duplicate_V")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactLastName":"Doe"}}""", "not mapped", TestName = "Reject_Unmapped_Field")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactID":"1"}}""", "not mapped", TestName = "Reject_Not_Allowed_Field")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"contactemail":"a@b.c"}}""", "not mapped", TestName = "Reject_Field_Name_Wrong_Case")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactFirstName":"A","ContactFirstName":"B"}}""", "more than one value", TestName = "Reject_Duplicate_Field")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactFirstName":5}}""", "not a string", TestName = "Reject_Value_Number")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactFirstName":null}}""", "not a string", TestName = "Reject_Value_Null")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactFirstName":{"x":"y"}}}""", "not a string", TestName = "Reject_Value_Object")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactFirstName":" Jane"}}""", "not trimmed", TestName = "Reject_Value_Leading_Space")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c","ContactFirstName":"Jane\t"}}""", "not trimmed", TestName = "Reject_Value_Trailing_Tab")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactFirstName":"Jane"}}""", "no value for the key field", TestName = "Reject_Key_Field_Missing_In_V")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"x@y.z"}}""", "not the normalized value", TestName = "Reject_K_Differs_From_Key_Field")]
    [TestCase("""{"r":1,"k":"A@b.c","v":{"ContactEmail":"A@b.c"}}""", "not the normalized value", TestName = "Reject_Email_Key_Not_Lowercase")]
    [TestCase("""{"r":1,"k":"not-an-email","v":{"ContactEmail":"not-an-email"}}""", "not the normalized value", TestName = "Reject_Invalid_Email_Key")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"},"f":["invalidEmail"]}""", "only allowed when", TestName = "Reject_InvalidEmail_Flag_When_Email_Is_Key")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"},"f":["other"]}""", "unknown flag", TestName = "Reject_Unknown_Flag")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"},"f":[1]}""", "unknown flag", TestName = "Reject_Flag_Number")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"},"f":"invalidEmail"}""", "not an array", TestName = "Reject_Flags_String")]
    [TestCase("""{"r":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"},"x":1}""", "unknown property", TestName = "Reject_Unknown_Property")]
    [TestCase("""{"R":1,"k":"a@b.c","v":{"ContactEmail":"a@b.c"}}""", "unknown property", TestName = "Reject_Property_Wrong_Case")]
    public void Invalid_Record_Rejects_The_Chunk(string record, string expectedMessagePart)
    {
        var result = Validate(Context(), 2, record + "\n" + TestData.Record(7, "last@example.com"));

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.False);
            Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
            Assert.That(result.Message, Does.Contain(expectedMessagePart));
            Assert.That(result.Message, Does.StartWith("Record 1 of the chunk"));
        });
    }

    [Test]
    public void Duplicate_InvalidEmail_Flag_Is_Rejected()
    {
        var result = Validate(
            Context("CustomerID", "CustomerID", "ContactEmail"),
            """{"r":1,"k":"A","v":{"CustomerID":"A","ContactEmail":"x"},"f":["invalidEmail","invalidEmail"]}""");

        Assert.That(result.Message, Does.Contain("duplicate flag"));
    }

    [Test]
    public void InvalidEmail_Flag_Is_Rejected_When_Email_Is_Not_Mapped()
    {
        var result = Validate(
            Context("CustomerID", "CustomerID", "ContactFirstName"),
            """{"r":1,"k":"A","v":{"CustomerID":"A"},"f":["invalidEmail"]}""");

        Assert.That(result.Message, Does.Contain("only allowed when"));
    }

    [Test]
    public void Value_Longer_Than_Field_Is_Rejected_With_Row_Number()
    {
        string tooLong = new('x', 101);
        string atLimit = new('x', 100);

        var accepted = Validate(Context(), TestData.Record(5, "a@b.c", atLimit));
        var rejected = Validate(Context(), TestData.Record(5, "a@b.c", tooLong));

        Assert.Multiple(() =>
        {
            Assert.That(accepted.IsValid, Is.True, accepted.Message);
            Assert.That(rejected.IsValid, Is.False);
            Assert.That(rejected.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
            Assert.That(rejected.RowNumber, Is.EqualTo(5));
            Assert.That(rejected.Message, Does.Contain("ContactFirstName").And.Contain("longer than 100").And.Contain("row 5"));
            Assert.That(rejected.Message, Does.Not.Contain("xxx"), "messages must not echo values");
        });
    }

    [Test]
    public void Non_Text_Field_Value_Is_Capped()
    {
        var context = Context("ContactEmail", "ContactEmail", "ContactBirthday");
        string value = new('1', ContactFieldDefinition.MaxNonTextValueLength + 1);

        var result = Validate(context, "{\"r\":1,\"k\":\"a@b.c\",\"v\":{\"ContactEmail\":\"a@b.c\",\"ContactBirthday\":\"" + value + "\"}}");

        Assert.That(result.Message, Does.Contain("ContactBirthday").And.Contain("longer than"));
    }

    [Test]
    public void Key_Longer_Than_450_Is_Rejected()
    {
        var fields = TestData.Fields.Append(new ContactFieldDefinition("LongKey", "Long key", "text", 1000)).ToArray();
        Assert.That(ChunkValidationContext.TryCreate(TestData.Job(null, "LongKey", "LongKey"), fields, out var context, out _), Is.True);
        string ok = new('k', 450);
        string tooLong = new('k', 451);

        using var okStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"r\":1,\"k\":\"" + ok + "\",\"v\":{\"LongKey\":\"" + ok + "\"}}"));
        using var longStream = new MemoryStream(Encoding.UTF8.GetBytes("{\"r\":1,\"k\":\"" + tooLong + "\",\"v\":{\"LongKey\":\"" + tooLong + "\"}}"));
        var accepted = ChunkValidator.Validate(okStream, context!, MaxBytes, 1);
        var rejected = ChunkValidator.Validate(longStream, context!, MaxBytes, 1);

        Assert.Multiple(() =>
        {
            Assert.That(accepted.IsValid, Is.True, accepted.Message);
            Assert.That(rejected.IsValid, Is.False);
            Assert.That(rejected.Message, Does.Contain("empty or longer than 450"));
        });
    }

    [Test]
    public void Row_Numbers_Must_Strictly_Increase()
    {
        var same = Validate(Context(), TestData.Record(2, "a@b.c"), TestData.Record(2, "b@b.c"));
        var lower = Validate(Context(), TestData.Record(2, "a@b.c"), TestData.Record(1, "b@b.c"));

        Assert.Multiple(() =>
        {
            Assert.That(same.Reason, Is.EqualTo(ChunkRejectReason.RowOrder));
            Assert.That(same.RowNumber, Is.EqualTo(2));
            Assert.That(lower.Reason, Is.EqualTo(ChunkRejectReason.RowOrder));
            Assert.That(lower.RowNumber, Is.EqualTo(1));
        });
    }

    [Test]
    public void Row_Count_Must_Match_Declared_Rows()
    {
        string twoRecords = TestData.Record(1, "a@b.c") + "\n" + TestData.Record(2, "b@b.c");

        var fewer = Validate(Context(), 3, twoRecords);
        var more = Validate(Context(), 1, twoRecords);
        var empty = Validate(Context(), 1, "");

        Assert.Multiple(() =>
        {
            Assert.That(fewer.Reason, Is.EqualTo(ChunkRejectReason.RowCountMismatch));
            Assert.That(more.Reason, Is.EqualTo(ChunkRejectReason.RowCountMismatch));
            Assert.That(empty.Reason, Is.EqualTo(ChunkRejectReason.RowCountMismatch));
        });
    }

    [Test]
    public void Blank_Line_Between_Records_Is_Rejected()
    {
        var result = Validate(Context(), 2, TestData.Record(1, "a@b.c") + "\n\n" + TestData.Record(2, "b@b.c"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
            Assert.That(result.Message, Does.StartWith("Record 2 of the chunk"));
        });
    }

    [Test]
    public void Byte_Order_Mark_Is_Rejected()
    {
        byte[] content = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(TestData.Record(1, "a@b.c"))];
        using var stream = new MemoryStream(content);

        var result = ChunkValidator.Validate(stream, Context(), MaxBytes, 1);

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
    }

    [Test]
    public void Invalid_Utf8_Is_Rejected()
    {
        byte[] record = Encoding.UTF8.GetBytes(TestData.Record(1, "a@b.c", "J?ne"));
        record[Array.IndexOf(record, (byte)'?')] = 0xFF;
        using var stream = new MemoryStream(record);

        var result = ChunkValidator.Validate(stream, Context(), MaxBytes, 1);

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
    }

    [Test]
    public void Content_Larger_Than_Limit_Is_Rejected()
    {
        string content = string.Join('\n', Enumerable.Range(1, 2000).Select(i => TestData.Record(i, $"user{i}@example.com")));

        var result = Validate(Context(), 2000, content, maxBytes: 1000);

        Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.DecompressedTooLarge));
    }

    [Test]
    public void Record_Longer_Than_Record_Limit_Is_Rejected()
    {
        var context = Context("ContactEmail", "ContactEmail", "ContactNotes");
        string notes = new('n', ChunkValidator.MaxRecordBytes + 10);

        var result = Validate(context, 1, "{\"r\":1,\"k\":\"a@b.c\",\"v\":{\"ContactEmail\":\"a@b.c\",\"ContactNotes\":\"" + notes + "\"}}", maxBytes: 100L * 1024 * 1024);

        Assert.Multiple(() =>
        {
            Assert.That(result.Reason, Is.EqualTo(ChunkRejectReason.InvalidRecord));
            Assert.That(result.Message, Does.Contain("longer than"));
        });
    }

    [Test]
    public void Many_Records_Spanning_Buffer_Boundaries_Are_Read_Correctly()
    {
        var context = Context("ContactEmail", "ContactEmail", "ContactNotes");
        string[] lines = [.. Enumerable.Range(1, 5000).Select(i =>
            "{\"r\":" + i + ",\"k\":\"user" + i + "@example.com\",\"v\":{\"ContactEmail\":\"user" + i + "@example.com\",\"ContactNotes\":\"" + new string('n', i % 300) + "\"}}")];

        var result = Validate(context, lines);

        Assert.Multiple(() =>
        {
            Assert.That(result.IsValid, Is.True, result.Message);
            Assert.That(result.Rows, Is.EqualTo(5000));
            Assert.That(result.LastRow, Is.EqualTo(5000));
        });
    }

    [Test]
    public void Context_Fails_For_Unknown_Target_Duplicate_Target_Or_Unmapped_Key()
    {
        var unknown = TestData.Job(null, "ContactEmail", "ContactEmail", "ContactID");
        var duplicate = TestData.Job(null, "ContactEmail", "ContactEmail", "ContactEmail");
        var unmappedKey = TestData.Job(null, "CustomerID", "ContactEmail");

        Assert.Multiple(() =>
        {
            Assert.That(ChunkValidationContext.TryCreate(unknown, TestData.Fields, out _, out string? unknownError), Is.False);
            Assert.That(unknownError, Does.Contain("ContactID"));
            Assert.That(ChunkValidationContext.TryCreate(duplicate, TestData.Fields, out _, out string? duplicateError), Is.False);
            Assert.That(duplicateError, Does.Contain("more than once"));
            Assert.That(ChunkValidationContext.TryCreate(unmappedKey, TestData.Fields, out _, out string? keyError), Is.False);
            Assert.That(keyError, Does.Contain("not mapped"));
        });
    }
}
