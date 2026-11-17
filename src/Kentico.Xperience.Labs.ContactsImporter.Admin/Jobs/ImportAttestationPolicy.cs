namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// When confirming an import is also an attestation of documented consent (PLAN D26), and which statement is attested.
/// </summary>
internal static class ImportAttestationPolicy
{
    /// <summary>
    /// Version of the consent statement shown in the Run confirmation dialog. Change it whenever the statement text
    /// changes: an attestation is valid only for the version the user saw, so imports confirmed with an older
    /// statement must be confirmed again. The text is resx key <c>contactsimporter.attestation.statement</c>
    /// (<see cref="ImportTextKeys.ATTESTATION_STATEMENT"/>): bump this version whenever that text changes.
    /// </summary>
    /// <remarks>
    /// The version identifies the en-US text. A translation of the statement (a resx of another admin UI culture) says
    /// the same and is attested under the same version; the culture the user saw is not stored.
    /// </remarks>
    public const string StatementVersion = "1";

    internal static ImportMessage MissingError => ImportMessages.AttestationMissing;

    /// <summary>
    /// An import that adds contacts to a recipient list (which includes resubscribing) or records consent agreements
    /// needs an attestation.
    /// </summary>
    public static bool IsRequired(ImportOptions options) => options.RecipientListId is not null || options.Consent != ConsentMode.None;

    /// <summary>
    /// Whether <paramref name="attestation"/> covers an import with <paramref name="options"/>: not needed, or given
    /// by a known user for the current statement.
    /// </summary>
    public static bool IsSatisfied(ImportOptions options, ImportAttestation? attestation) =>
        !IsRequired(options)
        || (attestation is { UserId: > 0 } && string.Equals(attestation.StatementVersion, StatementVersion, StringComparison.Ordinal));
}
