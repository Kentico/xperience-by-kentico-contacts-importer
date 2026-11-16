# Usage Guide

## Installation

Add the package to your application:

```powershell
dotnet add package Kentico.Xperience.Labs.ContactsImporter.Admin
```

## Registration

Register the services in your application's `Program.cs`:

```csharp
using Kentico.Xperience.Labs.ContactsImporter.Admin;

builder.Services.AddContactsImporter();
```

## Administration

The package adds a **Contacts Importer** application, in a **Contacts Importer** category, at `/admin/contacts-importer`.

---The *Usage-Guide.md* file contains full information required to install, set up, and use the library. Organize the file into multiple sections using Markdown headings. If the materials are extensive, separate them into multiple files, with *Usage-Guide.md* acting as an overview with links.---