using Projects;

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

IResourceBuilder<ProjectResource> apiProject = builder.AddProject<api>("api")
    .WithReference(sparkrockRwcDb)
    .WaitFor(postgres)
    .WaitFor(sparkrockRwcDb);

builder.Build().Run();