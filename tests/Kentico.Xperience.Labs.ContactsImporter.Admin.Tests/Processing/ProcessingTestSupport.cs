namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.Processing;

internal static class ProcessingTestData
{
    public const string Email = "ContactEmail";
    public const string FirstName = "ContactFirstName";
    public const string LastName = "ContactLastName";
    public const string CustomerId = "CustomerID";
    public const string Source = "ContactSource";

    public static readonly ContactFieldDefinition[] Fields =
    [
        .. TestData.Fields,
        new("Age", "Age", "integer", null),
        new("Big", "Big", "longinteger", null),
        new("Total", "Total", "decimal", 10, 2),
        new("Score", "Score", "double", null),
        new("Day", "Day", "date", null),
        new("Active", "Active", "boolean", null),
        new("Uid", "Uid", "guid", null),
        new("Picture", "Picture", "binary", null),
        new(Country, "Country", "integer", null, null, new ContactFieldReference("cms.country", "CMS_Country", "CountryID")),
        new(Owner, "Owner", "integer", null, null, new ContactFieldReference("cms.user", "CMS_User", "UserID", "dbo")),
        new(Deputy, "Deputy", "integer", null, null, new ContactFieldReference(null, "cms_user", "userid", "dbo")),
        new(Persona, "Persona", "integer", null, null, new ContactFieldReference("om.persona")),
    ];

    /// <summary>Reference to <c>CMS_Country</c>.</summary>
    public const string Country = "CountryID";

    /// <summary>Reference to <c>CMS_User</c>.</summary>
    public const string Owner = "OwnerID";

    /// <summary>A second reference to <c>CMS_User</c>, known from a foreign key only.</summary>
    public const string Deputy = "DeputyID";

    /// <summary>Reference to an object type whose table is not known: flagged, not checked.</summary>
    public const string Persona = "PersonaID";

    /// <summary>Hash of the consent the fake audience resolver knows.</summary>
    public const string ConsentHash = "consent-hash";

    public static ImportProcessingContext Context(string keyField, string[] targets, ImportOptions? options = null, ImportConsentMapping? consentMapping = null, int softBounceLimit = ImportAudience.DefaultSoftBounceLimit)
    {
        var job = TestData.Job(keyField: keyField, targets: targets) with { Options = options ?? new ImportOptions(), ConsentMapping = consentMapping };
        Assert.That(ImportProcessingContext.TryCreate(job, Fields, out var context, out string? error, Audience(job.Options, softBounceLimit)), Is.True, error);

        return context!;
    }

    /// <summary>The audience a resolver returns for options whose list and consent exist.</summary>
    public static ImportAudience Audience(ImportOptions options, int softBounceLimit = ImportAudience.DefaultSoftBounceLimit) =>
        new(options.RecipientListId, options.Consent == ConsentMode.None ? null : options.ConsentId, options.Consent == ConsentMode.None ? null : ConsentHash, softBounceLimit);

    public static ImportRecord Record(long row, string key, params string?[] values) => new(row, key, values);
}

/// <summary>
/// In-memory stand-in for the contact table: matches keys ordinally (or ignoring case) and applies creates and
/// updates with the merge rule, so processor tests can check end states without a database.
/// </summary>
internal sealed class FakeContactRepository : IContactImportRepository
{
    private int nextId = 1;

    public List<FakeContact> Contacts { get; } = [];

    public bool IgnoreCase { get; set; }

    public int Lookups { get; private set; }

    public int EquivalenceLookups { get; private set; }

    public int Writes { get; private set; }

    /// <summary>Runs before every lookup; tests use it to move time, cancel the job or throw.</summary>
    public Action<int>? OnLookup { get; set; }

    /// <summary>Runs before every write; throwing simulates a database error.</summary>
    public Action<int>? OnWrite { get; set; }

    /// <summary>Jobs whose import lock is held, as the database holds it for all application instances.</summary>
    public HashSet<Guid> LockedJobs { get; } = [];

    /// <summary>Number of attempts to take a job lock that found it held.</summary>
    public int LockRefusals { get; private set; }

    /// <summary>Throwing simulates a database error while the lock is taken.</summary>
    public Action? OnLock { get; set; }

    /// <summary>
    /// Does not wait: a test runs the competing call inside a hook of the call that holds the lock, on one thread.
    /// </summary>
    public IDisposable? TryLockJob(Guid jobId, TimeSpan timeout)
    {
        OnLock?.Invoke();
        if (!LockedJobs.Add(jobId))
        {
            LockRefusals++;

            return null;
        }

        return new Releaser(() => LockedJobs.Remove(jobId));
    }

    private sealed class Releaser(Action release) : IDisposable
    {
        public void Dispose() => release();
    }

