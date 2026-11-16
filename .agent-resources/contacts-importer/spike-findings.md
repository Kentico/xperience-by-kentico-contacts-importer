# Spike findings — XbyK internals (v31.6.0)

Source: ILSpy decompile of `Kentico.Xperience.Core` 31.6.0 (`CMS.*.dll`) + SQL install scripts embedded in `CMS.DbDataManager.dll` (`CMS.DbDataManager.Data.SQL.zip` → `SQL/Objects/*.sql`) + docs.kentico.com (Kentico Docs MCP). Decompiled output kept outside repo (scratchpad). Behavior may differ in other 31.x minors; re-check on target version.

---

## 1. Send-time bounce exclusion

**Answer**: address excluded when `EmailBounceIsHardBounce = 1 OR EmailBounceSoftBounceCount >= SoftBounceLimit`. Limit = `BouncedEmailsGlobalOptions.SoftBounceLimit` (ASP.NET options, **not** a settings key), default **5**. Configured via `builder.Services.Configure<BouncedEmailsGlobalOptions>(config.GetSection("BouncedEmailsGlobalOptions"))`. Match is `EmailBounceEmailAddress = ContactEmail` (plain equality; case sensitivity = DB collation).

**Evidence**
- `CMS.EmailMarketing.BouncedEmailsGlobalOptions { int SoftBounceLimit = 5 }`.
- `EmailMarketingRecipientsService.GenerateRecipients` → `Proc_EmailLibrary_EmailMarketingRecipient_GenerateRecipients @SoftBounceLimit`:
  ```sql
  bounced_emails AS (SELECT EmailBounceEmailAddress FROM EmailLibrary_EmailBounce
                     WHERE EmailBounceIsHardBounce = 1 OR EmailBounceSoftBounceCount >= @SoftBounceLimit)
  ... AND NOT EXISTS (SELECT 1 FROM bounced_emails be WHERE be.EmailBounceEmailAddress = r.EmailMarketingRecipientContactEmail)
  ```
  Same proc also excludes unsubscribed (newest confirmation row not approved) and empty `ContactEmail`.
- Same rule in `ContactInfoMethods.IsEmailBounced` (macro) and `CMSContactIsInRecipientListInstanceTranslator.GetBouncedContacts` (contact group condition).
- Quirk: `BounceChecker.UpsertBouncedEmail` sets `EmailBounceIsHardBounce = isHardBounce` on every bounce → a later soft bounce **clears** an earlier hard-bounce flag (soft count keeps growing).
- Docs: "Set up email tracking" + "Reference – Configuration keys" confirm `SoftBounceLimit`, default 5, SMTP only (SendGrid bounces handled by SendGrid).
- `EmailLibrary_EmailBounce`: unique index on `EmailBounceEmailAddress` (nvarchar 256).

**Confidence**: high.

**Plan changes**
- §2 Bounces: replace ⚠️ with the rule above (hard flag OR soft count ≥ `BouncedEmailsGlobalOptions.SoftBounceLimit`, default 5).
- §8/§9: bounce check must read `IOptionsMonitor<BouncedEmailsGlobalOptions>.CurrentValue.SoftBounceLimit` and report "bounced (hard)" vs "bounced (soft ≥ limit)" vs "soft < limit (still sends)". Join on `ContactEmail = EmailBounceEmailAddress` (unique index → cheap).
- D16: mark send-time threshold resolved.

---

## 2. `IConsentAgreementService.Agree` internals; bulk insert; backdating

**Answer**: `Agree` does very little: builds `ConsentAgreementInfo`, `.Insert()`, then clears one cache item. **No events** (only `Revoke` fires `DataProtectionEvents.RevokeConsentAgreement`). No activity logging, no revision lookup beyond reading `consent.ConsentHash`. → `BulkInsert` of `ConsentAgreementInfo` rows is equivalent if the same columns are set, and **backdating works** (time is just a column).

