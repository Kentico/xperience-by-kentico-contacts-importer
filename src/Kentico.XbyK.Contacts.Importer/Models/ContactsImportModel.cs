using Kentico.Xperience.Admin.Base.FormAnnotations;

namespace Kentico.Xperience.Contacts.Importer.Models;

public class ContactsImportModel
{
    // [EmailValidationRule(AllowMultipleAddresses = true)]
    // [TextInputComponent(Label = "To", Order = 1, Tooltip = "Email receiver(s). Supports multiple addresses separated by semicolons.")]
    // public string Recipients { get; set; }

    [RequiredValidationRule]
    [TextAreaComponent(Label = "Contacts", Order = 1)]
    public string Contacts { get; set; }
    
    [RequiredValidationRule()]
    [RadioGroupComponent(Inline = false, Order = 2, Label = "Mode",
        Options = $"upsert;Upsert\r\ndelete;Delete\r\n")]
    public string Mode { get; set; }

    // [TextAreaComponent(Label = "Body", Order = 3)]
    // [VisibleIfNotEmpty(nameof(Subject))]
    // public string Body { get; set; }

    //[DropDownComponent(Label = "Contact group", Order = 4, Options = $"submit;Submit\r\nsubmitandlog;Submit and log\r\n")]
    
    
    [VisibleIfNotEqualTo(nameof(Mode), "delete")]
    [RequiredValidationRule]
    [DropDownComponent(Label = "Contact group", Order = 4, DataProviderType = typeof(ContactGroupDataProvider))]
    public string ContactGroup { get; set; }
}