    public FakeContact Add(ImportProcessingContext context, DateTime lastModified, params object?[] values)
    {
        var contact = new FakeContact(nextId++, [.. values]) { LastModified = lastModified };
        Assert.That(values, Has.Length.EqualTo(context.Fields.Count));
        Contacts.Add(contact);

        return contact;
    }

    public IReadOnlyList<ContactMatch> FindMatches(ImportProcessingContext context, IReadOnlyList<object> keys)
    {
        Lookups++;
        OnLookup?.Invoke(Lookups);

        var matches = new List<ContactMatch>();
        for (int i = 0; i < keys.Count; i++)
        {
            foreach (var contact in Contacts.Where(c => KeyEquals(c.Values[context.KeyIndex], keys[i])))
            {
                object?[] values = [.. contact.Values];
                values[context.KeyIndex] = null;
                matches.Add(new ContactMatch(i, contact.Id, contact.LastModified, values));
            }
        }

        return matches;
    }

    public IReadOnlyList<(int Index, int EquivalentIndex)> FindEquivalentKeys(IReadOnlyList<string> keys)
    {
        EquivalenceLookups++;
        var pairs = new List<(int, int)>();
        for (int i = 1; i < keys.Count; i++)
        {
            int first = Enumerable.Range(0, i).FirstOrDefault(j => KeyEquals(keys[j], keys[i]), -1);
            if (first >= 0)
            {
                pairs.Add((i, first));
            }
        }

        return pairs;
    }

