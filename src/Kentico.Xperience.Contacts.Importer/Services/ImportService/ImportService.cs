
using System.Data;
using System.Globalization;

using CMS.Base;
using CMS.ContactManagement;
using CMS.DataEngine;

using CsvHelper;
using CsvHelper.Configuration;

using Microsoft.Data.SqlClient;

using Newtonsoft.Json;

namespace Kentico.Xperience.Contacts.Importer.Services;
/// <inheritdoc />
public class ImportService : IImportService
{
    private readonly IInfoProvider<ContactGroupInfo> contactGroupInfoProvider;
    private readonly IInfoProvider<ContactInfo> contactInfoProvider;
    private readonly IContactsDeleteService contactsDeleteService;

    /// <param name="contactGroupInfoProvider"></param>
    /// <param name="contactInfoProvider"></param>
    /// <param name="contactsDeleteService"></param>
    public ImportService(IInfoProvider<ContactGroupInfo> contactGroupInfoProvider, IInfoProvider<ContactInfo> contactInfoProvider, IContactsDeleteService contactsDeleteService)
    {
        this.contactGroupInfoProvider = contactGroupInfoProvider;
        this.contactInfoProvider = contactInfoProvider;
        this.contactsDeleteService = contactsDeleteService;
    }

    /// <summary>
    /// Defined how ContactInfo columns will be mapped from CSV
    /// </summary>
    public sealed class ContactInfoMap : ClassMap<ContactInfo>
    {
        /// <summary>
        /// Defines import map for ContactInfo.TYPEINFO.ColumnNames
        /// </summary>
        public ContactInfoMap()
        {
            Map(m => m.ContactGUID);
            Map(m => m.ContactCreated);
            Map(m => m.ContactFirstName);
            Map(m => m.ContactLastName);
            Map(m => m.ContactEmail);
            Map(m => m.ContactAddress1);
            Map(m => m.ContactMiddleName);
        }
    }

    private sealed class ContactDeleteArgument
    {
        // Pragma disable reason: used implicitly
#pragma warning (disable: S3459 S1144)
        // ReSharper disable once InconsistentNaming // kentico naming convention
        public Guid ContactGUID { get; set; }
#pragma warning (restore: S3459 S1144)
    };

    private sealed class SimplifiedMap : ClassMap<ContactDeleteArgument>
    {
#pragma warning disable S1144
        public SimplifiedMap() => Map(m => m.ContactGUID);
#pragma warning restore S1144
    }

    /// <exception cref="Exception">Thrown when contact group is missing</exception>
    /// <inheritdoc />
    public async Task RunImport(Stream csvStream, ImportContext context, Func<List<ImportResult>, int, Task> onResultCallbackAsync, Func<Exception, Task> onErrorCallbackAsync)
    {
        switch (context.ImportKind)
        {
            case ImportKind.InsertAndSkipExisting:
            {
                await InsertContactsFromCsvAsync(csvStream, context, onResultCallbackAsync, onErrorCallbackAsync);
                break;
            }
            case ImportKind.Delete:
            {
                await BulkDeleteContactFromCsvAsync(csvStream, context, onResultCallbackAsync, onErrorCallbackAsync);
                break;
            }
            default:
            {
                throw new InvalidOperationException($"Unknown import kind '{context.ImportKind}'");
            }
        }
    }

