# Xperience by Kentico Contacts Importer

[![CI: Build and Test](https://github.com/Kentico/xperience-by-kentico-contacts-importer/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/Kentico/xperience-by-kentico-contacts-importer/actions/workflows/ci.yml)

<!-- ABOUT THE PROJECT -->

## About The Project

Please put here some general information about your Intergration / App / Solution.

<!-- GETTING STARTED -->

## Getting Started

This is an example of how you may give instructions on setting up your project locally.
To get a local copy up and running follow these simple example steps.

### Prerequisites

- Xperience by Kentico >= 26.3.2

  - <https://docs.xperience.io/xp/changelog#Changelog-Hotfix(June29,2023)>

### Installation

Add the package to your ASP.NET Core application or Admin class library.

### Register Contacts importer to you dependency injection container

```csharp
builder.Services.AddKentico();
// ... other registrations
services.AddContactsImport();
```

### Register Contacts importer to your application

```csharp
app.InitKentico();
// ... other registrations
app.AddContactsImport();
```

<!-- USAGE EXAMPLES -->

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

### Supported columns

| Column            | .NET Type | Required |
| ----------------- | --------- | -------- |
| ContactGUID       | Guid      | yes      |
| ContactCreated    | DateTime  | yes      |
| ContactFirstName  | string    | no       |
| ContactLastName   | string    | no       |
| ContactEmail      | string    | no       |
| ContactAge        | Int32     | no       |
| ContactMiddleName | string    | no       |

<!-- CONTRIBUTING -->

## Contributing

- .NET SDK >= 7.0.109

  - <https://dotnet.microsoft.com/en-us/download/dotnet/7.0>

- Node.js >= 18.12

  - <https://nodejs.org/en/download>
  - <https://github.com/coreybutler/nvm-windows>

For Contributing please see [`CONTRIBUTING.md`](./CONTRIBUTING.md) for more information.

<!-- LICENSE -->

## License

Distributed under the MIT License. See [`LICENSE.md`](./LICENSE.md) for more information.
