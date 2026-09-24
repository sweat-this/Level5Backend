# Level5Backend

## Local development setup

Secrets (`ConnectionStrings:DefaultConnection`, `Jwt:Key`) are never stored in `appsettings.json`
or any other file in this repo - they're loaded from
[`dotnet user-secrets`](https://learn.microsoft.com/aspnet/core/security/app-secrets) locally, and
from `ConnectionStrings__DefaultConnection` / `Jwt__Key` environment variables in production. See
`Program.cs` for the startup checks that enforce this.

To get a local Postgres instance and a real JWT signing key set up in one step:

```powershell
./scripts/setup-local-dev.ps1
```

This starts the Postgres container defined in `docker-compose.local-db.yml`, generates a
cryptographically random `Jwt:Key`, stores both secrets via `dotnet user-secrets`, and applies EF
Core migrations. Safe to re-run - it just rotates the JWT key and leaves the Postgres container and
its data alone.

Then `dotnet run` starts the API against that local database.

### Rotating just the JWT key

```powershell
./scripts/generate-jwt-key.ps1 | ForEach-Object { dotnet user-secrets set "Jwt:Key" $_ }
```

Rotating the key invalidates every previously-issued token - fine for local dev, worth knowing
before doing this in production.

### Postgres only

```powershell
docker compose -f docker-compose.local-db.yml up -d
```

Connects on `127.0.0.1:5432` (use `127.0.0.1` rather than `localhost`, see the compose file), database
`level5`, user `level5` (see the compose file for the local
dev password - it's not a real secret, just a fixed value so the container is reproducible).

### Applying new migrations

After pulling changes that include new files under `Migrations/`:

```powershell
dotnet ef database update
```
