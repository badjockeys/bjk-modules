# bjk-modules

Feature modules for the Bad Jockeys platform. Each module is a self-contained vertical slice (API, use cases, infrastructure, domain), published as a NuGet package to GitHub Packages and loaded by a host: `bjk-identity-server` or `bjk-core`.

## Layout

```
src/
  BJK.Modules.Shared/            Module registration, MediatR validation pipeline, error handling,
                                 current user, safe startup migrations
  BJK.Modules.User/              Hosted by bjk-identity-server
    API/Controllers/             UserController
    API/Contracts/               Request/response records
    UseCases/
      Add/                       Register: sign-in account + profile
      Update/  Delete/           Change or soft-delete a profile (owner or admin)
      Reset/                     Forgot password: request token, confirm with new password
      Get/  List/                Read side (CQRS queries)
    Infrastructure/Services/     IUserRepository, IIdentityService (implemented by the host)
    Infrastructure/Data/         UserDbContext (schema: users) and Migrations/
    Domain/                      UserProfile
    UserModule.cs                Module entry point
  BJK.Modules.Media/             Hosted by bjk-core
    API/Controllers/             MediaController (/api/media)
    UseCases/
      Upload/                    Request an upload link, then complete the upload
      Get/                       One item, or the caller's own uploads
      Delete/                    Delete files and mark the record Deleted
    Infrastructure/Storage/      IMediaStorage over the S3 API (Cloudflare R2)
    Infrastructure/Messaging/    IMediaJobQueue over RabbitMQ, queue topology
    Infrastructure/Data/         MediaDbContext (schema: media) and Migrations/
    Domain/                      MediaItem, formats, storage keys
    MediaModule.cs               Module entry point
tests/
  BJK.Modules.User.Tests/
  BJK.Modules.Media.Tests/
```

## Hosting a module

```csharp
builder.Services.AddBjkModules(builder.Configuration, new UserModule("IdentityDatabase"));
// The host also registers: a token handler under ModuleAuthentication.Scheme ("BjkApi"),
// and for the User module, an IIdentityService implementation.

var app = builder.Build();
await app.Services.InitialiseBjkModulesAsync(); // applies each module's migrations

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
```

### Configuration

| Key | Notes |
|---|---|
| `ConnectionStrings:<name>` | The name passed to `new UserModule(...)`. The module's tables live in the `users` schema. |
| `MediatR:LicenseKey` | MediatR 13+ needs a licence key (free Community licence under $5M revenue). Store it in Key Vault. |
| `Database:AllowDestructiveMigrations` | Default `false`. See below. |

### One id per person

A user's profile id is the same GUID as their sign-in account id, which is the `sub` claim in every token. Every module (users, media, and posts later) identifies a person by that one id, so no module ever needs to look up another id.

### Access

| Scope | Gives |
|---|---|
| `bjk.users` | Required on every authenticated call. Users can read, update and delete their own profile. |
| `bjk.users.manage` | Admin: list users and manage anyone's profile. |

## Media module

Photos and videos follow the same flow; only the worker's processing differs.

1. `POST /api/media/uploads` with `{ fileName, contentType, sizeBytes }` creates a `media.Media` row (`Uploading`) and returns a signed link, valid for 15 minutes, to PUT the file straight into the private **originals** bucket. The API never handles the file.
2. The app PUTs the file to that link, sending the `Content-Type` header it was given.
3. `POST /api/media/{id}/complete` checks the file is in storage and within the size limit, sets `Processing`, and puts a job on the RabbitMQ queue.
4. A worker (built next) processes the file into the **public** bucket and sets `Ready`. Only `Ready` media is visible to other users.

Storage keys are `{userId}/{mediaId}/{fileName}`, where `userId` is the uploader's account id (the token's `sub`) and file names are ours (`original.mov`, `720p.mp4`, `thumbnail.jpg`, `image.webp`, `thumbnail.webp`).

| Endpoint | Access |
|---|---|
| `POST /api/media/uploads`, `POST /api/media/{id}/complete` | Signed-in user (`bjk.media` scope) |
| `GET /api/media/{id}` | Anyone with `bjk.media` once Ready; the owner or an admin before that |
| `GET /api/media/mine` | The signed-in user's own uploads, any state |
| `DELETE /api/media/{id}` | Owner, or admin (`bjk.media.manage`) |

Storage is Cloudflare R2. R2 speaks the S3 API and Cloudflare has no .NET SDK of its own, so the module uses the `AWSSDK.S3` client pointed at the R2 endpoint, as Cloudflare's R2 docs recommend. Nothing is sent to AWS and no AWS account is needed. The storage code sits behind `IMediaStorage`, so any S3-compatible store works by changing configuration.

Configuration (user secrets locally, Key Vault in Azure):

| Key | Notes |
|---|---|
| `ConnectionStrings:<name>` | Name passed to `new MediaModule(...)`. Tables in the `media` schema. |
| `Media:Storage:ServiceUrl` | `https://<account id>.r2.cloudflarestorage.com` |
| `Media:Storage:AccessKeyId`, `Media:Storage:SecretAccessKey` | R2 API token. Secret. |
| `Media:Storage:OriginalsBucket`, `Media:Storage:PublicBucket` | Private and public buckets |
| `Media:Storage:PublicBaseUrl` | Where the public bucket is served through Cloudflare |
| `Media:Queue:ConnectionUri` | `amqp://user:password@host:5672/vhost`. Secret. |
| `Media:Queue:QueueName` | Default `media.process`; failed jobs go to `media.process.failed` |
| `Media:MaxVideoBytes`, `Media:MaxImageBytes`, `Media:MaxVideoDurationSeconds`, `Media:UploadUrlLifetime`, `Media:OriginalRetention` | Defaults: 500 MB, 25 MB, 300 s, 15 min, 90 days |

## Database migrations

Each module owns its `DbContext`, schema, migrations and `__EFMigrationsHistory` table, so modules can share one database without touching each other's tables.

Migrations are applied when the host starts (`InitialiseBjkModulesAsync`). Before applying them, `DatabaseMigrator` checks every pending migration and **refuses to start** if any would drop a table, column or schema, or delete rows, listing the offending operations. To deliberately apply such a migration, review it and set `Database:AllowDestructiveMigrations=true` for that one deployment.

Adding a migration after changing an entity or its configuration:

```bash
dotnet tool restore
dotnet ef migrations add <Name> --project src/BJK.Modules.User --context UserDbContext --output-dir Infrastructure/Data/Migrations
dotnet ef migrations add <Name> --project src/BJK.Modules.Media --context MediaDbContext --output-dir Infrastructure/Data/Migrations
```

CI fails if the model has changed without a migration, and a unit test fails if any migration drops data.

## Branching and releases

- Work happens on `feature/*` branches merged into `master` by pull request (protect `master` in the repo settings).
- CI builds and tests every pull request.
- To publish a release, tag a commit on `master`: `git tag v0.2.0 && git push origin v0.2.0`.
- Every push to a `feature/*` branch publishes a preview package, `<VersionPrefix>-ci.<run number>` (e.g. `0.2.0-ci.7`), so other repos can test a branch before it merges. Bump `VersionPrefix` in `Directory.Build.props` after each release.
