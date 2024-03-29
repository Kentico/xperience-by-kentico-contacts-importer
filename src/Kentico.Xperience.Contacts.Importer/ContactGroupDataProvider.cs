﻿using CMS.ContactManagement;
using Kentico.Xperience.Admin.Base.FormAnnotations;

namespace Kentico.Xperience.Contacts.Importer;

public class ContactGroupDataProvider : IDropDownOptionsProvider
{
    private readonly IContactGroupInfoProvider contactGroupInfoProvider;

    public ContactGroupDataProvider(IContactGroupInfoProvider contactGroupInfoProvider) => this.contactGroupInfoProvider = contactGroupInfoProvider;

    public Task<IEnumerable<DropDownOptionItem>> GetOptionItems()
    {
        var groups = contactGroupInfoProvider.Get().ToList();

        var options = groups.Select(i => new DropDownOptionItem { Text = i.ContactGroupName, Value = i.ContactGroupGUID.ToString() });

        return Task.FromResult(options);
    }
}
