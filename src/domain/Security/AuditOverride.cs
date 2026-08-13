namespace domain.Security;

/// <inheritdoc cref="IAuditOverride" />
public sealed class AuditOverride : IAuditOverride
{
    public bool IsActive { get; private set; }

    public Guid ActingUserId { get; private set; }

    /// <exception cref="InvalidOperationException">An override is already active.</exception>
    public IDisposable Begin(Guid actingUserId)
    {
        // Nesting would make "who is the actor" ambiguous at the point the interceptor reads it.
        if (IsActive)
            throw new InvalidOperationException("An audit override is already active; overrides do not nest.");

        IsActive = true;
        ActingUserId = actingUserId;

        return new Scope(this);
    }

    private void End()
    {
        IsActive = false;
        ActingUserId = default;
    }

    private sealed class Scope(AuditOverride owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner.End();
        }
    }
}
