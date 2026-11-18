# Xperience by Kentico Labs: Contacts Importer

[![Kentico Labs](https://img.shields.io/badge/Kentico_Labs-grey?labelColor=orange&logo=data:image/svg+xml;base64,PHN2ZyBjbGFzcz0ic3ZnLWljb24iIHN0eWxlPSJ3aWR0aDogMWVtOyBoZWlnaHQ6IDFlbTt2ZXJ0aWNhbC1hbGlnbjogbWlkZGxlO2ZpbGw6IGN1cnJlbnRDb2xvcjtvdmVyZmxvdzogaGlkZGVuOyIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxwYXRoIGQ9Ik05NTYuMjg4IDgwNC40OEw2NDAgMjc3LjQ0VjY0aDMyYzE3LjYgMCAzMi0xNC40IDMyLTMycy0xNC40LTMyLTMyLTMyaC0zMjBjLTE3LjYgMC0zMiAxNC40LTMyIDMyczE0LjQgMzIgMzIgMzJIMzg0djIxMy40NEw2Ny43MTIgODA0LjQ4Qy00LjczNiA5MjUuMTg0IDUxLjIgMTAyNCAxOTIgMTAyNGg2NDBjMTQwLjggMCAxOTYuNzM2LTk4Ljc1MiAxMjQuMjg4LTIxOS41MnpNMjQxLjAyNCA2NDBMNDQ4IDI5NS4wNFY2NGgxMjh2MjMxLjA0TDc4Mi45NzYgNjQwSDI0MS4wMjR6IiAgLz48L3N2Zz4=)](https://github.com/Kentico/.github/blob/main/SUPPORT.md#labs-limited-support) [![CI: Build and Test](https://github.com/Kentico/xperience-by-kentico-contacts-importer/actions/workflows/ci.yml/badge.svg)](https://github.com/Kentico/xperience-by-kentico-contacts-importer/actions/workflows/ci.yml)

## Description

Lets marketers import contacts into Xperience by Kentico from CSV, TSV, NDJSON, JSON and Excel (`.xlsx`) files, also gzipped (`.gz`) or zipped (`.zip`), with deduplication, dry runs, recipient list assignment and consent agreements. The file is read in the browser; only mapped columns are uploaded.

### Limitations

Some behavior is intentional, for speed on large files. Details: [Limitations and documented choices](./docs/Usage-Guide.md#limitations-and-documented-choices).

- Contacts are written with set-based SQL: per-object event handlers, automation triggers and contact group recalculation do not run for imported contacts. Recalculate contact groups after an import.
- Caches are cleared by type keys, and fully only when an import completes. After a cancelled or failed import, clear the cache in the System application. Custom caches keyed only per contact stay stale until they expire.
- Clean data is expected: duplicate rows are applied in file order (last non-empty value wins). Rows without a valid match value are always skipped.
- Reference fields (country, state, contact status, owner) take IDs, not names.
- The browser tab drives the import: closing it pauses the import. Do not import during deployments.
- Contacts added to a recipient list get no double opt-in email. Bounce records are never changed.
- Web farm and SaaS need shared storage for the import files; see [Setup](./docs/Setup.md#storage).
- File size is limited (5 GB by default); request size limits and timeouts of the hosting apply.

## Requirements

### Library Version Matrix

| Xperience Version | Library Version    |
| ----------------- | ------------------ |
| >= 31.10.3        | 1.0.0-prerelease-5 |

### Dependencies

- [ASP.NET Core 10.0](https://dotnet.microsoft.com/en-us/download)
- [Xperience by Kentico](https://docs.kentico.com)

## Package Installation

Add the package to your application using the .NET CLI.

```powershell
dotnet add package --prerelease Kentico.Xperience.Labs.ContactsImporter.Admin
```

## Quick Start

1. Install the package (see [Package Installation](#package-installation)).
1. Register the services in your application's `Program.cs`:

   ```csharp
   using Kentico.Xperience.Labs.ContactsImporter.Admin;

   builder.Services.AddContactsImporter();
   ```

1. In **Role management**, give marketers the **View** and **Import contacts** permissions of **Contacts Importer (Labs)**. Users who add contacts to recipient lists or record consent also need **Confirm consent for imported contacts**. See [Permissions](./docs/Setup.md#permissions).
1. Web farm or SaaS: import files are stored under `~/App_Data/contacts-importer` by default and must be shared by all instances (and, on Azure App Service, registered for storage path mapping). See [Storage](./docs/Setup.md#storage).
1. Open **Contacts Importer (Labs)** in the **Digital marketing** category and select **New import**.

## Full Instructions

View the [Usage Guide](./docs/Usage-Guide.md) for more detailed instructions.

## Contributing

To see the guidelines for Contributing to Kentico open source software, please see [Kentico's `CONTRIBUTING.md`](https://github.com/Kentico/.github/blob/main/CONTRIBUTING.md) for more information and follow the [Kentico's `CODE_OF_CONDUCT`](https://github.com/Kentico/.github/blob/main/CODE_OF_CONDUCT.md).

Instructions and technical details for contributing to **this** project can be found in [Contributing Setup](./docs/Contributing-Setup.md).

## License

Distributed under the MIT License. See [`LICENSE.md`](./LICENSE.md) for more information.

## Support

[![Kentico Labs](https://img.shields.io/badge/Kentico_Labs-grey?labelColor=orange&logo=data:image/svg+xml;base64,PHN2ZyBjbGFzcz0ic3ZnLWljb24iIHN0eWxlPSJ3aWR0aDogMWVtOyBoZWlnaHQ6IDFlbTt2ZXJ0aWNhbC1hbGlnbjogbWlkZGxlO2ZpbGw6IGN1cnJlbnRDb2xvcjtvdmVyZmxvdzogaGlkZGVuOyIgdmlld0JveD0iMCAwIDEwMjQgMTAyNCIgdmVyc2lvbj0iMS4xIiB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxwYXRoIGQ9Ik05NTYuMjg4IDgwNC40OEw2NDAgMjc3LjQ0VjY0aDMyYzE3LjYgMCAzMi0xNC40IDMyLTMycy0xNC40LTMyLTMyLTMyaC0zMjBjLTE3LjYgMC0zMiAxNC40LTMyIDMyczE0LjQgMzIgMzIgMzJIMzg0djIxMy40NEw2Ny43MTIgODA0LjQ4Qy00LjczNiA5MjUuMTg0IDUxLjIgMTAyNCAxOTIgMTAyNGg2NDBjMTQwLjggMCAxOTYuNzM2LTk4Ljc1MiAxMjQuMjg4LTIxOS41MnpNMjQxLjAyNCA2NDBMNDQ4IDI5NS4wNFY2NGgxMjh2MjMxLjA0TDc4Mi45NzYgNjQwSDI0MS4wMjR6IiAgLz48L3N2Zz4=)](https://github.com/Kentico/.github/blob/main/SUPPORT.md#labs-limited-support)

This project has **Kentico Labs limited support**.

See [`SUPPORT.md`](https://github.com/Kentico/.github/blob/main/SUPPORT.md#labs-limited-support) for more information.

For any security issues see [`SECURITY.md`](https://github.com/Kentico/.github/blob/main/SECURITY.md).
