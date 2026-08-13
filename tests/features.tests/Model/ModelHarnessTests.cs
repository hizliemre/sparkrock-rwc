using domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace features.tests.Model;

public sealed class ModelHarnessTests
{
    /// <summary>
    ///     The harness's own regression test. Everything else in this folder asserts names, so if the
    ///     convention drifts from what the application configures, every one of those assertions
    ///     checks a name that is never produced — and all of them still pass.
    /// </summary>
    [Fact]
    public void Model_UsesSnakeCasedPluralTableName()
    {
        IEntityType entityType = ModelFactory.Create().FindEntityType(typeof(TestEntity))!;

        Assert.Equal("test_entities", entityType.GetTableName());
    }
}
