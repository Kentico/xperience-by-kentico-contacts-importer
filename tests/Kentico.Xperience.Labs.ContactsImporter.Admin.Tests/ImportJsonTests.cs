using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// The JSON written to <c>job.json</c> and <c>status.json</c> must match the wire contract in
/// <c>.agent-resources/contacts-importer/implementation-spec-1.md</c>.
/// </summary>
public class ImportJsonTests
{
    private static JsonObject ToJson<T>(T value) => JsonNode.Parse(ImportJson.Serialize(value))!.AsObject();

    private static T FromJson<T>(string json) where T : class => ImportJson.Deserialize<T>(Encoding.UTF8.GetBytes(json));

    [Test]
    public void Job_Serializes_To_The_Contract_Shape()
    {
        var job = TestData.Job(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff"));

        var json = ToJson(job);

        Assert.Multiple(() =>
        {
            Assert.That(json.Select(p => p.Key), Is.EqualTo(new[] { "jobId", "createdByUserId", "createdUtc", "file", "keyField", "mapping", "dateFormat", "consentMapping", "options", "attestation" }));
            Assert.That(json["jobId"]!.GetValue<string>(), Is.EqualTo("6f9619ff-8b86-d011-b42d-00c04fc964ff"));
            Assert.That(json["createdByUserId"]!.GetValue<int>(), Is.EqualTo(53));
            Assert.That(json["createdUtc"]!.GetValue<string>(), Is.EqualTo("2026-11-16T12:00:00Z"));
            Assert.That(json["file"]!.ToJsonString(), Is.EqualTo("""{"name":"contacts.csv","size":1234,"fingerprint":"0a1b2c"}"""));
            Assert.That(json["keyField"]!.GetValue<string>(), Is.EqualTo("ContactEmail"));
            Assert.That(json["mapping"]![0]!.ToJsonString(), Is.EqualTo("""{"source":"col ContactEmail","target":"ContactEmail"}"""));
            Assert.That(json["dateFormat"]!.GetValue<string>(), Is.EqualTo("iso"));
            Assert.That(json["attestation"], Is.Null);
            Assert.That(json["consentMapping"], Is.Null);
        });
    }

    [Test]
    public void Consent_Mapping_Round_Trips_In_The_Contract_Shape()
    {
        var job = TestData.Job() with { ConsentMapping = new ImportConsentMapping { Agreed = "Consent given", Date = "Consent date" } };
        string withoutMapping = ToJson(TestData.Job()).ToJsonString().Replace(",\"consentMapping\":null", "", StringComparison.Ordinal);

        Assert.Multiple(() =>
        {
            Assert.That(ToJson(job)["consentMapping"]!.ToJsonString(), Is.EqualTo("""{"agreed":"Consent given","date":"Consent date"}"""));
            Assert.That(FromJson<ImportJob>(ToJson(job).ToJsonString()).ConsentMapping, Is.EqualTo(job.ConsentMapping));
            Assert.That(FromJson<ImportJob>(ToJson(job with { ConsentMapping = new ImportConsentMapping { Date = "d" } }).ToJsonString()).ConsentMapping, Is.EqualTo(new ImportConsentMapping { Date = "d" }));
            Assert.That(FromJson<ImportJob>(withoutMapping).ConsentMapping, Is.Null, "Jobs stored before consent columns existed have no property.");
            Assert.That(() => FromJson<ImportOptions>("""{"unsubscribed":"reportOnly"}"""), Throws.InstanceOf<JsonException>(), "The option without a behavior of its own is gone.");
            Assert.That(FromJson<ImportOptions>("""{"unsubscribed":"resubscribe"}""").Unsubscribed, Is.EqualTo(UnsubscribedBehavior.Resubscribe));
        });
    }

    [Test]
    public void Options_Serialize_Defaults_As_CamelCase_Strings()
    {
        var json = ToJson(new ImportOptions());

        Assert.That(json.ToJsonString(), Is.EqualTo(
            """{"newContacts":"create","existingContacts":"update","emptyValues":"ignore","multipleMatches":"updateNewest","invalidEmail":"skipRow","recipientListId":null,"unsubscribed":"keep","bouncedAddress":"addAndReport","consent":"none","consentId":null,"revokedConsent":"keep","sourceField":null,"sourceValue":null,"sourceStamp":"createdOnly","reportIncludesKey":false}"""));
    }

    [Test]
    public void Status_Serializes_To_The_Contract_Shape()
    {
        var status = new ImportJobStatus
        {
            State = ImportJobState.Uploading,
            Chunks = [new ImportChunkInfo { Index = 0, Sha256 = "abc", Rows = 1234, FirstRow = 1, LastRow = 1300 }],
            Checkpoint = new ImportCheckpoint { Phase = ImportPhase.Analyze },
            UpdatedUtc = TestData.Now,
        };

        var json = ToJson(status);

        Assert.Multiple(() =>
        {
            Assert.That(json.Select(p => p.Key), Is.EqualTo(new[] { "state", "chunks", "profile", "checkpoint", "aggregates", "analysis", "heartbeatUtc", "driverToken", "lastInstance", "updatedUtc", "finishedUtc", "error", "errorArguments", "version", "driverCallId", "longestBatchSeconds" }));
            Assert.That(json["state"]!.GetValue<string>(), Is.EqualTo("Uploading"));
            Assert.That(json["chunks"]![0]!.ToJsonString(), Is.EqualTo("""{"index":0,"sha256":"abc","rows":1234,"firstRow":1,"lastRow":1300}"""));
            Assert.That(json["profile"]!.ToJsonString(), Is.EqualTo("""{"rowsRead":0,"rowsValid":0,"rowsRejected":0,"likelyDuplicates":0,"chunkCount":0}"""));
            Assert.That(json["checkpoint"]!.ToJsonString(), Is.EqualTo("""{"phase":"analyze","chunkIndex":0,"rowOffset":0,"lastRow":0}"""));
            Assert.That(json["analysis"], Is.Null);
            Assert.That(json["aggregates"]!.ToJsonString(), Is.EqualTo("{}"));
            Assert.That(json["heartbeatUtc"], Is.Null);
            Assert.That(json["driverToken"], Is.Null);
            Assert.That(json["lastInstance"], Is.Null);
            Assert.That(json["updatedUtc"]!.GetValue<string>(), Is.EqualTo("2026-11-16T12:00:00Z"));
            Assert.That(json["error"], Is.Null);
        });
    }

    [Test]
    public void Every_State_Is_Serialized_By_Its_Exact_Name()
    {
        foreach (var state in Enum.GetValues<ImportJobState>())
        {
            var json = ToJson(new ImportJobStatus { State = state });

            Assert.That(json["state"]!.GetValue<string>(), Is.EqualTo(state.ToString()));
        }
    }

    [Test]
    public void Contract_Samples_Deserialize()
    {
        var job = FromJson<ImportJob>(
            """
            {
              "jobId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","createdByUserId":53,"createdUtc":"2026-11-16T12:00:00Z",
              "file":{"name":"x.csv","size":123,"fingerprint":"ab12"},
              "keyField":"ContactEmail",
              "mapping":[{"source":"Email","target":"ContactEmail"}],
              "dateFormat":"iso",
              "options":{ },
              "attestation":null
            }
            """);
        var status = FromJson<ImportJobStatus>(
            """
            {
              "state":"Uploading",
              "chunks":[{"index":0,"sha256":"aa","rows":1234}],
              "profile":{"rowsRead":0,"rowsValid":0,"rowsRejected":0,"likelyDuplicates":0,"chunkCount":0},
              "checkpoint":{"phase":"analyze","chunkIndex":0,"rowOffset":0},
              "aggregates":{ },
              "heartbeatUtc":null,"driverToken":null,"lastInstance":null,
              "updatedUtc":"2026-11-16T12:00:00Z","error":null
            }
            """);

        Assert.Multiple(() =>
        {
            Assert.That(job.JobId, Is.EqualTo(Guid.Parse("6f9619ff-8b86-d011-b42d-00c04fc964ff")));
            Assert.That(job.CreatedUtc, Is.EqualTo(TestData.Now));
            Assert.That(job.CreatedUtc.Kind, Is.EqualTo(DateTimeKind.Utc));
            Assert.That(job.Mapping, Is.EqualTo(new[] { new ImportFieldMapping { Source = "Email", Target = "ContactEmail" } }));
            Assert.That(job.Options, Is.EqualTo(new ImportOptions()));
            Assert.That(job.Attestation, Is.Null);
            Assert.That(status.State, Is.EqualTo(ImportJobState.Uploading));
            Assert.That(status.Chunks, Has.Count.EqualTo(1));
            Assert.That(status.Chunks[0].Rows, Is.EqualTo(1234));
            Assert.That(status.Checkpoint.Phase, Is.EqualTo(ImportPhase.Analyze));
            Assert.That(status.UpdatedUtc, Is.EqualTo(TestData.Now));
        });
    }

    [Test]
    public void Job_Round_Trips_With_Options_And_Attestation()
    {
        var job = TestData.Job() with
        {
            Options = new ImportOptions
            {
                NewContacts = NewContactsBehavior.Skip,
                MultipleMatches = MultipleMatchesBehavior.UpdateAll,
                RecipientListId = 12,
                Unsubscribed = UnsubscribedBehavior.Resubscribe,
                Consent = ConsentMode.BlanketWithColumnOverride,
                ConsentId = 3,
                RevokedConsent = RevokedConsentBehavior.ReAgree,
                SourceField = "ContactSource",
                SourceValue = "import-2026-11-16",
                SourceStamp = SourceStampBehavior.CreatedAndUpdated,
            },
            Attestation = new ImportAttestation { UserId = 53, Utc = TestData.Now, StatementVersion = "1" },
        };

        var roundTripped = ImportJson.Deserialize<ImportJob>(ImportJson.Serialize(job));

        Assert.Multiple(() =>
        {
            Assert.That(roundTripped.Options, Is.EqualTo(job.Options));
            Assert.That(roundTripped.Attestation, Is.EqualTo(job.Attestation));
            Assert.That(ToJson(job)["options"]!["consent"]!.GetValue<string>(), Is.EqualTo("blanketWithColumnOverride"));
            Assert.That(ToJson(job)["options"]!["revokedConsent"]!.GetValue<string>(), Is.EqualTo("reAgree"));
        });
    }

    [TestCase("""{"state":3,"updatedUtc":"2026-11-16T12:00:00Z"}""", TestName = "Reject_State_Number")]
    [TestCase("""{"state":"Unknown","updatedUtc":"2026-11-16T12:00:00Z"}""", TestName = "Reject_State_Unknown")]
    [TestCase("""{"state":"Uploading","checkpoint":{"phase":2}}""", TestName = "Reject_Phase_Number")]
    [TestCase("""{"state":"Uploading","checkpoint":null}""", TestName = "Reject_Null_Checkpoint")]
    [TestCase("null", TestName = "Reject_Null_Document")]
    [TestCase("", TestName = "Reject_Empty_Document")]
    public void Invalid_Status_Json_Throws(string json) =>
        Assert.That(() => FromJson<ImportJobStatus>(json), Throws.InstanceOf<JsonException>());

    [TestCase("""{"createdByUserId":53,"createdUtc":"2026-11-16T12:00:00Z","file":{"name":"x.csv","size":1,"fingerprint":"ab"},"keyField":"ContactEmail","mapping":[]}""", TestName = "Reject_Job_Without_Id")]
    [TestCase("""{"jobId":"../x","createdByUserId":53,"createdUtc":"2026-11-16T12:00:00Z","file":{"name":"x.csv","size":1,"fingerprint":"ab"},"keyField":"ContactEmail","mapping":[]}""", TestName = "Reject_Job_Id_Not_Guid")]
    [TestCase("""{"jobId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","createdByUserId":53,"createdUtc":"2026-11-16T12:00:00Z","file":{"name":"x.csv","size":1,"fingerprint":"ab"},"keyField":"ContactEmail"}""", TestName = "Reject_Job_Without_Mapping")]
    [TestCase("""{"jobId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","createdByUserId":53,"createdUtc":"2026-11-16T12:00:00Z","file":{"name":"x.csv","size":1,"fingerprint":"ab"},"keyField":null,"mapping":[]}""", TestName = "Reject_Job_Null_Key_Field")]
    [TestCase("""{"jobId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","createdByUserId":53,"createdUtc":"2026-11-16T12:00:00Z","file":{"name":"x.csv","size":1,"fingerprint":"ab"},"keyField":"ContactEmail","mapping":[],"options":{"newContacts":"delete"}}""", TestName = "Reject_Job_Unknown_Option_Value")]
    [TestCase("""{"jobId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","createdByUserId":53,"createdUtc":"2026-11-16T12:00:00Z","file":{"name":"x.csv","size":1,"fingerprint":"ab"},"keyField":"ContactEmail","mapping":[],"options":{"newContacts":1}}""", TestName = "Reject_Job_Numeric_Option_Value")]
    [TestCase("""{"jobId":"6f9619ff-8b86-d011-b42d-00c04fc964ff","createdByUserId":"53","createdUtc":"2026-11-16T12:00:00Z","file":{"name":"x.csv","size":1,"fingerprint":"ab"},"keyField":"ContactEmail","mapping":[]}""", TestName = "Reject_Job_Number_As_String")]
    public void Invalid_Job_Json_Throws(string json) =>
        Assert.That(() => FromJson<ImportJob>(json), Throws.InstanceOf<JsonException>());
}
