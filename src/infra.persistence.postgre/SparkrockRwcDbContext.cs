using System.Linq.Expressions;
using System.Reflection;
using domain;
using domain.Alerts;
using domain.Attendance;
using domain.AttendanceCodes;
using domain.Import;
using domain.Schools;
using domain.SchoolTerms;
using domain.Students;
using domain.ValueObjects;
using domain.Abstraction;
using infra.persistence.postgre.ErrorTranslation;
using infra.persistence.sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace infra.persistence.postgre;

internal sealed class SparkrockRwcDbContext(
    DbContextOptions<SparkrockRwcDbContext> options,
    IConstraintErrorRegistry? constraintErrors = null) : DbContext(options), IDbContext
{
    // Optional, defaulted: AddDbContext resolves the parameter from the application provider when a
    // feature has registered a registry, while the default keeps the design-time DbContextFactory
    // (new SparkrockRwcDbContext(options)) and the in-memory test factory compiling unchanged.
    private readonly IConstraintErrorRegistry _constraintErrors = constraintErrors ?? ConstraintErrorRegistry.Empty;

    public DbSet<School> Schools { get; set; }

    public DbSet<Student> Students { get; set; }

    public DbSet<AttendanceCode> AttendanceCodes { get; set; }

    public DbSet<SchoolTerm> SchoolTerms { get; set; }

    public DbSet<StudentAttendance> StudentAttendances { get; set; }

    public DbSet<StudentAttendanceSummary> StudentAttendanceSummaries { get; set; }

    public DbSet<StudentAlert> StudentAlerts { get; set; }

    public DbSet<AttendanceSubmissionLog> AttendanceSubmissionLogs { get; set; }

    /// <summary>
    ///     Import quarantine. Deliberately absent from <c>IDbContext</c> — only the importer writes
    ///     here, and exposing it through the port would invite a slice to read it.
    /// </summary>
    public DbSet<LegacyImportAnomaly> LegacyImportAnomalies { get; set; }

    public DbSet<TestEntity> TestEntities { get; set; }

    /// <summary>
    ///     Translates a mapped constraint violation into a domain exception on the way out.
    /// </summary>
    /// <remarks>
    ///     An override rather than an interceptor: <c>SaveChangesFailed</c> observes the failure but
    ///     cannot replace the exception that is already in flight, so a handler in <c>features</c>
    ///     would still be handed a <see cref="DbUpdateException" /> it cannot inspect (DEC-14, VC-23).
    ///     <para>
    ///         The <c>when</c> filter is what keeps the untranslatable cases exact: no match means no
    ///         catch, so the original exception — including <see cref="DbUpdateConcurrencyException" />
    ///         and its stack — propagates untouched rather than being caught and rethrown.
    ///     </para>
    /// </remarks>
    public override async Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await base.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (ConstraintErrorTranslator.Translate(_constraintErrors, exception) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    ///     Registers the school-year conversion once for the whole model.
    /// </summary>
    /// <remarks>
    ///     Per-property registration would let a new entity carrying a school year silently map a
    ///     bare int and bypass the value object's range guard.
    /// </remarks>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Properties<SchoolYear>().HaveConversion<SchoolYearToIntConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        Type dbContextType = typeof(SparkrockRwcDbContext);
        modelBuilder.ApplyConfigurationsFromAssembly(dbContextType.Assembly);
        
         foreach (IMutableEntityType entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (entityType.IsOwned())
                continue;

            // For TPH inheritance, only apply filter to the root entity type
            IMutableEntityType rootType = entityType.GetRootType();
            if (rootType != entityType)
                continue;

            // soft delete filter - applies only to SoftDeletableEntity types (DEC-20)
            if (!typeof(SoftDeletableEntity).IsAssignableFrom(entityType.ClrType)) continue;
            MethodInfo? softDeleteMethod = typeof(SparkrockRwcDbContext)
                .GetMethod(nameof(GetSoftDeleteFilter), BindingFlags.Public | BindingFlags.Static);

            if (softDeleteMethod == null) continue;
            MethodInfo genericSoftDeleteMethod = softDeleteMethod.MakeGenericMethod(entityType.ClrType);
            object? softDeleteFilter = genericSoftDeleteMethod.Invoke(null, []);

            if (softDeleteFilter != null)
                modelBuilder.Entity(entityType.ClrType)
                    .HasQueryFilter((LambdaExpression)softDeleteFilter);
        }
    }
    
    public static LambdaExpression GetSoftDeleteFilter<TEntity>()
        where TEntity : SoftDeletableEntity
    {
        ParameterExpression entityParam = Expression.Parameter(typeof(TEntity), "e");

        // e.IsDeleted
        MemberExpression isDeletedProperty = Expression.Property(entityParam, nameof(SoftDeletableEntity.IsDeleted));

        // !e.IsDeleted
        UnaryExpression notDeleted = Expression.Not(isDeletedProperty);

        // Build lambda: e => !e.IsDeleted
        Expression<Func<TEntity, bool>> filter = Expression.Lambda<Func<TEntity, bool>>(notDeleted, entityParam);

        return filter;
    }
}