using CMS.ContactManagement;
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

    private class ContactModel
    {
        public Guid ContactGuid { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
    }

    protected override async Task<ICommandResponse> ProcessFormData(ContactsImportModel model,
        ICollection<IFormItem> formItems)
    {
        var items = GetContacts();

        // Custom logic that processes the submitted data
        // e.g., sends an email using IEmailClient
        // var result = await _emailClient.SendEmail(new EmailMessage
        // {
        //     From = "admin@site.com",
        //     Recipients = model.Recipients,
        //     Subject = model.Subject,
        //     Body = model.Body
        // });

        //var contacts = model.Contacts;

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

        var list = new List<string>()
        {
            "one",
            "two"
        };

        //1. nacti dictionary guids, contactid pro parovaci klice -> ContactGuid

        // var group = ContactGroupInfoProvider.ProviderObject.Get("");
        //
        //
        // foreach (var item in list)
        // {
        //     //create - bulk insert po 1000
        //     //update - continue
        //     //delete - bulk delete po 1000
        //
        //     var provider = Provider<ContactInfo>.Instance;
        //
        //     provider.Set(new ContactInfo());
        //
        //     provider.Delete(new ContactInfo());
        //
        //     ContactGroupMemberInfo.Provider.Set(new ContactGroupMemberInfo()
        //     {
        //         ContactGroupMemberType = ContactGroupMemberTypeEnum.Contact,
        //         //ContactGroupMemberID = 1334, //unique
        //         ContactGroupMemberRelatedID = 1, //contact ID
        //         ContactGroupMemberFromManual = true,
        //         ContactGroupMemberContactGroupID = group.ContactGroupID
        //     });
        // }

        if (!string.IsNullOrWhiteSpace(model.Mode) && model.Mode.Equals(ContactImporterConstants.ModeUpsert,
                StringComparison.InvariantCultureIgnoreCase))
        {
            var groupId = Guid.Parse(model.ContactGroup);
            var group = await _contactGroupInfoProvider.GetAsync(groupId);

            if (group == null)
            {
                var groupResponse = ResponseFrom(new FormSubmissionResult(FormSubmissionStatus.ValidationFailure)
                {
                    // Returns the submitted field values to the client (repopulates the form)
                    Items = await formItems.OnlyVisible().GetClientProperties()
                });
                
                groupResponse.AddErrorMessage("Unable to find the provided contact group.");

                return groupResponse;
            }

            var contactProvider = Provider<ContactInfo>.Instance;

            foreach (var item in items)
            {
                //TODO 6/21/2023 PavelHess: rewrite to check existence against pre-loaded dictionary
                var contact = contactProvider.Get().WhereEquals(nameof(ContactInfo.ContactGUID), item.ContactGuid)
                    .FirstOrDefault();

                if (contact != null)
                    continue;

                contact = new ContactInfo()
                {
                    ContactGUID = item.ContactGuid,
                    ContactFirstName = item.FirstName,
                    ContactLastName = item.LastName,
                    ContactEmail = item.Email
                };
                
                contactProvider.Set(contact);
            }
        }

        var result = true;

        // Initializes a client response
        var response = ResponseFrom(new FormSubmissionResult(result
            ? FormSubmissionStatus.ValidationSuccess
            : FormSubmissionStatus.ValidationFailure)
        {
            // Returns the submitted field values to the client (repopulates the form)
            Items = await formItems.OnlyVisible().GetClientProperties()
        });

        if (result)
        {
            //response.AddSuccessMessage($"Contacts upserted {lineIndex}.");
        }
        else
        {
            response.AddErrorMessage(string.Format("Email sending failed."));
        }

        return response;
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