using CMS.ContactManagement;
using Bogus;
using BindingFlags = System.Reflection.BindingFlags;

namespace Kentico.Xperience.Contacts.Importer.Generator;

public static class ContactsGenerator
{
    public static IEnumerable<ContactInfo> Generate(int count)
    {
        var binder = new CustomBinder();

        var faker = new Faker<ContactInfo>(binder: binder);

        faker.RuleFor(c => c.ContactGUID, f => f.Random.Guid())
            .RuleFor(c => c.ContactCreated, f => f.Date.Between(new DateTime(2023, 4, 1), new DateTime(2023, 7, 15)))
            .RuleFor(c => c.ContactFirstName, f => f.Person.FirstName)
            .RuleFor(c => c.ContactEmail, f => f.Internet.Email())
            .RuleFor(c => c.ContactLastName, f => f.Person.LastName)
            .RuleFor(c => c.ContactMiddleName, f => f.Person.FirstName)
            .RuleFor(c => c.ContactAge, f => f.Random.Number(18, 64))
            .RuleFor(c => c.ContactAddress1, f => f.Address.StreetAddress())
            .RuleFor(c => c.ContactID, f => f.Random.Number())
            .CustomInstantiator(f => new ContactInfo());

        foreach (int _ in Enumerable.Range(0, count))
        {
            yield return faker.Generate();
        }
    }
}

public class CustomBinder : Binder
{
    public CustomBinder() : base(
        BindingFlags.Public |
        BindingFlags.GetProperty |
        BindingFlags.Instance |
        BindingFlags.ExactBinding |
        BindingFlags.DeclaredOnly)
    { }
}
