using System.Globalization;
using domain.SchoolTerms;
using domain.Security;
using domain.ValueObjects;
using infra.persistence.postgre;
using infra.persistence.sql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using service.defaults;
using tools.seed;

// F00's seeder. Composition only: guard, compose, resolve, write, print. Any branch that wants to
// appear here belongs in SeedCatalog, SeedWriter or SeedGuard instead, because nothing in this file
// is covered by a test.
//
// Exit codes: 0 seeded, 1 refused or failed, 2 seeded with conflicts (see SeedResult.Conflicts).
try
{
    return await RunAsync(args).ConfigureAwait(false);
}
catch (InvalidOperationException exception)
{
    // The guard's refusals and an unresolvable timezone both land here. Message only, no stack: the
    // refusals are the expected outcome of running the tool wrong, and a stack trace buries the
    // sentence that says which condition failed.
    await Console.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
    return 1;
}

static async Task<int> RunAsync(string[] args)
{
    // Args are deliberately NOT handed to the host builder: --confirm must not become a
    // configuration key, because configuration is inheritable and that flag exists to be the one
    // thing that is not. Everything else comes from user secrets and the environment.
    HostApplicationBuilder builder = Host.CreateApplicationBuilder();

    // Explicit rather than inherited: a console host defaults to the Production environment, and
    // HostApplicationBuilder adds user secrets only in Development. Without this the connection
    // string would have to be an environment variable in every case. Mirrors DbContextFactory.
    builder.Configuration
        .AddUserSecrets(typeof(SeedCatalog).Assembly, optional: true)
        .AddEnvironmentVariables();

    // First, before any service is resolved and before a connection is opened.
    SeedGuard.EnsureSeedingIsPermitted(builder.Configuration, args);

    // The tool's output *is* the summary. At the default Information level EF logs every one of the
    // 42 key lookups and the insert batch, which pushes the summary several screens up and makes a
    // successful run look like something went wrong. Raised through configuration rather than
    // hard-set, so `Logging__LogLevel__Default=Information` still gets the SQL back when a run needs
    // diagnosing.
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));

    builder.Services.AddScoped(_ => SystemImportUser.AsCurrentUser());
    builder.Services.AddScoped<IAuditOverride, AuditOverride>();

    // WithApi() is deliberately not called: it runs the anonymous-stub deployment guard and
    // registers StubCurrentUser. The seed runs as SystemImportUser.
    builder.AddSparkrockRwc().WithPostgre();

    using IHost host = builder.Build();
    using IServiceScope scope = host.Services.CreateScope();

    IDbContext dbContext = scope.ServiceProvider.GetRequiredService<IDbContext>();
    IAuditOverride auditOverride = scope.ServiceProvider.GetRequiredService<IAuditOverride>();
    TimeProvider clock = scope.ServiceProvider.GetRequiredService<TimeProvider>();

    // DEC-12: the school year is resolved in the school's local day, never from UtcNow.Date. The
    // clock is read here rather than in SeedCatalog so the catalogue stays pure.
    TimeZoneInfo zone = TimeZoneInfo.FindSystemTimeZoneById(SeedCatalog.SchoolTimeZoneId);
    DateOnly schoolLocalToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(clock.GetUtcNow(), zone).DateTime);
    SchoolYear schoolYear = SchoolYear.FromLocalDate(schoolLocalToday);

    SeedPlan plan = SeedCatalog.Build(schoolYear);
    SeedResult result = await new SeedWriter(dbContext, auditOverride)
        .WriteAsync(plan, CancellationToken.None)
        .ConfigureAwait(false);

    Report(plan, schoolLocalToday, schoolYear, result);

    return result.Conflicts.Count == 0 ? 0 : 2;
}

static void Report(SeedPlan plan, DateOnly schoolLocalToday, SchoolYear schoolYear, SeedResult result)
{
    Write($"School-local today ({SeedCatalog.SchoolTimeZoneId}): {schoolLocalToday:yyyy-MM-dd}");
    Write($"Resolved school year: {schoolYear}");
    Write($"");
    Write($"{"Entity",-16}{"created",9}{"updated",9}{"unchanged",11}{"skipped",9}");

    foreach (SeedOutcome outcome in result.Outcomes)
        Write($"{outcome.Entity,-16}{outcome.Created,9}{outcome.Updated,9}{outcome.Unchanged,11}{outcome.Skipped,9}");

    Write($"");
    Write($"Terms (bounds are closed):");

    foreach (SchoolTerm term in plan.Terms)
    {
        string status = term.IsActive ? "active" : "inactive";
        Write($"  {term.Name,-20} {term.StartDate:yyyy-MM-dd} .. {term.EndDate:yyyy-MM-dd}  {status}");
    }

    if (result.Conflicts.Count == 0)
        return;

    Write($"");
    Write($"{result.Conflicts.Count} row(s) were left untouched:");

    foreach (string conflict in result.Conflicts)
        Write($"  {conflict}");
}

static void Write(FormattableString line) =>
    Console.WriteLine(line.ToString(CultureInfo.InvariantCulture));
