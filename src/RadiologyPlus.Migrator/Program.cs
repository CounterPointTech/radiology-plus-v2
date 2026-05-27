using System.Globalization;
using Microsoft.Extensions.Configuration;
using RadiologyPlus.Migrator;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(formatProvider: CultureInfo.InvariantCulture)
    .CreateLogger();

try
{
    var (positional, flags) = ArgParser.Parse(args);
    var command = positional.Count > 0 ? positional[0] : null;

    var config = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: true)
        .AddJsonFile("appsettings.Local.json", optional: true)
        .AddEnvironmentVariables(prefix: "RADPLUS_")
        .Build();

    var connectionString = config.GetConnectionString("AppDb")
        ?? throw new InvalidOperationException(
            "ConnectionStrings:AppDb is required. Set it via appsettings.Local.json or RADPLUS_ConnectionStrings__AppDb.");

    // Subcommands take precedence; if none, run the migration loop.
    return command switch
    {
        "init-tenant"   => await new InitTenantCommand(connectionString, config).RunAsync(flags),
        "create-nrs"    => await new CreateNrsCommand(connectionString).RunAsync(flags),
        "add-facility"  => await new AddFacilityCommand(connectionString).RunAsync(flags),
        "set-novarad-connection" => await new SetNovaradConnectionCommand(connectionString, config).RunAsync(flags),
        "help" or "--help" or "-h" or "/?" => PrintUsage(),
        _               => await new MigrationRunner(connectionString).RunAsync(),
    };
}
catch (UsageException ex)
{
    Log.Error("{Message}", ex.Message);
    PrintUsage();
    return 2;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Migrator terminated unexpectedly.");
    return 99;
}
finally
{
    Log.CloseAndFlush();
}

static int PrintUsage()
{
    Console.WriteLine("""
        RadiologyPlus.Migrator — schema migrations + bootstrap commands.

        Usage:
          RadiologyPlus.Migrator                                    Apply pending migrations from Migrations/ (default).

          RadiologyPlus.Migrator init-tenant
            --code=<short-code>                                     e.g. salient
            --name="<Display Name>"                                 e.g. "Salient Imaging"
            --novarad-host=<host>                                   e.g. 10.30.0.10
           [--novarad-port=<5432>]
            --novarad-db=<db>                                       e.g. novarad
            --novarad-user=<user>                                   e.g. radiology_plus_app
            --novarad-password=<plaintext>                          will be AES-GCM encrypted at rest
           [--use-ssl=true|false]                                   default: true

          RadiologyPlus.Migrator create-nrs
            --tenant=<code>                                         tenant short code
            --username=<u>                                          NRS account username
           [--display-name="<Name>"]
           [--email=<addr>]
           [--password=<plaintext>]                                 if omitted, a 16-char temp password is generated and printed

          RadiologyPlus.Migrator add-facility
            --tenant=<code>
            --code=<facility-code>                                  e.g. SAL-MAIN
            --name="<Facility Name>"
            --novarad-facility-id=<int>                             matches shared.facilities.facility_id in Novarad

          RadiologyPlus.Migrator set-novarad-connection             Repoint an existing tenant's Novarad connection.
            --tenant=<code>                                         Partial update — pass only what changes.
           [--novarad-host=<host>]
           [--novarad-port=<5432>]
           [--novarad-db=<db>]
           [--novarad-user=<user>]
           [--novarad-password=<plaintext>]                         re-encrypted at rest (needs Encryption:Key)
           [--use-ssl=true|false]
           [--audit-table=<base>]                                  dual-audit base table; writer appends _<year> (e.g. shared.audit)
            (Restart the API afterward — the connection pool caches per tenant.)

        Environment:
          RADPLUS_ConnectionStrings__AppDb           Postgres connection string for the Radiology Plus DB.
          RADPLUS_Encryption__Key                    Base64-encoded 16/24/32-byte master key (for init-tenant).
        """);
    return 0;
}