    private async Task BulkDeleteContactFromCsvAsync(Stream csvStream, ImportContext context, Func<List<ImportResult>, int, Task> onResultCallbackAsync, Func<Exception, Task> onErrorCallbackAsync)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = context.Delimiter,
            PrepareHeaderForMatch = args => args.Header.ToLower(),
            IgnoreBlankLines = true,
        };

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<SimplifiedMap>();

        var records = CsvReadRecords<ContactDeleteArgument>(csv);

        int totalProcessed = 0;

        IEnumerable<List<Guid>> Pipe2TransformBatches(IEnumerable<ContactDeleteArgument> models)
        {
            var currentBatch = new List<Guid>(context.BatchSize);

            foreach (var item in models)
            {
                try
                {
                    currentBatch.Add(item.ContactGUID);
                }
                catch (Exception ex)
                {
                    onErrorCallbackAsync?.Invoke(ex);
                    continue;
                }
                finally
                {
                    totalProcessed += 1;

                    if (totalProcessed % context.BatchSize == 0)
                    {
                        // we are no concerned here that totalProcessed is captured from foreign closure
                        // we do not await this task, it doesn't concern import routine 
#pragma warning disable CS4014
                        Task.Run(async () => await onResultCallbackAsync.Invoke([], totalProcessed));
#pragma warning restore CS4014
                    }
                }

                if (currentBatch.Count >= context.BatchSize)
                {
                    yield return currentBatch;
                    currentBatch = [];
                }
            }

            if (currentBatch.Count < context.BatchSize && currentBatch.Count != 0)
            {
                Task.Run(async () => await onResultCallbackAsync.Invoke([], totalProcessed));
            }

            yield return currentBatch;
        }

        foreach (var deleteArg in Pipe2TransformBatches(records))
        {

            // assign existing task and continue with preparation of next
            await DeletedContactsAsync(deleteArg, context.BatchSize);
        }
    }

    private async Task InsertContactsFromCsvAsync(Stream csvStream, ImportContext context, Func<List<ImportResult>, int, Task> onResultCallbackAsync, Func<Exception, Task> onErrorCallbackAsync)
    {
        ContactGroupInfo? group = null;

        if (context.AssignToContactGroupGuid is { } assignToContactGroupGuid)
        {
            group = await contactGroupInfoProvider.GetAsync(assignToContactGroupGuid);
            if (group == null)
            {
                throw new ArgumentException("Contact group not found", nameof(context));
            }
        }

        var contactGuids = ConnectionHelper.ExecuteQuery(@"SELECT ContactGUID FROM OM_Contact", [], QueryTypeEnum.SQLQuery)
            .Tables[0].AsEnumerable().Select(x => (Guid)x[nameof(ContactInfo.ContactGUID)]).ToHashSet();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = context.Delimiter,
            PrepareHeaderForMatch = args => args.Header.ToLower(),
            IgnoreBlankLines = true,
        };

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<ContactInfoMap>();

        var records = CsvReadRecords<ContactInfo>(csv);
        int totalProcessed = 0;

        IEnumerable<List<(ContactInfo info, bool insert)>> Pipe2TransformBatches(IEnumerable<ContactInfo> models)
        {
            var currentBatch = new List<(ContactInfo info, bool insert)>(context.BatchSize);

            foreach (var item in models)
            {
                try
                {
                    currentBatch.Add((item, !contactGuids.Contains(item.ContactGUID)));
                }
                catch (Exception ex)
                {
                    onErrorCallbackAsync?.Invoke(ex);
                    continue;
                }
                finally
                {
                    totalProcessed += 1;

                    if (totalProcessed % context.BatchSize == 0)
                    {
                        Task.Run(async () => await onResultCallbackAsync.Invoke([], totalProcessed));
                    }
                }

                if (currentBatch.Count >= context.BatchSize)
                {
                    yield return currentBatch;
                    currentBatch = [];
                }
            }

            if (currentBatch.Count < context.BatchSize && currentBatch.Count != 0)
            {
                Task.Run(async () => await onResultCallbackAsync.Invoke([], totalProcessed));
            }

            yield return currentBatch;
        }

        using (new CMSActionContext
        {
            LogEvents = false,
        })
        {
            // we cannot use ContactInfoProvider.ProviderObject.BulkInsertInfos - insert is not immediate (all items stored in memory before insert) so direct piping is not possible 

            foreach (var contactBatch in Pipe2TransformBatches(records))
            {
                contactInfoProvider.BulkInsert(contactBatch.Where(x => x.insert).Select(x => x.info), new BulkInsertSettings
                {
                    // BatchSize = context.BatchSize,
                    Options = SqlBulkCopyOptions.Default,
                });

                if (group != null)
                {
                    // cannot employ async insert, it is not stable (bricks bulk contact sql connection)
                    await InsertGroupMembersAsync(contactBatch.Select(x => x.info.ContactGUID), group);
                }
            }
        }
    }

    private IEnumerable<T> CsvReadRecords<T>(CsvReader csv)
    {
        while (csv.Read())
        {
            string? rawLine = csv.Context.Parser?.RawRecord;

            if (string.IsNullOrWhiteSpace(rawLine) || rawLine.Trim('\0', ' ', '\r', '\n', '\t') == string.Empty)
            {
                yield break;
            }

            yield return csv.GetRecord<T>();
        }
    }


    private static Task InsertGroupMembersAsync(IEnumerable<Guid> contactGuids, ContactGroupInfo group) =>
        Task.Run(() =>
        {
            string query = @"
INSERT INTO [dbo].[OM_ContactGroupMember] ([ContactGroupMemberContactGroupID], [ContactGroupMemberType], [ContactGroupMemberRelatedID],
                                           [ContactGroupMemberFromCondition], [ContactGroupMemberFromAccount], [ContactGroupMemberFromManual])
-- OUTPUT [inserted].[ContactGroupMemberContactGroupID]
SELECT @contactGroupId [ContactGroup], @contactGroupMemberType AS [ContactGroupMemberType], [C].[ContactID], NULL, NULL, 1 [ContactGroupMemberFromManual]
FROM [dbo].[OM_Contact] [C]
WHERE EXISTS (SELECT 1
              FROM OPENJSON(@jsonGuidArray, '$')
              WHERE CAST([value] AS UNIQUEIDENTIFIER) = [C].[ContactGUID])
      AND NOT EXISTS (SELECT 1
          FROM [dbo].[OM_ContactGroupMember] [CGM]
          WHERE [CGM].[ContactGroupMemberContactGroupID] = @contactGroupId
            AND [CGM].[ContactGroupMemberRelatedID] = [C].[ContactID])
";

            string jsonGuidArray = JsonConvert.SerializeObject(contactGuids);

            ConnectionHelper.ExecuteNonQuery(query,
            [
                new("contactGroupId", group.ContactGroupID),
                new("jsonGuidArray", jsonGuidArray),
                new("contactGroupMemberType", (int)ContactGroupMemberTypeEnum.Contact),
            ], QueryTypeEnum.SQLQuery);
        });

    private Task DeletedContactsAsync(List<Guid> contactGuids, int batchLimit)
    {
        // for future implementation of bulk delete
#pragma warning disable S125
        // var query = @"WITH [CTE]([Guid])
        //  AS
        //  (SELECT CAST([l].[value] AS UNIQUEIDENTIFIER) [Guid]
        //   FROM OPENJSON('@jsonGuidArray', '$') [l])
        // DELETE
        // FROM [dbo].[OM_Contact]
        // OUTPUT [deleted].[ContactID]
        // WHERE EXISTS (SELECT 1 FROM [CTE] WHERE [CTE].[Guid] = [ContactGUID])";
#pragma warning restore S125
        if (contactGuids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(() =>
        {
            string jsonGuidArray = JsonConvert.SerializeObject(contactGuids);
            string whereCondition = $"""
EXISTS (SELECT 1 FROM OPENJSON('{jsonGuidArray}', '$') [l] WHERE CAST([l].[value] AS UNIQUEIDENTIFIER) = [ContactGUID])
""";

            // if results are needed we can run SP ([dbo].[Proc_OM_Contact_MassDelete]) manually, it returns list of deleted contacts
            contactsDeleteService.BulkDelete(whereCondition, batchLimit);
        });
    }
}