    /// <summary>Existing values by referenced table name; a table that is not listed has no rows.</summary>
    public Dictionary<string, HashSet<object>> ReferencedRows { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every reference check: the field asked through and the values asked about.</summary>
    public List<(string Field, object[] Values)> ReferenceLookups { get; } = [];

    public IReadOnlyList<int> FindMissingReferences(ImportField field, IReadOnlyList<object> values)
    {
        ReferenceLookups.Add((field.Name, [.. values]));
        var existing = ReferencedRows.GetValueOrDefault(field.Reference!.TableName!) ?? [];

        return [.. Enumerable.Range(0, values.Count).Where(i => !existing.Contains(values[i]))];
    }

    /// <summary>Contacts that are members of the recipient list of the job.</summary>
    public List<int> Members { get; } = [];

    /// <summary>Subscription confirmations for the recipient list of the job, in insert order.</summary>
    public List<FakeSubscription> Subscriptions { get; } = [];

    /// <summary>Consent agreements with the consent of the job, in insert order.</summary>
    public List<FakeAgreement> Agreements { get; } = [];

    /// <summary>Bounce state by email address.</summary>
    public Dictionary<string, (bool Hard, int Soft)> Bounces { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Emails of contacts whose email field is not mapped, by contact ID.</summary>
    public Dictionary<int, string> Emails { get; } = [];

    public int AudienceLookups { get; private set; }

    /// <summary>Runs after the contacts of a batch are written and before its recipient list and consent rows are; throwing simulates a failure inside the transaction.</summary>
    public Action? OnAudienceWrite { get; set; }

    public IReadOnlyList<AudienceState> FindAudienceStates(ImportProcessingContext context, IReadOnlyList<AudienceTarget> targets, IReadOnlyList<int>? createdContactIds = null, bool usePlannedEmails = true)
    {
        AudienceLookups++;
        var states = new List<AudienceState>();
        for (int i = 0; i < targets.Count; i++)
        {
            var target = targets[i];
            int? id = target.ContactId ?? (createdContactIds is not null && target.CreateIndex >= 0 ? createdContactIds[target.CreateIndex] : null);
            var contact = id is null ? null : Contacts.SingleOrDefault(c => c.Id == id);
            string? stored = null;
            if (contact is not null)
            {
                stored = context.EmailFieldIndex >= 0 ? contact.Values[context.EmailFieldIndex] as string : Emails.GetValueOrDefault(contact.Id);
            }

            string? email = usePlannedEmails && target.HasPlannedEmail ? target.PlannedEmail : stored;
            var subscription = contact is null ? null : Subscriptions.Where(s => s.ContactId == contact.Id).OrderByDescending(s => s.Date).FirstOrDefault();
            var agreement = contact is null ? null : Agreements.Where(a => a.ContactId == contact.Id).OrderByDescending(a => a.Time).FirstOrDefault();
            var (Hard, Soft) = email is null ? default : Bounces.GetValueOrDefault(email);

            states.Add(new AudienceState(
                i,
                contact?.Id,
                !string.IsNullOrWhiteSpace(email),
                context.HasRecipientList && contact is not null && Members.Contains(contact.Id),
                context.HasRecipientList ? subscription?.Approved : null,
                context.HasRecipientList ? subscription?.Date : null,
                context.HasRecipientList ? subscription?.Guid : null,
                context.HasRecipientList && Hard,
                context.HasRecipientList ? Soft : 0,
                context.RecordsConsent ? agreement?.Revoked : null,
                context.RecordsConsent ? agreement?.Time : null,
                context.RecordsConsent ? agreement?.Guid : null));
        }

        return states;
    }

    public AudienceResult? Write(ImportProcessingContext context, IReadOnlyList<ContactCreate> creates, IReadOnlyList<ContactUpdate> updates, DateTime now, AudienceWrite? audience = null)
    {
        Writes++;
        OnWrite?.Invoke(Writes);

        var createdIds = new List<int>();
        foreach (var create in creates)
        {
            createdIds.Add(nextId);
            Contacts.Add(new FakeContact(nextId++, [.. create.Values]) { LastModified = now, Created = now, Source = context.SourceValue });
        }

        foreach (var update in updates)
        {
            var contact = Contacts.Single(c => c.Id == update.ContactId);
            for (int i = 0; i < context.Fields.Count; i++)
            {
                if (i == context.KeyIndex)
                {
                    continue;
                }

                if (update.States[i] == FieldValueState.Value)
                {
                    contact.Values[i] = update.Values[i];
                }
                else if (update.States[i] == FieldValueState.Empty && context.ClearEmptyValues)
                {
                    contact.Values[i] = null;
                }
            }

            contact.LastModified = now;
            if (context.StampUpdatedContacts)
            {
                contact.Source = context.SourceValue;
            }
        }

        if (audience is not { Targets.Count: > 0 } || !context.HasAudience)
        {
            return null;
        }

        OnAudienceWrite?.Invoke();
        var result = audience.Decide(FindAudienceStates(context, audience.Targets, createdIds, usePlannedEmails: false));
        Members.AddRange(result.Memberships);
        Subscriptions.AddRange(result.Subscriptions.Select(s => new FakeSubscription(s.ContactId, true, now, s.Guid)));
        Agreements.AddRange(result.Agreements.Select(a => new FakeAgreement(a.ContactId, false, a.Time, a.Guid, context.Audience.ConsentHash)));

        return result;
    }

    private bool KeyEquals(object? contactKey, object key)
    {
        var comparison = IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return contactKey is string text && key is string keyText ? string.Equals(text, keyText, comparison) : Equals(contactKey, key);
    }
}

internal sealed class FakeContact(int id, object?[] values)
{
    public int Id { get; } = id;

    public object?[] Values { get; } = values;

    public DateTime LastModified { get; set; }

    public DateTime? Created { get; set; }

    public string? Source { get; set; }
}

internal sealed record FakeSubscription(int ContactId, bool Approved, DateTime Date, Guid Guid);

internal sealed record FakeAgreement(int ContactId, bool Revoked, DateTime Time, Guid Guid, string? Hash = null);

/// <summary>
/// Knows every recipient list and consent unless told otherwise; the consent hash is <see cref="ProcessingTestData.ConsentHash"/>.
/// </summary>
internal sealed class FakeAudienceResolver : IImportAudienceResolver
{
    public string? Error { get; set; }

    public int SoftBounceLimit { get; set; } = ImportAudience.DefaultSoftBounceLimit;

    public int Calls { get; private set; }

    public ImportAudienceResolution Resolve(ImportOptions options)
    {
        Calls++;

        return Error is null
            ? new ImportAudienceResolution(ProcessingTestData.Audience(options, SoftBounceLimit))
            : new ImportAudienceResolution(null, Error);
    }
}

internal sealed class FakeEventPublisher : IImportEventPublisher
{
    public List<(ImportJob Job, ImportJobStatus Status, ContactImportStatus Outcome, bool IsDryRun, int? UserId, ImportAudience? Audience)> Events { get; } = [];

    /// <summary>Runs when an event is published; tests use it to check what is already stored or released.</summary>
    public Action? OnPublish { get; set; }

    public void Publish(ImportJob job, ImportJobStatus status, ContactImportStatus outcome, bool isDryRun, int? userId = null, ImportAudience? audience = null)
    {
        Events.Add((job, status, outcome, isDryRun, userId, audience));
        OnPublish?.Invoke();
    }
}

internal sealed class FakeCacheInvalidator : IImportCacheInvalidator
{
    public int Calls { get; private set; }

    public Exception? Throw { get; set; }

    public void InvalidateAfterImport(ImportJob job)
    {
        Calls++;
        if (Throw is not null)
        {
            throw Throw;
        }
    }
}

internal sealed class ListLogger<T>(List<string> messages) : Microsoft.Extensions.Logging.ILogger<T>
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        messages.Add($"{logLevel} {eventId.Name}: {formatter(state, exception)} {exception}");
}
