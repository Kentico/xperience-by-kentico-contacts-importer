# Xperience by Kentico Contacts Importer

[![CI: Build and Test](https://github.com/Kentico/xperience-by-kentico-contacts-importer/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Kentico/xperience-by-kentico-contacts-importer/actions/workflows/ci.yml)

## About The Project

Enables bulk importing of Contacts into an Xperience by Kentico solution using a `.csv` file.

![View of the Import Upload dashboard](https://raw.githubusercontent.com/Kentico/xperience-by-kentico-contacts-importer/main/images/screenshot-upload.jpg)

## Getting Started

### Prerequisites

- Xperience by Kentico >= 26.3.2

  - <https://docs.xperience.io/xp/changelog#Changelog-Hotfix(June29,2023)>

### Installation

Add the package to your application using the .NET CLI

```powershell
dotnet add package Kentico.Xperience.Contacts.Importer
```

### Add to your application dependencies

```csharp
builder.Services.AddKentico();
// ... other registrations
services.AddContactsImport();
```

### Add to your middleware pipeline

```csharp
app.InitKentico();
// ... other registrations
app.UseContactsImport();
```

## Usage

1. open contact importer application <br>
   ![ContactImporterApplication](./images/ContactImporterApplication.png)
1. select file
1. select mode
   1. Delete - this mode will delete contact by ContactGUID (CSV shall contain column with ContactGUID header)
   1. Insert (skip existing) - this mode will import contacts (CSV shall contain column with ContactGUID)
1. "Assign to contact group" - all contact (existing included) will be assigned to specified group
1. Delimiter - delimiter used for CSV (common are `;`, `,`, `\\t`, ..)
1. "Batch size" - size of batch used for database operation, for instances with limited resources this value should not exeed 5000, lower than 100 is not reccomended.
1. Click "Send file" button

> Depends on resources available to application, but degraded application performance is expected during import.

Notes:

- do not close window with progress, file is uploaded from that window.
- do not manipulate file during import, close all applications write access to file (or lock)

### Supported CSV columns

| Column            | .NET Type | Required |
| ----------------- | --------- | -------- |
| ContactGUID       | Guid      | yes      |
| ContactCreated    | DateTime  | yes      |
| ContactFirstName  | string    | no       |
| ContactLastName   | string    | no       |
| ContactEmail      | string    | no       |
| ContactAge        | Int32     | no       |
| ContactMiddleName | string    | no       |

## Contributing

- .NET SDK >= 7.0.109

  - <https://dotnet.microsoft.com/en-us/download/dotnet/7.0>

- Node.js >= 18.12

  - <https://nodejs.org/en/download>
  - <https://github.com/coreybutler/nvm-windows>

For Contributing please see [`CONTRIBUTING.md`](https://github.com/Kentico/.github/blob/main/CONTRIBUTING.md) for more information.

## License

Distributed under the MIT License. See [`LICENSE.md`](./LICENSE.md) for more information.

## Support

This contribution has **Full support by 7-day bug-fix policy**.

See [`SUPPORT.md`](https://github.com/Kentico/.github/blob/main/SUPPORT.md#full-support) for more information.

For any security issues see [`SECURITY.md`](https://github.com/Kentico/.github/blob/main/SECURITY.md).
