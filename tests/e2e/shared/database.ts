import { spawnSync } from "node:child_process";
import path from "node:path";

// Direct access to the database DancingGoat uses, for the few states the administration UI cannot
// produce: a contact who unsubscribed from a recipient list, a contact who withdrew a consent.
// Everything else the specs set up and check through the administration, as a marketer would.
//
// Runs scripts/Invoke-E2ESql.ps1 with PowerShell 7 (`pwsh`), which finds the connection string
// (see that script and the README). Specs that need it skip when the database cannot be reached.

const scriptPath = path.resolve(__dirname, "../scripts/Invoke-E2ESql.ps1");

export type SqlValue = string | number;

type SqlResult = { ok: true; rows: Record<string, unknown>[] } | { ok: false; error: string };

function invoke(query: string, parameters: Record<string, SqlValue>): SqlResult {
  const result = spawnSync("pwsh", ["-NoProfile", "-NonInteractive", "-File", scriptPath], {
    encoding: "utf8",
    timeout: 120_000,
    env: { ...process.env, E2E_SQL_QUERY: query, E2E_SQL_PARAMETERS: JSON.stringify(parameters) },
  });
  if (result.error) {
    return { ok: false, error: `pwsh could not be started: ${result.error.message}` };
  }
  if (result.status !== 0) {
    return { ok: false, error: (result.stderr || result.stdout || `exit code ${result.status}`).trim() };
  }

  const output = result.stdout.trim();
  return { ok: true, rows: output === "" ? [] : (JSON.parse(output) as Record<string, unknown>[]) };
}

let availability: string | null | undefined;

// Why the database cannot be used, or null when it can. Checked once per worker.
export function sqlUnavailableReason(): string | null {
  if (availability === undefined) {
    const result = invoke("SELECT 1 AS ok", {});
    availability = result.ok ? null : result.error;
  }
  return availability;
}

export function runSql(query: string, parameters: Record<string, SqlValue> = {}): Record<string, unknown>[] {
  const result = invoke(query, parameters);
  if (!result.ok) {
    throw new Error(`SQL failed: ${result.error}`);
  }
  return result.rows;
}

// The contact unsubscribes from the recipient list: what Xperience stores when a recipient uses the
// unsubscribe link, a subscription confirmation that is not approved and newer than all others
// (the app writes local time and SQL Server may run in UTC, so "newer" is taken from the stored rows).
export function unsubscribeFromRecipientList(email: string, recipientListId: number): void {
  const rows = runSql(
    `DECLARE @contact int = (SELECT MIN(ContactID) FROM OM_Contact WHERE ContactEmail = @email);
     DECLARE @newest datetime2 = (SELECT MAX(EmailSubscriptionConfirmationDate) FROM EmailLibrary_EmailSubscriptionConfirmation
       WHERE EmailSubscriptionConfirmationContactID = @contact AND EmailSubscriptionConfirmationRecipientListID = @list);
     INSERT INTO EmailLibrary_EmailSubscriptionConfirmation
       (EmailSubscriptionConfirmationContactID, EmailSubscriptionConfirmationRecipientListID, EmailSubscriptionConfirmationIsApproved,
        EmailSubscriptionConfirmationDate, EmailSubscriptionConfirmationGUID)
     SELECT @contact, @list, 0, DATEADD(second, 1, @newest), NEWID() WHERE @contact IS NOT NULL AND @newest IS NOT NULL;
     SELECT @@ROWCOUNT AS inserted;`,
    { email, list: recipientListId },
  );
  expectOne(rows, `unsubscribe ${email} from recipient list ${recipientListId}`);
}

// The contact withdraws the consent: a revoked agreement newer than all others, as IConsentAgreementService.Revoke stores it.
export function revokeConsent(email: string, consentId: number): void {
  const rows = runSql(
    `DECLARE @contact int = (SELECT MIN(ContactID) FROM OM_Contact WHERE ContactEmail = @email);
     DECLARE @newest datetime2 = (SELECT MAX(ConsentAgreementTime) FROM CMS_ConsentAgreement
       WHERE ConsentAgreementContactID = @contact AND ConsentAgreementConsentID = @consent);
     INSERT INTO CMS_ConsentAgreement
       (ConsentAgreementGuid, ConsentAgreementRevoked, ConsentAgreementContactID, ConsentAgreementConsentID, ConsentAgreementConsentHash, ConsentAgreementTime)
     SELECT NEWID(), 1, @contact, @consent, (SELECT ConsentHash FROM CMS_Consent WHERE ConsentID = @consent), DATEADD(second, 1, @newest)
       WHERE @contact IS NOT NULL AND @newest IS NOT NULL;
     SELECT @@ROWCOUNT AS inserted;`,
    { email, consent: consentId },
  );
  expectOne(rows, `revoke consent ${consentId} of ${email}`);
}

function expectOne(rows: Record<string, unknown>[], what: string): void {
  if (rows.length !== 1 || Number(rows[0].inserted) !== 1) {
    throw new Error(
      `Could not ${what}: the contact or its earlier subscription/agreement is not in the database the SQL helper uses. ` +
        "If DancingGoat runs against another database, set E2E_SQL_CONNECTION_STRING.",
    );
  }
}
