# Usage Guide

## Screenshot

![Import page](../images/screenshot.png)

## Importing and deleting contacts

1. Open **Digital marketing > Contact importer** application

    ![App tile](../images/app-tile.png)

1. Upload your CSV file. See [Supported CSV columns](#supported-csv-columns) for help with formatting the data
1. In the **Import mode** field, choose the import mode:
    - Insert: Imports new contacts while skipping existing contacts with matching ContactGUIDs from the CSV file
    - Delete: Deletes existing contacts using the ContactGUIDs from the CSV file
1. If you would like to assign newly-imported contacts _and_ existing contacts from your CSV to a contact group, select the group from the **Assign to contact group** dropdown. You may leave this option empty if you don't wish to assign the contacts to a group
1. If you would like to assign newly-imported contacts _and_ existing contacts from your CSV to a recipient list, select the group from the **Assign to recipient list** dropdown. You may leave this option empty if you don't wish to assign the contacts to a recipient list
1. The **Delimiter** field can generally be skipped as the default character `,` is most common. Change this value if your CSV file uses a different character- common examples are `;` or `\\t`
1. Customize the **Batch size** as needed. For instances with limited resources this value should not exeed 5000. A batch size lower than 100 is not recommended
1. Click **Run import** to begin the process. Degraded application performance is expected during import

> [!CAUTION]
> During the import process, do not:
>
> - Close the window
> - Shut down the application
> - Modify the CSV file

## Supported CSV columns

| Column            | .NET Type | Required |
| ----------------- | --------- | -------- |
| ContactGUID       | Guid      | yes      |
| ContactCreated    | DateTime  | yes      |
| ContactFirstName  | string    | no       |
| ContactLastName   | string    | no       |
| ContactEmail      | string    | no       |
| ContactAddress1   | string    | no       |
| ContactMiddleName | string    | no       |

The first row of the CSV file should contain the header names of the individual columns.
Header validation is performed during the file upload. See the [sample file](https://github.com/Kentico/xperience-by-kentico-contacts-importer/blob/main/data/contact_sample.csv) for an example.

> [!IMPORTANT]  
> For correct functionality, please make sure to add a newline at the end of the CSV file.