**Evidence** (`CMS.DataProtection.ConsentAgreementService`, internal sealed):
```csharp
ConsentAgreementContactID = contact.ContactID;
ConsentAgreementConsentID = consent.ConsentID;
ConsentAgreementTime      = dateTimeNowService.GetDateTimeNow();   // local time
ConsentAgreementConsentHash = consent.ConsentHash;                  // current consent text hash
ConsentAgreementRevoked   = false;
consentAgreementInfo.Insert();
CacheHelper.ClearCache("consentagreement|{contactId}|{consentId}");  // prefix clear + web farm task
```
- `ConsentAgreementInfo` TYPEINFO: `cms.consentagreement`, parent `om.contact` (`ConsentAgreementContactID`), `IsBinding`, `TouchCacheDependencies = true`, `LogEvents = false`, GUID column `ConsentAgreementGuid`.
- `IsAgreed` = newest row by `ConsentAgreementTime DESC` not revoked; cached 10 min under `consentagreement|{contactId}|{consentId}` with dependency `om.contact|byid|{contactId}|children|cms.consentagreement`.
- `GetAgreedConsents` / `ConsentContactsRetriever` / macro `CMSContactHasAgreedWithConsent` use same "newest row by time wins" rule (ROW_NUMBER over `ConsentAgreementTime DESC`).
- `BulkInsert` (`AbstractInfoProvider.BulkInsert`): calls `EnsureSystemFields` per info (**GUID auto-generated**, last-modified n/a), fires `ObjectEvents.BulkInsert`, SqlBulkCopy, `ClearHashtables(logTasks:true)`, touches `cms.consentagreement|all`. Does **not** touch per-contact child keys → `IsAgreed` cache may be stale ≤10 min unless cleared.
- Only other DLL refs to ConsentAgreement: `CMS.DataEngine` (PredefinedObjectType) — no hidden handlers in other assemblies (binary string scan of all 31.6.0 core DLLs).
- Table: `CMS_ConsentAgreement`, index `(ConsentAgreementContactID, ConsentAgreementConsentID)`, FKs to `OM_Contact` + `CMS_Consent`. `ConsentAgreementConsentHash nvarchar(100)`.

**Backdating caveat**: because "newest time wins", a backdated agreement older than an existing revocation row stays revoked (correct: withdrawal wins). A backdated agreement *after* importer checks "already agreed" is harmless. Use same clock as XbyK (`IDateTimeNowService`, local server time) for non-backdated rows.

**Confidence**: high (code), med (no other subscribers — custom project handlers on `ConsentAgreementInfo.TYPEINFO.Events.Insert` would be skipped; document).

**Plan changes**
- §9 Consent + §13: replace per-row `Agree` with `BulkInsert` of `ConsentAgreementInfo` (`ContactID`, `ConsentID`, `Time` = import time or mapped source date, `ConsentHash = consent.ConsentHash`, `Revoked = false`). Existence check set-based: newest row per contact for consent (`ROW_NUMBER() OVER (PARTITION BY ContactID ORDER BY Time DESC)`).
- Backdating: allowed → map optional consent-date column to `ConsentAgreementTime`. Validate not in future. Remove ⚠️ in §9 + §15.
- After run: `CacheHelper.ClearCache("consentagreement|")` (one prefix clear, logs web farm task).
- D4 wording: "records agreements via `IConsentAgreementService`" → "records agreements as `ConsentAgreementInfo` rows (same columns as `Agree`)".
- Note: `ConsentHash` pins the consent text version — dedicated consent should be finalized before import (text edits later archive the old hash in `CMS_ConsentArchive`).

---

## 3. `WhereIn` with string list

**Answer**: depends on distinct count vs `CMSDefaultSQLInlineLimit` (appsetting, default **1000**):
- `< 1000` distinct values → **inlined literals** `col IN (N'a', N'b', …)` (escaped, not parameterized → new plan per distinct list).
- `>= 1000` → **table-valued parameter** `col IN (SELECT * FROM @List)` of type `Type_CMS_StringTable (Value nvarchar(450))`.
No batching. Values deduped (`Distinct`).

**Evidence**: `WhereConditionBase.WhereIn(string, IEnumerable<string>)` → `WhereInGeneric` → `SelectCondition.PrepareConditionInternal`: `if (InlineLimit != -1 && (InlineLimit == 0 || list.Count >= InlineLimit)) → SqlHelper.BuildStringTable(...)` (TVP) else `SqlHelper.GetWhereInCondition(...)` (literals). `SqlHelper.DefaultSQLInlineLimit = new IntAppSetting("CMSDefaultSQLInlineLimit", 1000)`.

**Confidence**: high.

**Plan changes**
- §7: replace ⚠️ — "`WhereIn` inlines < 1000 values, uses TVP (`Type_CMS_StringTable`, nvarchar(450)) ≥ 1000. No 2,100-param risk either way." Keep own `OPENJSON(@keys)` raw query as preferred (one plan, stable), or use `WhereIn` — both safe. Note TVP truncates nothing for email (254) but custom key > 450 chars would fail; validate key length.
- Last-batch / small batches (<1000) produce inline lists → plan cache churn; minor. OPENJSON path avoids it.

---

## 4. `OM_Contact` indexes

**Answer**: `ContactEmail` **is indexed** — `IX_OM_Contact_ContactEmail` (nonclustered, non-unique). Other indexes: PK `ContactID` (clustered), unique `ContactGUID`, `ContactLastName`, `ContactCountryID`, `ContactStateID`, `ContactOwnerUserID`, `ContactStatusID`. Custom fields added via Modules (field editor) get **no index**: `AbstractTableManager.AddTableColumn` only `ALTER TABLE ADD`; `CreateColumnIndexes` only re-creates pre-existing indexes after column alter.

