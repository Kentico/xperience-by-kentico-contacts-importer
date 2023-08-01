
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
    private readonly IContactGroupInfoProvider contactGroupInfoProvider;

    /// <param name="contactGroupInfoProvider"></param>
    public ImportService(IContactGroupInfoProvider contactGroupInfoProvider) => this.contactGroupInfoProvider = contactGroupInfoProvider;

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
            Map(m => m.ContactAge);
            Map(m => m.ContactMiddleName);
        }
    }

    private sealed class ContactDeleteArgument
    {
        // Pragma disable reason: used implicitly
#pragma warning disable S3459
        // ReSharper disable once InconsistentNaming // kentico naming convention
        public Guid ContactGUID { get; }
#pragma warning restore S3459
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
        };

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<SimplifiedMap>();

        var records = csv.GetRecordsAsync<ContactDeleteArgument>();

        int totalProcessed = 0;

        async IAsyncEnumerable<List<Guid>> Pipe2TransformBatches(IAsyncEnumerable<ContactDeleteArgument> models)
        {
            var currentBatch = new List<Guid>(context.BatchSize);

            await foreach (var item in models)
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
                        Task.Run(async () => await onResultCallbackAsync.Invoke(new List<ImportResult>(), totalProcessed));
#pragma warning restore CS4014
                    }
                }

                if (currentBatch.Count >= context.BatchSize)
                {
                    yield return currentBatch;
                    currentBatch = new List<Guid>();
                }
            }

            yield return currentBatch;
        }

        Task? previousDeleteBatch = null;

        await foreach (var deleteArg in Pipe2TransformBatches(records))
        {
            if (previousDeleteBatch != null)
            {
                // await previous
                await previousDeleteBatch;
            }

            // assign existing task and continue with preparation of next
            previousDeleteBatch = DeletedContactsAsync(deleteArg, context.BatchSize);
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
                throw new("Contact group not found");
            }
        }

        var contactGuids = ConnectionHelper.ExecuteQuery(@"SELECT ContactGUID FROM OM_Contact", new QueryDataParameters(), QueryTypeEnum.SQLQuery)
            .Tables[0].AsEnumerable().Select(x => (Guid)x[nameof(ContactInfo.ContactGUID)]).ToHashSet();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = context.Delimiter,
            PrepareHeaderForMatch = args => args.Header.ToLower(),
        };

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<ContactInfoMap>();

        var records = csv.GetRecords<ContactInfo>();
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
                        Task.Run(async () => await onResultCallbackAsync.Invoke(new List<ImportResult>(), totalProcessed));
                    }
                }

                if (currentBatch.Count >= context.BatchSize)
                {
                    yield return currentBatch;
                    currentBatch = new List<(ContactInfo info, bool insert)>();
                }
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
                ContactInfoProvider.ProviderObject.BulkInsertInfos(contactBatch.Where(x => x.insert).Select(x => x.info), new BulkInsertSettings
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

            ConnectionHelper.ExecuteNonQuery(query, new QueryDataParameters
            {
                new("contactGroupId", group.ContactGroupID),
                new("jsonGuidArray", jsonGuidArray),
                new("contactGroupMemberType", (int)ContactGroupMemberTypeEnum.Contact),
            }, QueryTypeEnum.SQLQuery);
        });

    private static Task DeletedContactsAsync(List<Guid> contactGuids, int batchLimit)
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
            ContactInfoProvider.DeleteContactInfos(whereCondition, batchLimit);
        });
    }
}
