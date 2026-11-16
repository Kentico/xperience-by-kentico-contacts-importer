using System.Globalization;

using CMS.ContactManagement;
using CMS.Core;
using CMS.DataEngine;
using CMS.FormEngine;

using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;

namespace Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests;

/// <summary>
/// Points Xperience at a real Xperience database on SQL Server and adds the custom contact fields the tests import
/// into. Without <see cref="ConnectionStringVariable"/>, every test is ignored (not failed).
/// </summary>
/// <remarks>
/// <para>
/// Create the database once (and again after an Xperience update) with the product tool, from the repository root so
/// no project <c>appsettings.json</c> is rewritten and no sample data is installed. No license is needed:
/// </para>
/// <code>
/// dotnet tool restore
/// dotnet kentico-xperience-dbmanager -- --server-name "localhost,1433" --username sa --password "&lt;sa password&gt;" `
///   --admin-password "&lt;any password&gt;" --database-name xperience-by-kentico-contacts-importer-integration --recreate-existing-database
/// $env:CONTACTSIMPORTER_INTEGRATION_SQL = "Data Source=localhost,1433;Initial Catalog=xperience-by-kentico-contacts-importer-integration;User ID=sa;Password=&lt;sa password&gt;;Encrypt=False"
/// dotnet test tests/Kentico.Xperience.Labs.ContactsImporter.Admin.IntegrationTests
/// </code>
/// <para>
/// The tests delete all contacts, so only a database whose name ends with <see cref="DatabaseNameSuffix"/> is accepted;
/// never point them at the DancingGoat database. Checks read the database with plain ADO.NET, not with the code under
/// test. All tests have the NUnit category <see cref="Category"/>. CI creates the database in a SQL Server service container.
/// </para>
/// </remarks>
[SetUpFixture]
public sealed class IntegrationDatabase
{
    /// <summary>
    /// Environment variable with the connection string of the test database.
    /// </summary>
    public const string ConnectionStringVariable = "CONTACTSIMPORTER_INTEGRATION_SQL";

    /// <summary>
    /// Required end of the database name, so the tests never delete the contacts of a real database.
    /// </summary>
    public const string DatabaseNameSuffix = "-integration";

    public const string Category = "Integration";

    // Custom contact fields, added the way the Modules application adds them (column + class definition).
    public const string CodeField = "ImporterTestCode";
    public const string NumberField = "ImporterTestNumber";
    public const string BigNumberField = "ImporterTestBigNumber";
    public const string AmountField = "ImporterTestAmount";
    public const string ScoreField = "ImporterTestScore";
    public const string DateField = "ImporterTestDate";
    public const string FlagField = "ImporterTestFlag";
    public const string GuidField = "ImporterTestGuid";
    public const string SourceField = "ImporterTestSource";

    /// <summary>
    /// A custom field that references a user by its field settings only: the table has no foreign key for it.
    /// </summary>
    public const string UserField = "ImporterTestUserID";

    private static readonly (string Name, string DataType, int Size, int Precision, string SqlType)[] customFields =
    [
        (UserField, FieldDataType.Integer, 0, -1, "int"),
        (CodeField, FieldDataType.Text, 100, -1, "nvarchar(100)"),
        (NumberField, FieldDataType.Integer, 0, -1, "int"),
        (BigNumberField, FieldDataType.LongInteger, 0, -1, "bigint"),
        (AmountField, FieldDataType.Decimal, 10, 2, "decimal(10,2)"),
        (ScoreField, FieldDataType.Double, 0, -1, "float"),
        (DateField, FieldDataType.Date, 0, -1, "date"),
        (FlagField, FieldDataType.Boolean, 0, -1, "bit"),
        (GuidField, FieldDataType.Guid, 0, -1, "uniqueidentifier"),
        (SourceField, FieldDataType.Text, 200, -1, "nvarchar(200)"),
    ];

    private static string? connectionString;

    [OneTimeSetUp]
    public void Initialize()
    {
        string? value = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(value))
        {
            Assert.Ignore($"Integration tests need an Xperience database: set {ConnectionStringVariable} (see the remarks of {nameof(IntegrationDatabase)}).");
        }

        string database = new SqlConnectionStringBuilder(value).InitialCatalog;
        if (!database.EndsWith(DatabaseNameSuffix, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"The database of {ConnectionStringVariable} must end with '{DatabaseNameSuffix}' (its contacts are deleted), not '{database}'.");
        }

