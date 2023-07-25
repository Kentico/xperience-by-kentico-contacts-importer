using System.Reflection;
using CMS;
using CMS.Core;
using Microsoft.Extensions.Configuration;
using Kentico.Xperience.Contacts.Importer.Generator.Configuration;

using CMSModule = CMS.DataEngine.Module;

[assembly: RegisterModule(typeof(AppSettingsJsonRegistrationModule))]

namespace Kentico.Xperience.Contacts.Importer.Generator.Configuration
{
    public class AppSettingsJsonRegistrationModule : CMSModule
    {
        public AppSettingsJsonRegistrationModule() : base(nameof(AppSettingsJsonRegistrationModule))
        {

        }

        protected override void OnPreInit()
        {
            base.OnPreInit();

            Service.Use<IConfiguration>(() => new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", false, true)
                .AddJsonFile("appsettings.local.json", true, true)
                .AddUserSecrets(Assembly.GetEntryAssembly(), true, true)
                .Build());
        }
    }
}
