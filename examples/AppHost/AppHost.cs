using Microsoft.Extensions.Configuration;

// Runs everything DancingGoat needs for local development: `aspire run` from the repository root.
//
// SQL Server ("Sql:UseExistingServer" in appsettings.json, AppHost user secrets, or environment):
//   true  (default) DancingGoat uses its own connection string and salt from user secrets
//                   (scripts/Initialize-Database.ps1 or the setup-local skill)
//   false           a persistent SQL Server container; the database is created on first run
//
// Mailpit ("Mail:UseExistingServer", same sources):
//   true  (default) DancingGoat sends to the machine-wide Mailpit from its appsettings (localhost:1025),
//                   shared by every project; the dashboard links its inbox
//   false           a Mailpit container for this run only

var builder = DistributedApplication.CreateBuilder(args);

var repositoryRoot = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", ".."));

var dancingGoat = builder.AddProject<Projects.DancingGoat>("dancinggoat")
    .WithUrls(context =>
    {
        if (context.Urls.FirstOrDefault(url => url.Endpoint?.EndpointName == "http") is { } site)
        {
            site.DisplayText = "Live site";
            context.Urls.Add(new() { Url = $"{site.Url.TrimEnd('/')}/admin", DisplayText = "Administration", Endpoint = site.Endpoint });
        }
    });

if (builder.Configuration.GetValue("Mail:UseExistingServer", true))
{
    // Not started or stopped by Aspire: docker run -d --name mailpit -p 1025:1025 -p 8025:8025 axllent/mailpit
    builder.AddExternalService("mailpit", "http://localhost:8025")
        .WithHttpHealthCheck("/");
}
else
{
    var mailpit = builder.AddMailPit("mailpit");

    dancingGoat
        .WithEnvironment("SmtpOptions__Server__Host", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Host))
        .WithEnvironment("SmtpOptions__Server__Port", mailpit.GetEndpoint("smtp").Property(EndpointProperty.Port))
        .WaitFor(mailpit);
}

if (!builder.Configuration.GetValue("Sql:UseExistingServer", true))
{
    const string databaseName = "xperience-by-kentico-contacts-importer";

    var sql = builder.AddSqlServer("sql")
        .WithDataVolume()
        .WithLifetime(ContainerLifetime.Persistent);

    // IPv4: Docker publishes on 127.0.0.1, and "localhost" makes SqlClient wait on ::1 first
    var server = ReferenceExpression.Create($"{sql.Resource.PrimaryEndpoint.Property(EndpointProperty.IPV4Host)},{sql.Resource.PrimaryEndpoint.Property(EndpointProperty.Port)}");

    // Creates the database when it is missing (dbmanager generates the salt, kept in DancingGoat
    // user secrets) and updates it when it is behind the application
    var databaseInit = builder.AddExecutable("database-init", "pwsh", repositoryRoot)
        .WithArgs(context =>
        {
            context.Args.Add("-NoProfile");
            context.Args.Add("-File");
            context.Args.Add(Path.Combine("scripts", "Initialize-Database.ps1"));
            context.Args.Add("-IfNotReady");
            context.Args.Add("-SaveTo");
            context.Args.Add("Salt");
            context.Args.Add("-ServerName");
            context.Args.Add(server);
            context.Args.Add("-DatabaseName");
            context.Args.Add(databaseName);
            context.Args.Add("-Username");
            context.Args.Add("sa");

            // Optional license file path from the environment or AppHost configuration
            if (builder.Configuration["XPERIENCE_LICENSE_FILE"] is { Length: > 0 } licenseFile)
            {
                context.Args.Add("-LicenseFile");
                context.Args.Add(licenseFile);
            }
        })
        // Secrets as environment variables: resource arguments are shown in logs and the dashboard
        .WithEnvironment("XPERIENCE_SQL_PASSWORD", sql.Resource.PasswordParameter)
        .WaitFor(sql);

    dancingGoat
        .WithEnvironment("ConnectionStrings__CMSConnectionString", ReferenceExpression.Create(
            $"Data Source={server};Initial Catalog={databaseName};User ID=sa;Password={sql.Resource.PasswordParameter};TrustServerCertificate=True;Encrypt=False"))
        .WaitForCompletion(databaseInit);
}

// Admin client webpack dev server (DancingGoat's Development settings use Proxy mode)
builder.AddJavaScriptApp("admin-client", Path.Combine(repositoryRoot, "src", "Kentico.Xperience.Labs.ContactsImporter.Admin", "Client"), "start")
    .WithNpm(install: true);

builder.Build().Run();