        EnsureReadCommittedSnapshot(value);
        connectionString = value;
        InitializeXperience(value);
        EnsureCustomFields();
        DeleteContacts();
    }

    [OneTimeTearDown]
    public void CleanUp()
    {
        if (connectionString is not null)
        {
            DeleteContacts();
        }
    }

    internal static void DeleteContacts() => Execute("DELETE FROM [OM_Contact]");

    internal static int Execute(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = CreateCommand(connection, sql, parameters);

        return command.ExecuteNonQuery();
    }

    internal static T Scalar<T>(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = CreateCommand(connection, sql, parameters);
        object? result = command.ExecuteScalar();

        return result is null or DBNull
            ? default!
            : (T)Convert.ChangeType(result, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T), CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Rows of a query; <c>NULL</c> values are <c>null</c>.
    /// </summary>
    internal static List<Dictionary<string, object?>> Query(string sql, params (string Name, object? Value)[] parameters)
    {
        using var connection = Open();
        using var command = CreateCommand(connection, sql, parameters);
        using var reader = command.ExecuteReader();

        var rows = new List<Dictionary<string, object?>>();
        while (reader.Read())
        {
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    private static SqlConnection Open()
    {
        var connection = new SqlConnection(connectionString ?? throw new InvalidOperationException("The test database is not configured."));
        connection.Open();

        return connection;
    }

    private static SqlCommand CreateCommand(SqlConnection connection, string sql, (string Name, object? Value)[] parameters)
    {
        var command = new SqlCommand(sql, connection) { CommandTimeout = 300 };
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }

    // Azure SQL (and so Xperience SaaS) runs with read committed snapshot isolation; a database created by the
    // dbmanager tool on a local SQL Server does not. The tests run with it, because it is the mode in which a lookup
    // cannot see contacts another call has inserted and not committed (ConcurrentCallsTests).
    private static void EnsureReadCommittedSnapshot(string connection)
    {
        // Not pooled: changing the option needs to be the only connection to the database.
        using var sql = new SqlConnection(new SqlConnectionStringBuilder(connection) { Pooling = false }.ConnectionString);
        sql.Open();
        using var check = new SqlCommand("SELECT is_read_committed_snapshot_on FROM sys.databases WHERE name = DB_NAME()", sql);
        if (!Convert.ToBoolean(check.ExecuteScalar(), CultureInfo.InvariantCulture))
        {
            using var alter = new SqlCommand("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE", sql) { CommandTimeout = 120 };
            alter.ExecuteNonQuery();
        }
    }

    // The importer runs its SQL through ConnectionHelper, creates contacts with BulkInsert and clears caches, which need
    // Xperience's service container, connection string and initialized modules (BulkInsert and the cache APIs create
    // web farm tasks, whose types the modules register). No web application and no license are involved.
    private static void InitializeXperience(string connection)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:CMSConnectionString"] = connection })
            .Build();

        Service.Use<IConfiguration>(configuration);
        CMSApplication.PreInit(true);
        CMSApplication.Init();
    }

    private static void EnsureCustomFields()
    {
        var dataClass = DataClassInfoProvider.GetDataClassInfo(ContactInfo.TYPEINFO.ObjectClassName)
            ?? throw new InvalidOperationException("The contact class was not found.");
        var form = new FormInfo(dataClass.ClassFormDefinition);
        bool changed = false;

        foreach (var (name, dataType, size, precision, sqlType) in customFields)
        {
            // Names and types are the constants above.
            Execute($"IF COL_LENGTH('{dataClass.ClassTableName}', '{name}') IS NULL ALTER TABLE [{dataClass.ClassTableName}] ADD [{name}] {sqlType} NULL");
            if (!form.FieldExists(name))
            {
                form.AddFormItem(new FormFieldInfo
                {
                    Name = name,
                    Caption = name,
                    DataType = dataType,
                    Size = size,
                    Precision = precision,
                    AllowEmpty = true,
                    System = false,
                    Enabled = true,
                    ReferenceToObjectType = name == UserField ? CMS.Membership.UserInfo.OBJECT_TYPE : null,
                });
                changed = true;
            }
        }

        if (changed)
        {
            dataClass.ClassFormDefinition = form.GetXmlDefinition();
            dataClass.ClassXmlSchema = new TableManager(null).GetXmlSchema(dataClass.ClassTableName);
            DataClassInfoProvider.SetDataClassInfo(dataClass);
        }
    }
}
