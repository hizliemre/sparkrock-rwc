using Projects;
using service.defaults;

IDistributedApplicationBuilder builder = DistributedApplication.CreateBuilder(args);

IResourceBuilder<ParameterResource> pgPassword = builder.AddParameter("pg-password", true);

IResourceBuilder<PostgresServerResource> postgres = builder.AddPostgres("postgres")
    .WithImageTag("17")
    .WithPassword(pgPassword)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithDataVolume("sparkrock-rwc-postgres-data")
    .PublishAsConnectionString();

postgres.WithHostPort(5433);

IResourceBuilder<PostgresDatabaseResource> sparkrockRwcDb = postgres.AddDatabase("sparkrock-rwc");

// Forwarded, never set here. The opt-in has to be typed by a person into user secrets or the
// environment; originating it in the AppHost would make every developer run inherit it silently.
IResourceBuilder<ProjectResource> apiProject = builder.AddProject<api>("api")
    .WithEnvironment(
        "Attendance__AllowAnonymousStubIdentity",
        builder.Configuration[DeploymentGuard.AllowAnonymousStubIdentityKey] ?? "false")
    .WithReference(sparkrockRwcDb)
    .WaitFor(postgres)
    .WaitFor(sparkrockRwcDb);

builder.Build().Run();