**Evidence**: `SQL/Objects/OM_Contact.sql` (`CREATE NONCLUSTERED INDEX [IX_OM_Contact_ContactEmail] ON [dbo].[OM_Contact] ([ContactEmail] ASC)`); `ContactEmail nvarchar(254) NULL`. Docs (stable customization guidelines → "Database table indexes"): custom indexes supported, created manually, use unique prefix, review after updates.

Related tables (useful for set-based writes):
- `OM_ContactGroupMember`: index `(ContactGroupID, Type) INCLUDE (RelatedID, FromCondition, FromAccount, FromManual)`, index on `RelatedID`. **No unique constraint on (group, contact)** → duplicates possible; importer must existence-check (already planned). Filtered unique index `(ContactGroupID, ProfileID) WHERE RelatedID = 0` (profiles preview) — irrelevant for contact rows (RelatedID ≠ 0).
- `EmailLibrary_EmailSubscriptionConfirmation`: indexes on `ContactID` and `RecipientListID` separately (no composite).

**Confidence**: high (default schema; customers may have changed indexes).

**Plan changes**
- §7: `ContactEmail` indexed (non-unique) → remove ⚠️. Custom key fields not indexed → UI warning + docs snippet for creating custom index (prefixed name, per guidelines). Keep perf measurement for non-indexed keys.
- Consider column max lengths from class schema (`ContactEmail` 254) in server validation.

---

## 5. Cache keys + web farm

**Answer**: **No custom web farm task needed.** Built-in APIs already log web-farm tasks:
- `CacheHelper.TouchKeys(keys)` / `TouchKey(key)` → touches locally + logs `TouchCacheItemWebFarmTask` (built-in, registered).
- `CacheHelper.ClearCache(prefix)` → local prefix clear + `ClearCacheItemsWebFarmTask`.
- `ObjectTypeInfo.InvalidateAllObjects()` → `InvalidateAllWebFarmTask`.
- `ProviderHelper.ClearHashtables(objectType, logTasks: true)`.

Dummy keys (from `BaseInfo.GetCacheDependencies`): `{objtype}|all`, `{objtype}|byid|{id}`, `{objtype}|byname|…`, `{objtype}|byguid|…`, `{parentType}|byid|{parentId}|children`, `{parentType}|byid|{parentId}|children|{objtype}`. Object type names:

| Info | Object type | Parent |
|---|---|---|
| `ContactInfo` | `om.contact` (`SupportsInvalidation = true`) | — |
| `ContactGroupMemberInfo` (contact) | `om.contactgroupmembercontact` (not `om.contactgroupmember`) | `om.contact` |
| `EmailSubscriptionConfirmationInfo` | `emaillibrary.emailsubscriptionconfirmation` | `om.contact` |
| `ConsentAgreementInfo` | `cms.consentagreement` | `om.contact` |
| recipient list `ContactGroupInfo` | `om.recipientlist` (also `om.contactgroup`) | — |

What bulk APIs already do: `BulkInsert` / `BulkUpdate` / `BulkDelete` → `ClearHashtables(logTasks:true)` + `TouchCacheKey("{objtype}|all")` (logs task) **regardless of `CMSActionContext.LogWebFarmTasks`** (that flag only affects per-object `TouchKeys` in `AbstractInfoBase`). So per batch: ~2 web farm tasks per bulk call — acceptable, not a flood.

System caches that per-type `|all` does **not** invalidate (per-contact keys, 10-min TTL):
- `EmailSubscriptionConfirmationService.IsConfirmed`: item `emailsubscriptionconfirmation|{contactId}|{listId}`, deps `om.contact|byid|{id}`, `om.recipientlist|byid|{listId}`, `om.contact|byid|{id}|children|emaillibrary.emailsubscriptionconfirmation` → **touch `om.recipientlist|byid|{listId}` once** invalidates all.
- `ConsentAgreementService.IsAgreed`: item `consentagreement|{contactId}|{consentId}`, dep per-contact child key → **`CacheHelper.ClearCache("consentagreement|")`** once.

Raw SQL UPDATE of `OM_Contact` touches nothing → after run: `CacheHelper.TouchKeys(["om.contact|all"])`, `ContactInfo.TYPEINFO.InvalidateAllObjects()`, `ProviderHelper.ClearHashtables("om.contact", true)`. Per-contact `om.contact|byid|{id}` keys not touched by `|all` — touching per-ID keys for millions of rows is not practical; rely on `|all` + invalidation; document that custom caches keyed only on `byid` may stay stale until expiry.

