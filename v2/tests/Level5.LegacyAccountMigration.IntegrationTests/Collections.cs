namespace Level5.LegacyAccountMigration.IntegrationTests;

/// <summary>V2 Postgres only - for tests exercising ImportLegacyAccountUseCase/LegacyLinkConsistencyChecker directly.</summary>
[CollectionDefinition(Name)]
public sealed class V2PostgresCollection : ICollectionFixture<V2PostgresFixture>
{
    public const string Name = "V2Postgres";
}

/// <summary>Both V1 and V2 Postgres - for tests exercising the audit/migrate/verify commands end-to-end.</summary>
[CollectionDefinition(Name)]
public sealed class MigrationTestCollection : ICollectionFixture<LegacyPostgresFixture>, ICollectionFixture<V2PostgresFixture>
{
    public const string Name = "LegacyMigration";
}
