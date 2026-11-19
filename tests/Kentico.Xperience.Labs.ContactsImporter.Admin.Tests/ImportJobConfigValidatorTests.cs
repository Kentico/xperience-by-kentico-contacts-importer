namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

internal class ImportJobConfigValidatorTests
{
    private static readonly ContactsImporterOptions options = new() { MaxFileSizeBytes = 10_000 };

    /// <summary>The problems in English (en-US resources).</summary>
    private static IReadOnlyList<string> Validate(ImportJob job) => [.. ImportJobConfigValidator.Validate(job, TestData.Fields, options).Select(error => error.ToString())];

    [Test]
    public void Valid_Configuration_Has_No_Errors()
    {
        var job = TestData.Job(null, "ContactEmail", "ContactEmail", "ContactFirstName", "CustomerID");
        job = job with
        {
            KeyField = "CustomerID",
            Options = job.Options with { RecipientListId = 4, Consent = ConsentMode.Blanket, ConsentId = 2, SourceField = "ContactSource", SourceValue = "import-1" },
        };

        Assert.That(Validate(job), Is.Empty);
    }

    private static IEnumerable<TestCaseData> InvalidJobs()
    {
        var job = TestData.Job();
        static TestCaseData Case(string name, ImportJob invalid, string messagePart) => new TestCaseData(invalid, messagePart).SetName("Invalid_" + name);

        yield return Case("Empty_Job_Id", job with { JobId = Guid.Empty }, "job ID");
        yield return Case("File_Name_Missing", job with { File = job.File with { Name = " " } }, "file name");
        yield return Case("File_Name_Too_Long", job with { File = job.File with { Name = new string('a', 261) } }, "file name");
        yield return Case("File_Empty", job with { File = job.File with { Size = 0 } }, "file is empty");
        yield return Case("File_Too_Large", job with { File = job.File with { Size = 10_001 } }, "larger than");
        yield return Case("Fingerprint_Not_Hex", job with { File = job.File with { Fingerprint = "xyz" } }, "fingerprint");
        yield return Case("Fingerprint_Empty", job with { File = job.File with { Fingerprint = "" } }, "fingerprint");
        yield return Case("No_Mapping", job with { Mapping = [] }, "No columns");
        yield return Case("Source_Missing", job with { Mapping = [new ImportFieldMapping { Source = "", Target = "ContactEmail" }] }, "source column");
        yield return Case("Target_Unknown", job with { Mapping = [.. job.Mapping, new ImportFieldMapping { Source = "x", Target = "ContactID" }] }, "not an importable");
        yield return Case("Target_Wrong_Case", job with { Mapping = [.. job.Mapping, new ImportFieldMapping { Source = "x", Target = "contactlastname" }] }, "not an importable");
        yield return Case("Target_Twice", job with { Mapping = [.. job.Mapping, new ImportFieldMapping { Source = "x", Target = "ContactEmail" }] }, "more than once");
        yield return Case("Key_Not_Mapped", job with { KeyField = "CustomerID" }, "key field");
        yield return Case("Date_Format_Empty", job with { DateFormat = "" }, "date format");
        yield return Case("Date_Format_Too_Long", job with { DateFormat = new string('d', 65) }, "date format");
        yield return Case("Option_Undefined_Enum", job with { Options = job.Options with { NewContacts = (NewContactsBehavior)99 } }, "unknown value");
        yield return Case("Recipient_List_Zero", job with { Options = job.Options with { RecipientListId = 0 } }, "recipient list");
        yield return Case("Consent_Without_Id", job with { Options = job.Options with { Consent = ConsentMode.Blanket } }, "consent must be selected");
        yield return Case("Consent_Id_Negative", job with { Options = job.Options with { ConsentId = -1 } }, "consent must be selected");
        yield return Case("Source_Field_Unknown", job with { Options = job.Options with { SourceField = "Nope", SourceValue = "x" } }, "source field");
        yield return Case("Source_Field_Not_Text", job with { Options = job.Options with { SourceField = "ContactBirthday", SourceValue = "x" } }, "source field");
        yield return Case("Source_Value_Missing", job with { Options = job.Options with { SourceField = "ContactSource" } }, "source value");
        yield return Case("Source_Value_Too_Long", job with { Options = job.Options with { SourceField = "ContactSource", SourceValue = new string('s', 201) } }, "source value");
        yield return Case("Unsubscribed_Undefined_Enum", job with { Options = job.Options with { Unsubscribed = (UnsubscribedBehavior)2 } }, "unknown value");
        yield return Case("Consent_Mapping_Empty", job with { ConsentMapping = new ImportConsentMapping() }, "without a column");
        yield return Case("Consent_Mapping_Empty_Name", job with { ConsentMapping = new ImportConsentMapping { Agreed = "" } }, "consent source column");
        yield return Case("Consent_Mapping_Long_Name", job with { ConsentMapping = new ImportConsentMapping { Date = new string('c', 1025) } }, "consent source column");
        yield return Case("Consent_Mapping_Same_Column", job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent", Date = "consent" } }, "different columns");
        yield return Case("Consent_Mapping_Agreed_Is_A_Field_Column", job with { ConsentMapping = new ImportConsentMapping { Agreed = "col ContactEmail" } }, "cannot also be mapped");
        yield return Case("Consent_Mapping_Date_Is_A_Field_Column", job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent", Date = "col ContactFirstName" } }, "cannot also be mapped");
        yield return Case(
            "Consent_From_Columns_Without_Columns",
            job with { Options = job.Options with { Consent = ConsentMode.BlanketWithColumnOverride, ConsentId = 2 } },
            "needs a mapped consent column");
    }

    [TestCaseSource(nameof(InvalidJobs))]
    public void Invalid_Configuration_Is_Reported(ImportJob job, string expectedMessagePart)
    {
        var errors = Validate(job);

        Assert.That(errors, Has.Some.Contains(expectedMessagePart));
    }

    [Test]
    public void All_Problems_Are_Reported_Together()
    {
        var job = TestData.Job() with { Mapping = [], DateFormat = "" };

        Assert.That(Validate(job), Has.Count.GreaterThanOrEqualTo(3));
    }

    [Test]
    public void Error_Does_Not_Echo_A_Long_Client_Value()
    {
        var job = TestData.Job();
        job = job with { Mapping = [.. job.Mapping, new ImportFieldMapping { Source = "x", Target = new string('T', 5000) }] };

        Assert.That(Validate(job).Max(e => e.Length), Is.LessThan(300));
    }

    private static readonly ContactFieldDefinition[] typedFields =
    [
        .. TestData.Fields,
        new("Age", "Age", "integer", null),
        new("Big", "Big", "longinteger", null),
        new("Uid", "Uid", "guid", null),
        new("Total", "Total", "decimal", 10, 2),
        new("Score", "Score", "double", null),
        new("Day", "Day", "date", null),
        new("Active", "Active", "boolean", null),
        new("Picture", "Picture", "binary", null),
        new("Pages", "Pages", "webpages", null),
    ];

    [TestCase("ContactEmail")]
    [TestCase("CustomerID")]
    [TestCase("Age")]
    [TestCase("Big")]
    [TestCase("Uid")]
    public void Text_Whole_Number_And_Guid_Fields_Can_Be_The_Key(string keyField)
    {
        var job = TestData.Job(null, keyField, "ContactFirstName", keyField);

        Assert.That(ImportJobConfigValidator.Validate(job, typedFields, options), Is.Empty);
    }

    [TestCase("ContactNotes")]
    [TestCase("ContactBirthday")]
    [TestCase("Total")]
    [TestCase("Score")]
    [TestCase("Day")]
    [TestCase("Active")]
    public void Other_Fields_Cannot_Be_The_Key(string keyField)
    {
        var job = TestData.Job(null, keyField, "ContactEmail", keyField);

        var errors = ImportJobConfigValidator.Validate(job, typedFields, options);

        Assert.That(errors, Is.EqualTo(new[] { ImportMessages.KeyFieldNotAllowed(keyField) }));
        Assert.That(errors[0].ToString(), Is.EqualTo($"Contact field '{keyField}' cannot be the key field: only text, whole number and GUID fields can."));
    }

    [TestCase("Picture")]
    [TestCase("Pages")]
    public void Field_With_A_Data_Type_The_Import_Cannot_Write_Cannot_Be_Mapped(string target)
    {
        var job = TestData.Job(null, "ContactEmail", "ContactEmail", target);

        var errors = ImportJobConfigValidator.Validate(job, typedFields, options);

        Assert.That(errors, Is.EqualTo(new[] { ImportMessages.FieldTypeNotSupported(target) }));
    }

    [Test]
    public void Every_Supported_Data_Type_Can_Be_Mapped()
    {
        var job = TestData.Job(null, "ContactEmail", [.. typedFields.Where(f => f.IsSupported).Select(f => f.Name)]);

        Assert.Multiple(() =>
        {
            Assert.That(ImportJobConfigValidator.Validate(job, typedFields, options), Is.Empty);
            Assert.That(job.Mapping, Has.Count.EqualTo(typedFields.Length - 2));
        });
    }

    [TestCase("text", true, true)]
    [TestCase("Text", true, true)]
    [TestCase("longtext", true, false)]
    [TestCase("integer", true, true)]
    [TestCase("longinteger", true, true)]
    [TestCase("guid", true, true)]
    [TestCase("double", true, false)]
    [TestCase("decimal", true, false)]
    [TestCase("datetime", true, false)]
    [TestCase("date", true, false)]
    [TestCase("boolean", true, false)]
    [TestCase("binary", false, false)]
    [TestCase("xml", false, false)]
    [TestCase("timespan", false, false)]
    [TestCase("contentitemreference", false, false)]
    [TestCase("objectids", false, false)]
    [TestCase("taxonomy", false, false)]
    [TestCase("richtexthtml", false, false)]
    [TestCase("", false, false)]
    public void Field_Definition_Says_What_The_Import_Supports(string dataType, bool supported, bool canBeKey)
    {
        var field = new ContactFieldDefinition("Field", "Field", dataType, null);

        Assert.Multiple(() =>
        {
            Assert.That(field.IsSupported, Is.EqualTo(supported));
            Assert.That(field.CanBeKey, Is.EqualTo(canBeKey));
            Assert.That(field.CanBeKey && !field.IsSupported, Is.False, "A key field is always importable.");
        });
    }

    [Test]
    public void Validator_Key_Rule_Is_Not_Looser_Than_The_Engine()
    {
        // The engine checks again when a phase starts (ImportProcessingContext); what the validator accepts must pass there.
        foreach (var field in typedFields.Where(f => f.CanBeKey))
        {
            var job = TestData.Job(null, field.Name, "ContactNotes", field.Name);

            Assert.That(ImportProcessingContext.TryCreate(job, typedFields, out _, out var error), Is.True, error?.ToString());
        }
    }

    [TestCase("ContactNotes")]
    [TestCase("ContactBirthday")]
    [TestCase("Total")]
    [TestCase("Active")]
    public void Engine_Refuses_The_Same_Key_Fields_As_The_Validator(string keyField)
    {
        var job = TestData.Job(null, keyField, "ContactEmail", keyField);

        bool created = ImportProcessingContext.TryCreate(job, typedFields, out _, out var error);

        Assert.Multiple(() =>
        {
            Assert.That(created, Is.False);
            Assert.That(error, Is.EqualTo(ImportJobConfigValidator.Validate(job, typedFields, options).Single()));
        });
    }

    [Test]
    public void Consent_Columns_Are_Valid_With_Either_Or_Both_Columns_And_With_Any_Consent_Option()
    {
        var job = TestData.Job();
        var both = new ImportConsentMapping { Agreed = "consent", Date = "consent date" };

        Assert.Multiple(() =>
        {
            Assert.That(Validate(job with { ConsentMapping = both }), Is.Empty);
            Assert.That(Validate(job with { ConsentMapping = new ImportConsentMapping { Agreed = "consent" } }), Is.Empty);
            Assert.That(Validate(job with { ConsentMapping = new ImportConsentMapping { Date = "consent date" } }), Is.Empty);
            Assert.That(Validate(job with { ConsentMapping = both, Options = job.Options with { Consent = ConsentMode.BlanketWithColumnOverride, ConsentId = 2 } }), Is.Empty);
            Assert.That(Validate(job with { ConsentMapping = both, Options = job.Options with { Consent = ConsentMode.Blanket, ConsentId = 2 } }), Is.Empty);
        });
    }

    [Test]
    public void Recipient_List_And_Consent_Are_Checked_Against_The_Database_When_A_Resolver_Is_Given()
    {
        var job = TestData.Job();
        job = job with { Options = job.Options with { RecipientListId = 4 } };
        var resolver = new Processing.FakeAudienceResolver();

        var found = ImportJobConfigValidator.Validate(job, TestData.Fields, options, resolver);
        resolver.Error = ImportAudienceResolver.RecipientListMissingError;
        var missing = ImportJobConfigValidator.Validate(job, TestData.Fields, options, resolver);
        int calls = resolver.Calls;
        var invalidAnyway = ImportJobConfigValidator.Validate(job with { DateFormat = "" }, TestData.Fields, options, resolver);

        Assert.Multiple(() =>
        {
            Assert.That(found, Is.Empty);
            Assert.That(missing, Is.EqualTo(new[] { ImportAudienceResolver.RecipientListMissingError }));
            Assert.That(invalidAnyway, Has.Count.EqualTo(1));
            Assert.That(resolver.Calls, Is.EqualTo(calls), "A configuration with other problems is not worth a database query.");
        });
    }

    [Test]
    public void Processing_Refuses_A_Job_Whose_List_Or_Consent_Was_Not_Checked()
    {
        var processingFields = Processing.ProcessingTestData.Fields;
        var list = TestData.Job() with { Options = new ImportOptions { RecipientListId = 4 } };
        var consent = TestData.Job() with { Options = new ImportOptions { Consent = ConsentMode.Blanket, ConsentId = 2 } };
        var columns = TestData.Job() with { Options = new ImportOptions { Consent = ConsentMode.BlanketWithColumnOverride, ConsentId = 2 } };

        Assert.Multiple(() =>
        {
            Assert.That(ImportProcessingContext.TryCreate(list, processingFields, out _, out var noAudience), Is.False);
            Assert.That(noAudience, Is.EqualTo(ImportMessages.AudienceUnchecked));
            Assert.That(ImportProcessingContext.TryCreate(list, processingFields, out _, out _, new ImportAudience(RecipientListId: 5)), Is.False, "Another list than the one of the options.");
            Assert.That(ImportProcessingContext.TryCreate(list, processingFields, out var listContext, out _, new ImportAudience(RecipientListId: 4)), Is.True);
            Assert.That(listContext!.HasRecipientList && !listContext.RecordsConsent && listContext.HasAudience, Is.True);
            Assert.That(ImportProcessingContext.TryCreate(consent, processingFields, out _, out _, ImportAudience.None), Is.False);
            Assert.That(ImportProcessingContext.TryCreate(consent, processingFields, out var consentContext, out _, new ImportAudience(ConsentId: 2, ConsentHash: "h")), Is.True);
            Assert.That(consentContext!.RecordsConsent && !consentContext.ConsentFromColumns, Is.True);
            Assert.That(ImportProcessingContext.TryCreate(columns, processingFields, out _, out var noColumns, new ImportAudience(ConsentId: 2, ConsentHash: "h")), Is.False);
            Assert.That(noColumns, Is.EqualTo(ImportJobConfigValidator.ConsentColumnsMissingError));
            Assert.That(ImportProcessingContext.TryCreate(TestData.Job(), processingFields, out var plain, out _), Is.True);
            Assert.That(plain!.HasAudience, Is.False);
        });
    }
}
