using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Labs.ContactsImporter.Admin;

/*
 * This file demonstrates a UI page based on a custom template (CustomLayoutTemplate.tsx).
 * The page defines a single page command that returns the server's DateTime.Now value.
 */

// Defines a new application and registers its root page under <webappdomain>/admin/contacts-importer
[assembly: UIApplication(
    "Kentico.Xperience.Labs.ContactsImporter.Admin.CustomTemplate",
    typeof(CustomTemplate),
    "contacts-importer",
    "Contacts Importer",
    ContactsImporterAdminModule.CUSTOM_CATEGORY,
    Icons.Clock,
    "@kentico/xperience-labs-contacts-importer-admin/CustomLayout")]

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

internal class CustomTemplate : Page<CustomLayoutProperties>
{
    // Sets default property values for the client template (CustomLayoutTemplate.tsx)
    public override Task<CustomLayoutProperties> ConfigureTemplateProperties(CustomLayoutProperties properties)
    {
        properties.Label = "Click the button to get server time.";
        return Task.FromResult(properties);
    }

    // Registers the 'SetLabel' page command that can be invoked from the client template
    [PageCommand]
    public Task<ResponseResult> SetLabel() => Task.FromResult(new ResponseResult(DateTime.Now.ToString()));
}

// Data object encapsulating page command response
internal readonly record struct ResponseResult(string Label);

// Defines the properties of the client template (CustomLayoutTemplate.tsx)
internal class CustomLayoutProperties : TemplateClientProperties
{
    public string Label { get; set; } = "";
}
