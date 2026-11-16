using System.Data;
using System.Globalization;

using CMS.ContactManagement;
using CMS.DataEngine;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Database access of the import: key lookups and the writes of one batch.
/// </summary>
internal interface IContactImportRepository
{
    /// <summary>
    /// Finds existing contacts whose key field equals one of <paramref name="keys"/> (database comparison rules).
    /// <see cref="ContactMatch.GroupIndex"/> is the position of the matched key in <paramref name="keys"/>.
    /// </summary>
    public IReadOnlyList<ContactMatch> FindMatches(ImportProcessingContext context, IReadOnlyList<object> keys);

    /// <summary>
    /// For text keys the database treats as equal to an earlier key in <paramref name="keys"/>: the position of the
    /// key and of the first equal key.
    /// </summary>
    public IReadOnlyList<(int Index, int EquivalentIndex)> FindEquivalentKeys(IReadOnlyList<string> keys);

    /// <summary>
    /// For the values of a reference field (<see cref="ImportField.Reference"/>): the positions in
    /// <paramref name="values"/> of those that do not exist in the referenced table. One set-based query.
    /// </summary>
    public IReadOnlyList<int> FindMissingReferences(ImportField field, IReadOnlyList<object> values);

    /// <summary>
    /// Creates and updates the contacts of one batch in one transaction.
    /// </summary>
    /// <param name="context">Job being processed.</param>
    /// <param name="creates">Contacts to create.</param>
    /// <param name="updates">Contacts to update with the merge rule.</param>
    /// <param name="now">Creation / last modification time to store (application local time, as Xperience does).</param>
    public void Write(ImportProcessingContext context, IReadOnlyList<ContactCreate> creates, IReadOnlyList<ContactUpdate> updates, DateTime now);

    /// <summary>
    /// Takes the database lock that lets one call at a time import a job, waiting up to <paramref name="timeout"/>.
    /// Returns <c>null</c> when another call still holds it. While the lock is held, the lookups and writes of the
    /// calling thread use the connection that owns the lock, so the lock cannot be lost without them failing too.
    /// Dispose releases it.
    /// </summary>
    public IDisposable? TryLockJob(Guid jobId, TimeSpan timeout);
}

/// <summary>
/// <see cref="IContactImportRepository"/> over the Xperience database. Reads and the batch update are raw set-based
/// SQL through <see cref="ConnectionHelper"/> (PLAN D22: fast, no per-row events; allowed for bulk operations by the
/// stable customization guidelines and covered by integration tests); creates use <c>BulkInsert</c>.
/// </summary>
internal sealed class ContactImportRepository(IInfoProvider<ContactInfo> contactProvider) : IContactImportRepository
{
    private readonly Lazy<ContactTable> table = new(GetContactTable);

    public IReadOnlyList<ContactMatch> FindMatches(ImportProcessingContext context, IReadOnlyList<object> keys)
    {
        if (keys.Count == 0)
        {
            return [];
        }

        var parameters = new QueryDataParameters { { ContactSqlBuilder.KeysParameter, ContactSqlBuilder.BuildKeysJson(context.KeyField, keys) } };
        using var data = ConnectionHelper.ExecuteQuery(ContactSqlBuilder.BuildLookupSql(table.Value, context), parameters, QueryTypeEnum.SQLQuery);

        var matches = new List<ContactMatch>();
        if (data.Tables.Count == 0)
        {
            return matches;
        }

        int fieldCount = context.Fields.Count;
        foreach (DataRow row in data.Tables[0].Rows)
        {
            object?[] values = new object?[fieldCount];
            foreach (int index in context.Fields.Select(field => field.Index).Where(index => index != context.KeyIndex))
            {
                object value = row[ContactSqlBuilder.ValueColumn(index)];
                values[index] = value is DBNull ? null : value;
            }

            object lastModified = row[ContactSqlBuilder.LastModifiedColumn];
            matches.Add(new ContactMatch(
                Convert.ToInt32(row[ContactSqlBuilder.GroupIndexColumn], CultureInfo.InvariantCulture),
                Convert.ToInt32(row[ContactSqlBuilder.ContactIdColumn], CultureInfo.InvariantCulture),
                lastModified is DBNull ? DateTime.MinValue : Convert.ToDateTime(lastModified, CultureInfo.InvariantCulture),
                values));
        }

        return matches;
    }

    public IReadOnlyList<(int Index, int EquivalentIndex)> FindEquivalentKeys(IReadOnlyList<string> keys)
    {
        if (keys.Count < 2)
        {
            return [];
        }

        var keyField = new ImportField(0, "k", ImportValueKind.Text);
        var parameters = new QueryDataParameters { { ContactSqlBuilder.KeysParameter, ContactSqlBuilder.BuildKeysJson(keyField, [.. keys]) } };
        using var data = ConnectionHelper.ExecuteQuery(ContactSqlBuilder.BuildEquivalentKeysSql(), parameters, QueryTypeEnum.SQLQuery);

        var pairs = new List<(int, int)>();
        if (data.Tables.Count == 0)
        {
            return pairs;
        }

        foreach (DataRow row in data.Tables[0].Rows)
        {
            pairs.Add((
                Convert.ToInt32(row[ContactSqlBuilder.GroupIndexColumn], CultureInfo.InvariantCulture),
                Convert.ToInt32(row[ContactSqlBuilder.EquivalentIndexColumn], CultureInfo.InvariantCulture)));
        }

        return pairs;
    }

