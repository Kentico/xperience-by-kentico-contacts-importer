using CMS;
using Kentico.Xperience.Admin.Base;
using Kentico.Xperience.Admin.Base.UIPages;
using Kentico.Xperience.Contacts.Importer.Admin;

[assembly: AssemblyDiscoverable]

// UI applications
[assembly: UIApplication(ContactsImporterApplication.IDENTIFIER, typeof(ContactsImporterApplication), "contacts-importer", "{$contacts.importer.applicationname$}", BaseApplicationCategories.DEVELOPMENT, Icons.Users, TemplateNames.SECTION_LAYOUT)]

[assembly: UIPage(typeof(ContactsImporterApplication), "import", typeof(ContactsImportForm), "{$contacts.importer.edit$}", TemplateNames.EDIT, UIPageOrder.First)]
