using CMS.DataEngine;
using CMS.Membership;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin;

/// <summary>
/// Resolves user names for the recent imports list.
/// </summary>
internal interface IImportUserNameProvider
{
    /// <summary>
    /// Returns the user names of the given users. Users that no longer exist are left out.
    /// </summary>
    public IReadOnlyDictionary<int, string> GetUserNames(IReadOnlyCollection<int> userIds);
}

internal sealed class ImportUserNameProvider(IInfoProvider<UserInfo> userProvider) : IImportUserNameProvider
{
    public IReadOnlyDictionary<int, string> GetUserNames(IReadOnlyCollection<int> userIds)
    {
        if (userIds.Count == 0)
        {
            return new Dictionary<int, string>();
        }

        return userProvider.Get()
            .WhereIn(nameof(UserInfo.UserID), userIds)
            .Columns(nameof(UserInfo.UserID), nameof(UserInfo.UserName))
            .GetEnumerableTypedResult()
            .ToDictionary(user => user.UserID, user => user.UserName);
    }
}