    public IReadOnlyList<int> FindMissingReferences(ImportField field, IReadOnlyList<object> values)
    {
        if (values.Count == 0)
        {
            return [];
        }

        var parameters = new QueryDataParameters { { ContactSqlBuilder.KeysParameter, ContactSqlBuilder.BuildKeysJson(field, values) } };
        using var data = ConnectionHelper.ExecuteQuery(ContactSqlBuilder.BuildMissingReferencesSql(field), parameters, QueryTypeEnum.SQLQuery);

        var missing = new List<int>();
        if (data.Tables.Count == 0)
        {
            return missing;
        }

        foreach (DataRow row in data.Tables[0].Rows)
        {
            missing.Add(Convert.ToInt32(row[ContactSqlBuilder.GroupIndexColumn], CultureInfo.InvariantCulture));
        }

        return missing;
    }

    public void Write(ImportProcessingContext context, IReadOnlyList<ContactCreate> creates, IReadOnlyList<ContactUpdate> updates, DateTime now)
    {
        if (creates.Count == 0 && updates.Count == 0)
        {
            return;
        }

        using var transaction = new CMSTransactionScope();

        if (creates.Count > 0)
        {
            contactProvider.BulkInsert(creates.Select(create => ToContact(context, create, now)));
        }

        if (updates.Count > 0)
        {
            var parameters = new QueryDataParameters
            {
                { ContactSqlBuilder.RowsParameter, ContactSqlBuilder.BuildUpdateJson(context, updates) },
                { ContactSqlBuilder.ClearEmptyParameter, context.ClearEmptyValues },
                { ContactSqlBuilder.NowParameter, now },
            };
            if (context.StampUpdatedContacts)
            {
                parameters.Add(ContactSqlBuilder.SourceParameter, context.SourceValue);
            }

            ConnectionHelper.ExecuteNonQuery(ContactSqlBuilder.BuildUpdateSql(table.Value, context), parameters, QueryTypeEnum.SQLQuery);
        }

        transaction.Commit();
    }

    public IDisposable? TryLockJob(Guid jobId, TimeSpan timeout)
    {
        string resource = JobLockResource(jobId);

        // The scope keeps one open connection as the current connection of this thread. The lock belongs to its
        // session (not to a transaction), so it spans the batch transactions and the checkpoints between them.
        var scope = new CMSConnectionScope().Open();
        try
        {
            var parameters = new QueryDataParameters
            {
                { "@resource", resource },
                { "@timeout", (int)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue) },
            };
            using var data = ConnectionHelper.ExecuteQuery(
                "DECLARE @result int; EXEC @result = sp_getapplock @Resource = @resource, @LockMode = 'Exclusive', @LockOwner = 'Session', @LockTimeout = @timeout; SELECT @result AS [Result];",
                parameters,
                QueryTypeEnum.SQLQuery);

            // 0 and 1 = granted; negative = timeout, cancelled, deadlock victim or error.
            if (Convert.ToInt32(data.Tables[0].Rows[0][0], CultureInfo.InvariantCulture) >= 0)
            {
                return new JobLock(scope, resource);
            }
        }
        catch
        {
            scope.Dispose();
            throw;
        }

        scope.Dispose();

        return null;
    }

    internal static string JobLockResource(Guid jobId) => "Kentico.Xperience.Labs.ContactsImporter:" + ImportJobId.Format(jobId);

    private static ContactInfo ToContact(ImportProcessingContext context, ContactCreate create, DateTime now)
    {
        var contact = new ContactInfo { ContactCreated = now };
        foreach (var field in context.Fields)
        {
            if (create.Values[field.Index] is { } value)
            {
                contact.SetValue(field.Name, value);
            }
        }

        if (context.SourceField is not null)
        {
            contact.SetValue(context.SourceField, context.SourceValue);
        }

        return contact;
    }

    private static ContactTable GetContactTable()
    {
        var typeInfo = ContactInfo.TYPEINFO;
        var dataClass = DataClassInfoProvider.GetDataClassInfo(typeInfo.ObjectClassName)
            ?? throw new InvalidOperationException("The contact class was not found.");

        return new ContactTable(dataClass.ClassTableName, typeInfo.IDColumn, typeInfo.TimeStampColumn);
    }

    private sealed class JobLock(CMSConnectionScope scope, string resource) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                ConnectionHelper.ExecuteNonQuery(
                    "EXEC sp_releaseapplock @Resource = @resource, @LockOwner = 'Session';",
                    new QueryDataParameters { { "@resource", resource } },
                    QueryTypeEnum.SQLQuery);
            }
            catch (Exception)
            {
                // The connection is gone, and the lock of its session with it.
            }
            finally
            {
                scope.Dispose();
            }
        }
    }
}
