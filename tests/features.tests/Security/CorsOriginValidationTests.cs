namespace features.tests.Security;

/// <summary>
///     The half of the CORS wildcard ban that the analyzer cannot cover.
/// </summary>
/// <remarks>
///     <para>
///         <c>src/api/BannedSymbols.txt</c> blocks <c>AllowAnyOrigin</c>, <c>SetIsOriginAllowed</c> and
///         <c>SetIsOriginAllowedToAllowWildcardSubdomains</c>, so a wildcard cannot be written in code.
///         It can still arrive through configuration: <c>WithOrigins("*")</c> — the sanctioned API,
///         which cannot be banned because it is the one the policy is built from — sets
///         <c>AllowAnyOrigin</c> on the resulting policy. A single <c>"*"</c> in
///         <c>Cors:AllowedOrigins</c>, from any provider including an environment variable, therefore
///         reproduces the exact policy that was removed, with every analyzer still green.
///     </para>
///     <para>
///         The failure mode is the reason this is worth a startup throw rather than a filter. With no
///         authentication anywhere in the system, the same-origin policy is the only access control
///         there is, so the wildcard does not weaken a defence — it removes the last one.
///     </para>
/// </remarks>
public sealed class CorsOriginValidationTests
{
    [Fact]
    public void ValidateOrigins_WhenOriginIsWildcard_Throws()
    {
        InvalidOperationException exception =
            Assert.Throws<InvalidOperationException>(() => api.ServiceExtensions.ValidateOrigins(["*"]));

        Assert.Contains("AllowAnyOrigin", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The wildcard is rejected wherever it appears, not only first.
    /// </summary>
    [Fact]
    public void ValidateOrigins_WhenWildcardFollowsAValidOrigin_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => api.ServiceExtensions.ValidateOrigins(["https://school.example", "*"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("school.example")]
    [InlineData("//school.example")]
    [InlineData("ftp://school.example")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://school.example/app")]
    [InlineData("https://school.example/")]
    [InlineData("https://school.example?a=1")]
    public void ValidateOrigins_WhenOriginCannotMatchAnOriginHeader_Throws(string origin)
    {
        Assert.Throws<InvalidOperationException>(() => api.ServiceExtensions.ValidateOrigins([origin]));
    }

    [Theory]
    [InlineData("https://school.example")]
    [InlineData("http://localhost:5173")]
    [InlineData("https://school.example:8443")]
    public void ValidateOrigins_WhenOriginIsAConcreteSchemeAndAuthority_DoesNotThrow(string origin)
    {
        api.ServiceExtensions.ValidateOrigins([origin]);
    }

    /// <summary>
    ///     An empty allowlist is the default and must stay startable — a policy that allows nothing is
    ///     inert, which is the point of registering it in every environment.
    /// </summary>
    [Fact]
    public void ValidateOrigins_WhenListIsEmpty_DoesNotThrow()
    {
        api.ServiceExtensions.ValidateOrigins([]);
    }
}
