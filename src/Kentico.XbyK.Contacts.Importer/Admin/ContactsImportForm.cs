using CMS.EmailEngine;
using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.Forms;
using Kentico.Xperience.Contacts.Importer.Models;

namespace Kentico.Xperience.Contacts.Importer.Admin;

public class ContactsImportForm : ModelEditPage<ContactsImportModel>
{
    private ContactsImportModel _model;

    /// <summary>
    /// Mandatory constructor providing instances of required services to the base class
    /// Can be used to initialize additional dependencies via dependency injection
    /// </summary>
    /// <param name="formItemCollectionProvider"></param>
    /// <param name="formDataBinder"></param>
    public ContactsImportForm(IFormItemCollectionProvider formItemCollectionProvider, IFormDataBinder formDataBinder)
        : base(formItemCollectionProvider, formDataBinder)
    {
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
    
    protected override async Task<ICommandResponse> ProcessFormData(ContactsImportModel model, ICollection<IFormItem> formItems)
    {
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
        var lineIndex = -1;
        var contacts = new List<ContactImportModel>();

        using (var reader = new StringReader(model.Contacts))
        {
            while (reader.Peek() > -1)
            {
                var line = await reader.ReadLineAsync();
                lineIndex++;

                if (lineIndex == 0)
                {
                    var header = line;
                    
                    continue;
                }

                if (string.IsNullOrEmpty(line))
                    continue;

                var properties = line.Split(';').ToList();
                
                var contact = new ContactImportModel()
                {
                    Email = properties[2],
                    LineIndex = lineIndex
                };

                contacts.Add(contact);
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
            response.AddSuccessMessage($"Contacts upserted {lineIndex}.");
        }
        else
        {
            response.AddErrorMessage(string.Format("Email sending failed."));
        }

        return response;
    }
}