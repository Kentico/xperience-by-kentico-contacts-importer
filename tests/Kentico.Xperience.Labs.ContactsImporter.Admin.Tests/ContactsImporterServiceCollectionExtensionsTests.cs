using Microsoft.Extensions.DependencyInjection;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.Tests;

public class ContactsImporterServiceCollectionExtensionsTests
{
    [Test]
    public void AddContactsImporter_Returns_Same_ServiceCollection()
    {
        var services = new ServiceCollection();

        var result = services.AddContactsImporter();

        Assert.That(result, Is.SameAs(services));
    }
}
