using CMS.ContactManagement;
using CMS.DataEngine;

using Kentico.Xperience.Admin.Base.FormAnnotations;

namespace Kentico.Xperience.Contacts.Importer;

public class ContactGroupDataProvider(IInfoProvider<ContactGroupInfo> contactGroupInfoProvider) : IDropDownOptionsProvider
{
    private readonly IInfoProvider<ContactGroupInfo> contactGroupInfoProvider = contactGroupInfoProvider;

    public async Task<IEnumerable<DropDownOptionItem>> GetOptionItems()
    {
        var groups = await contactGroupInfoProvider.Get().GetEnumerableTypedResultAsync();

        var options = groups.Select(i => new DropDownOptionItem { Text = i.ContactGroupName, Value = i.ContactGroupGUID.ToString() });

        return options;
    }
}
