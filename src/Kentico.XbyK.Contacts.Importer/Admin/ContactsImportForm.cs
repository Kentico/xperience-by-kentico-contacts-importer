﻿using CMS.ContactManagement;
using CMS.DataEngine.Internal;
using CMS.MediaLibrary;
using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.Forms;
using Kentico.Xperience.Contacts.Importer.Models;

namespace Kentico.Xperience.Contacts.Importer.Admin;

public class ContactsImportForm : ModelEditPage<ContactsImportModel>
{
    private readonly IMediaFileInfoProvider _mediaFileInfoProvider;
    private readonly IMediaLibraryInfoProvider _mediaLibraryInfoProvider;
    private readonly IContactGroupInfoProvider _contactGroupInfoProvider;

    private ContactsImportModel _model;

    /// <summary>
    /// Mandatory constructor providing instances of required services to the base class
    /// Can be used to initialize additional dependencies via dependency injection
    /// </summary>
    /// <param name="formItemCollectionProvider"></param>
    /// <param name="formDataBinder"></param>
    public ContactsImportForm(IFormItemCollectionProvider formItemCollectionProvider, IFormDataBinder formDataBinder,
        IMediaFileInfoProvider mediaFileInfoProvider, IMediaLibraryInfoProvider mediaLibraryInfoProvider, IContactGroupInfoProvider contactGroupInfoProvider)
        : base(formItemCollectionProvider, formDataBinder)
    {
        _mediaFileInfoProvider = mediaFileInfoProvider;
        _mediaLibraryInfoProvider = mediaLibraryInfoProvider;
        _contactGroupInfoProvider = contactGroupInfoProvider;
    }

    protected override ContactsImportModel Model
    {
        get
        {
            if (_model is null)
            {
                _model = new ContactsImportModel()
                {
                    // Initializes the 'Recipients' field to a default value
                    //Recipients = "administrator@admin.com"
                    Contacts = "ContactID;ContactGuid;Email;FirstName;LastName",
                    Mode = "delete"
                };
            }

            return _model;
        }
    }

    protected override async Task<ICommandResponse> ProcessFormData(ContactsImportModel model,
        ICollection<IFormItem> formItems)
    {
        var items = GetContacts();

        // var contactFile = new ObjectQuery<MediaFileInfo>().ForAssets(model.File).GetEnumerableTypedResult()
        //     ?.FirstOrDefault();
        //
        // var fileId = model.File.FirstOrDefault().Identifier;
        //
        // var media = _mediaFileInfoProvider.Get(fileId, 1);
        //
        // var library = _mediaLibraryInfoProvider.Get(media.FileLibraryID);
        //
        // var libraryFolderPath = MediaLibraryInfoProvider.GetMediaLibraryFolderPath(library);
        // var fullPath = Path.GetFullPath(CMS.IO.Path.EnsureSlashes(Path.Combine(libraryFolderPath, library.LibraryFolder)));

        if (!string.IsNullOrWhiteSpace(model.Mode) && model.Mode.Equals(ContactImporterConstants.ModeUpsert,
                StringComparison.InvariantCultureIgnoreCase))
        {
            return await ProcessUpsert(model, formItems, items);
        }

        if (!string.IsNullOrWhiteSpace(model.Mode) && model.Mode.Equals(ContactImporterConstants.ModeDelete,
                StringComparison.InvariantCultureIgnoreCase))
        {
            return await ProcessDelete(formItems, items);
        }

        return await GetResponse(formItems, FormSubmissionStatus.ValidationFailure,
            $"Unexpected mode provided: {model.Mode}.");
    }

