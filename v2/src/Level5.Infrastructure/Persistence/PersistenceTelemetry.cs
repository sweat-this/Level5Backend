namespace Level5.Infrastructure.Persistence;

/// <summary>
/// The one piece of database-technology-specific telemetry knowledge the Api composition root
/// needs: which <see cref="System.Diagnostics.ActivitySource"/> name to subscribe to for database
/// command/connection spans (Npgsql's own native tracing support - no separate EF Core/Npgsql
/// instrumentation package is needed). Kept here, not in Api, because it names a Npgsql
/// implementation detail that could change if the persistence provider ever did.
///
/// A <c>static readonly</c> field, deliberately not a <c>const</c>: a <c>const</c> is inlined by
/// the compiler into every call site, which would put Npgsql's own name back into Level5.Api's own
/// assembly - exactly the direct Npgsql coupling the architecture tests
/// (<c>Api_does_not_reference_persistence_libraries_directly</c>) exist to catch.
/// </summary>
public static class PersistenceTelemetry
{
    public static readonly string DatabaseActivitySourceName = "Npgsql";
}
