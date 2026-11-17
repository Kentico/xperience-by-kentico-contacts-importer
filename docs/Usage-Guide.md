# Usage Guide

## Installation

Add the package to your application:

```powershell
dotnet add package --prerelease Kentico.Xperience.Labs.ContactsImporter.Admin
```

## Registration

Register the services in your application's `Program.cs`:

```csharp
using Kentico.Xperience.Labs.ContactsImporter.Admin;

builder.Services.AddContactsImporter();
```

## Administration

The package adds a **Contacts Importer** application, in a **Contacts Importer** category, at `/admin/contacts-importer`.

## Administration language

The importer shows its texts in the admin UI language of the user and formats numbers, sizes and dates for that language. The package includes English (`en-US`). To add a language, translate both resource files of the package, `ContactsImporterClientResources` (texts of the browser) and `ContactsImporterResources` (messages of the server), and register them for the language as described in [Translate the admin UI](https://docs.kentico.com/documentation/developers-and-admins/customization/admin-ui-localization/translate-the-admin-ui). `dotnet run -- --kxp-export-localization-resources` exports their keys (`contactsimporter.*`) under `en-US/custom/`.

The downloadable row reports keep their column names (`row`, `outcome`, `reason`, ...) and codes in every language, so files can be processed the same way whatever language the user works in. The list of skipped rows the browser creates during the upload has the same fixed column names; its reasons are texts in the user's language.

---The *Usage-Guide.md* file contains full information required to install, set up, and use the library. Organize the file into multiple sections using Markdown headings. If the materials are extensive, separate them into multiple files, with *Usage-Guide.md* acting as an overview with links.---