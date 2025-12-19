using System.Data;
using System.Globalization;

using CMS.Base;
using CMS.ContactManagement;
using CMS.DataEngine;
using CMS.EmailMarketing;

using CsvHelper;
using CsvHelper.Configuration;

using Microsoft.Data.SqlClient;


namespace Kentico.Xperience.Contacts.Importer.Services.ImportService;

/// <inheritdoc />
public class ImportService(
    IInfoProvider<ContactGroupInfo> contactGroupInfoProvider,
    IInfoProvider<ContactInfo> contactInfoProvider,
    IInfoProvider<ContactGroupMemberInfo> contactGroupMemberInfoProvider,
    IInfoProvider<EmailSubscriptionConfirmationInfo> emailSubscriptionConfirmationInfoProvider,
    IContactsBulkDeletionService contactsBulkDeletionService) : IImportService
{
    /// <summary>
    /// Defines how ContactInfo columns will be mapped from CSV.
    /// </summary>
    public sealed class ContactInfoMap : ClassMap<ContactInfo>
    {
        /// <summary>
        /// Defines import map for <c>ContactInfo.TYPEINFO.ColumnNames</c>.
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
#pragma warning disable S3459
#pragma warning disable S1144
        // ReSharper disable once InconsistentNaming // kentico naming convention
        public Guid ContactGUID { get; set; }
#pragma warning restore S3459
#pragma warning restore S1144
    }


    private sealed class SimplifiedMap : ClassMap<ContactDeleteArgument>
    {
#pragma warning disable S1144
        public SimplifiedMap() => Map(m => m.ContactGUID);
#pragma warning restore S1144
    }


    /// <inheritdoc />
    /// <exception cref="Exception">Thrown when contact group is missing.</exception>
    public async Task RunImport(
        Stream csvStream,
        ImportContext context,
        Func<List<ImportResult>, int, Task> onResultCallbackAsync,
        Func<Exception, Task> onErrorCallbackAsync)
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


    private async Task BulkDeleteContactFromCsvAsync(
        Stream csvStream,
        ImportContext context,
        Func<List<ImportResult>, int, Task> onResultCallbackAsync,
        Func<Exception, Task> onErrorCallbackAsync)
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
                        // we are not concerned here that totalProcessed is captured from foreign closure
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


    private void InsertContactGroupBindings(ContactGroupInfo group, IEnumerable<ContactInfo> importedContacts)
    {
        var currentDateTime = DateTime.Now;

        List<ContactGroupMemberInfo> groupMemberList = [];
        List<EmailSubscriptionConfirmationInfo> subscriptionConfirmationList = [];

        var existingGroupMemberIdSubquery = contactGroupMemberInfoProvider.Get()
            .WhereEquals(nameof(ContactGroupMemberInfo.ContactGroupMemberType), ContactGroupMemberTypeEnum.Contact)
            .WhereEquals(nameof(ContactGroupMemberInfo.ContactGroupMemberContactGroupID), group.ContactGroupID)
            .Column(nameof(ContactGroupMemberInfo.ContactGroupMemberRelatedID));

        var contactIDs = contactInfoProvider.Get()
            .WhereNotIn(nameof(ContactInfo.ContactID), existingGroupMemberIdSubquery)
            .WhereIn(nameof(ContactInfo.ContactGUID), importedContacts.Select(x => x.ContactGUID))
            .Column(nameof(ContactInfo.ContactID))
            .GetListResult<int>();

        foreach (int contactID in contactIDs)
        {
            groupMemberList.Add(new ContactGroupMemberInfo
            {
                ContactGroupMemberContactGroupID = group.ContactGroupID,
                ContactGroupMemberType = ContactGroupMemberTypeEnum.Contact,
                ContactGroupMemberRelatedID = contactID,
                ContactGroupMemberFromManual = true,
                ContactGroupMemberFromCondition = false,
            });

            if (group.ContactGroupIsRecipientList)
            {
                subscriptionConfirmationList.Add(new EmailSubscriptionConfirmationInfo
                {
                    EmailSubscriptionConfirmationContactID = contactID,
                    EmailSubscriptionConfirmationRecipientListID = group.ContactGroupID,
                    EmailSubscriptionConfirmationIsApproved = true,
                    EmailSubscriptionConfirmationDate = currentDateTime,
                });
            }
        }

        contactGroupMemberInfoProvider.BulkInsert(groupMemberList);

        if (group.ContactGroupIsRecipientList)
        {
            emailSubscriptionConfirmationInfoProvider.BulkInsert(subscriptionConfirmationList);
        }
    }


    private async Task InsertContactsFromCsvAsync(
        Stream csvStream,
        ImportContext context,
        Func<List<ImportResult>, int, Task> onResultCallbackAsync,
        Func<Exception, Task> onErrorCallbackAsync)
    {
        ContactGroupInfo? group = null;
        ContactGroupInfo? recipientList = null;

        if (context.AssignToContactGroupGuid is { } assignToContactGroupGuid)
        {
            group = await contactGroupInfoProvider.GetAsync(assignToContactGroupGuid);
            if (group is null)
            {
                throw new ArgumentException("Contact group not found", nameof(context));
            }
        }
        if (context.AssignToRecipientListGuid is { } assignToRecipientListGuid)
        {
            recipientList = await contactGroupInfoProvider.GetAsync(assignToRecipientListGuid);

            if (recipientList is null)
            {
                throw new ArgumentException("Recipient list not found", nameof(context));
            }
        }

        var contactGuids = contactInfoProvider.Get()
            .Column(nameof(ContactInfo.ContactGUID))
            .GetListResult<Guid>()
            .ToHashSet();

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
            foreach (var contactBatch in Pipe2TransformBatches(records))
            {
                contactInfoProvider.BulkInsert(contactBatch.Where(x => x.insert).Select(x => x.info), new BulkInsertSettings
                {
                    // BatchSize = context.BatchSize,
                    Options = SqlBulkCopyOptions.Default,
                });

                if (group is not null)
                {
                    InsertContactGroupBindings(group, contactBatch.Select(x => x.info));
                }

                if (recipientList is not null)
                {
                    InsertContactGroupBindings(recipientList, contactBatch.Select(x => x.info));
                }
            }
        }
    }


    private static IEnumerable<T> CsvReadRecords<T>(CsvReader csv)
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


    private Task DeletedContactsAsync(List<Guid> contactGuids, int batchLimit)
    {
        if (contactGuids.Count == 0)
        {
            return Task.CompletedTask;
        }

        return Task.Run(async () =>
        {
            var whereCondition = new WhereCondition()
                .WhereIn(nameof(ContactInfo.ContactGUID), contactGuids);
            // if results are needed we can run SP ([dbo].[Proc_OM_Contact_MassDelete]) manually, it returns list of deleted contacts
            await contactsBulkDeletionService.BulkDelete(whereCondition, batchLimit);
        });
    }
}