    private async Task<ICommandResponse> ProcessUpsert(ContactsImportModel model, ICollection<IFormItem> formItems, List<ContactModel> items)
    {
        var groupId = Guid.Parse(model.ContactGroup);
        var group = await _contactGroupInfoProvider.GetAsync(groupId);

        if (group == null)
        {
            return await GetResponse(formItems, FormSubmissionStatus.ValidationFailure,
                "Unable to find the provided contact group.");
        }

        var contactProvider = Provider<ContactInfo>.Instance;

        var existingContactsMapping = contactProvider.Get()
            .Columns(nameof(ContactInfo.ContactGUID), nameof(ContactInfo.ContactID))
            .AsEnumerable()
            .ToDictionary(key => key.ContactGUID, value => value.ContactID);

        var existingGroupMembersMapping = Provider<ContactGroupMemberInfo>.Instance.Get()
            .Columns(nameof(ContactGroupMemberInfo.ContactGroupMemberRelatedID))
            .WhereEquals(nameof(ContactGroupMemberInfo.ContactGroupMemberRelatedID), group.ContactGroupID)
            .WhereEquals(nameof(ContactGroupMemberInfo.ContactGroupMemberType), ContactGroupMemberTypeEnum.Contact)
            .AsEnumerable()
            .Select(i => i.ContactGroupMemberRelatedID)
            .ToHashSet();

        var contactIds = new List<int>();

        foreach (var item in items)
        {
            var contactExists = existingContactsMapping.ContainsKey(item.ContactGuid);
            var contactId = contactExists ? existingContactsMapping[item.ContactGuid] : default(int?);

            if (!contactExists)
            {
                var contact = new ContactInfo()
                {
                    ContactGUID = item.ContactGuid,
                    ContactID = item.ContactId,
                    ContactFirstName = item.FirstName,
                    ContactLastName = item.LastName,
                    ContactEmail = item.Email
                };

                contactProvider.Set(contact);

                contactIds.Add(contact.ContactID);
                contactId = contact.ContactID;
            }

            if (contactId.HasValue && !existingGroupMembersMapping.Contains(contactId.Value))
            {
                ContactGroupMemberInfo.Provider.Set(new ContactGroupMemberInfo()
                {
                    ContactGroupMemberType = ContactGroupMemberTypeEnum.Contact,
                    ContactGroupMemberRelatedID = contactId.Value,
                    ContactGroupMemberFromManual = true,
                    ContactGroupMemberContactGroupID = group.ContactGroupID
                });
            }
        }

        return await GetResponse(formItems, FormSubmissionStatus.ValidationSuccess,
            $"Contacts upserted {contactIds.Count}.");
    }

    private async Task<ICommandResponse> ProcessDelete(ICollection<IFormItem> formItems, List<ContactModel> items)
    {
        var count = 0;

        var contactProvider = Provider<ContactInfo>.Instance;

        var existingContacts = contactProvider.Get().Columns(nameof(ContactInfo.ContactID))
            .WhereIn(nameof(ContactInfo.ContactGUID), items.Select(i => i.ContactGuid).ToList())
            .AsEnumerable()
            .Select(i => i.ContactID)
            .ToList();

        foreach (var item in existingContacts)
        {
            contactProvider.Delete(new ContactInfo() { ContactID = item });
            count++;
        }

        return await GetResponse(formItems, FormSubmissionStatus.ValidationSuccess, $"Contacts deleted {count}.");
    }

    private async Task<ICommandResponse> GetResponse(ICollection<IFormItem> formItems, FormSubmissionStatus status, string message)
    {
        var upsertResult = ResponseFrom(new FormSubmissionResult(status)
        {
            Items = await formItems.OnlyVisible().GetClientProperties()
        });

        if (string.IsNullOrWhiteSpace(message))
            upsertResult.AddSuccessMessage(message);

        return upsertResult;
    }

    private class ContactModel
    {
        public Guid ContactGuid { get; set; }
        public int ContactId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
    }

    private List<ContactModel> GetContacts()
    {
        return new List<ContactModel>
        {
            new()
            {
                ContactGuid = Guid.Parse("EAD6AFC2-0A06-4343-8586-E6B7FAB8C0E9"),
                FirstName = "Jan",
                LastName = "Novák",
                Email = "novak@test.fff"
            },
            new()
            {
                ContactGuid = Guid.Parse("5A908E81-75BC-4290-B75A-FFA40440C2CC"),
                FirstName = "Josef",
                LastName = "jasný",
                Email = "jasny@test.fff"
            },
            new()
            {
                ContactGuid = Guid.Parse("DBB1E962-2068-46DD-88F8-C712AF407CCB"),
                FirstName = "eee",
                LastName = "",
                Email = "bleee@test.fff"
            },
            new()
            {
                ContactGuid = Guid.Parse("564945EC-6C7E-4A10-921A-398AEBA5EF11"),
                FirstName = "ddd",
                LastName = "aaa",
                Email = "aa-bbb@test.fff"
            },
            new()
            {
                ContactGuid = Guid.Parse("A8A74F31-A26A-4B92-B15C-5BF60A1EFD96"),
                FirstName = "John",
                LastName = "doe",
                Email = "doe@test.fff"
            },
            new()
            {
                ContactGuid = Guid.Parse("1F1C5E7E-068A-4E37-ABC6-88EA4B385600"),
                FirstName = "Tim",
                LastName = "Phillips",
                Email = "phil@test.fff"
            },
            new()
            {
                ContactGuid = Guid.Parse("4BA80E54-8CBD-4BA4-B93D-C6740360697B"),
                FirstName = "Yan",
                LastName = "Harris",
                Email = "harris@test.fff"
            }
        };
    }
}