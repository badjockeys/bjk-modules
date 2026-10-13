# bjk-modules

Feature modules for the Bad Jockeys platform. Each module is a self-contained vertical slice (API, use cases, infrastructure, domain), published as a NuGet package to GitHub Packages and loaded by the `bjk-core` API host.

## Layout

```
src/
  BJK.Modules.Shared/          Module registration, MediatR validation pipeline, error handling
  BJK.Modules.User/
    API/Controllers/           UserController
    API/Contracts/             Request/response records
    UseCases/{Add,Update,Reset,Delete}/   Command + validator + handler (MediatR CQRS)
    Infrastructure/Services/   Interfaces and implementations (repository, identity server client)
    Infrastructure/Data/       DbContext and table configurations (schema: users)
    Domain/                    UserProfile entity
    UserModule.cs              Module entry point
tests/
  BJK.Modules.User.Tests/
```

## Using a module from bjk-core

```csharp
builder.Services.AddBjkModules(builder.Configuration, new UserModule());

var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

`AddBjkModules` registers each module's services, then wires MediatR, FluentValidation and the module controllers across every module assembly in one place.

The host must also configure JWT bearer authentication against bjk-identity-server; the module controllers use `[Authorize]`.

### Configuration each module expects

| Key | Used by | Notes |
|---|---|---|
| `ConnectionStrings:AppDatabase` | All modules | Azure SQL app database. Each module uses its own schema. |
| `IdentityServer:Authority` | User | Base URL of bjk-identity-server. |
| `MediatR:LicenseKey` | Shared | MediatR 13+ needs a licence key (free Community licence under $5M revenue). Store it in Key Vault. |

## Database

Each module has its own `DbContext`, schema and `__EFMigrationsHistory` table. Migrations are created against the bjk-core host as the startup project, and applied by the deploy pipeline as an idempotent script rather than at app startup.

## Branching and releases

- Work happens on `feature/*` branches merged into `master` by pull request (protect `master` in the repo settings).
- CI builds and tests every pull request.
- To publish packages, tag `master` with a version: `git tag v0.1.0 && git push origin v0.1.0`.