If a custom task is ever needed: class `: WebFarmTaskBase` (`CMS.Core`), override `ExecuteTask()` (+ optional `ConditionMethod()`), JSON-serialized public props; register in module `OnInit`: `Service.Resolve<IWebFarmService>().RegisterTask<MyTask>(isMemoryTask: true)`; create via `WebFarmHelper.CreateTask(new MyTask{…})`. Unregistered task → `NotSupportedException`. No attribute-based registration. Not in public docs (only `WebFarmHelper.CreateTask` example) → treat as semi-internal.

Read-only mode: `WebFarmTaskCreator.CreateTaskInternal` silently returns false when read-only → cache-clear after run may be skipped if app turns read-only; run cleanup again on resume.

**Confidence**: high (keys/APIs), med (completeness of system caches depending on contact data — only scanned EmailMarketing, ContactManagement, DataProtection).

**Plan changes**
- §3 diagram, §11 Web farm, §13, D7: drop "custom web farm task". Replace with "after each run (and pause/cancel): built-in cache APIs — `TouchKeys` of `om.contact|all`, `om.contactgroupmembercontact|all`, `emaillibrary.emailsubscriptionconfirmation|all`, `cms.consentagreement|all`, `om.recipientlist|byid|{listId}`; `ClearCache("consentagreement|")`; `ContactInfo.TYPEINFO.InvalidateAllObjects()`; `ProviderHelper.ClearHashtables("om.contact", true)`. All log built-in web farm tasks."
- `CMSActionContext.LogWebFarmTasks = false` only matters for per-object API calls — importer has none → optional, keep for safety.
- Remove ⚠️ in §11 and §15 "cache keys".

---

## 6. Raw parameterized SQL (`UPDATE … FROM OPENJSON(@json)`)

**Answer**: yes. Use `ConnectionHelper.ExecuteNonQuery(string queryText, QueryDataParameters parameters, QueryTypeEnum.SQLQuery, bool transaction = false)` (or `ExecuteNonQueryAsync(queryText, parameters, QueryTypeEnum.SQLQuery, ct)`; `ExecuteQuery`/`ExecuteReader` for SELECT). Parameters: `new QueryDataParameters { { "@rows", json } }` → `new SqlParameter(name, value)`; string > 4000 chars → `nvarchar(max)` (SqlClient inference). Wrap batch in `CMSTransactionScope`. Respects `CMSConnectionScope` / configured connection string.

**Evidence**: `ConnectionHelper` (lines ~400–560 in decompile); `CMS.DataProviderSQL.DataConnection.CreateParameter` (`new SqlParameter(parameter.Name, obj)`, DataType conversion only for registered types); Kentico itself uses the same API for set-based work (`EmailMarketingRecipientsService` → `ConnectionHelper.ExecuteNonQueryAsync(queryName, …)`). Read-only DB failures surface as exceptions flagged `IsReadOnlyModeException()`.

Kentico guidance (docs "Guidelines for stable customization" → Database → Data access and management):
> "Avoid direct access to the Xperience database structures (custom SQL scripts, procedures, etc.). The API is the primary way to work with data in Xperience. You may encounter advanced scenarios where direct database access is required, typically for performance optimization of operations that handle very large amounts of data. In these cases, use caution and provide test coverage for your functionality to ensure that your custom queries remain compatible with any database schema or data changes."

→ Importer's case (bulk perf) is the explicitly allowed exception, conditional on test coverage. Custom DB objects (procs/views) allowed with unique prefix; don't modify system ones.

**Confidence**: high (API), high (guidance text).

**Plan changes**
- §13: name API explicitly: `ConnectionHelper.ExecuteNonQuery(sql, QueryDataParameters, QueryTypeEnum.SQLQuery)` inside `CMSTransactionScope`. Pass JSON as `DataParameter` typed string (explicitly nvarchar(max) not possible via `DataParameter`; inference fine since payload > 4000 chars; for tiny batches pad nothing — plan reuse minor).
- §13/D22: cite docs exception ("performance optimization of operations that handle very large amounts of data") + requirement: integration tests against MSSQL container on each supported XbyK version (schema drift guard). Build column list from `DataClassInfo`/`FormInfo` of `OM.Contact`, quote with `[]`, validate against allow-list.
- Don't create custom stored procs (keeps install zero-DB-object); inline SQL is fine.

---

## Other notes found on the way

- `BulkInsert` fires `ObjectEvents.BulkInsert` (type-level, once per batch) — custom handlers subscribed to it will run; per-object Insert handlers won't.
- `ContactInfo` last-modified column = `ContactLastModified`, GUID = `ContactGUID` (both auto-set by `BulkInsert.EnsureSystemFields`). Raw UPDATE must set `ContactLastModified` itself (already in plan).
- Recipient generation (send time) re-checks unsubscribed + bounced; importer can't cause sends to bounced addresses.

## Blockers

None. Remaining unverified: custom-handler impact in customer projects (doc-only), per-ID cache staleness for custom caches (document).
