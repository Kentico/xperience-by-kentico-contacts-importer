using CMS.ContactManagement;

namespace Kentico.Xperience.Contacts.Importer.Generator;

public static class ContactsGenerator
{
    public static IEnumerable<ContactInfo> Generate(int count)
    {
        var faker = new Bogus.Faker<ContactInfo>();

        faker.RuleFor(c => c.ContactGUID, f => f.Random.Guid())
            .RuleFor(c => c.ContactCreated, f => f.Date.Between(
                new DateTime(2023, 01, 01, 01, 01, 01, DateTimeKind.Utc), new DateTime(2023, 08, 01, 01, 01, 01, DateTimeKind.Utc)))
            .RuleFor(c => c.ContactFirstName, f => f.Person.FirstName)
            .RuleFor(c => c.ContactEmail, f => f.Internet.Email())
            .RuleFor(c => c.ContactLastName, f => f.Person.LastName)
            .RuleFor(c => c.ContactMiddleName, f => f.Person.FirstName)
            .RuleFor(c => c.ContactAge, f => f.Random.Number(18, 64))
            .RuleFor(c => c.ContactAddress1, f => f.Address.StreetAddress());

        foreach (int _ in Enumerable.Range(0, count))
        {
            yield return faker.Generate();
        }
    }
}
