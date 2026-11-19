using System.Reflection;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

/// <summary>
/// The public payload of <see cref="AfterContactImportEvent"/> covers every internal option and count: a new internal
/// value, option or count without a public one fails here.
/// </summary>
public class ContactImportEventPayloadTests
{
    /// <summary>Options of <see cref="ImportOptions"/> that are top-level properties of <see cref="ContactImportEventData"/>.</summary>
    private static readonly string[] topLevelOptions = [nameof(ImportOptions.RecipientListId), nameof(ImportOptions.ConsentId), nameof(ImportOptions.SourceField)];

    private static readonly Dictionary<Type, Action> enumMappings = new()
    {
        [typeof(NewContactsBehavior)] = () => AssertMapsEveryValue<NewContactsBehavior, ContactImportNewContacts>(ContactImportEventMapping.Map),
        [typeof(ExistingContactsBehavior)] = () => AssertMapsEveryValue<ExistingContactsBehavior, ContactImportExistingContacts>(ContactImportEventMapping.Map),
        [typeof(EmptyValuesBehavior)] = () => AssertMapsEveryValue<EmptyValuesBehavior, ContactImportEmptyValues>(ContactImportEventMapping.Map),
        [typeof(MultipleMatchesBehavior)] = () => AssertMapsEveryValue<MultipleMatchesBehavior, ContactImportMultipleMatches>(ContactImportEventMapping.Map),
        [typeof(InvalidEmailBehavior)] = () => AssertMapsEveryValue<InvalidEmailBehavior, ContactImportInvalidEmail>(ContactImportEventMapping.Map),
        [typeof(UnsubscribedBehavior)] = () => AssertMapsEveryValue<UnsubscribedBehavior, ContactImportUnsubscribed>(ContactImportEventMapping.Map),
        [typeof(BouncedAddressBehavior)] = () => AssertMapsEveryValue<BouncedAddressBehavior, ContactImportBouncedAddress>(ContactImportEventMapping.Map),
        [typeof(ConsentMode)] = () => AssertMapsEveryValue<ConsentMode, ContactImportConsent>(ContactImportEventMapping.Map),
        [typeof(RevokedConsentBehavior)] = () => AssertMapsEveryValue<RevokedConsentBehavior, ContactImportRevokedConsent>(ContactImportEventMapping.Map),
        [typeof(SourceStampBehavior)] = () => AssertMapsEveryValue<SourceStampBehavior, ContactImportSourceStamp>(ContactImportEventMapping.Map),
    };

    [Test]
    public void Every_Value_Of_Every_Option_Enum_Maps_To_The_Public_Value_Of_The_Same_Name()
    {
        var optionEnums = typeof(ImportOptions).GetProperties().Select(p => p.PropertyType).Where(t => t.IsEnum).ToHashSet();

        Assert.That(enumMappings.Keys, Is.EquivalentTo(optionEnums), "Every option enum of ImportOptions has a public enum and a mapping test.");
        Assert.Multiple(() =>
        {
            foreach (var mapping in enumMappings.Values)
            {
                mapping();
            }
        });
    }

    [Test]
    public void Every_Option_Is_In_The_Public_Options_Or_The_Event_Data()
    {
        var internalNames = typeof(ImportOptions).GetProperties().Select(p => p.Name).Except(topLevelOptions);
        var publicNames = typeof(ContactImportOptions).GetProperties().Select(p => p.Name);

        Assert.That(publicNames, Is.EquivalentTo(internalNames));
    }

    [Test]
    public void Public_Options_Have_The_Values_Of_The_Import_Options()
    {
        var options = new ImportOptions
        {
            NewContacts = NewContactsBehavior.Skip,
            ExistingContacts = ExistingContactsBehavior.Skip,
            EmptyValues = EmptyValuesBehavior.Clear,
            MultipleMatches = MultipleMatchesBehavior.UpdateAll,
            InvalidEmail = InvalidEmailBehavior.ImportWithoutEmail,
            Unsubscribed = UnsubscribedBehavior.Resubscribe,
            BouncedAddress = BouncedAddressBehavior.SkipAndReport,
            Consent = ConsentMode.BlanketWithColumnOverride,
            RevokedConsent = RevokedConsentBehavior.ReAgree,
            SourceStamp = SourceStampBehavior.CreatedAndUpdated,
            SourceValue = "source",
            ReportIncludesKey = true,
        };

        var mapped = ContactImportOptions.From(options);

        Assert.Multiple(() =>
        {
            foreach (var property in typeof(ContactImportOptions).GetProperties())
            {
                object? expected = typeof(ImportOptions).GetProperty(property.Name)!.GetValue(options);
                Assert.That(property.GetValue(mapped)?.ToString(), Is.EqualTo(expected?.ToString()), property.Name);
            }
        });
    }

    [Test]
    public void Every_Count_Key_Is_A_Public_Count_Of_The_Same_Name()
    {
        var keys = typeof(ImportAggregateKeys)
            .GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Where(f => f.Name is not nameof(ImportAggregateKeys.FieldChangedPrefix) and not nameof(ImportAggregateKeys.FieldClearedPrefix))
            .ToList();
        // Distinct values: a count read from the wrong key shows up.
        var stored = keys.Select((f, i) => (Key: (string)f.GetRawConstantValue()!, Count: i + 1L)).ToDictionary(k => k.Key, k => k.Count);

        var counts = ContactImportCounts.From(ImportAggregates.From(stored));

        var countProperties = typeof(ContactImportCounts).GetProperties().Where(p => p.PropertyType == typeof(long)).ToList();
        Assert.That(countProperties.Select(p => p.Name), Is.EquivalentTo(keys.Select(f => f.Name)), "One public count per key.");
        Assert.Multiple(() =>
        {
            foreach (var key in keys)
            {
                Assert.That(typeof(ContactImportCounts).GetProperty(key.Name)?.GetValue(counts), Is.EqualTo(stored[(string)key.GetRawConstantValue()!]), key.Name);
            }
        });
    }

    [Test]
    public void Field_Counts_Are_Public_Field_Changes_Ordered_By_Field_Name()
    {
        var stored = new Dictionary<string, long>
        {
            [ImportAggregateKeys.FieldChangedPrefix + "ContactLastName"] = 4,
            [ImportAggregateKeys.FieldChangedPrefix + "ContactFirstName"] = 3,
            [ImportAggregateKeys.FieldClearedPrefix + "ContactFirstName"] = 1,
        };

        var counts = ContactImportCounts.From(ImportAggregates.From(stored));

        Assert.That(counts.FieldChanges.Select(f => (f.FieldName, f.Changed, f.Cleared)), Is.EqualTo(new[] { ("ContactFirstName", 3L, 1L), ("ContactLastName", 4L, 0L) }));
    }

    private static void AssertMapsEveryValue<TInternal, TPublic>(Func<TInternal, TPublic> map)
        where TInternal : struct, Enum
        where TPublic : struct, Enum
    {
        var mapped = new List<TPublic>();
        foreach (var value in Enum.GetValues<TInternal>())
        {
            TPublic? result = null;
            try
            {
                result = map(value);
                mapped.Add(result.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
                // No public value: the assertion below fails.
            }

            Assert.That(result?.ToString(), Is.EqualTo(value.ToString()), $"{typeof(TInternal).Name}.{value} maps to the public value of the same name.");
        }

        Assert.That(mapped, Is.EquivalentTo(Enum.GetValues<TPublic>()), $"Every value of {typeof(TPublic).Name} is used.");
    }
}
