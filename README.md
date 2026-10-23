# bjk-modules

Feature modules for the Bad Jockeys platform. Each module is a self-contained vertical slice (API, use cases, infrastructure, domain), published as a NuGet package to GitHub Packages and loaded by a host: `bjk-identity-server` or `bjk-core`.

## Layout

```
src/
  BJK.Modules.Shared/            The common base every module and host needs: module registration, MediatR
                                 validation pipeline, error handling, current user and sign-in scheme, staff roles
                                 and plans, safe startup migrations, deployed-settings checks, provider retries and
                                 MessageDeliveryException (Email, SMS), GET /api/me/data-export
    Moderation/  Notifications/  Small interfaces modules use to talk to each other without referencing each other:
    Uploads/  Users/             IModerationAuditLog, INotificationSender, IAreaAlerts, IHomeAreas, IMediaUsage,
                                 user lifecycle events and IUserLifecycleHandler
  BJK.Modules.Safety/            Online safety: TextSafety (plates, phones, emails, addresses, handles),
                                 IContentScreener (keywords + optional classifier, blocked web addresses),
                                 ICaptchaVerifier (Turnstile), the age verification step (AddAgeVerification),
                                 SafetyOptions. Used by Posts, Media and Profiles
  BJK.Modules.Queues/            Durable queues: SQL Server queues and topics (Rebus), outbox, message consumers,
                                 AddUserLifecycleHandler. Used by Media, User, Profiles, Posts and Notifications
  BJK.Modules.Observability/     AddBjkObservability: OpenTelemetry logs, traces and metrics over OTLP. Hosts only
  BJK.Modules.User/              Hosted by bjk-identity-server
    API/Controllers/             UserController
    API/Contracts/               Request/response records
    UseCases/
      Add/                       Register (13+, date of birth): sign-in account + account record
      SignUp/                    Date of birth rules; asking older accounts for theirs
      Deactivate/  Reactivate/   Deactivate (self with password, or admin); reactivate within the grace period
      Delete/                    Admin "delete now"; the retention job that deletes expired accounts
      Reset/                     Forgot password: request token, confirm with new password
      Get/  List/                Read side (CQRS queries)
      AgeChecks/                 Age check (18+, on demand, for sensitive posts): selfie or photo ID, status, admin reset
      AccountLifecycle.cs        Deactivate / reactivate / delete, each with an outbox event
    Infrastructure/Services/     IUserRepository, IIdentityService (implemented by the host)
    Infrastructure/AgeAssurance/ ILivenessChecker, IAgeEstimator, IDocumentAgeCheck; Yoti estimator;
                                 Development stand-ins; settings checked at startup
    Contracts/                   IAccountAgeLookup and the age_verified and minor claims, for the identity server's tokens
    Infrastructure/Data/         UserDbContext (schema: users: Accounts, Outbox) and Migrations/
    Infrastructure/              AccountRetentionService (hourly), account data export
    Domain/                      UserAccount, AgeRules (13+, under-18s), AgeCheckRules
    UserModule.cs                Module entry point
  BJK.Modules.Media/             Hosted by bjk-core
    API/Controllers/             MediaController (/api/media)
    UseCases/
      Upload/                    Request an upload link, then complete the upload
      Get/                       One item, or the caller's own uploads
      Delete/                    Delete files and mark the record Deleted
      Review/                    Moderators: review queue, approve, reject, add blur regions (audited)
    Infrastructure/Storage/      IMediaStorage over the S3 API (Cloudflare R2)
    Infrastructure/Messaging/    IMediaJobQueue on the media.process SQL Server queue (process, segment and assemble jobs)
    Infrastructure/Processing/   The worker: MediaProcessor (whole media, segments, assembly), ffmpeg, Redaction/
    Infrastructure/Data/         MediaDbContext (schema: media; outbox), VideoJobStore and Migrations/
    Domain/                      MediaItem, VideoJob + VideoSegment (segment progress), formats, storage keys
    Contracts/                   IMediaDirectory, and MediaEvents (media.ready / media.failed) for other modules
    MediaModule.cs               Module entry point
  BJK.Modules.Profiles/          Hosted by bjk-core
    API/Controllers/             ProfilesController (/api/profiles)
    UseCases/
      Create/  Update/  Get/     Create, edit, change @handle, set avatar; read by handle
      Blocks/                    Block, unblock, list my blocks
      Follows/                   Follow, unfollow, followers and following, Who to follow
      Moderation/                Admin: look up any profile, reset an offensive profile (audited)
    Contracts/                   IProfileDirectory (author names and avatars), IBlockDirectory, IFollowDirectory, IProfileModeration
    Infrastructure/Data/         ProfilesDbContext (schema: profiles) and Migrations/
    Domain/                      Profile, Handle rules
    ProfilesModule.cs            Module entry point
  BJK.Modules.Posts/             Hosted by bjk-core
    API/Controllers/             PostsController (/api/posts), ReportsController (public form, profile reports),
                                 AdminPostsController (/api/admin), ReelsController (/api/reels),
                                 HeatmapController (/api/heatmap)
                                 TrendsController (/api/trends)
                                 HashtagsController (/api/hashtags)
    UseCases/
      Create/  Update/  Delete/  Publish, edit and delete posts
      Get/                       One post, the public feed, my posts
      Reactions/  Comments/      React to posts and comments; comments and one level of replies
      Reports/                   Report a post, comment or profile; the public report form; admin review queue
      Appeals/                   Appeal a moderation decision once; admin decisions
      Moderation/                Screening queues, sensitive tag, audit log, report and appeal emails
      Safety/                    New-account limits
      Publishing/                Post now, publish when ready: PostMediaPublisher
      Reels/                     Reels: list (ranked, daily allowance by plan), record a play
      Heatmap/                   Incident heat map: cells, area figures, CSV export (entitlements by plan); category trends
    Infrastructure/              Moderation audit log (stored for every module), media event handler, lifecycle
                                 and data export, ReelsMaintenance (reels ranking refresh, play retention)
    Infrastructure/Data/         PostsDbContext (schema: posts) and Migrations/
    Domain/                      Post (+ PinRounding), PostMedia, Reaction, Comment, CommentReaction, Report,
                                 Appeal, ModerationAuditRecord
    PostsModule.cs               Module entry point
  BJK.Modules.Email/             Hosted by bjk-identity-server and bjk-core (no database, no endpoints)
    Contracts/                   IEmailSender, EmailMessage
    Layout/                      BrandedEmail: black-on-white email layout, text + HTML
    Templates/                   EmailTemplates: the wording of every email we send
    Infrastructure/              SendGrid sender; Log sender for local development
    EmailModule.cs               Module entry point
  BJK.Modules.SMS/               Hosted by bjk-identity-server and bjk-core (no database, no endpoints)
    Contracts/                   ISMSSender, SMSMessage, PhoneNumbers (E.164 check)
    Templates/                   SMSTemplates: the wording of every text we send
    Infrastructure/              Twilio sender; Log sender for local development
    SMSModule.cs                 Module entry point
tests/
  BJK.Modules.Shared.Tests/
  BJK.Modules.Safety.Tests/
  BJK.Modules.Queues.Tests/
  BJK.Modules.Observability.Tests/
  BJK.Modules.User.Tests/
  BJK.Modules.Media.Tests/
  BJK.Modules.Profiles.Tests/
  BJK.Modules.Posts.Tests/
  BJK.Modules.Email.Tests/
  BJK.Modules.SMS.Tests/
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
| `ConnectionStrings:<name>` | The name passed to `new UserModule(...)`. The module's tables live in the `users` schema of the `idsrv` database (the identity server hosts this module). |
| `Queues:*` | Durable queues in SQL Server; every key has a default. See [Messaging](#messaging-sql-server-queues). The identity server puts its queues in bjk-core's database in code (`UseQueuesIn("BadJockeysDatabase", createTables: false)`), not by a setting. |
| `Users:DeactivationGracePeriod` | Identity server only. Default `90.00:00:00`. |
| `Users:RetentionCheckInterval` | Identity server only. Default `01:00:00`. |
| `AgeAssurance:*` | Identity server only. The age check for sensitive posts: see [Age policy](#age-policy-13-and-the-age-check-for-sensitive-posts). |
| `MediatR:LicenseKey` | MediatR 13+ needs a licence key (free Community licence under $5M revenue). A secret: user secrets locally, a container secret when deployed. |
| `Database:AllowDestructiveMigrations` | Default `false`. See below. |
| `Safety:RequireAgeVerification` | Default **`false`** (since 19 Oct 2026): posting, commenting and reacting no longer need the age check. `true` makes posting, editing posts, commenting, reacting and reporting need the `age_verified` claim again; kept for future use. |
| `Safety:NewAccounts:Period`, `:MaxPosts`, `:MaxComments` | New-account limits. Defaults `1.00:00:00`, 3, 20. bjk-core only. |
| `Safety:OffTopic:ReportsToHold`, `:RemovalsToPause`, `:Window`, `:Pause` | Keeping posts about driving: off-topic reports that hold a post, and pausing repeat posters. Defaults 3, 2, `30.00:00:00`, `7.00:00:00`; 0 turns holding or pausing off. bjk-core only. |
| `Posts:AllowedRegions` | Where incidents may be posted. Default (empty): `GB`, the UK including Northern Ireland. Also `IM` (Isle of Man), `JE` (Jersey), `GG` (Guernsey). Unknown codes stop startup. bjk-core only. |
| `Posts:ReportsInbox` | Where every new report is emailed, as well as going to the moderation queue (until there's a ticketing system). Default `support@badjockeys.com`; empty turns it off. Must be a valid address. bjk-core only. |
| `Posts:AppUrl` | The web app's address (e.g. `https://badjockeys.com`), for the "Open it on Bad Jockeys" link in report emails. Optional. bjk-core only. |
| `Screening:Endpoint`, `:FlagThreshold`, `:HoldThreshold`, `:Timeout` | bjk-core only. The keyword list always runs; `Endpoint` (optional) adds a classifier service. Thresholds default 0.5 and 0.8; timeout default 3 s. See "Online safety". |
| `Plans:Overrides:<user id>` | `free`, `premium` or `premium_plus`, for testing paid features before billing exists. See [Plans](#plans-and-entitlements). |
| `Reels:*` | Daily plays and the ranking job. bjk-core only. See [Reels](#reels). |
| `Heatmap:*` | Suppression threshold and history by plan. bjk-core only. See [Incident heat map](#incident-heat-map). |
| `Hashtags:*` | Trending's minimums (`TrendingMinPosts` 3, `TrendingMinAuthors` 3), how many it shows (`TrendingTags` 10, `TrendingPlaces` 5), `BaselineDays` (7), `RebuildInterval` (5 minutes), `HourlyRetention` (90 days) and a `Blocklist` of extra tags never recorded. All optional. bjk-core only. See [Hashtags and Trending](#hashtags-and-trending). |
| `Captcha:TurnstileSecretKey` | bjk-core: the public report form, checked with Cloudflare Turnstile. Required (checked at startup); locally Cloudflare's test key. Same key as the identity server. Secret. |

`user-secrets.template.json` lists these with local development values.

### Deployed environments refuse to start without every setting

Outside Development (DEV, QA, Staging and Live alike), a host refuses to start until every setting it needs has a live value, and the error lists **all** the problems at once. Modules declare their own with `services.RequireWhenDeployed(key, requirement, isValid)` (`BJK.Modules.Shared.Configuration`); hosts add theirs the same way. A value still in the template's `<...>` placeholder form fails too, required or not.

| Setting | Required outside Development | From |
|---|---|---|
| `MediatR:LicenseKey` | Set | Every host |
| `Email:SendGrid:ApiKey`, `Email:FromAddress` | Set (without the key emails are only logged; use SendGrid's `SandboxMode` on test servers) | Email module |
| `SMS:Twilio:AccountSid` (and its credentials) | Set (without Twilio texts are only logged) | SMS module |
| `Captcha:TurnstileSecretKey` | A real secret, not one of Cloudflare's `1x0000`/`2x0000`/`3x0000` test keys | Captcha (Posts' public report form) |
| `Notifications:AppUrl` | An https address | Notifications module |
| `Posts:AppUrl`, `Reels:ViewerKeySecret` | An https address; set | Posts module |
| `Media:Storage:SensitiveBucket` | Set | Media module |
| `Media:Redaction:FaceModelPath`, `PlateModelPath`, `PersonModelPath` | Set | Media worker |

The modules' other checks (SendGrid or Twilio keys once chosen, the age check providers, storage settings and so on) still apply as before.

**`--check-settings`:** a host started with this argument builds its services, runs every startup settings check, prints the result and exits (0 when ready, 1 with every problem listed) without touching the database. `SettingsCheck.Requested(args)` and `SettingsCheck.Run(app.Services)` do it. Deploy pipelines run each image with the environment's settings and `--check-settings` before rolling it out, so a missing setting fails the pipeline rather than the release.

### Logs, traces and metrics (OpenTelemetry)

Every host calls `builder.AddBjkObservability("<service name>")` (`BJK.Modules.Observability`): ASP.NET Core requests (not `/health`), outgoing HTTP calls and SQL Server calls are traced, request, HTTP client and .NET runtime metrics are recorded, and logs go out with them. Everything is sent over OTLP to `OTEL_EXPORTER_OTLP_ENDPOINT` (with `OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf`): locally Elastic's OpenTelemetry collector in bjk-core's Docker group (`http://localhost:4318`, viewed in Kibana at http://localhost:5601), when deployed Elastic Cloud's managed OTLP endpoint with `OTEL_EXPORTER_OTLP_HEADERS=Authorization=ApiKey <key>`. Only those settings change between them. Without an address nothing is exported; outside Development an https address is required. No personal data: query string values are redacted, SQL text isn't recorded, nothing identifies the user, and log messages never contain emails, names or IP addresses.

### One id per person, one copy of each fact

A user's id is the same GUID everywhere: the sign-in account (`auth.AspNetUsers`), the account record (`users.Accounts`), the core profile and every post, comment and media item. It's the `sub` claim in every token, so no module ever needs to look up another id.

Nothing is stored twice. The **email and password** are in the sign-in account only. The **account status** (active, deactivated, deletion date) is in `users.Accounts` only. The **handle, display name, bio and avatar** are in the core profile only.

### Access

| Scope | Gives |
|---|---|
| `bjk.users` | Required on every authenticated call to the identity server's API. Users can read and deactivate their own account. |
| `bjk.users.manage` | Admin: list accounts; deactivate, reactivate or delete any of them. |

| Endpoint (identity server) | Access |
|---|---|
| `POST /api/users` | Anonymous + CAPTCHA: register (`email`, `password`, `dateOfBirth` e.g. `"2010-05-31"`). Under 13: 400 with error code `too_young`, nothing created or kept. |
| `GET /api/users/me` | Signed-in user: email, status, dates, date of birth, age check |
| `POST /api/users/me/deactivate` | Signed-in user, confirming with their password |
| `POST /api/users/reactivate` | Anonymous + CAPTCHA: email and password of a deactivated account |
| `POST /api/users/password-reset`, `.../confirm` | Anonymous (+ CAPTCHA for the request) |
| `GET /api/users/me/age-check` | Signed-in user: age check status, attempts left, which routes are open (none when `underAgeByDateOfBirth`) |
| `POST /api/users/me/age-check/face` | Signed-in adult (by date of birth): multipart `video` (3–5 s head-turn selfie) |
| `POST /api/users/me/age-check/document` | Signed-in adult (by date of birth): multipart `image` (photo of an ID) |
| `POST /api/admin/users/{id}/age-check/reset` | Admin: clear the age check so the user takes it again |
| `GET /api/users?status=`, `GET /api/users/{id}` | Admin (or yourself for `{id}`) |
| `POST /api/users/{id}/deactivate`, `POST /api/users/{id}/reactivate`, `DELETE /api/users/{id}` | Admin. `DELETE` deletes permanently now. |

## Age policy: 13+, and the age check for sensitive posts

Decided 19 Oct 2026 (replaces "18+ with an age check at sign-up").

**Minimum age 13, by declared date of birth.** Every sign-up route asks for the date of birth: `POST /api/users`, the identity server's sign-up page and a first Google, Apple or Facebook sign-in (`AddUserCommand`, `AddExternalUserCommand`, both validated by `DateOfBirthValidation`). Under 13 is refused with a kind message (error code `too_young`); no account is created and that date of birth isn't kept or logged. The date of birth is stored on `users.Accounts.DateOfBirth` (a `date`), shown to the user and moderators on the account, included in "Download my data", and deleted with the account. Accounts created before this change have none; the identity server asks for it at their next sign-in (`SetMyDateOfBirthCommand`, once only; after that only support can change it). Ages use the UTC date; a 29 February birthday counts from 1 March in other years (`AgeRules`).

**Under-18s.** Tokens carry **`minor` = `"true"`** while the declared date of birth says the user is under 18. The identity server works it out each time it issues a token (`IAccountAgeLookup`), so it disappears on the 18th birthday. Modules read it with `ClaimsPrincipal.IsMinor()` (Shared) to apply the high-privacy defaults of the ICO Children's Code.

**Sign-up is the date of birth, Cloudflare Turnstile and email confirmation.** There's no selfie or liveness check at sign-up (decided 19 Oct 2026). The identity server refuses tokens (`IsActive` = false) only to an older account with no date of birth on record, until it gives one (`IAccountAgeLookup` / `AccountAgeFacts.IsSignUpComplete`).

## Age check (18+, on demand, for sensitive posts)

The "highly effective" age check is no longer part of sign-up. A signed-in user takes it when they first want to view a sensitive post ("Verify your age to view sensitive posts", `/Account/CheckAge` on the identity server). This is the only place the liveness check is used: against spoofing, before the face age estimate. Their tokens carry **`age_verified` = `"true"`** once they've passed. If the declared date of birth says they're under 18, the check isn't offered at all (`underAgeByDateOfBirth`; the commands refuse with a kind message explaining sensitive posts are for adults). Posting, commenting and reacting don't need it (`Safety:RequireAgeVerification` is off by default).

1. **Head-turn selfie.** The browser records a 3–5 second video while the user turns their head left, then right.
   - `ILivenessChecker` (open source, on our servers) checks it shows one live person turning their head. The identity server registers `YuNet` (OpenCV Zoo YuNet face detection through ONNX Runtime).
   - If it passes, `IAgeEstimator` (a specialist provider behind HTTP) estimates the age.
2. **Rules** (`AgeCheckRules`):
   - Estimated **25 or over** (`AgeAssurance:ChallengeAge`) passes.
   - **18 to 24** needs photo ID. The selfie route then closes, so retrying can't fish for a higher estimate.
   - **Can't tell** or a failed liveness check: try again, or use photo ID.
   - **Clearly under 18** (even allowing for the provider's margin of error) fails with a kind message. Photo ID is still offered, because estimates can be wrong.
3. **Photo ID** (`IDocumentAgeCheck`): 18 or over passes. Under 18 on the ID is final until an admin resets it.
4. **Retries:** 3 attempts (selfie or ID) per 24 hours (`AgeAssurance:MaxAttemptsPerDay`).
5. **Support reference.** Every attempt gets a short reference (`AgeCheckReference`, e.g. `AGE-7KQ2MX`), stored with the outcome and written to the log line that records it. When the check fails, can't verify the user, or they've used their tries (`AgeCheckDto.needsSupport`), the identity server's page shows the reference with a "Contact support" link. Support finds the attempt by reference (`ageCheck.reference` on `GET /api/admin/users/{id}`). It holds nothing about the person, and a reset clears it.

**What's kept.** Only the outcome, on `users.Accounts`: `AgeCheckStatus` (NotChecked, DocumentRequired, Passed, Failed), `AgeCheckMethod` (FaceEstimate, Document), `AgeCheckResult` (the last attempt's outcome), `AgeCheckProvider`, `AgeCheckRecordedAt`, `AgeCheckReference`, and the attempt counter. The video and ID photo are read into memory (the upload limits stop ASP.NET buffering them to disk), handed to the providers, and zeroed when the request ends. They're never written to the database or logs. Neither are the estimated age or anything read from the ID.

**Where it shows up.**
- Admins and moderators see the status (never the video) as `ageCheck` on `GET /api/admin/users/{id}`.
- Admins can reset it.
- "Download my data" includes it, with the date of birth.
- Deleting the account deletes it, because it lives on the account row.

| Setting | Default | Notes |
|---|---|---|
| `AgeAssurance:ChallengeAge` | 25 | At least 18 |
| `AgeAssurance:MaxAttemptsPerDay` | 3 | |
| `AgeAssurance:MaxVideoBytes`, `MaxImageBytes` | 15 MB, 10 MB | |
| `AgeAssurance:Liveness:ModelPath` | | OpenCV Zoo's `face_detection_yunet_2023mar.onnx`. Set: the identity server's YuNet head-turn check runs (it reads the rest of `AgeAssurance:Liveness`) |
| `AgeAssurance:Yoti:SdkId`, `PrivateKey` (secret) | | Yoti's facial age estimation with anti-spoofing (`YotiAgeEstimator`), used when both are set. One frame from the start of the selfie video goes to Yoti's `/age-antispoofing`, with each request signed (SHA256withRSA over the method, path with `nonce` and `timestamp`, and base64 body). A spoof, a margin over `MaxUncertainty`, or a picture Yoti can't use (no face, too dark...) counts as "couldn't tell", so photo ID is offered |
| `AgeAssurance:Yoti:BaseUrl`, `Timeout`, `MaxUncertainty`, `FfmpegPath` | `https://api.yoti.com/ai/v1`, 20 s, 6, `ffmpeg` | |
| `AgeAssurance:Development:LivenessPasses`, `EstimatedAge`, `DocumentAge` | true, 30, 30 | What the stand-ins answer locally, for trying every path. Ignored outside Development |

**No provider switches.** Each step uses the real provider whose settings are filled in. Without one, the Development environment uses a stand-in (and logs that it does); anywhere else the app refuses to start and names what's missing. Photo ID has no real provider yet (Yoti's document check needs their hosted session flow, still to build), so for now **deployed environments won't start** until it is. A host adds a provider by registering another `ILivenessChecker`, `IAgeEstimator` or `IDocumentAgeCheck` singleton that reports `IsConfigured`.

## Deactivation, retention and deletion

1. **Deactivate.** The user confirms with their password, or an admin does it. The sign-in account is locked and its sessions and refresh tokens are revoked. `users.Accounts` becomes `Deactivated` with `DeletionDueAt` 90 days later.
2. **Hidden, not deleted.** bjk-core hides the user's profile (it looks like it doesn't exist), posts and comments. The handle stays reserved for them.
3. **Reactivate.** Within the 90 days, the user sends their email and password to `POST /api/users/reactivate`. Sign-in works again and everything reappears.
4. **Delete.** Once `DeletionDueAt` passes, the identity server's hourly retention job deletes the sign-in account and the account record. bjk-core then permanently deletes:
   - the profile;
   - posts, together with the comments, reactions and reports on them;
   - the user's own comments, reactions and reports elsewhere;
   - every file under `{userId}/` in both R2 buckets, and every media row.

   An admin can skip the wait with `DELETE /api/users/{id}`.

The identity server and bjk-core have separate databases, so the identity server tells core through **user lifecycle events** (`Deactivated`, `Reactivated`, `Deleted`):

- **Outbox.** Each event is saved in `users.Outbox` in the same transaction as the account change. A background publisher then publishes it to the topic `bjk.users`, which writes a copy into every subscribed queue table in the app database (see [Messaging](#messaging-sql-server-queues) for how the identity server reaches it). An event is never lost, and never sent for a change that was rolled back. Events wait in the outbox until a queue subscribes, so nothing is lost on a fresh environment where bjk-core hasn't started yet.
- **Core's queue.** bjk-core reads its own queue, `[messaging].[bjk-core.user-events]` (subscribed to `bjk.users` when it starts), and passes each event to every module's `IUserLifecycleHandler` (Profiles, Posts, Media), one at a time.
- **Failures.** A failing event is retried after 5, 10, 20 and 40 seconds. After 5 attempts it moves to `[messaging].[bjk-core.user-events.failed]` for someone to look at.
- **Idempotent.** Handlers are safe to run twice.

## Download my data

`GET /api/me/data-export` is served by **both** the identity server and bjk-core. Each returns a JSON file of everything that host holds about the signed-in user, gathered from every module's `IPersonalDataExporter`:

| Host | Sections |
|---|---|
| Identity server | `account`: id, email, status, dates, date of birth, age check outcome |
| bjk-core | `profile`; `posts` (posts, comments, reactions, reports made); `media` (every upload, with a 24-hour download link for each original still kept, and the processed files) |

The apps call both endpoints and offer the two files (or one zip) as "Download my data". A deactivated user can't sign in, so they reactivate, download, then deactivate again if they want.

## Messaging (SQL Server queues)

Durable jobs and events are rows in SQL Server, in the `messaging` schema of the app database (`badjockeys`), handled by [Rebus](https://github.com/rebus-org/Rebus) and [Rebus.SqlServer](https://github.com/rebus-org/Rebus.SqlServer) (both MIT). There's no separate broker, so the queues get the zone-redundant Azure SQL database's redundancy and backups at no extra cost, and run anywhere SQL Server runs. MediatR stays for in-process commands and queries; Rebus is only used for queues and events.

**Abstractions** (`BJK.Modules.Queues`), which is all the modules see:

- `IMessageSender`: `SendAsync(queue, message)` (point to point, e.g. a media job) and `PublishAsync(topic, message)` (a copy to every subscribed queue, e.g. user lifecycle events). Messages are a type name plus JSON (`OutgoingMessage`), optionally delayed (`Delay`).
- `IMessageConsumer` + `services.AddMessageConsumer<T>(configuration, connectionStringName, new MessageConsumerDefinition(queue) { MaxAttempts, Concurrency, Topics })`: returns `Completed`, `Retry` or `DeadLetter`; `OnGivenUpAsync` runs once when a message is given up on.
- `db.SaveChangesWithMessagesAsync(ct => sender.SendAsync(...), ct)`: saves a DbContext's changes and the messages sent meanwhile in **one transaction**, so a job is never queued for a change that was rolled back, and never lost after one that was saved.
- The outbox (`OutboxMessage`, `AddOutboxPublisher<TContext>(configuration, topic, connectionStringName)`), for events that must reach another database.

Rebus only appears in `Messaging/SqlServer/`. Going back to a broker later (Rebus has RabbitMQ and Azure Service Bus transports) is a change there and in configuration, not in the modules.

**Tables**, created on first use (except by the identity server, below):

| Table | What |
|---|---|
| `[messaging].[media.process]` | Media processing jobs (process, segment, assemble). Fed by the API (upload complete, moderator re-process; in the same transaction as the media change) and the workers (segment and assemble jobs). Read by the media worker. 3 attempts. |
| `[messaging].[bjk-core.media-events]` | `media.ready` / `media.failed` for Posts, subscribed to the topic `bjk.media`. Read by the API host. 5 attempts. |
| `[messaging].[bjk-core.user-events]` | `Deactivated` / `Reactivated` / `Deleted` from the identity server, subscribed to the topic `bjk.users`. Read by the API host. 5 attempts. |
| `[messaging].[<queue>.failed]` | Dead letters: messages given up on, with the error (`rbs2-error-details` header) and the queue they came from. |
| `[messaging].[subscriptions]` | Which queues subscribe to which topics (`topic`, `address`). |

Each queue row has `id`, `priority`, `visible` (hidden until then: delays and retries), `expiration`, `headers`, `body` (UTF-8 JSON, readable in SQL), and the lease columns `leasedat`, `leaseduntil`, `leasedby`, `deliverycount`.

**How a message is handled.**

- **Competing consumers.** A consumer claims the next visible row with `UPDLOCK, READPAST, ROWLOCK` (others skip locked rows, so each message goes to one consumer) and leases it for `Queues:LeaseDuration` (60 s), renewed every 20 s while the work goes on. Done: the row is deleted. Replicas share a queue simply by reading the same table.
- **Attempts.** `deliverycount` goes up on every claim, including claims by a worker that then crashed, like RabbitMQ's `x-delivery-count`; it reaches the code as `ReceivedMessage.Attempt` (Rebus's `rbs2-delivery-count` header). A host that stops cleanly hands its message back without using up an attempt.
- **Retries.** A failure (an exception or `Retry`) hides the message for `Queues:RetryDelay` (5 s), doubling each time up to `Queues:MaxRetryDelay` (5 min).
- **Dead letters.** If the last attempt fails, `OnGivenUpAsync` runs (the media worker marks the media `Failed`) and the message moves to `<queue>.failed`. If a worker crashed on the last attempt, the next delivery gives up straight away without running the work again. A consumer can also dead-letter an unreadable message at once.
- **Crashes.** A worker that dies stops renewing its lease, so the message is delivered again when the lease ends (within about a minute).
- **At least once.** A message can occasionally be handled twice (a crash after the work but before the delete), so every handler is idempotent.

**The identity server (another database).** The identity server's data is in `idsrv`; the queues are in `badjockeys` on the same server. The identity server keeps its transactional outbox (`users.Outbox`, in the same transaction as the account change) and its outbox publisher writes the events straight into bjk-core's queue tables through a second connection string, `ConnectionStrings:BadJockeysDatabase`, chosen in code with `services.UseQueuesIn("BadJockeysDatabase", createTables: false)`. Because that's another database, an event is published first and then marked as published, so it may very occasionally be delivered twice (handlers are idempotent). Events wait in the outbox while no queue subscribes to `bjk.users`, so a fresh environment loses nothing before bjk-core's first start. The identity server never creates tables; bjk-core does.

That connection uses its own database user in `badjockeys`, allowed to read the subscriptions and add rows to the queue tables, and nothing else (no access to bjk-core's data, no deletes, no schema changes):

```sql
-- In master: a SQL login, its password a container secret (plain SQL logins work the same on any cloud).
CREATE LOGIN [bjk-identity-server-messaging] WITH PASSWORD = '<a long random password>';
-- Then in the badjockeys database:
CREATE USER [bjk-identity-server] FOR LOGIN [bjk-identity-server-messaging];
GRANT SELECT ON OBJECT::[messaging].[subscriptions] TO [bjk-identity-server];
GRANT INSERT ON SCHEMA::[messaging] TO [bjk-identity-server];
```

**Settings** (section `Queues`, all optional):

| Key | Default | Notes |
|---|---|---|
| `Schema` | `messaging` | |
| `PollingInterval` | `00:00:01` | How often an idle consumer looks for new messages. |
| `LeaseDuration` | `00:01:00` | How soon a crashed worker's message is retried. |
| `RetryDelay`, `MaxRetryDelay` | `00:00:05`, `00:05:00` | Waits between attempts. |
| `UserEvents`, `MediaEvents`, `MediaProcessing` | `bjk-core.user-events`, `bjk-core.media-events`, `media.process` | bjk-core's queues. |

**Why Rebus, not Wolverine.** Both are MIT, support .NET 10 and have a SQL Server transport with row locking, retries, dead letters, delayed messages and pub/sub. Rebus is a small bus that sits next to MediatR rather than replacing it, has a stable API (v8; the project is over ten years old), needs no code generation at startup (the media worker scales from zero), stores the queues in plain tables we can query (and scale on with KEDA), and its transport's connection is independent of the app database, which makes the identity server's cross-database publishing simple. Wolverine has more built in (durable attempt counts, an EF Core outbox) but is a whole application framework: a second handler model alongside MediatR, runtime code generation, a dozen tables plus node and leader agents, its transport tied to its own message store's database, and crash recovery that depends on detecting dead nodes. Rebus's SQL transport doesn't keep a delivery count or delay retries, so `SqlServerQueueTransport` adds both (about 200 lines over Rebus's lease transport).

**Scaling the media worker.** Container Apps can scale the worker with KEDA's SQL Server (`mssql`) scaler on the number of jobs due, waiting or in progress, so a busy replica isn't removed while it works (see bjk-core's docs):

```sql
SELECT COUNT(*) FROM [messaging].[media.process] WITH (NOLOCK)
WHERE [visible] <= SYSDATETIMEOFFSET() AND [expiration] > SYSDATETIMEOFFSET()
```

Dead letters to watch (they should stay at 0): `SELECT COUNT(*) FROM [messaging].[media.process.failed]`, and the same for the other `.failed` tables.

## Media module

Photos and videos follow the same flow; only the worker's processing differs.

1. `POST /api/media/uploads` with `{ fileName, contentType, sizeBytes }` creates a `media.Media` row (`Uploading`) and returns a signed link, valid for 15 minutes, to PUT the file straight into the private **originals** bucket. The API never handles the file.
2. The app PUTs the file to that link, sending the `Content-Type` header it was given.
3. `POST /api/media/{id}/complete` checks the file is in storage and within the size limit, sets `Processing`, and puts a job on the `media.process` queue in the same database transaction (both happen, or neither).
4. The **media worker** (bjk-core's worker container, using `services.AddMediaProcessing(...)` from this module) takes the job, blurs faces and number plates, converts the file and publishes it to the **public** bucket, then sets `Ready`. If automatic blurring failed or wasn't sure, it keeps the blurred files private and sets `NeedsReview` instead; a moderator approves (then `Ready`) or rejects (then `Failed`). Videos longer than 5 seconds are split into segments that several worker replicas blur at once (see "Videos in segments"). Only `Ready` media is visible to other users.
5. Every change to `Ready` or `Failed` (and deleting media before it was ready) publishes a `media.ready` / `media.failed` event (see "Media events"). The Posts module uses them to publish posts that were created while their media were still processing.

### Processing

| | Video | Photo |
|---|---|---|
| Output | `720p.mp4`: H.264, no sound (see `Media:Redaction:RemoveAudio`), at most 720 pixels high and 30 fps, fast start for streaming | `image.webp`: at most 2048 pixels on its longest side |
| Thumbnail | `thumbnail.jpg`, from one second in | `thumbnail.webp`, at most 640 pixels |
| Feed copy | (none) | `feed.webp`: at most 1080 pixels on its longest side, same quality (82), never enlarged. Feeds load it (`feedUrl`); the full `image.webp` (`playbackUrl`) opens on tap |
| Checks | Must contain a picture; no longer than `Media:MaxVideoDurationSeconds` (**30 seconds**, with half a second of slack for clips trimmed to exactly 30 s) | Must contain a picture |

- **Metadata is stripped** from every published file, so no GPS position, device or owner details go public. The original keeps them, in the private bucket, only until it's deleted (see "Originals" below: as soon as the post is published, 24 hours at most otherwise).
- **SHA-256** of each original is recorded, for spotting duplicates later.
- **Limits** (since 19 Oct 2026): videos up to **30 seconds** and **150 MB**, photos up to 25 MB. 150 MB is plenty for 30 seconds even of 4K dashcam footage (about 80 to 100 MB at typical bit rates), and the apps shrink videos before uploading. The size is checked when the upload is requested and again when it completes; the length once the worker has read the video. The messages say what to do, e.g. "Videos can be up to 30 seconds long, and this one is 47 seconds. Trim it to 30 seconds or less and upload it again." or "Videos can be up to 150 MB, and this one is 400 MB. Trim it to 30 seconds or export it at a lower quality, then upload it again."
- **Failures.** A file ffmpeg can't read, with no picture, or too long fails straight away, with a reason the app can show. Anything temporary (storage or database unavailable) is retried; the job is delivered at most 3 times, and the last attempt marks the media `Failed`, so nothing stays at `Processing`.
- **Deleted meanwhile.** If the owner deletes the media while it's being processed, the worker removes what it published.
- ffmpeg runs as a child process with its arguments passed as a list, never through a shell.
- The worker doesn't load the Media API, migrations or the user lifecycle consumer. The API does those, so account events aren't split between the two.

| Setting (`Media:Processing:*`, worker only) | Default |
|---|---|
| `FfmpegPath`, `FfprobePath` | `ffmpeg`, `ffprobe` on the PATH (ffmpeg 5.1 or later) |
| `MaxConcurrentJobs` | 1: one job per replica (each job already uses every core); scale out with more replicas |
| `FfmpegThreads` | 0 = every core of the replica (`Environment.ProcessorCount`, which respects the container's CPU limit; ffmpeg's own automatic choice doesn't) |
| `SegmentSeconds` | 5: videos longer than this are split into segments of this length, processed in parallel; 0 turns splitting off |
| `SegmentOverlapSeconds` | 1.0: extra video each segment analyses either side of its own part, so tracking matches at the joins |
| `VideoHeight`, `VideoCrf` | 720, 23 |
| `ImageMaxSize`, `FeedImageMaxSize`, `ThumbnailMaxSize` | 2048, 1080, 640 |
| `CommandTimeout` | 15 minutes |
| `WorkingDirectory` | The system temp folder |

HEIC photos from iPhones need an ffmpeg built with HEIF support (ffmpeg 7.1 or later); with an older ffmpeg they fail with a "couldn't read" reason.

Storage keys are `{userId}/{mediaId}/{fileName}`, where `userId` is the uploader's account id (the token's `sub`) and file names are ours (`original.mov`, `720p.mp4`, `thumbnail.jpg`, `image.webp`, `feed.webp`, `thumbnail.webp`). The feed copy follows the photo everywhere: review copies, approval, the sensitive bucket and signed links for sensitive photos (never public then), re-processing and deletion. Photos processed before it existed have no `feedKey`; the API then returns `feedUrl: null` and the apps use `playbackUrl`.

| Endpoint | Access |
|---|---|
| `POST /api/media/uploads`, `POST /api/media/{id}/complete` | Signed-in user (`bjk.media` scope) |
| `GET /api/media/{id}` | Anyone with `bjk.media` once Ready; the owner or an admin before that |
| `GET /api/media/mine` | The signed-in user's own uploads, any state |
| `DELETE /api/media/{id}` | Owner |
| `GET /api/vault`, `POST /api/vault/uploads`, `POST /api/vault/{id}/complete`, `GET /api/vault/{id}/download`, `DELETE /api/vault/{id}` | The signed-in user's own vault (see "Vault") |
| `GET`, `POST /api/media/{id}/share-copy` | Owner only (404 for anyone else): the video's vertical copy for sharing (see "Share copies") |
| `DELETE /api/admin/media/{id}?reason=` | Moderator (`bjk.media.manage`); the reason is required and the action is audited |

Storage is Cloudflare R2. R2 speaks the S3 API and Cloudflare has no .NET SDK of its own, so the module uses the `AWSSDK.S3` client pointed at the R2 endpoint, as Cloudflare's R2 docs recommend. Nothing is sent to AWS and no AWS account is needed. The storage code sits behind `IMediaStorage`, so any S3-compatible store works by changing configuration.

Configuration (user secrets locally, container settings and secrets when deployed):

| Key | Notes |
|---|---|
| `ConnectionStrings:<name>` | Name passed to `new MediaModule(...)`. Tables in the `media` schema of the main app database, `badjockeys`. |
| `Media:Storage:ServiceUrl` | `https://<account id>.r2.cloudflarestorage.com` |
| `Media:Storage:AccessKeyId`, `Media:Storage:SecretAccessKey` | R2 API token. Secret. |
| `Media:Storage:OriginalsBucket`, `Media:Storage:PublicBucket` | Private and public buckets |
| `Media:Storage:PublicBaseUrl` | Where the public bucket is served through Cloudflare |
| `Media:Storage:SensitiveBucket` | Optional private bucket for the real files of sensitive media (default: the originals bucket, under `sensitive/`). Set it if the originals bucket has any deletion (lifecycle) rule |
| `Media:Storage:VaultBucket` | Optional private bucket for the vault (default: the sensitive bucket, under `{userId}/vault/`). Needs a CORS rule allowing `PUT` and `GET` from the web app, and no lifecycle rule |
| `Media:Vault:*` | The vault by plan: `FreeRetentionDays` 30, `PremiumRetentionDays` 365, `PremiumPlusRetentionDays` 1095; `FreeQuotaBytes` 2 GB, `PremiumQuotaBytes` 25 GB, `PremiumPlusQuotaBytes` 100 GB; `MaxFileBytes` 2 GB; `DowngradeNoticeDays` 7 |
| `Media:SensitiveLinkLifetime` | How long signed links to sensitive media work (default 10 minutes, 1 minute to 1 hour) |
| `Media:Processing:SensitivePreviewSourceSize`, `SensitivePreviewSize` | Blurred preview: shrink to 64 px, then enlarge to 480 px (worker) |
| `Media:SensitiveDetection:*` | Automatic graphic content and nudity checks (worker; required when deployed; see below) |
| `Media:KnownImages:*` | Checking uploads against known illegal images (worker; see below) |
| `Queues:MediaProcessing` | Default `media.process` (table `[messaging].[media.process]`); jobs given up on go to `[messaging].[media.process.failed]` |
| `Media:MaxVideoBytes`, `Media:MaxImageBytes`, `Media:MaxVideoDurationSeconds`, `Media:UploadUrlLifetime` | Defaults: 150 MB (157286400), 25 MB, 30 s, 15 min |
| `Media:UnusedOriginalRetention`, `Media:OriginalSweepInterval` | Defaults 24 hours and 1 hour: the most an original is kept when nothing releases it sooner, and how often the API host sweeps (see "Originals"). `Media:OriginalRetention` (90 days) is gone |
| `Queues:MediaEvents` | Default `bjk-core.media-events`: the API host's queue for `media.ready` / `media.failed` (dead-lettered to `.failed` after 5 attempts) |

### Originals (deleted as soon as they're not needed)

Owner's decision, 19 Oct 2026, replacing 90-day retention: an uploaded original (which still has faces, plates, sound and GPS) is deleted as soon as nothing needs it.

- **Post published:** when a post's media are all `Ready` and it's published (straight away if they were ready when posting, or when the last one becomes ready, including after a moderator approves media from the review queue or automatic detection tags it sensitive), the Posts module calls `IMediaDirectory.ReleaseOriginalsAsync` with the post's id. Since 23 Oct 2026 each original is first copied into its owner's **vault** (see "Vault"), then the originals are deleted (`media.Media.OriginalDeletedAt` is set). An original whose copy failed is left for the sweep.
- **Post deleted or discarded before publishing:** its media are deleted, originals included, at once (as before).
- **Processing failed, media never posted, upload abandoned:** the API host's sweeper (`OriginalsSweeper`, every `Media:OriginalSweepInterval`, 1 hour) deletes the original once `Media:UnusedOriginalRetention` (24 hours) has passed since upload (`OriginalDeletesAt`), or since an upload was started and never completed. Media still processing or waiting for a moderator keeps its original until it's ready (then the post's publication, or the sweep, deletes it).
- **Consequences:** re-processing after a moderator adds blur regions works from the processed file (see "Moderation of media"); the admin link to the unblurred original and data-export original links only work while the original exists. The R2 lifecycle rule on the originals bucket is no longer needed (the review copies and rework copies live there too, so don't add a short one).

### Vault (people's originals, kept for them)

Owner's decision, 23 Oct 2026: each person has a vault of their original clips, unblurred with their sound and GPS, kept privately as evidence (for the police or an insurer). It reverses the "delete originals at once" rule above only for what goes in a vault.

- **What goes in:** the originals of their published posts' photos and videos (copied inside the storage service, `IMediaStorage.CopyInsideAsync`, so large files take moments), while there's room; and clips uploaded straight to the vault (`POST /api/vault/uploads`, then PUT the file, then `POST /api/vault/{id}/complete`, which checks the real size). A clip posted from the vault (the web app re-uploads it trimmed, with `vaultClipId` on `POST /api/media/uploads`) isn't copied in again.
- **By plan** (`Media:Vault`): Free keeps clips 30 days in 2 GB, Premium 12 months in 25 GB, Premium + 3 years in 100 GB. Files up to 2 GB each. A full vault refuses uploads (400), and new posts' originals are then deleted as before.
- **Plans without a request:** plans come with each request (the token or `Plans:Overrides`). The plan seen when someone opens their vault or uploads anything is recorded in `media.VaultAccounts`, so a post published in the background, and the cleanup, know it (`VaultPlans`; an override wins, then the plan last seen, then Free).
- **Plan changes:** every clip's expiry follows the plan (`VaultClip.FollowPlan`): an upgrade keeps clips longer at once; after a downgrade, clips the new plan would already have deleted are kept 7 more days (`DowngradeNoticeDays`), so nobody loses a clip without notice. The vault page shows each clip's expiry.
- **Private:** only the owner sees their vault (404 for anyone else); downloads are 15-minute signed links named `bad-jockeys-original-{date}.{ext}`, never the uploaded name. Files are in `Media:Storage:VaultBucket` (or the sensitive bucket), under `{userId}/vault/{clipId}/`. Never the originals bucket, whose lifecycle rule would cut them short.
- **Deleted** by the hourly sweep once past `ExpiresAt` (and vault uploads never finished within 24 hours), by the owner at any time, or with the account (the files go with the user's storage folder; `media.VaultClips` and `media.VaultAccounts` rows too). The data export includes the vault, with 24-hour links (section `vault`).

### Videos in segments (parallel processing)

A 30-second video is processed as six 5-second pieces by up to six worker replicas at once, then joined, so it's ready several times sooner than in one job (measured timings below). Photos and videos of 5 seconds or less (`SegmentSeconds`) are processed in one job as before.

1. **Plan** (the normal `media.process` job): the worker downloads the original, checks it (length, picture), records its SHA-256, length and size, and saves a plan in `media.VideoJobs` with one row per segment in `media.VideoSegments`. Segments are counted in output frames on one frame grid (150 frames at 30 fps), so every worker agrees exactly where each segment starts and ends. A last piece shorter than a quarter of a segment joins the one before. It then queues one **segment** job per segment on the same queue.
2. **Segment** (any replica): downloads the original and decodes only its part, plus `SegmentOverlapSeconds` either side (ffmpeg seeks a second early and picks frames by their place on the grid, which is exact whatever the keyframes). Detection runs on the same every-5th frames as for the whole video, and the tracker sees the overlap too, so a face or plate crossing a join is tracked as if the video were in one piece: blurs don't drop or start late at the joins (tested with a face that disappears just before a join). Only the segment's own frames are blurred and encoded, with exactly the same x264 settings as a whole video (each segment starts on a keyframe), and stored privately at `{userId}/{mediaId}/segments/r{revision}/{index}.mp4`. The segment is then marked done.
3. **Assemble** (once all segments are done): joins the segments with ffmpeg's concat demuxer without re-encoding (`-c copy`, fast start), adds the soundtrack from the original if `RemoveAudio` is off, makes the thumbnail from the joined video, publishes it and sets `Ready`, or keeps it private and sets `NeedsReview` if any segment's detection was uncertain or failed (or a moderator added regions). The segment files are deleted.

**Restart-safe and idempotent.** Progress is in the database, and every update that parallel workers might race on is one conditional SQL statement: a segment is marked done only if it wasn't already (so a redelivered or duplicated job is skipped, not redone); exactly one worker wins the claim to queue the assemble job when the last segment finishes; a redelivered segment job (the worker may have died after claiming but before queueing) queues it again, and the assemble job stops at once if the media is already finished. A plan job delivered again re-queues only the unfinished segments. Jobs for an older revision (a moderator re-processed the media) or for deleted media are skipped. A segment that fails three times marks the media `Failed` ("Processing failed. Try uploading the file again."), as a whole-video job would. Face and plate counts are summed over segments, so a face crossing a join counts once in each.

### Share copies (vertical videos for TikTok, Reels, Shorts and X)

Owner's decision, 23 Oct 2026: the person who posted a video can download a vertical copy to post on other apps; everyone else shares the link.

- **Asking:** `POST /api/media/{id}/share-copy` (202) checks the video is the caller's, Ready and not marked sensitive (400 otherwise; photos can't be shared this way), records a `media.ShareCopies` row (`Rendering`) and queues a `ShareCopy` step on `media.process`. If a current copy is already made, or being made, that's returned instead. `GET` gives the state: `None`, `Rendering`, `Ready` (with a 15-minute signed link that downloads as `bad-jockeys-clip.mp4`) or `Failed`.
- **Making it** (the media worker, `MediaProcessor.ShareCopy.cs`): from the **published** file, which is already blurred and silent, never the original. 1080×1920 at 30 fps: the video fitted in the middle over a blurred, darkened copy of itself, a small "badjockeys.com" watermark bottom right, then a 2.5-second end card ("Seen on Bad Jockeys", badjockeys.com, faces and number plates are blurred). No sound, no metadata. The two pictures are built into the assembly (`Infrastructure/Processing/Share`). The copy is stored privately at `{userId}/{mediaId}/share/vertical.mp4` in the originals bucket.
- **When it's made again:** after the video is re-processed (a moderator added blur: a new revision), after 30 days (`ShareCopy.KeptFor`; the bucket's 90-day rule removes old files), if it failed, or if it's been "rendering" for over 15 minutes (lost). A failure only marks the copy `Failed`; the media is never touched.
- **Removed** with the media (`DELETE`, account deletion); the row goes with the media row. Media tagged sensitive later can't be shared: `GET` shows `None` and the worker refuses.

### Media events (`media.ready`, `media.failed`)

When media becomes `Ready` (processed, or approved by a moderator) or `Failed` (processing failed, too long or too big, rejected by a moderator), or is deleted before it was ready, `MediaDbContext` writes a `MediaEvent` (`eventId`, `mediaId`, `userId`, `type`: `Ready` / `Failed`, `reason`, `occurredAt`) to `media.Outbox` in the same transaction as the status change, whichever host made it (the API or a worker). The API host publishes the outbox to the topic **`bjk.media`** (message type `media.ready` or `media.failed`) using Shared's outbox publisher, as for user events; the queues are in the same database, so each batch is published and marked as published in one transaction. Workers write to the outbox but don't publish it. Consumers register `IMediaEventHandler` with `services.AddMediaEventHandler<T>(configuration, connectionStringName)`; the host reads them from `Queues:MediaEvents` (5 attempts, then `.failed`). A message can arrive twice, so handlers re-read the current state. `NeedsReview` doesn't publish an event (it's still on its way).

### Automatic blurring (faces and number plates)

Every photo and video is blurred by the worker before anything is published (Online Safety Act design, 18 Oct 2026).

**How it works**

1. ffmpeg decodes the picture (photos: upright, at most 2048 px; videos: at most 720p and 30 fps) into memory over a pipe. Nothing unblurred is written out.
2. Open-source detection models run on the CPU with ONNX Runtime (`Microsoft.ML.OnnxRuntime`, MIT): YuNet for faces (each frame at full size and again at 640 px for faces close to the camera) and LPD-YuNet for number plates (the whole frame plus a 2x2 grid of overlapping zoomed tiles for small, distant plates), plus **YOLOX-S for people** (OpenCV Zoo's `object_detection_yolox_2022nov.onnx`, Apache-2.0, COCO). The top 22% of every person found, full width, is blurred as their head (`RedactionTarget.Head`). Person detectors find pedestrians far more reliably than face detectors at night, at a distance, side-on or from behind. On the owner's night-time clip, YOLOX found 141 people in 48 of 60 sampled frames, where YuNet found 51 faces. Head blurs never send media to review; a missing or broken person model does. Added 22 Oct 2026.
   For videos, plate detection (and the content checks) run on a copy of each analysed frame scaled down to at most 640 pixels on its longest side (`DetectionMaxSize`), and the boxes are mapped back to the full frame; plates are searched in zoomed 2x2 tiles of that copy, so small ones are still found. **Faces are searched in their own copy, scaled up or down to 1440 pixels on its longest side** (`FaceDetectionSize`; changed 22 Oct 2026 after the owner's night-time test clip showed most pedestrians' faces unblurred). YuNet finds faces down to about 10 pixels, and finds far more of the 10–20 pixel faces on pavements and behind windscreens once they're enlarged. On that clip (724x470, 30 s, sampled twice a second) the old 640 px copy found 5 faces in 60 frames, full size 11, and 1440 px 51 (in 27 of the 60 frames). It costs roughly 100–200 ms per analysed frame. It still misses faces that are very dark, side-on or turned away: a person detector that blurs the head area is the next step for those. Photos are always analysed at full size.
3. Each box is grown by 25% on every side (`BoxPadding`) and blurred irreversibly: reduced to a few large blocks of average colour (at most about 8 across and 3 down a plate) and then smoothed. A light Gaussian blur, which can be partly undone, isn't used.
4. **Videos:** detection runs on every 5th frame (`DetectEveryNthFrame`). A tracker links boxes from one pass to the next (by overlap, or by distance for fast movers), matching each detection against where a track is heading (its last box moved on by its speed) rather than where it was last seen, and interpolates a box for every frame in between. Once a face or plate is being followed, a weaker detection (down to `FaceTrackConfidence` / `PlateTrackConfidence`, 0.3) still continues it, so the blur holds while a plate turns side-on or blurs with motion; weak detections never start a new blur on their own. A face or plate missed by up to 3 passes in a row (`MaxMissedDetections`) stays blurred across the gap, and each track starts one interval before its first detection and ends one after its last, following its motion, so blurs don't flicker, drop out or appear late. The video is read once; frames wait in memory only until detection has run far enough ahead (about 20 frames, ~55 MB at 720p).
5. The blurred frames are encoded to H.264 as before. **The soundtrack is removed** (`-an`) unless `RemoveAudio` is false. Metadata (GPS, device) is stripped. Thumbnails are made from the blurred output only.
6. Photos: the blurred picture is converted to WebP and its thumbnail as before.

**Fail safe.** The media goes to `NeedsReview` instead of `Ready` when:

- either model isn't configured, its file is missing, or it won't load;
- a detector throws on any frame (it's then not used again for that job);
- any detection is uncertain: a photo detection, or a video track whose best score never reaches `FaceConfidentAt` / `PlateConfidentAt` (0.8), while scoring at least `FaceMinConfidence` / `PlateMinConfidence` (0.5), the blur threshold. `FaceConfidentAt` is 0.5 by default (owner, 22 Oct 2026), so faces never hold an upload (anything found is blurred, and heads are blurred from the person detector); plates do, at 0.8;
- a moderator added blur regions (the result is always checked again);
- `ReviewEverything` is on.

The blurred files are still produced, but kept in the **private** bucket under `{userId}/{mediaId}/review/` (originals bucket) until a moderator approves them. They're never public before that, and the media can't be posted (Posts only accepts `Ready` media). The reason is in `ReviewReason`.

**Limits to know.** Detection can miss things without being "uncertain" (nothing is found at all), so a miss doesn't always reach review; moderators add missed blurs by hand. The plate model was trained on Chinese plates: in our test a synthetic UK plate scored 0.75 (uncertain, so reviewed), and real UK plates may score lower or be missed. In a night dashcam test (22 Oct 2026) it found rear yellow plates but missed the white front plates of oncoming cars, so each vehicle's plate area is blurred when no plate is found on it (`BlurPlateAreas`). Consider `ReviewEverything=true` at launch until real-world accuracy has been checked, and replace the plate model if a permissively licensed UK/EU-trained one becomes available (the `IRedactionDetector` interface makes that a new class).

**Models and licences**

| | Model | Licence | Source (pinned) |
|---|---|---|---|
| Faces | YuNet, `face_detection_yunet_2026may.onnx` (230 KB, any input size) | MIT (Shiqi Yu) | OpenCV Zoo `models/face_detection_yunet`, commit `47534e2`, SHA-256 `ebafce4e3c118d6554634be5c27ab333b4c047a9a8c3faf1d7cf93101c22f0f0` |
| Number plates | LPD-YuNet, `license_plate_detection_lpd_yunet_2023mar.onnx` (4.1 MB, 320x240 input) | Apache 2.0 (Dong Xu, watrix.ai) | OpenCV Zoo `models/license_plate_detection_yunet`, same commit, SHA-256 `6d4978a7b6d25514d5e24811b82bfb511d166bdd8ca3b03aa63c1623d4d039c7` |
| Runtime | ONNX Runtime (`Microsoft.ML.OnnxRuntime` 1.30.0) | MIT | NuGet |

No Ultralytics/YOLO code or models (AGPL) are used. The models are **not in git**. The worker image downloads them at build time with `scripts/download-redaction-models.sh`, which pins the URLs to one OpenCV Zoo commit and fails unless every SHA-256 matches. In bjk-core's worker Dockerfile:

```dockerfile
COPY --from=modules scripts/download-redaction-models.sh /tmp/
RUN /tmp/download-redaction-models.sh /opt/bjk/models
ENV Media__Redaction__FaceModelPath=/opt/bjk/models/face_detection_yunet_2026may.onnx \
    Media__Redaction__PlateModelPath=/opt/bjk/models/license_plate_detection_lpd_yunet_2023mar.onnx
```

(or copy the script into bjk-core; it only needs `curl` and `sha256sum`). Locally, run the script into a folder and set the two paths in user secrets. Without them, every upload goes to `NeedsReview`, which is safe but means approving each one.

| Setting (`Media:Redaction:*`, worker only) | Default |
|---|---|
| `FaceModelPath`, `PlateModelPath` | Not set: the detector is unavailable and media goes to review |
| `FaceMinConfidence`, `PlateMinConfidence` | 0.5: boxes scoring at least this are blurred |
| `FaceConfidentAt`, `PlateConfidentAt` | 0.5 and 0.8: anything blurred that never scores this sends the media to review (so faces never do; plates under 0.8 do) |
| `PersonModelPath` | YOLOX-S model; not set: the person detector is unavailable and media goes to review |
| `HeadMinConfidence`, `HeadTrackConfidence` | 0.4 and 0.25: people scoring at least this have their head blurred (videos: weaker detections continue a head already blurred) |
| `BlurPlateAreas` | true: the person model also finds vehicles (cars, motorbikes, buses, vans and lorries); where the plate model found no plate on one, the lower middle of the vehicle (middle 70% of its width, 45–92% of its height) is blurred. Never sends media to review |
| `VehicleMinConfidence`, `VehicleTrackConfidence` | 0.3 and 0.2: vehicles scoring at least this have their plate area blurred (videos: weaker detections continue one already blurred) |
| `FaceTrackConfidence`, `PlateTrackConfidence` | 0.3: videos only; a weaker detection can continue a face or plate already being blurred (at most the MinConfidence) |
| `BoxPadding` | 0.25 of the box's width/height added on each side |
| `DetectEveryNthFrame` | 5 (6 detection passes a second at 30 fps) |
| `MaxMissedDetections` | 3 |
| `PlateTiles` | true (also search zoomed tiles for small plates; about 6x the plate detection cost) |
| `RemoveAudio` | true |
| `ReviewEverything` | false |
| `InferenceThreads` | 0 = every core of the replica (`Environment.ProcessorCount`); ONNX Runtime's intra-op threads, which don't spin while idle so ffmpeg keeps its CPU |
| `DetectionMaxSize` | 640: video frames are analysed for plates at most this big (boxes mapped back); 0 for full size. Photos always full size |
| `FaceDetectionSize` | 1440: video frames are scaled up or down to this (longest side) to look for faces; 0 for the frame as it is |
| `ExecutionProvider` | `Cpu`. `Cuda` is reserved for a GPU worker (see below) |
| `CudaDeviceId` | 0 |
| `VideoTimeout` | 25 minutes per video (or per segment) |

**GPU-ready.** `Media:Redaction:ExecutionProvider=Cuda` makes the detectors ask ONNX Runtime for its CUDA provider. A future GPU worker only needs the `Microsoft.ML.OnnxRuntime.Gpu` package in place of `Microsoft.ML.OnnxRuntime` (same API, no code changes) and a CUDA base image; ffmpeg stays on the CPU. With today's CPU package, `Cuda` makes both detectors unavailable (logged as an error), so every upload goes to review rather than being published unchecked.

**Timing** (measured in CI, three runs on 19 Oct 2026 which varied by up to 60% between runners, on a 2-vCPU GitHub runner with the Media tests running alone; 30-second 1080p30 clips; decode, detect, track, blur, encode and thumbnail, not counting storage downloads and uploads):

| 30-second 1080p30 clip | Time | Of which detection |
|---|---|---|
| Crowd photo panning (~200 faces a frame, worst case for blurring), **one job** | 31–50 s | 16–30 s |
| The same, **6 segments** of 5 s, each as its own job | 5.4–11.2 s each (39–63 s in total: the overlap and per-job start-up cost about a quarter more) | |
| Joining the segments + thumbnail | 0.3 s | |
| **Estimated with 6 worker replicas in parallel** (probe + slowest segment + join) | **7.5–11.6 s, about 4x faster** | |
| Moving test pattern (no faces, detailed picture), one job | 41–62 s | 16–30 s |

Per frame analysed: faces 24–59 ms at full 720p, about 4 times less on the 640 px copy now used; plates 65–138 ms with the zoomed tiles (12–26 ms whole frame only), which is now most of the detection time. For comparison, before this change the same clips took 106–172 s per minute of video. Add a second or two per segment for downloading the original from R2, plus the queue wait if fewer than 6 replicas are free; with one replica the segments run one after another (about 25% slower than one job), so give the worker a KEDA maximum of at least 6. Speed scales roughly with cores. To go faster at some cost in safety: `PlateTiles=false` (detection would drop to roughly a quarter) or `DetectEveryNthFrame=10`. `VideoTimeout` (25 minutes) stops a runaway job; meanwhile the worker keeps renewing the job's lease on the queue.

### Sensitive media (posts tagged sensitive)

Media in a post tagged sensitive (by its author, a moderator, or automatic detection) is **never in the public bucket**: only a heavily blurred preview is public, and the real files are read through short-lived signed links.

- **The mark.** `media.SensitiveMedia` holds one row per sensitive media item, with who marked it (`Post` or `Automatic`) and, for automatic marks, the score. The Posts module marks or clears it through `IMediaDirectory.SetSensitiveAsync` whenever a post is created sensitive, tagged or untagged; clearing leaves automatic marks unless a moderator clears them. It's a separate table so tagging a post never clashes with a worker that's processing its media.
- **Where the files are.** `media.Media.IsRestricted` says whether the processed files are kept privately, in the **sensitive bucket** (`Media:Storage:SensitiveBucket`, or the originals bucket if not set) at `{userId}/{mediaId}/sensitive/720p.mp4` and `.../sensitive/thumbnail.jpg`; `PreviewKey` is the blurred preview in the public bucket, `{userId}/{mediaId}/preview.webp`. The keys keep their published names either way. Set `SensitiveBucket` to a separate private bucket (no lifecycle rule, no public access) if the originals bucket has any lifecycle (deletion) rule, or sensitive media would disappear with it.
- **The blurred preview** is made by the worker from the (already face- and plate-blurred) thumbnail, which for a video is a frame one second in: shrunk so its longest side is `Media:Processing:SensitivePreviewSourceSize` (64) pixels, which destroys detail, blurred, enlarged to `SensitivePreviewSize` (480) and blurred hard again, as WebP without metadata.
- **Processing media that's already marked** (a post made sensitive while its video was processing, or detection flagged it): the worker uploads the real files to the sensitive bucket and the preview to the public bucket, and sets `Ready` with `IsRestricted`. After saving it checks the mark again, so a post tagged or untagged during processing is caught at once. A moderator approving a sensitive item from the review queue copies the files to the sensitive bucket and queues a job to make the preview.
- **Tagging or untagging later** queues a `Visibility` job on the existing media job queue (same transport, `MediaJobStep.Visibility`). It always works from the current mark, not the one at the time of queueing. Tagged: copy the public files to the sensitive bucket (if not there yet), make and publish the preview, save `IsRestricted`, then, only after a fresh read shows the media still marked, delete the public copies and call `IMediaCachePurger` for Cloudflare. Untagged: copy the files back to the public bucket (if missing), save, delete the preview. Every step is safe to repeat; a job that stops half way is simply run again; a save that clashes with another change starts again from the saved state; late or duplicate jobs do nothing. The private copies are kept after untagging (so two racing jobs can never lose the files) and deleted with the media. A job that keeps failing ends in the failed table (`[messaging].[media.process.failed]`) for replay; it never fails the media itself.
- **Cloudflare cache purge.** Published files are cached for a year, so after the public copies are deleted Cloudflare can keep serving them until purged. `IMediaCachePurger` is the hook; the default only logs a warning (event id 4801, `MediaCachePurgeNeeded`, naming the media id and file names). Purging (Cloudflare's purge-by-URL API, or a cache rule) is infra work: register a real `IMediaCachePurger` in the worker host before `AddMediaProcessing`, or alert on that event.
- **Who gets what.** `IMediaDirectory.GetAsync` returns restricted media with no playback or thumbnail address, only `PreviewUrl`; `GetWithSensitiveLinksAsync` returns signed links (`Media:SensitiveLinkLifetime`, 10 minutes, `LinksExpireAt`). The Posts module decides who may see them (age-verified adults, the author, moderators). `GET /api/media/{id}` blurs sensitive media (`isBlurred: true`, `previewUrl` only) for anyone but the uploader, moderators and age-verified adults; `GET /api/media/mine` always gives the uploader their own files; data exports give 24-hour signed links; the moderator review endpoints give signed links. Deleting media deletes its private copies and preview too.

### Automatic content checks: graphic content, nudity and known illegal images

Every photo, and sampled frames of every video, are checked by the media worker **before anything is published**, inside the existing blurring pass (frames already decoded and scaled down for face and plate detection, so nothing is decoded twice). Three checks, one pass:

| Check | How | What happens |
|---|---|---|
| **Known illegal images** | Meta's PDQ perceptual hash (`Pdq`, open source) of each checked frame against the PDQ hashes in `media.KnownImageHashes`; SHA-256 and MD5 of the original file against the file hashes there, before any processing | **Blocked.** Nothing is published, the original (and any copy) is deleted, the media is `Failed` with "This file can't be posted.", a `media.KnownImageMatches` row is kept (uploader, matched hash, list, distance; no picture), and a critical log entry is written |
| **Nudity** | OpenAI's CLIP (ViT-B/32, MIT licence; Xenova's quantised ONNX export of the vision model) as a zero-shot classifier: the frame's embedding against built-in text embeddings (`clip-prompts.json`) for ordinary road scenes, graphic content and nudity | At or above `NudityThreshold`: **held** (`NeedsReview`, "Possible nudity…"), never public until a moderator approves it |
| **Graphic content** | The same CLIP run (one model run per frame covers both) | At or above `Threshold`: marked sensitive automatically, so it's shown blurred and its post is queued for a moderator (below) |

Settings in `Media:SensitiveDetection` (worker only). There's no on/off or provider setting: with `ModelPath` set CLIP runs; without it a stand-in that flags mostly red pictures runs locally, and **deployed workers won't start**. A model that's missing or fails sends every upload to review (fail safe):

| Setting | Default | |
|---|---|---|
| `ModelPath` | | The quantised CLIP vision model, `https://huggingface.co/Xenova/clip-vit-base-patch32/resolve/d15189d7028b43f1d3e65039190477f6af591c2a/onnx/vision_model_quantized.onnx` (89 MB, SHA-256 `583fd111…9299`); bjk-core's `pipelines/scripts/download-redaction-models.sh` and local setup fetch it |
| `Threshold` | `0.35` | Graphic content score at or above which media is marked sensitive. Low on purpose: over-flag, moderators confirm or undo |
| `NudityThreshold` | `0.3` | Nudity score at or above which media is held for a moderator |
| `SampleEvery` | `3` | Check every Nth frame the blurring pass analyses (it analyses every 5th): about 2 frames a second at 30 fps. Photos are always checked |

Settings in `Media:KnownImages` (worker only):

| Setting | Default | |
|---|---|---|
| `Enabled` | `true` | Can't be `false` outside Development. An empty list blocks nothing |
| `RefreshInterval` | `00:05:00` | How often each worker checks SQL for changes to the list (it reloads only when the row count or newest row changed) |
| `PdqMaxDistance` | `31` | Bits (of 256) that may differ for a PDQ match: the figure Meta and the hash-sharing programmes use |
| `PdqMinQuality` | `50` | Frames with a lower PDQ quality (flat or blank pictures) aren't checked or recorded |
| `SyncInterval` | `06:00:00` | How often each hash list source is synced into SQL |

**The known-image list lives in SQL**, not in files: `media.KnownImageHashes` (kind `Pdq`, `Sha256` or `Md5`; value; `Source`; optional `Reference`). Rows come from three places:

1. **Hash list providers** (the IWF Hash List once we're members): an `IKnownImageSource` implementation per provider, registered in the worker host, fetches the provider's whole list over its API; `KnownImageSync` replaces that source's rows every `SyncInterval` (adds new hashes, removes withdrawn ones; a failed fetch keeps the previous list and logs a critical error). None is registered yet: the IWF adapter gets written against their API once we're members, and should be pointed at their test feed (if they have one) in DEV and QA first.
2. **Moderators** (`Source = Moderation`): `POST /api/admin/media/{id}/block-copies` adds a removed image's SHA-256 and the PDQ hashes recorded when it was processed (`media.Media.PerceptualHashes`: one for a photo, up to 8 spread through a video; not kept for videos processed in segments, which get the file hash only), so the same picture, or a resized or recompressed copy, is blocked if anyone uploads it again. Admins can also add a hash by hand (e.g. one given by the police).
3. **Tests** (`Source = Test`): harmless test hashes that bjk-core's local setup adds, so the blocking flow can be tried end to end.

Each worker keeps the list in memory (`KnownImageList`, refreshed by `KnownImageListRefresher`): a check is a few milliseconds of arithmetic (a linear scan of PDQ hashes: about 1 ms per 100,000 hashes per frame) with no database or network call. Until the list has loaded once, uploads go to review ("The known-image list hasn't loaded yet."). A frame match is passed from the blurring pass to the processor as a review reason starting `known-image:` (so it survives the segment path, where results are stored as reasons); the processor blocks the media and the marker is never shown to anyone.

**What we keep after a block.** The match record (no picture), the critical log entry, and the media row (`Failed`). The files are deleted. Whether to preserve evidence for the police instead is a legal question to settle with the IWF/NCA when we join; reporting is recorded with `POST /api/admin/known-images/matches/{id}/reported`.

**Scores are kept for tuning.** Every processed media keeps its highest graphic content score (`media.Media.SensitiveScore`) and nudity score (`NudityScore`) whether or not they crossed a threshold. For videos processed in segments the nudity score isn't kept (a high one still sends the video to review).

**How sensitive marks work.** At or above `Threshold`, the worker marks the media sensitive (`Automatic`) **before** publishing, so it goes straight to the sensitive bucket with a blurred preview and is never public; the Posts module then tags its post (`sensitiveSource: Automatic`) and queues it for a moderator. If the classifier is unavailable or fails on any frame, the media goes to `NeedsReview` ("The graphic content and nudity check failed.") instead of being published unchecked.

**Accuracy.** Zero-shot CLIP is a rough classifier, so it only ever adds caution: nudity holds media for a person, graphic content blurs it for a person to confirm; neither removes anything. Expect false positives (red cars, brake lights, sunsets, fire engines for graphic content; skin-toned or blurry frames for nudity) and misses (small or distant casualties). Tune both thresholds from the recorded scores against moderators' decisions. The ONNX export was checked against the PyTorch model (cosine similarity 0.987 for the quantised file). PDQ is checked in the tests against Meta's reference implementation.

**What these checks don't cover.** New illegal images that aren't on any list (the nudity check and user reports are the safeguards there); text-only harms (content screening, in the Posts module); audio (removed by default). The IWF lists themselves need membership.

**Latency.** CLIP ViT-B/32 (quantised) takes roughly 30–60 ms per frame on a 4-vCPU worker; PDQ about 1–5 ms. At `SampleEvery=3` a 30-second clip has 60 frames checked: about 2–4 s of CPU, spread over the six 5-second segments processed in parallel, so roughly **0.5–1 s added before the clip is live**, plus a one-off model load (about a second) per worker replica. Photos: one frame, under 100 ms.

### AI suggestions: removed (22 Oct 2026)

The owner removed every AI suggestion for good on 22 Oct 2026: "Suggest a description" (Qwen3-VL), the "Looks like" type chips and the CLIP summaries behind them, and the web app's pre-set trim handles. The `AiSuggestions` (media) and `TypeSuggestions` (posts) migrations were deleted rather than reversed, because nothing was deployed. CLIP stays, only for the nudity and graphic content checks above. A local database made before the change keeps working; to drop the leftovers, run this against `badjockeys`:

```sql
DROP TABLE IF EXISTS media.Descriptions;
ALTER TABLE media.Media DROP COLUMN IF EXISTS ClipSummary;
DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc
    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('media.VideoSegments') AND c.name = 'ClipFrames');
IF @df IS NOT NULL EXEC('ALTER TABLE media.VideoSegments DROP CONSTRAINT ' + QUOTENAME(@df));
ALTER TABLE media.VideoSegments DROP COLUMN IF EXISTS ClipSummary, ClipFrames;
ALTER TABLE posts.Posts DROP COLUMN IF EXISTS SuggestedTypeFlags;
DROP TABLE IF EXISTS messaging.[media.describe];
DELETE FROM media.__EFMigrationsHistory WHERE MigrationId = '20261021210200_AiSuggestions';
DELETE FROM posts.__EFMigrationsHistory WHERE MigrationId = '20261021210210_TypeSuggestions';
```

### Road user type: removed (22 Oct 2026)

The owner removed the optional Driver / Cyclist / Both setting completely: `PUT /api/profiles/me/road-user`, `roadUser` and `showRoadUser` on profiles, and `roadUser` on post and comment authors. The `RoadUserAndAgeBand` migration keeps its name but now only adds `IsMinor`. A local database made before the change keeps working; to drop the columns, run this against `badjockeys`:

```sql
DECLARE @df sysname = (SELECT dc.name FROM sys.default_constraints dc
    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
    WHERE dc.parent_object_id = OBJECT_ID('profiles.Profiles') AND c.name = 'ShowRoadUser');
IF @df IS NOT NULL EXEC('ALTER TABLE profiles.Profiles DROP CONSTRAINT ' + QUOTENAME(@df));
ALTER TABLE profiles.Profiles DROP COLUMN IF EXISTS RoadUser, ShowRoadUser;
```

### Moderation of media (`/api/admin/media`, `bjk.media.manage` + Moderator/Admin with two-factor)

| Endpoint | What it does |
|---|---|
| `GET /api/admin/media/review?page=&pageSize=` | The review queue (`NeedsReview`), oldest first, with reasons, blur counts and signed preview links (15 minutes) |
| `GET /api/admin/media/{id}/review` | One item with signed links to its blurred preview and thumbnail (public links once Ready) |
| `GET /api/admin/media/{id}/original` | **Admins only.** A signed link (15 minutes) to the unblurred original. Each use is recorded in the moderation audit log (`ViewMediaOriginal`) |
| `POST /api/admin/media/{id}/approve` | Copies the blurred files to the public bucket and sets `Ready` (to the sensitive bucket, with the preview made by the worker, if the media is sensitive) |
| `POST /api/admin/media/{id}/reject` `{ "reason": "..." }` | Sets `Failed` with the reason (shown to the uploader) and removes the review copies |
| `POST /api/admin/media/{id}/redactions` `{ "regions": [{ "x":0.1, "y":0.4, "width":0.2, "height":0.1, "startSeconds":3, "endSeconds":8 }] }` | Adds blur regions (fractions of the picture from the top left; optional time range for videos) and processes the media again **from its current processed file** (copied aside to `{userId}/{mediaId}/rework/r{revision}.mp4|webp` in the originals bucket, since originals are deleted once the post is published): automatic detection plus every manual region, on top of the blurring already there. Works on `NeedsReview` or `Ready` media (e.g. after a report); a Ready item's public files are deleted at once (purge Cloudflare's cache too). The result comes back to the queue. Each re-processing gets new file names (`720p-r1.mp4`), because published files are cached as immutable |

| `POST /api/admin/media/{id}/block-copies` `{ "reason": "..." }` | Puts the media's file hash and recorded perceptual hashes on the known-image list (source `Moderation`), so copies are blocked if uploaded again. Use after removing it. Returns `{ "hashesAdded": n }` |

**Known images (`/api/admin/known-images`, admins only):**

| Endpoint | What it does |
|---|---|
| `GET /api/admin/known-images/matches?page=&pageSize=` | Blocked uploads, newest first: media, uploader, kind, list, reference, PDQ distance, SHA-256, when, and whether it's been reported |
| `POST /api/admin/known-images/matches/{id}/reported` `{ "reference": "..." }` | Records that the match was reported (e.g. to the NCA or the IWF), with their reference (`ReportKnownImageMatch`) |
| `GET /api/admin/known-images?source=&page=&pageSize=` | The hashes, newest first |
| `POST /api/admin/known-images` `{ "kind": "Pdq", "value": "...", "reference": "..." }` | Adds a hash by hand (`Moderation`) |
| `DELETE /api/admin/known-images/{id}` | Removes a `Moderation` or `Test` hash (a synced list's hashes would come back with the next sync) |

Approvals, rejections, added regions, blocked copies and known-image changes are recorded in the moderation audit log too (`ApproveMedia`, `RejectMedia`, `AddMediaRedactions`, `BlockMediaCopies`, `AddKnownImageHash`, `RemoveKnownImageHash`, `ReportKnownImageMatch`, with before/after status). Re-processing never needs the original. The unblurred-original link (admins) works only while the original still exists.

## Profiles module

Each user's public profile, in `profiles.Profiles`. The profile's id is the user's id. The User module (identity server) keeps the account: email, password, sign-in. A user creates a profile once, after registering, and needs one to post or comment.

- **@handle**: 3 to 30 characters, starting with a letter, using lower-case letters, digits and underscores. It's unique, stored lower-case (`@RoadHog` is `roadhog`), and can be changed. Some words are reserved (`admin`, `me`, `support`, ...).
- **Display name** (up to 50 characters), **bio** (500) and **location** (100, free text such as a town).
- **Avatar**: one of the user's own photos, uploaded through `/api/media` and Ready. Replacing or removing it deletes the old photo.

| Endpoint | Access |
|---|---|
| `POST /api/profiles` | Signed-in user (`bjk.profiles`): create my profile |
| `GET /api/profiles/me`, `PUT /api/profiles/me` | Signed-in user: read or edit my profile (404 = no profile yet) |
| `PUT /api/profiles/me/handle`, `PUT /api/profiles/me/avatar` | Signed-in user |
| `GET /api/profiles/{handle}` | Anyone, including anonymous callers |
| `GET /api/profiles/handles/{handle}/availability` | Anyone: check a handle as the user types |
| `GET /api/profiles/me/blocks`, `PUT` / `DELETE /api/profiles/me/blocks/{handle or user id}` | Signed-in user: list, block, unblock |
| `PUT` / `DELETE /api/profiles/me/following/{handle or user id}` | Signed-in user with a profile: follow, unfollow (returns `following` and `followerCount`) |
| `GET /api/profiles/{handle}/followers`, `GET /api/profiles/{handle}/following` (`?page=&pageSize=`) | Anyone: someone's followers, or who they follow, most recent first |
| `GET /api/profiles/me/suggestions?limit=3` | Signed-in user: Who to follow (`people`, `hasHomeArea`) |
| `POST /api/profiles/{handle}/reports` | Signed-in user: report a profile (served by the Posts module, which owns the reports queue) |
| `GET /api/admin/profiles/{handle}`, `POST /api/admin/profiles/{handle}/reset` | Moderator (`bjk.profiles.manage`): any profile; reset an offensive profile (reason required, audited) |

- **No personal details**: display names, bios and locations can't contain number plates, phone numbers, email addresses, street addresses or social handles (see "Online safety").
- **Blocking**: up to 1,000 people per user, in `profiles.Blocks`. The Posts module hides blocked users' posts and comments from the blocker and stops blocked users commenting on or reacting to the blocker's posts. Blocks go when either account is deleted.
- **Following** (UI overhaul step 6, 20 Oct 2026): up to 5,000 people per user, in `profiles.Follows`. Profiles show `followerCount` and `followingCount` (active profiles only), and to a signed-in viewer `isFollowing` and `followsYou`. Following needs your own profile; the person followed gets a **NewFollower** notification (in the bell; email off by default). **Blocking either way removes follows and prevents new ones.** Follower and following lists are public, like X. Follows go when either account is deleted, and are in Download my data.
- **Tag suggestions** (owner, 23 Oct 2026: "like Facebook and Instagram"): `GET /api/profiles/me/tag-suggestions?q=Aa&limit=8` for tagging passengers as you type. Matches people whose @handle or display name (any word of it) starts with what's typed, case-insensitively. People you're connected to come first: following each other, then your followers, then people you follow (`connection`: Mutual, FollowsYou, YouFollow). Then other members whose handle or name starts with it (`None`), **adults only**, shortest handle first. Never you, deactivated profiles or anyone blocked either way, and **under-18s only get people they're connected to**. The API still makes the passenger checks when the post is saved.
- **Who to follow** (ranking decided by the owner, 22 Oct 2026): the people who posted most in the last 90 days within 25 km of your **home area** (or its radius if larger), or of **where you usually post** (the middle of your own recent posts) if you haven't chosen one. Each is scored 10 a post, plus 1 for every 4 reactions and comments on those posts (up to 50), plus 15 for each person you follow who follows them (up to 3). If there aren't enough nearby, or you've no area and haven't posted, the **most active posters UK-wide** fill the list. Never you, people you follow, people blocked either way, or anyone not known to be 18 or over (**only adults are ever suggested**, and **under-18s get no suggestions**). `hasHomeArea: false` lets the app ask for a home area, but suggestions still come. Posting activity comes from the Posts module through `IActivePosters` (Shared). Contact-book suggestions come with the mobile app.
- **Moderation reset**: the display name goes back to the handle and the bio, location and avatar are removed (the avatar's files are deleted). The handle itself isn't changed.

Configuration: `ConnectionStrings:<name>`, the name passed to `new ProfilesModule(...)`. Its tables are in the `profiles` schema of `badjockeys`. It needs the Media module registered too.

## Posts module

Posts of driving incidents, in the `posts` schema: `Posts`, `PostMedia`, `Reactions`, `Comments`, `CommentEdits`, `CommentReactions`, `Reports`, `Appeals`, `ModerationAuditLog`.

- **Deactivated authors**: while an author's account is deactivated, their posts and comments are hidden from everyone (feed, post pages, comment lists and counts). Their reactions still count.
- **A post** has a title (up to 120 characters), an optional description (5,000), a category, when the incident happened (not in the future), a map pin (latitude/longitude, optional place name), and **1 to 10 photos or one video** (a video can't be mixed with anything else: "A post can have 1 to 10 photos or one video, not both. Post the video on its own.").
- **Categories** (signed off 23 Oct 2026, `IncidentTaxonomy`): **13 groups** (`IncidentGroup`: Parking, Speeding, Overtaking, Junctions, LaneDiscipline, FollowingTooClose, Distraction, RedLightsAndCrossings, VulnerableRoadUsers, RoadRage, Collisions, VehiclesAndDrivers, Motorways) of **74 subcategories** (`IncidentCategory`, e.g. `PavementParking`, `ClosePassCyclist`), every group with its own "Other" (`ParkingOther`...). A post has **1 to 3** subcategories (`types`, the first is the main one, also sent as `category`; more is a 400 "Choose up to 3 types of bad driving."). `PostDto.group` is the main one's group. They're stored as bits in two `bigint` columns, `TypeFlags` (subcategories 0 to 63) and `TypeFlagsHigh` (64 to 127), so the feed, reels, timelines and heat map filter with a bitwise test; `IncidentCategory` values are bit positions, so new ones only ever go on the end. The `CategoryTaxonomy` migration moved existing posts, drafts and timelines across (RoadRage → ShoutingOrGestures, DangerousOvertaking → OvertakeIntoOncoming, MobilePhoneUse → PhoneInHand, Speeding → SpeedingFastRoad, RedLightRunning → RanRedLight, CuttingUp → PushingInAtMerge, LaneHogging → MiddleLaneHogging, ClosePass → ClosePassCyclist, BadParking → ParkingOther, Other → VehiclesOther; Tailgating and Undertaking keep their names).
- **Votes**: each subcategory has its own question and two to four answers (`IncidentTaxonomy.VoteFor`, e.g. "Was the pavement blocked?" Blocked / Partly blocked / Room to pass); the main subcategory's is the post's. `VoteSummaryDto` carries `question` and `answers` (`choice` and `label`). Votes for an answer the current question doesn't offer (cast before the categories changed, or before the author changed the main subcategory) aren't counted or shown, and that voter can vote again.
- **Post now, publish when ready** (19 Oct 2026): the photos or video must be the author's own uploads, and the upload must be complete, but they don't have to have finished processing. If any is still `Processing` (or `NeedsReview`, waiting for a moderator), the post is saved with `mediaState: "Pending"` (`isProcessing: true`): only its author (in `GET /api/posts/mine` and on the post page) and moderators see it, and its `media` lists only the items that are ready. When the last item becomes `Ready`, the `media.ready` event publishes it automatically (`mediaState: "Ready"`), and it appears in the feed. If any item fails, is rejected by a moderator or is deleted, `media.failed` marks the post `mediaState: "Failed"` with `mediaFailureReason` (e.g. "Videos can be up to 30 seconds long..."), it's never published, and the author is emailed once ("Your post couldn't be published", template `EmailTemplates.PostNotPublished`) at the `email` claim their token had when they posted; that address is kept on the post only while it's pending (`posts.Posts.NotifyEmail`) and cleared as soon as it's published or they've been told. They can delete the post and try again. Media that finishes while the post is being saved is caught by a second check after saving. Posts whose media are already Ready are visible straight away, as before. Content screening, the sensitive tag, the age check, new-account limits and moderation all apply as before, whatever the media state: a post shows to others only when its status is `Published` **and** its media are `Ready`. Each photo or video can be in only one post. The author needs a profile.
- **Editing**: the author (or an admin) can change the details but not the media. Deleting a post also deletes its photos and videos. The row is kept, marked Deleted.
- **Reactions**: one per user per post (Like, Heart, Angry, Shocked; Heart replaced Laugh on 22 Oct 2026 and the `HeartReaction` migration turned existing Laugh reactions into Like). Reacting again changes it. Posts show counts per reaction and the viewer's own reaction.
- **Who reacted** (`GET /api/posts/{id}/reactions?type=&search=&page=&pageSize=`, signed-in members only): newest first, filtered by reaction and searched by display name or @handle, with totals per reaction. Under-18s (and anyone whose age isn't known) are counted but never named (`unnamedCount`); people blocked either way are left out.
- **Comments and replies**: up to 2,000 characters. Replies go **one level deep**: a reply points to a top-level comment on the same post, and replying to a reply is refused with a 400 ("You can't reply to a reply..."). The author can edit; the author or a moderator can delete. Commenting needs a profile. Comment lists return top-level comments, oldest first, each with its reply count and first 3 replies; the rest come from the replies endpoint. A hidden or deleted top-level comment that still has replies is shown as a placeholder (`isRemoved: true`, no author, empty body) with its replies underneath. The post's comment count includes replies.
- **Edit history**: every edit keeps the earlier text in `posts.CommentEdits` (deleted with the comment). Edited comments carry `editedAt`, and anyone who can see the comment can read every version, newest first (`GET .../comments/{commentId}/history`). Saving the same text changes nothing.
- **The post's author controls comments** (UI overhaul, 20 Oct 2026):
  - **Hide a comment or reply** on their post (`PUT .../comments/{commentId}/hidden` with `{"hidden": true}`, or `false` to show it again; not their own). Everyone else gets it as a placeholder (`isHiddenByPostAuthor: true`, no author, empty body, no replies) and the replies to a hidden comment are hidden too. The commenter and the post's author still see it, marked hidden. Only the post's author can reply to it. It's left off the commenter's profile Replies tab for others.
  - **Who can comment** (`PUT /api/posts/{id}/comment-setting` with `{"setting": "Everyone"}`, `"Following"` or `"Off"`; `PostDto.commentSetting`). With comments off, nobody but the author can add comments or replies (403); with **Following** ("People I follow"), only people the author follows can (403 for others, "Only people the author follows can comment on this post."); the ones already there stay.
- **Comment reactions**: one per user per comment or reply (Like, Heart, Angry, Shocked), changeable and removable, in `posts.CommentReactions`. Comments show counts per type and the viewer's own reaction.
- **Reports**: signed-in users can report a post, comment, reply or profile once, with a reason (DangerousOrIllegal, PersonalInformation, HarassmentOrHate, Spam, NotADrivingIncident, IllegalContent, Threat, InThisClip, Copyright, Other; and for profiles only, Impersonation and UnderThirteen). Anyone can use the public form. **Every new report is also emailed to `Posts:ReportsInbox`** (support@badjockeys.com) with its reference, reason, the reporter's words and a link to the content; a failed send is logged and never stops the report. Moderators work through the queue, urgent first. **Dismiss** leaves the content up. **HideContent** hides it (or resets a profile) and closes every open report about it. A hidden post looks missing to everyone except its author and moderators. See "Online safety" for priorities, emails and appeals.
- **Post analytics** (owner, 22 Oct 2026): `GET /api/posts/{id}/analytics`, the author only (or a moderator). Views, local views, reactions, visible comments and shares over time since posting, from when each was recorded (`PostViews.ViewedAt`, `Reactions.CreatedAt`, `Comments.CreatedAt`, `PostShares.SharedAt`): `interval` is `Hour` for a post's first 48 hours, then `Day`, then `Week` once it's over 90 days old, with one point per interval from posting until now. **Premium and Premium + only**: on Free, `locked` is true and there are no points (the app shows the graphs blurred with an upgrade prompt); `totals` are always given, as on `/reach`.
- **Household cars** (owner, 22 Oct 2026): `GET/POST /api/vehicles/mine`, `PUT/DELETE /api/vehicles/mine/{id}`, in `posts.HouseholdCars`. Up to 5 per member: a make and model from `GET /api/vehicles`, a colour and a nickname (30 characters, no personal details; "Make Model" if left blank). **No registration is ever stored.** Private to the member. When posting (or editing: leave it out to keep it, send an empty id to remove it), `filmedFromCarId` picks one; the post shows `filmedFrom` ("Filmed from: Dave's Golf"). The nickname is kept on the post (`Posts.FilmedFrom`), so it reads the same if the car is deleted; renaming the car renames it on its posts. Deleted with the account; in Download my data.
- **Feed**: newest first, filterable by subcategory (`type`, or the older `category`), `group` (any of a post's subcategories in it), author's @handle, hashtag, and area (a point plus a radius of up to 200 km). Reels take `category` and `group` too.
- **Home timelines** (UI overhaul step 6):
  - **For you** (anyone): public posts from the last `Reels:RankingWindow` (30 days) ranked by the same "recent and popular" score as reels ((1 + reactions + 2 × comments) / (age in hours + 2)^1.5, refreshed every 10 minutes, now for every post), with posts near the signed-in user's home area ranked 3 times higher (within its radius) or 1.5 times (within four times it); then older posts, newest first, so it never runs out. People blocked either way are left out.
  - **Following**: posts by the people you follow, and your own, newest first.
  - **Custom timelines** (`posts.Timelines`): a name (30 characters), one or more types of bad driving, and optionally a place and radius (2, 5, 10, 25 or 50 km). Named, reordered and deleted by their owner. **Free 1, Premium 5, Premium + 10** (`Timeline.LimitFor`); more is refused with a 400 naming the plan's limit. Their posts: any of the types, within the radius, newest first. Deleted with the account; in Download my data.

| Endpoint | Access |
|---|---|
| `GET /api/posts`, `GET /api/posts/{id}` | Anyone, including anonymous callers (sensitive posts' media blurred unless the caller is age-verified, the author or a moderator) |
| `GET /api/posts/mine` | Signed-in user (`bjk.posts`): my posts, including hidden and held ones and ones waiting for their media (`mediaState`) |
| `POST /api/posts`, `PUT` / `DELETE /api/posts/{id}` | Signed-in user; edit and delete are for the author |
| `PUT` / `DELETE /api/posts/{id}/reaction` | Signed-in user |
| `GET /api/posts/{id}/reactions` | Signed-in user: who reacted |
| `GET /api/posts/{id}/comments`, `GET /api/posts/{id}/comments/{commentId}/replies` | Signed-in user (logged-out viewers see no comments) |
| `POST /api/posts/{id}/comments`, `POST /api/posts/{id}/comments/{commentId}/replies`, `PUT` / `DELETE /api/posts/{id}/comments/{commentId}` | Signed-in user |
| `PUT` / `DELETE /api/posts/{id}/comments/{commentId}/reaction` | Signed-in user |
| `GET /api/posts/{id}/comments/{commentId}/history` | Signed-in user who can see the comment |
| `PUT /api/posts/{id}/comments/{commentId}/hidden`, `PUT /api/posts/{id}/comment-setting` | The post's author |
| `POST /api/posts/{id}/reports`, `POST /api/posts/{id}/comments/{commentId}/reports`, `POST /api/profiles/{handle}/reports` | Signed-in user |
| `POST /api/posts/{id}/appeal`, `POST /api/posts/{id}/comments/{commentId}/appeal` | Signed-in author, once per decision |
| `POST /api/reports/public` | Anyone, with a Turnstile token in `X-Captcha-Token` |
| `GET /api/timelines/for-you` | Anyone (`?page=&pageSize=`) |
| `GET /api/timelines/following` | Signed-in user |
| `GET /api/timelines`, `POST /api/timelines`, `PUT /api/timelines/order` `{ "ids": [...] }`, `PUT` / `DELETE /api/timelines/{id}`, `GET /api/timelines/{id}/posts` | Signed-in user: their custom timelines (`timelines`, `limit`, `plan`) |
| `GET /api/admin/reports`, `POST /api/admin/reports/{id}/resolve` | Moderator (`bjk.posts.manage`): the queue, urgent first; resolve (note required) |
| `GET /api/admin/appeals`, `POST /api/admin/appeals/{id}/decide` | Moderator: appeals queue; uphold or overturn (note required) |
| `GET /api/admin/screening/posts`, `GET /api/admin/screening/comments` (`?queue=Held` or `Flagged`), `POST .../screening/posts/{id}`, `POST .../screening/comments/{commentId}` | Moderator: held and flagged content; approve or remove (reason required) |
| `GET /api/admin/posts/{id}`, `POST /api/admin/posts/{id}/unhide`, `PUT /api/admin/posts/{id}/sensitive` | Moderator (reason required for changes) |
| `GET /api/admin/posts/sensitive-review`, `POST /api/admin/posts/{id}/sensitive-review` `{ "confirm": true, "reason": "..." }` | Moderator: posts tagged sensitive automatically; confirm or undo (reason required) |
| `DELETE /api/admin/posts/{id}?reason=`, `DELETE /api/admin/posts/{id}/comments/{commentId}?reason=` | Moderator |
| `GET /api/admin/audit` | Admin: the moderation audit log for Posts, Profiles and Media |

Configuration: `ConnectionStrings:<name>`, the name passed to `new PostsModule(...)`. Its tables are in the `posts` schema of `badjockeys`. It needs the Media and Profiles modules registered too:

```csharp
builder.Services.AddBjkModules(builder.Configuration,
    new MediaModule("BadJockeysDatabase"),
    new ProfilesModule("BadJockeysDatabase"),
    new PostsModule("BadJockeysDatabase"),
    new EmailModule()); // report and appeal emails
```

### Plans and entitlements

Billing isn't built yet. Plans come from the access token's **`plan`** claim (`free`, `premium`, `premium_plus`; missing or unknown = Free), which the identity server will issue once subscriptions exist. Modules read it through `IPlanResolver` (Shared), which also applies **`Plans:Overrides`**: a list of user ids with the plan to treat them as, for testing Premium features now (startup refuses ids that aren't GUIDs and unknown plan names). Remove the overrides once the claim is issued. Logged-out callers have no plan. Limits follow the "Pricing and plan limits" sheet (Plan matrix).

### Reels

A full-screen feed of video posts: **one reel = one post** (a post is 1–10 photos or one video, so only video posts are reels; photo posts stay in the feed). Reels carry the same `PostDto` as the feed: author, reactions, comments count, sensitive fields, `linksExpireAt` and so on.

| Endpoint | Access |
|---|---|
| `GET /api/reels?cursor=&category=&group=&pageSize=` | Anyone. Returns `items`, `nextCursor`, `playsRemainingToday` (null = unlimited), `dailyPlayLimit`, `playsToday`, `resetsAt`. With nothing left today: **403** with `code` = `daily_limit`, `resetsAt` and `limit` (problem details). |
| `POST /api/reels/{postId}/play` | Anyone. The app calls it once about **3 seconds** have been watched. Idempotent per viewer, reel and UK day. Returns `counted`, `playsRemainingToday`, `dailyPlayLimit`, `playsToday`, `resetsAt`; 403 `daily_limit` if the allowance is used up and this reel wasn't already played today (replays are free); 404 for photo posts, hidden ones and sensitive ones the caller can't see. |

- **Daily plays** per UK calendar day (Europe/London, so it follows British Summer Time; resets at midnight UK time): logged out **10** (`Reels:DailyPlays:Anonymous`), Free **25** (`Reels:DailyPlays:Free`), Premium and Premium + unlimited. A page is never longer than the plays left. Video in the main feed isn't counted yet (the apps can call the play endpoint for it later).
- **Who is counted.** Signed-in users by their id. Logged-out viewers by the **`X-Viewer-Id`** header (8–128 letters, digits, `-_.:`): the web app's BFF sets a signed, httpOnly cookie and forwards its id; the mobile app sends its install id. Without one, the **IP address** is a weak fallback (shared addresses share a count; the host must apply forwarded headers so it's the viewer's address, e.g. Cloudflare's `CF-Connecting-IP`). The API can't verify the header, so a determined logged-out caller can reset their count; the limit is a nudge to sign up, not a paywall. Viewer ids and IPs are stored only as a hash (HMAC-SHA256 with `Reels:ViewerKeySecret`; set it in deployed environments).
- **Ranking: recent and popular.** `score = (1 + reactions + 2 × comments) / (age in hours + 2)^1.5` (`ReelRanking`), stored on `posts.Posts.ReelScore` and **recomputed every 10 minutes** (`Reels:RankingRefreshInterval`) by a background job in the API host, for video posts from the last 30 days (`Reels:RankingWindow`; older ones get 0 and come after). Why a refreshed column rather than computing in each request: a score that decays with time can't be indexed, so every request would score every video; with the column, a page is an index seek on the filtered indexes `IX_Posts_Reels` and `IX_Posts_Reels_Category` (public video posts only, ordered by score and time), whatever the archive's size, and the refresh is one set-based statement over the last 30 days' videos. New videos start with the top score for no engagement, so they appear straight away. Cursors are the last reel's score and time; a refresh between pages can occasionally repeat or skip a reel, so the apps skip ids they already have. The refresh bumps those posts' row versions, so an edit saved in the same instant can get the usual concurrency error.
- **Sensitive posts are skipped** unless the caller is age-verified (`age_verified` claim) or a moderator; authors always see their own.
- **Blocking:** reels by people the caller blocked, or who blocked the caller, are left out (`IBlockDirectory.GetBlockedEitherWayAsync`).
- **Plays** are stored in `posts.ReelPlays` (key: viewer, UK day, post), with a daily total per post in `posts.ReelPlayDailyCounts` (kept with the post, for stats). Signed-in viewers' plays are kept **35 days** (`Reels:PlayRetention`), logged-out viewers' **2 days** (`Reels:AnonymousPlayRetention`); the same background job sweeps them hourly (`Reels:PlaySweepInterval`). A user's plays are in "Download my data" and deleted with the account.
- **Existing video posts:** the migration marks them (`HasVideo`) with a one-off read of `media.Media` in the same database; it's skipped if that table isn't there.

### Incident heat map

Aggregated and privacy-safe: counts per grid cell built **only from the stored, rounded (~500 m) pins** of public posts (published, media ready, author active; never hidden or held ones). Sensitive posts are counted (counts reveal nothing about them). Signed-in users only (logged out: **401**).

| Endpoint | Access |
|---|---|
| `GET /api/heatmap?bbox=minLon,minLat,maxLon,maxLat&zoom=&from=&to=&categories=&hours=&days=` | `cells` (centre, bounds, `count`), `cellLatitudeSize`, `cellLongitudeSize`, `minCellCount`, `maxCount`, `truncated`, `access` |
| `GET /api/heatmap/area?bbox=…&from=&to=` (+ the same filters) | `suppressed`, `total`, `changePercent` (vs the same number of days just before), `busiestHour` (UK hour starting the busiest one-hour band), `topCategories` (category, count, percent), `topPlaces` (place name, count), `placeName`, `previousFrom`, `previousTo`, `access` |
| `GET /api/heatmap/export.csv` (same parameters as the map) | Premium + only (403 otherwise): the cells as CSV, aggregated counts only |
| `GET /api/trends/categories?from=&to=&bbox=` (anyone, signed out too) | The right panel's **category trends**: `categories` (every type, busiest first: `category`, `suppressed`, `total`, `previousTotal`, `changePercent`, `points`), `period` (`Day`, `Week` or `Month`), `periods` (each point's first UK day), `previousFrom`, `previousTo`, `areaAllowed`, `areaIgnored`, `access` |

- **Dates** are UK calendar days, inclusive. **Filters:** `categories` (comma-separated subcategory or group names; a group stands for all its subcategories), `hours` (UK time of day, `7-10`, or `22-6` across midnight), `days` (`All`, `Weekdays`, `Weekends`, UK time).
- **Entitlements** (`access` in every response says what was applied): **Free** the last 7 days, filters ignored (`filtersIgnored`), the national teaser; **Premium** 12 months and filters; **Premium +** 5 years, filters and CSV export (`Heatmap:HistoryDays:Free|Premium|PremiumPlus`, 7/365/1826). A `from` earlier than the plan allows is moved later (`historyCapped`).
- **Grid:** fixed latitude/longitude cells on a worldwide grid (stable as the map pans), sized by zoom: about 500 m from zoom 14 (the pins' own rounding), 1 km at 13, 2 km at 12, 4 km at 11, 5 km at 10, 10 km at 9 and below. A cell's longitude size is 1.6 × its latitude size, roughly square across the UK (`HeatmapGrid`). The box is widened to whole cells so edge cells count everything in them.
- **Category trends** use the same plan limits for dates (Free and signed-out: the last 7 days; Premium 12 months; Premium + 5 years). Everyone sees the **whole UK**; Premium and Premium + can pass `bbox` for an area (others get the whole UK and `areaIgnored`). Points are per UK day up to 92 days, per week (from Monday) up to two years, then per month. A post counts under each of its types. A type with fewer than 3 incidents in the range is `suppressed` (no figures); the change is shown only if the previous period had at least 3.
- **Suppression:** cells with fewer than **3** incidents (`Heatmap:MinCellCount`, at least 2) are left out. In area figures, everything is hidden below 3; categories, places and the busiest hour are shown only with at least 3 each; the change is shown only if the previous period had at least 3.
- **Top roads:** posts store a place name (e.g. "A57 Warrington Road, Prescot"), so `topPlaces` lists the most common ones; the apps show them as "Top roads".
- **Performance: an index, not an aggregate table.** The pins are already rounded to a 500 m grid and incident times are spread out, so a daily aggregate table (day × pin × category × hour) would have about as many rows as posts and need a refresh job and backfills, for little gain. Instead the filtered covering index `IX_Posts_Heatmap` (public posts by `IncidentAt`, including latitude, longitude, category and place name) turns any period into one range seek with no lookups; the grouping is done in SQL. Queries are raw SQL with the index's filter written as literals, so SQL Server can match it. Revisit (a nightly aggregate per cell and day) if national 5-year queries become slow at millions of posts.
- Not built yet: area alerts, saved searches and Premium + monthly reports (in the plan matrix); a spatial index (the bounding box is a residual filter after the time seek).

### Hashtags and Trending

UI overhaul step 5 (decided 20 Oct 2026).

| Endpoint | Returns |
|---|---|
| `GET /api/trends` (anyone) | `hashtags` (up to 10: `tag` as first written, `posts` in the last 24 hours) and `places` (up to 5 towns: `name`, e.g. "Prescot", and `posts`) |
| `GET /api/hashtags?prefix=m6&limit=8` (anyone) | Suggestions as `#` is typed: tags starting with the prefix (any case), most used first: `tag`, `uses` |
| `GET /api/hashtags/{tag}` (anyone) | A tag's page: `tag`, `key`, `uses`, `firstUsedAt`; 404 if no post has used it |
| `GET /api/posts?hashtag=M62` | The feed, only posts with that tag (with or without the #, any case) |
| `PUT /api/admin/hashtags/{tag}/trending` `{ "hidden": true, "reason": "..." }` | Moderators: hide a tag from Trending and suggestions, or show it again (audited as `HideHashtag` / `ShowHashtag`) |

- **Where tags come from:** posts' titles and descriptions only, never comments. A tag is `#`, a letter, then up to 49 letters, digits or underscores, at the start of the text or after a space or punctuation (not inside a word or a link, so `C#` and `page#section` aren't tags). At most 10 per post. Tags are the same whatever the case (`key` is lower case); the first way a tag was written is how it's shown.
- **Tables:** `Hashtags` (one row per tag: key, display form, first used, total uses, hidden from Trending), `PostHashtags` (post, tag, author, when posted) and `HashtagHourlyCounts` (public posts and distinct authors per tag per UTC hour). Links are updated when a post is created or edited (`HashtagSync`), in the same save.
- **Trending** compares each tag's public posts in the last 24 hours with its daily average over the 7 days before (from the hourly counts): score = posts today ÷ (usual per day + 1), highest first. A tag needs at least 3 posts from at least 3 different people, so one person can't make a trend. Hidden, held and deleted posts and deactivated authors never count: the last 48 hours of hourly counts are rebuilt every 5 minutes from public posts (`HashtagMaintenance`, in the API), and counts are kept 90 days. **Places** trend the same way, by town: the part of posts' place names after the last comma.
- **Safety:** number plates can't be tags (the same `TextSafety` check as post text); tags that are, start or end with a threat, slur or flagged insult from the screening list are never recorded (`ScreeningKeywords.IsOffensiveTag`; words of four letters or fewer only as the whole tag, so Scunthorpe and Pakistan are fine); `Hashtags:Blocklist` adds more; and moderators can hide any tag from Trending and suggestions. Posts keep their tags.

### Communities

Owner's decision, 23 Oct 2026: forums branded "Communities", in the Posts module (`/api/communities`, `CommunitiesController`).

- **Starting one:** Premium and Premium + members who are adults (`ProfileSummary.IsAdult`), with a profile and a confirmed email; up to 5 each. Its creator is its admin. The address (`/communities/{slug}` in the web app) comes from the name and never changes; a few words the web app uses for its own pages (`new`) get `-community` added. Name, description and rules are screened like comments; text that would be held is refused.
- **Public or private:** public communities are open to everyone signed in (join at once; anyone sees inside). Private ones are listed by name and description, people ask to join and an admin or moderator approves; only members see inside. **Under-18s** join public communities only, and are never admins or moderators.
- **Area** (optional): a place and a radius (5, 10, 15, 25 or 50 km). "Near you" lists communities covering your home area; when posting, `GET /api/communities/suggestions` puts your communities covering where the incident happened first (`Recommended`). Communities without an area are "topics".
- **Inside:** posts shared by their authors (`POST /api/posts/{id}/communities`; the post stays public everywhere) and text discussions with replies (members; screened like comments, with the new-account limits). People blocked either way are left out.
- **Running it:** admins change the details, set roles and delete it; admins and moderators approve or decline requests, remove or ban members, and take out shared posts, discussions and replies. Moderators can't remove admins; there's always at least one admin.
- **Invitations:** admins and moderators invite people by @handle (`POST /api/communities/{slug}/invites`; status `Invited`, up to 100 open at once). The invitee joins with Join, even a private community, and declines with Leave; removing them cancels it. Under-18s can't be invited to private communities, and people blocked either way can't be invited (it reads as "nobody called @x"). Inviting someone who'd asked to join lets them in.
- **Notifications** (Notifications module, each one in the settings page): **CommunityJoinRequest** to the admins and moderators when someone asks to join a private community; **CommunityRequestApproved** when a request is approved (declines aren't announced); **CommunityInvite**; **CommunityReply** to a discussion's author. They carry a `Link` to the community page (`NotificationRequest.Link`, a path in the web app; the bell's `path` and push and email links use it instead of the post's).
- **Reports:** communities, discussions and replies can be reported to Bad Jockeys' moderators (`ReportTargetType.Community`, `CommunityDiscussion`, `CommunityReply`); hiding one removes it.
- **Data:** `posts.Communities`, `CommunityMembers`, `CommunityPosts`, `CommunityDiscussions`, `CommunityReplies` (migration `Communities`). Deleting an account removes the communities it made and its memberships, discussions, replies and shares; the data export has a `communities` section.

### Online safety

Built for the Online Safety Act design (architecture decisions, 18 Oct 2026).

- **No personal details in text.** Post titles, descriptions and place names, comments and replies, and profile display names, bios and locations are checked by `TextSafety` (BJK.Modules.Safety). Refused, with a 400 and a friendly message on the field: UK number plates in every format (current `AB12 CDE`, prefix `A123 BCD`, suffix `ABC 123D`, Northern Ireland `AXZ 99`, and dateless `ABC 123` / `123 ABC` when written in capitals), in any case and with or without the space; UK and international phone numbers (10 to 15 digits starting `0`, `00` or `+`); email addresses; street addresses (a house number, one or two name words, then a street type such as Road or Avenue); and social handles or profile links (`@handle`, `x.com/…`, `tiktok.com/@…`, `instagram.com/…`, `facebook.com/…` and similar). Road numbers, junctions, speeds and car models are deliberately allowed: "M62 J26", "A road", "doing 70 in a 30", "BMW 320d". A plate whose letters spell a common short word ("A56 THE") is missed rather than refusing ordinary text.
- **Content screening.** Every comment, reply and post text goes through `IContentScreener`, scoring 0 to 1. At or above `Screening:HoldThreshold` (0.8) it's **held for review**: only its author sees it (status `HeldForReview`) until a moderator approves or removes it. At or above `Screening:FlagThreshold` (0.5) it's published but **flagged** for a moderator. The default provider is a built-in keyword and phrase list (threats, slurs, abuse, and calls to find or confront people) with three severities; text is normalised so "k1lll" matches "kill". The `Http` provider also calls a classifier service, intended for an open-source Detoxify model in a sidecar container: `POST {"text": "..."}` returning `{"scores": {"toxicity": 0.97, ...}}`; the higher of the two scores wins, and if the service is down the keyword result is used (labelled `classifier_unavailable`). Edits are screened again; a flag stays until a moderator clears it.
- **Sensitive posts are shown blurred** (revised 19 Oct 2026, with the 13+ age policy). `isSensitive` (graphic injuries, serious crashes) is set by the author on create or edit, by a moderator, or by automatic detection in the media worker (see "Sensitive media" in the Media module). Sensitive posts appear for **everyone**, in the feed, profile lists and on the post page, with their title, description and details, but their photos and video come **blurred** unless the caller may see them: a signed-in user whose token has `age_verified` = `"true"` (they passed the on-demand age check), the post's author, or a moderator (moderator rights, with or without the claim). For everyone else (logged out, or not age-verified) the post has `requiresAgeVerification: true` and each `media` entry has `isBlurred: true`, `playbackUrl: null`, `thumbnailUrl: null` and only `previewUrl`, a heavily blurred low-resolution WebP (null for the few seconds until the worker makes it: show a placeholder). This holds even while the media's files are still being moved: the response never contains a real address for a blurred viewer. Callers who may see it get the real files: for sensitive media, short-lived signed links (`Media:SensitiveLinkLifetime`, 10 minutes) with `linksExpireAt`; fetch the post again for fresh links. `sensitiveSource` says who tagged it (`Author`, `Moderator`, `Automatic`). Authors can remove only a tag they set themselves, and not if detection also flagged the media (400). Tagging or untagging moves the media's files (Media module) within seconds.
- **Automatic sensitive tags and the review queue.** If the media worker's graphic content detection marked any of a post's media (`MediaSummary.isAutoSensitive`), the post is tagged sensitive with `sensitiveSource: "Automatic"` before it's published (when it's created with media already flagged, in the same save that publishes a post waiting for its media, or when a published post's media is processed again), recorded in the audit log (`AutoTagSensitive`, actor role `Automatic`) and put in the moderators' queue (`sensitiveReview: Pending`, `GET /api/admin/posts/sensitive-review`). A moderator confirms or undoes it (`POST /api/admin/posts/{id}/sensitive-review`, audited as `ReviewSensitiveTag`); undoing publishes the media again, and detection won't tag that post again. The outcome (`SensitiveReview` Confirmed/Undone, who, when) is kept on the post with the detection score (`SensitiveScore`), and every processed media keeps its own score (`media.Media.SensitiveScore`) whether or not it crossed the threshold, so the threshold can be tuned (or a model trained) against moderators' decisions, e.g. `SELECT p.SensitiveReview, m.SensitiveScore FROM posts.Posts p JOIN posts.PostMedia pm ON pm.PostId = p.Id JOIN media.Media m ON m.Id = pm.MediaId WHERE p.SensitiveReview IN ('Confirmed','Undone')`. A moderator setting the tag with `PUT .../sensitive` settles a pending review too.
- **Blocked web addresses.** Text containing an address on the blocked list (`posts.BlockedUrls`) is always **held for review** (score 1, label `blocked_url`), whatever else screening says. Entries are stored as host and path in lower case without `https://` or `www.`; a bare host (`example.com`) covers the whole site and its subdomains, an entry with a path covers that page and the pages under it. Addresses are found in text with or without `http(s)://`. The list is in SQL and held in memory in the API (`BlockedUrlList`, implementing Shared's `IBlockedUrlList`), refreshed every `BlockedUrls:RefreshInterval` (5 minutes). Rows come from an `IBlockedUrlSource` per provider (the IWF URL List once we're members; synced every `BlockedUrls:SyncInterval`, 6 hours), from moderators (`GET/POST /api/admin/blocked-urls`, `DELETE /api/admin/blocked-urls/{id}?reason=`, audited as `BlockUrl` / `UnblockUrl`), and harmless `Test` entries from bjk-core's local setup.
- **No comments for logged-out viewers.** The comment and reply lists need a signed-in token. Post responses to logged-out callers carry `commentsRequireSignIn: true`; the comment count is still included (it shows activity without revealing anything), so the apps can show "Sign in to see 12 comments".
- **UK only at launch.** The pin as given, before rounding, must be inside an allowed region (`Posts:AllowedRegions`, default the UK), or the post is refused with a 400 on `Location`: "Bad Jockeys only accepts incidents in the UK for now." The outlines are simplified polygons embedded in code (`ServiceRegions`), drawn at roughly 5 to 15 km accuracy: mainland Great Britain; Northern Ireland following the land border with Ireland; and the islands (Isle of Wight, Anglesey, Scilly, Arran, the Inner and Outer Hebrides, Orkney, Fair Isle, Shetland and Rathlin). They're generous at sea and tight on the Irish border and the Dover Strait, so Ireland, France and the Netherlands are refused. The Isle of Man and the Channel Islands are outside the UK, so they're refused unless added (`IM`, `JE`, `GG`). Points within a kilometre or two of the Irish border can fall either way (for example, Belcoo and Blacklion face each other across a stream).
- **Location rounding.** Map pins are rounded to about 500 m when saved (`PinRounding`): latitude to a grid of 0.0045° (500 m north–south), then longitude to 0.0045° / cos(latitude) (about 500 m east–west). The API returns only the rounded pin and the place name. The OnlineSafety migration rounds existing rows the same way (an update; nothing is deleted).
- **Age check.** Viewing the real media of sensitive posts needs the token's `age_verified` claim (above). Writes (create and edit posts, comment, reply, react, report as a signed-in user) need the token's `age_verified` claim (`"true"`), issued by the identity server once the account has passed the age check, but only while `Safety:RequireAgeVerification` is on. It's **off by default** since the age policy changed on 19 Oct 2026 (13+ can post, comment and react; the age check is for viewing sensitive posts), and kept for future use. When it's on and the claim is missing the API returns 403 with `code: "age_verification_required"` (type `https://badjockeys.com/problems/age-verification-required`), so the apps can send the user to the age check.
- **New-account limits.** While a profile is younger than `Safety:NewAccounts:Period` (24 hours) it can create at most `MaxPosts` (3) posts and `MaxComments` (20) comments and replies in any rolling 24 hours, counting deleted ones. Over the limit: 429 with `code: "new_account_limit"`.
- **Posts must be about driving** (owner, 23 Oct 2026). Three checks keep spam and off-topic posts out:
  - **No links.** Web addresses in a post's title, description or place name are refused with a 400 on the field: "Links can't be included in posts..." (`TextSafety.ContainsWebAddress`, rule `NoWebAddresses`). It catches anything with http(s):// or www. and bare lower-case domains ending in common top-level domains (`cheap-insurance.co.uk`, `bit.ly/x`), but not "left.In the lane" or "St.Helens".
  - **No vehicle, no publishing.** Automatic blurring records how many frames showed a vehicle or number plate (`MediaItem.VehicleFrames`, `MediaSummary.VehicleSeen`; needs `Media:Redaction:BlurPlateAreas`). If every photo and video in a post was checked and none showed one, the post is **held for review** with the label `no_vehicle` instead of going public. Media that weren't checked (detection off, or processed before this) never count against a post. Tagged passengers are told only once a post is public.
  - **"Not a driving incident" and spam reports act fast.** Once `Safety:OffTopic:ReportsToHold` (3) different established members (accounts past the new-account period, not the author) have reported a post for one of those reasons, or a Trusted reporter has once, it's **held for review** with the label `reported_off_topic`. A post a moderator has approved isn't held again.
  - **Repeat posters are paused.** When a moderator removes a post for one of those reasons (hiding it from an off-topic report, or removing it from the held queue after a `no_vehicle` or `reported_off_topic` hold) it's recorded (`Post.OffTopicRemovedAt`, cleared if it's shown again). `RemovalsToPause` (2) within `Window` (30 days) and the author can't post until `Pause` (7 days) after the latest: a 429 with code `posting_paused` and the date.
- **Reports.** In the app: posts, comments, replies and profiles. The public form `POST /api/reports/public` ("Report content / I'm in this clip") needs no account: the post id or link (plus an optional comment id), a reason (InThisClip, PersonalInformation, IllegalContent, Threat, HarassmentOrHate, Copyright, Other), details, the reporter's name and email, consent, and a Cloudflare Turnstile token in `X-Captcha-Token` (`ICaptchaVerifier`; a failed token is a 400, an unreachable Turnstile a 503). It returns a reference (`R-1A2B3C4D`), the priority and when a decision is due. **Priority**: `Urgent` for IllegalContent and Threat, with a 4-hour target; `Normal` otherwise, 48 hours. The queue lists urgent reports first, then by due time. **Emails** (Email module, sent straight away): an acknowledgement when the report is made and the outcome when it's resolved, to the public form's address or, for signed-in reporters, the `email` claim in their token if it has one. A failed email is logged and recorded (`acknowledgedAt` / `outcomeNotifiedAt` stay empty); it never undoes the report or decision.
- **Appeals.** An author can appeal a moderator's decision to hide their post or comment, once (`posts.Appeals`). Appeals have a 48-hour target and their own queue; overturning shows the content again. The author is emailed when the appeal is received and decided (if their token carries an email).
- **Moderation audit log.** Every moderator or admin action in Posts (resolve report, unhide, delete post or comment, approve or remove screened content, sensitive tag, appeal decision), Profiles (reset) and Media (delete, approve, reject, add blur regions, open an original) is recorded in `posts.ModerationAuditLog`: actor id and role (Admin, Moderator, or Service for machine tokens), action, target type and id, reason, before/after summaries (states only, never content or personal details) and time. A reason or note is required on every action. `GET /api/admin/audit` (admins only) filters by actor, module, action, target and time. Other modules write through Shared's `IModerationAuditLog`, which the Posts module implements; Posts actions are saved together with their audit entry.
- **Blocking.** See the Profiles module. Blocked users' posts are left out of the blocker's feed and look missing; their comments and replies are left out; and they can't comment on, reply to or react to the blocker's posts and comments.

### How modules talk to each other

A module never reads another module's tables. Each module that others depend on publishes a small interface in its `Contracts` folder, registered by the module itself:

| Contract | Used by | For |
|---|---|---|
| `IMediaDirectory` (Media) | Profiles, Posts | Checking media is the user's own and Ready, getting its URLs (signed links to sensitive media for allowed callers), marking it sensitive when its post is tagged, deleting it with a post or old avatar |
| `IProfileDirectory` (Profiles) | Posts | Authors' handles, names, avatars and profile age; does the user have a profile |
| `IBlockDirectory` (Profiles) | Posts | Who the viewer has blocked; has the author blocked this user |
| `IFollowDirectory` (Profiles) | Posts | Who the viewer follows (Following timeline); does the author follow the commenter ("People I follow") |
| `IHomeAreas` (Shared, stored by Notifications) | Posts, Profiles | The viewer's home area (For you), people near it (Who to follow) |
| `IActivePosters` (Shared, implemented by Posts) | Profiles | Who posts most where, and where a user usually posts (Who to follow) |
| `IProfileModeration` (Profiles) | Posts | Resetting a reported profile |
| `IModerationAuditLog` (Shared, stored by Posts) | Profiles, Media, Posts | Recording moderation actions |
| `media.ready` / `media.failed` events (Media, topic `bjk.media`) | Posts | Publishing (or failing) posts made while their media were processing |

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

## Email and SMS modules

Two modules, `BJK.Modules.Email` (SendGrid) and `BJK.Modules.SMS` (Twilio), for the other modules and the hosts. Neither has a database or endpoints: inject `IEmailSender` or `ISMSSender`. Register only the ones a host needs.

```csharp
builder.Services.AddBjkModules(builder.Configuration, new UserModule("IdentityDatabase"), new EmailModule(), new SMSModule());

// Anywhere:
var email = EmailTemplates.ConfirmEmail(link).To(address, emailOptions.Value.LogoUrl);
await emailSender.SendAsync(email, cancellationToken);

await smsSender.SendAsync(SMSTemplates.SignInCode("+447700900123", code), cancellationToken);
```

- **Templates live in code.** The wording of every email is in `BJK.Modules.Email/Templates/EmailTemplates.cs` and of every text in `BJK.Modules.SMS/Templates/SMSTemplates.cs`, so changes go through pull requests and are tested. We don't use SendGrid Dynamic Templates or Twilio Content Templates. To add a message, add a method there.
- **Sent straight away, not queued.** Each send calls the provider while the request waits, so the caller knows at once whether it worked (for example, the sign-in page can say "couldn't send the code"). There's deliberately no queue for email or SMS.
- **No provider switches.** SendGrid sends when `Email:SendGrid:ApiKey` is set, Twilio when `SMS:Twilio:AccountSid` is. Without them nothing is sent, and in Development the whole message is written to the console (`DEVELOPMENT ONLY - email not sent` / `SMS not sent`). Outside Development the app won't start without them.
- **Checked at startup.** A SendGrid key without a sender address, or a Twilio account without credentials or a sender, stops the app starting, with a message naming the missing setting.
- **Retries.** Timeouts, connection failures, 429 and 5xx are retried 3 times with backoff (honouring `Retry-After`, up to 10 seconds), using `ProviderRetry` from Shared. A rejection (bad number, unverified sender, bad key) isn't retried. Both throw `MessageDeliveryException` (Shared), whose `IsTransient` says which it was.
- **No click tracking.** Our emails carry sign-in, confirmation and reset codes in links, so SendGrid click and open tracking are switched off for every email.
- **Privacy.** Outside Development, logs show masked addresses only (`r***@example.com`, `+44********23`) plus the provider's message id, so a delivery can be traced in SendGrid or Twilio without logging personal data.
- **Branded emails.** `BrandedEmail` builds matching plain-text and HTML bodies (heading, paragraphs, a button, a large code, small print). All text is HTML-encoded, and buttons only accept http(s) links.

Both providers are called over their REST APIs with `HttpClient`, so there are no vendor SDKs to keep up to date.

| Key | Notes |
|---|---|
| `Email:FromAddress`, `Email:FromName` | Sender. The address (or its domain) must be verified in SendGrid. Name defaults to `Bad Jockeys`. |
| `Email:ReplyToAddress` | Optional, e.g. `support@badjockeys.com` |
| `Email:LogoUrl` | Optional absolute URL of a PNG logo for the top of branded emails (Gmail and Outlook don't show SVG) |
| `Email:SendGrid:ApiKey` | Secret. A key with the **Mail Send** permission only. Set: emails go through SendGrid |
| `Email:SendGrid:BaseUrl` | Default `https://api.sendgrid.com`; `https://api.eu.sendgrid.com` for an EU-pinned subuser |
| `Email:SendGrid:SandboxMode` | `true` = SendGrid checks each request but delivers nothing |
| `SMS:Twilio:AccountSid` | `AC…`. Set: texts go through Twilio |
| `SMS:Twilio:ApiKeySid`, `SMS:Twilio:ApiKeySecret` | Secret. Recommended: an API key can be revoked without changing the account's auth token. |
| `SMS:Twilio:AuthToken` | Secret. Used only if no API key is set. |
| `SMS:Twilio:MessagingServiceSid` | `MG…`, recommended: Twilio picks the sender from the service's pool |
| `SMS:Twilio:FromNumber` | E.164 number or alphanumeric sender ID, used when there's no Messaging Service |

Phone numbers must be E.164 (`+447700900123`); use `PhoneNumbers.IsE164` to check numbers you collect.
