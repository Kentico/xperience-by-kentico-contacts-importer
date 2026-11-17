namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests.UIPages;

/// <summary>
/// Recipient lists and consents "in the database" of the page command tests; also checks the options against them
/// like <see cref="ImportAudienceResolver"/> does against the database.
/// </summary>
internal sealed class FakeChoiceProvider : IImportChoiceProvider, IImportAudienceResolver
{
    public const int NewsletterListId = 11;
    public const int ProductNewsListId = 12;
    public const int SignupConsentId = 21;
    public const int WebinarConsentId = 22;

    public List<ImportChoiceDto> RecipientLists { get; } = [new(NewsletterListId, "Newsletter"), new(ProductNewsListId, "Product news")];

    public List<ImportChoiceDto> Consents { get; } = [new(SignupConsentId, "Newsletter signup 2025"), new(WebinarConsentId, "Webinar signup")];

    /// <summary>Number of queries, to check that pages without a list or consent do not ask.</summary>
    public int Queries { get; private set; }

    public IReadOnlyList<ImportChoiceDto> GetRecipientLists()
    {
        Queries++;

        return [.. RecipientLists];
    }

    public IReadOnlyList<ImportChoiceDto> GetConsents()
    {
        Queries++;

        return [.. Consents];
    }

    public ImportAudienceResolution Resolve(ImportOptions options)
    {
        if (options.RecipientListId is { } listId && !RecipientLists.Any(list => list.Id == listId))
        {
            return new ImportAudienceResolution(null, ImportAudienceResolver.RecipientListMissingError);
        }

        if (options.Consent == ConsentMode.None)
        {
            return new ImportAudienceResolution(new ImportAudience(options.RecipientListId));
        }

        if (options.ConsentId is not { } consentId)
        {
            return new ImportAudienceResolution(null, ImportAudienceResolver.ConsentNotSelectedError);
        }

        return Consents.Any(consent => consent.Id == consentId)
            ? new ImportAudienceResolution(new ImportAudience(options.RecipientListId, consentId, "hash"))
            : new ImportAudienceResolution(null, ImportAudienceResolver.ConsentMissingError);
    }
}
