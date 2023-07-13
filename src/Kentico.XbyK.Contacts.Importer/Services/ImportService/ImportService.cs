namespace Kentico.Xperience.Contacts.Importer.Services;

using System.Data;
using System.Globalization;
using CMS.Base;
using CMS.ContactManagement;
using CMS.DataEngine;
using CMS.Helpers.UniGraphConfig;
using CsvHelper;
using CsvHelper.Configuration;
using CsvHelper.Configuration.Attributes;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;

/// <inheritdoc />
public class ImportService : IImportService
{
    private readonly IContactGroupInfoProvider _contactGroupInfoProvider;

    /// <param name="contactGroupInfoProvider"></param>
    public ImportService(IContactGroupInfoProvider contactGroupInfoProvider)
    {
        _contactGroupInfoProvider = contactGroupInfoProvider;
    }

    public class ContactModel
    {
        [Name("ContactGUID")]
        public Guid ContactGUID { get; set; }

        // public int ContactID { get; set; }
        [Name("ContactFirstName")]
        public string ContactFirstName { get; set; }

        [Name("ContactLastName")]
        public string ContactLastName { get; set; }

        [Name("ContactEmail")]
        public string ContactEmail { get; set; }
    }

    public sealed class ContactInfoMap : ClassMap<ContactInfo>
    {
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
            // ContactInfo.TYPEINFO.ColumnNames
        }
    }

    private class ContactDeleteArgument
    {
        public Guid ContactGUID { get; set; }
    };

    private sealed class SimplifiedMap : ClassMap<ContactDeleteArgument>
    {
        public SimplifiedMap()
        {
            Map(m => m.ContactGUID);
        }
    }

    /// <exception cref="Exception">Thrown when contact group is missing</exception>
    /// <inheritdoc />
    public async Task RunImport(Stream csvStream, ImportContext context, Func<List<ImportResult>, int, Task> onResultCallbackAsync)
    {
        switch (context.ImportKind)
        {
            case ImportKind.InsertAndSkipExisting:
            {
                await InsertContactsFromCsvAsync(csvStream, context, onResultCallbackAsync);
                break;
            }
            case ImportKind.Delete:
            {
                await BulkDeleteContactFromCsvAsync(csvStream, context, onResultCallbackAsync);
                break;
            }
            default:
            {
                throw new InvalidOperationException($"Unknown import kind '{context.ImportKind}'");
            }
        }
    }

    private async Task BulkDeleteContactFromCsvAsync(Stream csvStream, ImportContext context, Func<List<ImportResult>, int, Task> onResultCallbackAsync)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = context.Delimiter, //";" // TODO tomas.krch: 2023-07-12 to config/dialog
            PrepareHeaderForMatch = args => args.Header.ToLower(),
        };

        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<SimplifiedMap>();

        var records = csv.GetRecords<ContactDeleteArgument>();

        var totalProcessed = 0;

        async IAsyncEnumerable<List<Guid>> Pipe2TransformBatches(IEnumerable<ContactDeleteArgument> models)
        {
            var currentBatch = new List<Guid>(context.BatchSize);
            foreach (var item in models)
            {
                try
                {
                    // if (contactGuids.Contains(item.ContactGUID))
                    // {
                    //     continue;
                    // }

                    currentBatch.Add(item.ContactGUID);
                }
                catch (Exception ex)
                {
                    // TODO tomas.krch: 2023-07-14 report error
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
                        Task.Run(async () => { await onResultCallbackAsync.Invoke(null, totalProcessed); });
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

    private async Task InsertContactsFromCsvAsync(Stream csvStream, ImportContext context, Func<List<ImportResult>, int, Task> onResultCallbackAsync)
    {
        ContactGroupInfo? group = null;
        if (context.AssignToContactGroupGuid is { } assignToContactGroupGuid)
        {
            group = await _contactGroupInfoProvider.GetAsync(assignToContactGroupGuid);
            if (group == null)
            {
                throw new("Contact group not found");
            }
        }


        // var contactProvider = Provider<ContactInfo>.Instance;


        var contactGuids = ConnectionHelper.ExecuteQuery(@"SELECT ContactGUID FROM OM_Contact", new QueryDataParameters(), QueryTypeEnum.SQLQuery)
            .Tables[0].AsEnumerable().Select(x => (Guid)x[nameof(ContactInfo.ContactGUID)]).ToHashSet();

        // var existingContactsMapping = contactProvider.Get()
        //     .Columns(nameof(ContactInfo.ContactGUID))
        //     .Select(x => x.ContactGUID)
        //     .ToHashSet();

        // var existingGroupMembersMapping = group != null
        //     ? Provider<ContactGroupMemberInfo>.Instance.Get()
        //         .Columns(nameof(ContactGroupMemberInfo.ContactGroupMemberRelatedID))
        //         .WhereEquals(nameof(ContactGroupMemberInfo.ContactGroupMemberRelatedID), group.ContactGroupID)
        //         .WhereEquals(nameof(ContactGroupMemberInfo.ContactGroupMemberType), ContactGroupMemberTypeEnum.Contact)
        //         .AsEnumerable()
        //         .Select(i => i.ContactGroupMemberRelatedID)
        //         .ToHashSet()
        //     : null;

        // var contactIds = new List<int>();

        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            Delimiter = context.Delimiter, //";" // TODO tomas.krch: 2023-07-12 to config/dialog
            PrepareHeaderForMatch = args => args.Header.ToLower(),
        };


        using var reader = new StreamReader(csvStream);
        using var csv = new CsvReader(reader, config);

        csv.Context.RegisterClassMap<ContactInfoMap>();

        var records = csv.GetRecords<ContactInfo>();
        var contactsToProcess = new List<ContactInfo>();
        // var contactsIds = new List<int>();

        var totalProcessed = 0;

        IEnumerable<ContactInfo> PipeTransform(IEnumerable<ContactModel> models)
        {
            foreach (var item in models)
            {
                ContactInfo? newContact = null;
                try
                {
                    if (contactGuids.Contains(item.ContactGUID))
                    {
                        continue;
                    }

                    newContact = new ContactInfo
                    {
                        ContactGUID = item.ContactGUID,
                        // ContactID = item.ContactId,
                        ContactFirstName = item.ContactFirstName,
                        ContactLastName = item.ContactLastName,
                        ContactEmail = item.ContactEmail,
                        ContactCreated = DateTime.Now
                    };

                    contactsToProcess.Add(newContact);
                }
                catch (Exception ex)
                {
                    continue;
                }
                finally
                {
                    totalProcessed += 1;
                    if (totalProcessed % context.BatchSize == 0)
                    {
                        Task.Run((Func<Task?>)(async () => { await onResultCallbackAsync.Invoke(contactsToProcess.Select(x => new ImportResult(true, x.ContactGUID, null)).ToList(), totalProcessed); }));
                    }
                }

                if (newContact != null)
                {
                    yield return newContact;
                }
            }
        }

        IEnumerable<ContactInfo> Pipe2Transform(IEnumerable<ContactInfo> models)
        {
            foreach (var item in models)
            {
                // ContactInfo? newContact = null;
                try
                {
                    if (contactGuids.Contains(item.ContactGUID))
                    {
                        continue;
                    }
                    //
                    // newContact = new ContactInfo
                    // {
                    //     ContactGUID = item.ContactGUID,
                    //     // ContactID = item.ContactId,
                    //     ContactFirstName = item.ContactFirstName,
                    //     ContactLastName = item.ContactLastName,
                    //     ContactEmail = item.ContactEmail,
                    //     ContactCreated = DateTime.Now
                    // };
                    //
                    // contactsToProcess.Add(newContact);
                }
                catch (Exception ex)
                {
                    continue;
                }
                finally
                {
                    totalProcessed += 1;
                    if (totalProcessed % context.BatchSize == 0)
                    {
                        Task.Run((Func<Task?>)(async () => { await onResultCallbackAsync.Invoke(null, totalProcessed); }));
                    }
                }

                yield return item;
            }
        }

        IEnumerable<List<ContactInfo>> Pipe2TransformBatches(IEnumerable<ContactInfo> models)
        {
            var currentBatch = new List<ContactInfo>(context.BatchSize);
            foreach (var item in models)
            {
                // ContactInfo? newContact = null;
                try
                {
                    if (contactGuids.Contains(item.ContactGUID))
                    {
                        continue;
                    }

                    currentBatch.Add(item);
                    //
                    // newContact = new ContactInfo
                    // {
                    //     ContactGUID = item.ContactGUID,
                    //     // ContactID = item.ContactId,
                    //     ContactFirstName = item.ContactFirstName,
                    //     ContactLastName = item.ContactLastName,
                    //     ContactEmail = item.ContactEmail,
                    //     ContactCreated = DateTime.Now
                    // };
                    //
                    // contactsToProcess.Add(newContact);
                }
                catch (Exception ex)
                {
                    continue;
                }
                finally
                {
                    totalProcessed += 1;
                    if (totalProcessed % context.BatchSize == 0)
                    {
                        Task.Run((Func<Task?>)(async () => { await onResultCallbackAsync.Invoke(null, totalProcessed); }));
                    }
                }

                if (currentBatch.Count >= context.BatchSize)
                {
                    yield return currentBatch;
                    currentBatch = new List<ContactInfo>();
                }
            }

            yield return currentBatch;
        }

        using (new CMSActionContext
               {
                   LogEvents = false,
               })
        {
            // insert is not so instance so direct piping is not possible 
            // ContactInfoProvider.ProviderObject.BulkInsertInfos(Pipe2Transform(records), new BulkInsertSettings
            // {
            //     BatchSize = context.BatchSize,
            //     Options = SqlBulkCopyOptions.Default,
            // });

            foreach (var contactBatch in Pipe2TransformBatches(records))
            {
                ContactInfoProvider.ProviderObject.BulkInsertInfos(contactBatch, new BulkInsertSettings
                {
                    // BatchSize = context.BatchSize,
                    Options = SqlBulkCopyOptions.Default,
                });
            }
        }


        // foreach (var item in records)
        // {
        //     var contactExists = existingContactsMapping.ContainsKey(item.ContactGUID);
        //     //var contactId = contactExists ? existingContactsMapping[item.ContactGuid] : default(int?);
        //
        //     if (!contactExists)
        //     {
        //         var contact = new ContactInfo
        //         {
        //             ContactGUID = item.ContactGUID,
        //             // ContactID = item.ContactId,
        //             ContactFirstName = item.ContactFirstName,
        //             ContactLastName = item.ContactLastName,
        //             ContactEmail = item.ContactEmail,
        //             ContactCreated = DateTime.Now
        //         };
        //
        //         contactsToProcess.Add(contact);
        //
        //         if (contactsToProcess.Count == context.BatchSize)
        //         {
        //             InsertContacts(contactsToProcess, contactIds, existingGroupMembersMapping);
        //             await onResultCallbackAsync.Invoke(contactsToProcess.Select(x => new ImportResult(true, x.ContactGUID, null)).ToList());
        //             contactsToProcess.Clear();
        //         }
        //     }
        // }

        // InsertContacts(contactsToProcess, contactIds, existingGroupMembersMapping);
        // await onResultCallbackAsync.Invoke(contactsToProcess.Select(x => new ImportResult(true, x.ContactGUID, null)).ToList());
        //
        // if (group != null && contactIds is { Count: > 0 })
        // {
        //     InsertGroupMembers(contactsIds, group);
        // }
    }

    private static void InsertContacts(List<ContactInfo> contactsToProcess, List<int> contactIds, HashSet<int>? existingGroupMembersMapping)
    {
        if (contactsToProcess.Count == 0)
            return;

        ContactInfoProvider.ProviderObject.BulkInsertInfos(contactsToProcess);

        if (existingGroupMembersMapping != null)
        {
            contactIds.AddRange(contactsToProcess
                .Where(i => !existingGroupMembersMapping.Contains(i.ContactID))
                .Select(i => i.ContactID));
        }
    }

    private void InsertGroupMembers(List<int> contactsIds, ContactGroupInfo group)
    {
        foreach (var chunk in contactsIds.Chunk(100))
        {
            var items = chunk.Select(i => new ContactGroupMemberInfo()
            {
                ContactGroupMemberType = ContactGroupMemberTypeEnum.Contact,
                ContactGroupMemberRelatedID = i,
                ContactGroupMemberFromManual = true,
                ContactGroupMemberContactGroupID = group.ContactGroupID
            });

            ContactGroupMemberInfoProvider.ProviderObject.BulkInsertInfos(items);
        }
    }

    private Task DeletedContactsAsync(List<Guid> contactGuids, int batchLimit)
    {
        // for future implementation of bulk delete
        // var query = @"WITH [CTE]([Guid])
        //  AS
        //  (SELECT CAST([l].[value] AS UNIQUEIDENTIFIER) [Guid]
        //   FROM OPENJSON('@jsonGuidArray', '$') [l])
        // DELETE
        // FROM [dbo].[OM_Contact]
        // OUTPUT [deleted].[ContactID]
        // WHERE EXISTS (SELECT 1 FROM [CTE] WHERE [CTE].[Guid] = [ContactGUID])";
        if (contactGuids.Count == 0) return Task.CompletedTask;

        return Task.Run(() =>
        {
            var jsonGuidArray = JsonConvert.SerializeObject(contactGuids);
            var whereCondition = $"""
EXISTS (SELECT 1 FROM OPENJSON('{jsonGuidArray}', '$') [l] WHERE CAST([l].[value] AS UNIQUEIDENTIFIER) = [ContactGUID])
""";

            // if results are needed we can run SP ([dbo].[Proc_OM_Contact_MassDelete]) manually, it returns list of deleted contacts
            ContactInfoProvider.DeleteContactInfos(whereCondition, batchLimit);
        });
    }
}