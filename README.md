# bjk-modules

Feature modules for the Bad Jockeys platform. Each module is a self-contained vertical slice (API, use cases, infrastructure, domain), published as a NuGet package to GitHub Packages and loaded by a host: `bjk-identity-server` or `bjk-core`.

## Layout

```
src/
  BJK.Modules.Shared/            Module registration, MediatR validation pipeline, error handling,
                                 current user, safe startup migrations, RabbitMQ connection and outbox,
                                 provider retries and MessageDeliveryException (Email, SMS),
                                 user lifecycle events, GET /api/me/data-export
    Safety/                      Online safety: TextSafety (plates, phones, emails, addresses, handles),
                                 IContentScreener (keywords + optional classifier), ICaptchaVerifier (Turnstile),
                                 age verification pipeline step, SafetyOptions
    Moderation/                  IModerationAuditLog contract and action names
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
    Infrastructure/AgeAssurance/ ILivenessChecker, IAgeEstimator, IDocumentAgeCheck; HTTP estimator adapter;
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
    Infrastructure/Messaging/    IMediaJobQueue over RabbitMQ (process, segment and assemble jobs), queue topology
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
      Moderation/                Admin: look up any profile, reset an offensive profile (audited)
    Contracts/                   IProfileDirectory (author names and avatars), IBlockDirectory, IProfileModeration
    Infrastructure/Data/         ProfilesDbContext (schema: profiles) and Migrations/
    Domain/                      Profile, Handle rules
    ProfilesModule.cs            Module entry point
  BJK.Modules.Posts/             Hosted by bjk-core
    API/Controllers/             PostsController (/api/posts), ReportsController (public form, profile reports),
                                 AdminPostsController (/api/admin)
    UseCases/
      Create/  Update/  Delete/  Publish, edit and delete posts
      Get/                       One post, the public feed, my posts
      Reactions/  Comments/      React to posts and comments; comments and one level of replies
      Reports/                   Report a post, comment or profile; the public report form; admin review queue
      Appeals/                   Appeal a moderation decision once; admin decisions
      Moderation/                Screening queues, sensitive tag, audit log, report and appeal emails
      Safety/                    New-account limits
      Publishing/                Post now, publish when ready: PostMediaPublisher
    Infrastructure/              Moderation audit log (stored for every module), media event handler, lifecycle
                                 and data export
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
| `Messaging:ConnectionUri` | RabbitMQ, e.g. `amqp://user:password@host:5672/vhost`. Secret. The identity server publishes user lifecycle events; bjk-core consumes them and Media queues processing jobs. |
| `Messaging:UserEventsQueue` | bjk-core only. Default `bjk-core.user-events`. |
| `Users:DeactivationGracePeriod` | Identity server only. Default `90.00:00:00`. |
| `Users:RetentionCheckInterval` | Identity server only. Default `01:00:00`. |
| `AgeAssurance:*` | Identity server only. The age check for sensitive posts: see [Age policy](#age-policy-13-and-the-age-check-for-sensitive-posts). |
| `MediatR:LicenseKey` | MediatR 13+ needs a licence key (free Community licence under $5M revenue). Store it in Key Vault. |
| `Database:AllowDestructiveMigrations` | Default `false`. See below. |
| `Safety:RequireAgeVerification` | Default **`false`** (since 19 Oct 2026): posting, commenting and reacting no longer need the age check. `true` makes posting, editing posts, commenting, reacting and reporting need the `age_verified` claim again; kept for future use. |
| `Safety:NewAccounts:Period`, `:MaxPosts`, `:MaxComments` | New-account limits. Defaults `1.00:00:00`, 3, 20. bjk-core only. |
| `Posts:AllowedRegions` | Where incidents may be posted. Default (empty): `GB`, the UK including Northern Ireland. Also `IM` (Isle of Man), `JE` (Jersey), `GG` (Guernsey). Unknown codes stop startup. bjk-core only. |
| `Screening:Provider` | `Keywords` (default) or `Http`. bjk-core only. See "Online safety". |
| `Screening:Endpoint`, `:FlagThreshold`, `:HoldThreshold`, `:Timeout` | Classifier URL (required for `Http`); thresholds default 0.5 and 0.8; timeout default 3 s. |
| `Captcha:Provider`, `Captcha:TurnstileSecretKey` | bjk-core: the public report form. `Turnstile` (default; needs the secret, which is checked at startup) or `AlwaysPass` (tests). Same keys as the identity server. Secret. |

`user-secrets.template.json` lists these with local development values.

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

**What's kept.** Only the outcome, on `users.Accounts`: `AgeCheckStatus` (NotChecked, DocumentRequired, Passed, Failed), `AgeCheckMethod` (FaceEstimate, Document), `AgeCheckResult` (the last attempt's outcome), `AgeCheckProvider`, `AgeCheckRecordedAt`, and the attempt counter. The video and ID photo are read into memory (the upload limits stop ASP.NET buffering them to disk), handed to the providers, and zeroed when the request ends. They're never written to the database or logs. Neither are the estimated age or anything read from the ID.

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
| `AgeAssurance:Liveness:Provider` | `Development` | `YuNet` (identity server) for real checks |
| `AgeAssurance:Estimator:Provider` | `Development` | `Http` for a real provider |
| `AgeAssurance:Estimator:Http:Name`, `Endpoint`, `ApiKey` (secret), `ApiKeyHeader`, `ApiKeyPrefix`, `VideoField`, `AgePath`, `UncertaintyPath`, `Timeout` | | Generic adapter. It POSTs the video as multipart/form-data and reads the age (and an optional margin of error) from the JSON response by dot path, e.g. `result.age.estimate`. A vendor with a different API gets its own `IAgeEstimator`. |
| `AgeAssurance:Document:Provider` | `Development` | No real photo-ID provider is built yet. Plug one in by registering an `IDocumentAgeCheck`. |
| `AgeAssurance:*:Development:Age`, `AgeAssurance:Liveness:Development:Pass` | 30, true | What the Development stand-ins answer, for trying every path locally |

**Development providers never run anywhere else.** They don't check anything real, so startup refuses them outside the Development environment, along with an unknown provider name or an Http estimator with no HTTPS endpoint or key. A host adds a provider by registering another `ILivenessChecker`, `IAgeEstimator` or `IDocumentAgeCheck` singleton with its own `Name`.

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

- **Outbox.** Each event is saved in `users.Outbox` in the same transaction as the account change. A background publisher then sends it to the RabbitMQ fanout exchange `bjk.users` and waits for the broker to confirm. An event is never lost, and never sent for a change that was rolled back.
- **Core's queue.** bjk-core reads its own queue, `bjk-core.user-events`, and passes each event to every module's `IUserLifecycleHandler` (Profiles, Posts, Media).
- **Failures.** A failing event is retried. After 5 attempts it moves to `bjk-core.user-events.failed` for someone to look at.
- **Idempotent.** Handlers are safe to run twice.

## Download my data

`GET /api/me/data-export` is served by **both** the identity server and bjk-core. Each returns a JSON file of everything that host holds about the signed-in user, gathered from every module's `IPersonalDataExporter`:

| Host | Sections |
|---|---|
| Identity server | `account`: id, email, status, dates, date of birth, age check outcome |
| bjk-core | `profile`; `posts` (posts, comments, reactions, reports made); `media` (every upload, with a 24-hour download link for each original still kept, and the processed files) |

The apps call both endpoints and offer the two files (or one zip) as "Download my data". A deactivated user can't sign in, so they reactivate, download, then deactivate again if they want.

## Media module

Photos and videos follow the same flow; only the worker's processing differs.

1. `POST /api/media/uploads` with `{ fileName, contentType, sizeBytes }` creates a `media.Media` row (`Uploading`) and returns a signed link, valid for 15 minutes, to PUT the file straight into the private **originals** bucket. The API never handles the file.
2. The app PUTs the file to that link, sending the `Content-Type` header it was given.
3. `POST /api/media/{id}/complete` checks the file is in storage and within the size limit, sets `Processing`, and puts a job on the RabbitMQ queue.
4. The **media worker** (bjk-core's worker container, using `services.AddMediaProcessing(...)` from this module) takes the job, blurs faces and number plates, converts the file and publishes it to the **public** bucket, then sets `Ready`. If automatic blurring failed or wasn't sure, it keeps the blurred files private and sets `NeedsReview` instead; a moderator approves (then `Ready`) or rejects (then `Failed`). Videos longer than 5 seconds are split into segments that several worker replicas blur at once (see "Videos in segments"). Only `Ready` media is visible to other users.
5. Every change to `Ready` or `Failed` (and deleting media before it was ready) publishes a `media.ready` / `media.failed` event (see "Media events"). The Posts module uses them to publish posts that were created while their media were still processing.

### Processing

| | Video | Photo |
|---|---|---|
| Output | `720p.mp4`: H.264, no sound (see `Media:Redaction:RemoveAudio`), at most 720 pixels high and 30 fps, fast start for streaming | `image.webp`: at most 2048 pixels on its longest side |
| Thumbnail | `thumbnail.jpg`, from one second in | `thumbnail.webp`, at most 640 pixels |
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
| `ImageMaxSize`, `ThumbnailMaxSize` | 2048, 640 |
| `CommandTimeout` | 15 minutes |
| `WorkingDirectory` | The system temp folder |

HEIC photos from iPhones need an ffmpeg built with HEIF support (ffmpeg 7.1 or later); with an older ffmpeg they fail with a "couldn't read" reason.

Storage keys are `{userId}/{mediaId}/{fileName}`, where `userId` is the uploader's account id (the token's `sub`) and file names are ours (`original.mov`, `720p.mp4`, `thumbnail.jpg`, `image.webp`, `thumbnail.webp`).

| Endpoint | Access |
|---|---|
| `POST /api/media/uploads`, `POST /api/media/{id}/complete` | Signed-in user (`bjk.media` scope) |
| `GET /api/media/{id}` | Anyone with `bjk.media` once Ready; the owner or an admin before that |
| `GET /api/media/mine` | The signed-in user's own uploads, any state |
| `DELETE /api/media/{id}` | Owner |
| `DELETE /api/admin/media/{id}?reason=` | Moderator (`bjk.media.manage`); the reason is required and the action is audited |

Storage is Cloudflare R2. R2 speaks the S3 API and Cloudflare has no .NET SDK of its own, so the module uses the `AWSSDK.S3` client pointed at the R2 endpoint, as Cloudflare's R2 docs recommend. Nothing is sent to AWS and no AWS account is needed. The storage code sits behind `IMediaStorage`, so any S3-compatible store works by changing configuration.

Configuration (user secrets locally, Key Vault in Azure):

| Key | Notes |
|---|---|
| `ConnectionStrings:<name>` | Name passed to `new MediaModule(...)`. Tables in the `media` schema of the main app database, `badjockeys`. |
| `Media:Storage:ServiceUrl` | `https://<account id>.r2.cloudflarestorage.com` |
| `Media:Storage:AccessKeyId`, `Media:Storage:SecretAccessKey` | R2 API token. Secret. |
| `Media:Storage:OriginalsBucket`, `Media:Storage:PublicBucket` | Private and public buckets |
| `Media:Storage:PublicBaseUrl` | Where the public bucket is served through Cloudflare |
| `Media:Storage:SensitiveBucket` | Optional private bucket for the real files of sensitive media (default: the originals bucket, under `sensitive/`). Set it if the originals bucket has any deletion (lifecycle) rule |
| `Media:SensitiveLinkLifetime` | How long signed links to sensitive media work (default 10 minutes, 1 minute to 1 hour) |
| `Media:Processing:SensitivePreviewSourceSize`, `SensitivePreviewSize` | Blurred preview: shrink to 64 px, then enlarge to 480 px (worker) |
| `Media:SensitiveDetection:*` | Automatic graphic content detection (worker; see below) |
| `Media:Queue:QueueName` | Default `media.process`; failed jobs go to `media.process.failed` |
| `Media:MaxVideoBytes`, `Media:MaxImageBytes`, `Media:MaxVideoDurationSeconds`, `Media:UploadUrlLifetime` | Defaults: 150 MB (157286400), 25 MB, 30 s, 15 min |
| `Media:UnusedOriginalRetention`, `Media:OriginalSweepInterval` | Defaults 24 hours and 1 hour: the most an original is kept when nothing releases it sooner, and how often the API host sweeps (see "Originals"). `Media:OriginalRetention` (90 days) is gone |
| `Messaging:MediaEventsQueue` | Default `bjk-core.media-events`: the API host's queue for `media.ready` / `media.failed` (failed events go to `.failed` after 5 attempts) |

### Originals (deleted as soon as they're not needed)

Owner's decision, 19 Oct 2026, replacing 90-day retention: an uploaded original (which still has faces, plates, sound and GPS) is deleted as soon as nothing needs it.

- **Post published:** when a post's media are all `Ready` and it's published (straight away if they were ready when posting, or when the last one becomes ready, including after a moderator approves media from the review queue or automatic detection tags it sensitive), the Posts module calls `IMediaDirectory.ReleaseOriginalsAsync` and the originals are deleted (`media.Media.OriginalDeletedAt` is set).
- **Post deleted or discarded before publishing:** its media are deleted, originals included, at once (as before).
- **Processing failed, media never posted, upload abandoned:** the API host's sweeper (`OriginalsSweeper`, every `Media:OriginalSweepInterval`, 1 hour) deletes the original once `Media:UnusedOriginalRetention` (24 hours) has passed since upload (`OriginalDeletesAt`), or since an upload was started and never completed. Media still processing or waiting for a moderator keeps its original until it's ready (then the post's publication, or the sweep, deletes it).
- **Consequences:** re-processing after a moderator adds blur regions works from the processed file (see "Moderation of media"); the admin link to the unblurred original and data-export original links only work while the original exists. The R2 lifecycle rule on the originals bucket is no longer needed (the review copies and rework copies live there too, so don't add a short one).

### Videos in segments (parallel processing)

A 30-second video is processed as six 5-second pieces by up to six worker replicas at once, then joined, so it's ready several times sooner than in one job (measured timings below). Photos and videos of 5 seconds or less (`SegmentSeconds`) are processed in one job as before.

1. **Plan** (the normal `media.process` job): the worker downloads the original, checks it (length, picture), records its SHA-256, length and size, and saves a plan in `media.VideoJobs` with one row per segment in `media.VideoSegments`. Segments are counted in output frames on one frame grid (150 frames at 30 fps), so every worker agrees exactly where each segment starts and ends. A last piece shorter than a quarter of a segment joins the one before. It then queues one **segment** job per segment on the same queue.
2. **Segment** (any replica): downloads the original and decodes only its part, plus `SegmentOverlapSeconds` either side (ffmpeg seeks a second early and picks frames by their place on the grid, which is exact whatever the keyframes). Detection runs on the same every-5th frames as for the whole video, and the tracker sees the overlap too, so a face or plate crossing a join is tracked as if the video were in one piece: blurs don't drop or start late at the joins (tested with a face that disappears just before a join). Only the segment's own frames are blurred and encoded, with exactly the same x264 settings as a whole video (each segment starts on a keyframe), and stored privately at `{userId}/{mediaId}/segments/r{revision}/{index}.mp4`. The segment is then marked done.
3. **Assemble** (once all segments are done): joins the segments with ffmpeg's concat demuxer without re-encoding (`-c copy`, fast start), adds the soundtrack from the original if `RemoveAudio` is off, makes the thumbnail from the joined video, publishes it and sets `Ready`, or keeps it private and sets `NeedsReview` if any segment's detection was uncertain or failed (or a moderator added regions). The segment files are deleted.

**Restart-safe and idempotent.** Progress is in the database, and every update that parallel workers might race on is one conditional SQL statement: a segment is marked done only if it wasn't already (so a redelivered or duplicated job is skipped, not redone); exactly one worker wins the claim to queue the assemble job when the last segment finishes; a redelivered segment job (the worker may have died after claiming but before queueing) queues it again, and the assemble job stops at once if the media is already finished. A plan job delivered again re-queues only the unfinished segments. Jobs for an older revision (a moderator re-processed the media) or for deleted media are skipped. A segment that fails three times marks the media `Failed` ("Processing failed. Try uploading the file again."), as a whole-video job would. Face and plate counts are summed over segments, so a face crossing a join counts once in each.

### Media events (`media.ready`, `media.failed`)

When media becomes `Ready` (processed, or approved by a moderator) or `Failed` (processing failed, too long or too big, rejected by a moderator), or is deleted before it was ready, `MediaDbContext` writes a `MediaEvent` (`eventId`, `mediaId`, `userId`, `type`: `Ready` / `Failed`, `reason`, `occurredAt`) to `media.Outbox` in the same transaction as the status change, whichever host made it (the API or a worker). The API host publishes the outbox to the RabbitMQ fanout exchange **`bjk.media`** with publisher confirms (message type `media.ready` or `media.failed`), using Shared's outbox publisher, as for user events. Workers write to the outbox but don't publish it. Consumers register `IMediaEventHandler` with `services.AddMediaEventHandler<T>(configuration)`; the host reads them from `Messaging:MediaEventsQueue` (a quorum queue, 5 attempts, then `.failed`). A message can arrive twice, so handlers re-read the current state. `NeedsReview` doesn't publish an event (it's still on its way).

### Automatic blurring (faces and number plates)

Every photo and video is blurred by the worker before anything is published (Online Safety Act design, 18 Oct 2026).

**How it works**

1. ffmpeg decodes the picture (photos: upright, at most 2048 px; videos: at most 720p and 30 fps) into memory over a pipe. Nothing unblurred is written out.
2. Open-source detection models run on the CPU with ONNX Runtime (`Microsoft.ML.OnnxRuntime`, MIT): YuNet for faces (each frame at full size and again at 640 px for faces close to the camera) and LPD-YuNet for number plates (the whole frame plus a 2x2 grid of overlapping zoomed tiles for small, distant plates).
   For videos, detection runs on a copy of each analysed frame scaled down to at most 640 pixels on its longest side (`DetectionMaxSize`) and the boxes are mapped back to the full frame. That makes face detection about four times cheaper than at 720p; plates are searched in the same zoomed 2x2 tiles of the smaller copy, so they're found as before and cost about the same. Faces smaller than about 20 pixels in a 720p frame may be missed; set `DetectionMaxSize` to 0 to analyse at full size. Photos are always analysed at full size.
3. Each box is grown by 25% on every side (`BoxPadding`) and blurred irreversibly: reduced to a few large blocks of average colour (at most about 8 across and 3 down a plate) and then smoothed. A light Gaussian blur, which can be partly undone, isn't used.
4. **Videos:** detection runs on every 5th frame (`DetectEveryNthFrame`). A tracker links boxes from one pass to the next (by overlap, or by distance for fast movers) and interpolates a box for every frame in between. A face or plate missed by up to 3 passes in a row (`MaxMissedDetections`) stays blurred across the gap, and each track starts one interval before its first detection and ends one after its last, following its motion, so blurs don't flicker, drop out or appear late. The video is read once; frames wait in memory only until detection has run far enough ahead (about 20 frames, ~55 MB at 720p).
5. The blurred frames are encoded to H.264 as before. **The soundtrack is removed** (`-an`) unless `RemoveAudio` is false. Metadata (GPS, device) is stripped. Thumbnails are made from the blurred output only.
6. Photos: the blurred picture is converted to WebP and its thumbnail as before.

**Fail safe.** The media goes to `NeedsReview` instead of `Ready` when:

- either model isn't configured, its file is missing, or it won't load;
- a detector throws on any frame (it's then not used again for that job);
- any detection is uncertain: a photo detection, or a video track whose best score never reaches `FaceConfidentAt` / `PlateConfidentAt` (0.8), while scoring at least `FaceMinConfidence` / `PlateMinConfidence` (0.5), the blur threshold;
- a moderator added blur regions (the result is always checked again);
- `ReviewEverything` is on.

The blurred files are still produced, but kept in the **private** bucket under `{userId}/{mediaId}/review/` (originals bucket) until a moderator approves them. They're never public before that, and the media can't be posted (Posts only accepts `Ready` media). The reason is in `ReviewReason`.

**Limits to know.** Detection can miss things without being "uncertain" (nothing is found at all), so a miss doesn't always reach review; moderators add missed blurs by hand. The plate model was trained on Chinese plates: in our test a synthetic UK plate scored 0.75 (uncertain, so reviewed), and real UK plates may score lower or be missed. Consider `ReviewEverything=true` at launch until real-world accuracy has been checked, and replace the plate model if a permissively licensed UK/EU-trained one becomes available (the `IRedactionDetector` interface makes that a new class).

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
| `FaceConfidentAt`, `PlateConfidentAt` | 0.8: anything blurred that never scores this sends the media to review |
| `BoxPadding` | 0.25 of the box's width/height added on each side |
| `DetectEveryNthFrame` | 5 (6 detection passes a second at 30 fps) |
| `MaxMissedDetections` | 3 |
| `PlateTiles` | true (also search zoomed tiles for small plates; about 6x the plate detection cost) |
| `RemoveAudio` | true |
| `ReviewEverything` | false |
| `InferenceThreads` | 0 = every core of the replica (`Environment.ProcessorCount`); ONNX Runtime's intra-op threads, which don't spin while idle so ffmpeg keeps its CPU |
| `DetectionMaxSize` | 640: video frames are analysed at most this big (boxes mapped back); 0 for full size. Photos always full size |
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

Per frame analysed: faces 24–59 ms at full 720p, about 4 times less on the 640 px copy now used; plates 65–138 ms with the zoomed tiles (12–26 ms whole frame only), which is now most of the detection time. For comparison, before this change the same clips took 106–172 s per minute of video. Add a second or two per segment for downloading the original from R2, plus the queue wait if fewer than 6 replicas are free; with one replica the segments run one after another (about 25% slower than one job), so give the worker a KEDA maximum of at least 6. Speed scales roughly with cores. To go faster at some cost in safety: `PlateTiles=false` (detection would drop to roughly a quarter) or `DetectEveryNthFrame=10`. `VideoTimeout` (25 minutes) stops a runaway job before RabbitMQ's 30-minute acknowledgement timeout.

### Sensitive media (posts tagged sensitive)

Media in a post tagged sensitive (by its author, a moderator, or automatic detection) is **never in the public bucket**: only a heavily blurred preview is public, and the real files are read through short-lived signed links.

- **The mark.** `media.SensitiveMedia` holds one row per sensitive media item, with who marked it (`Post` or `Automatic`) and, for automatic marks, the score. The Posts module marks or clears it through `IMediaDirectory.SetSensitiveAsync` whenever a post is created sensitive, tagged or untagged; clearing leaves automatic marks unless a moderator clears them. It's a separate table so tagging a post never clashes with a worker that's processing its media.
- **Where the files are.** `media.Media.IsRestricted` says whether the processed files are kept privately, in the **sensitive bucket** (`Media:Storage:SensitiveBucket`, or the originals bucket if not set) at `{userId}/{mediaId}/sensitive/720p.mp4` and `.../sensitive/thumbnail.jpg`; `PreviewKey` is the blurred preview in the public bucket, `{userId}/{mediaId}/preview.webp`. The keys keep their published names either way. Set `SensitiveBucket` to a separate private bucket (no lifecycle rule, no public access) if the originals bucket has any lifecycle (deletion) rule, or sensitive media would disappear with it.
- **The blurred preview** is made by the worker from the (already face- and plate-blurred) thumbnail, which for a video is a frame one second in: shrunk so its longest side is `Media:Processing:SensitivePreviewSourceSize` (64) pixels, which destroys detail, blurred, enlarged to `SensitivePreviewSize` (480) and blurred hard again, as WebP without metadata.
- **Processing media that's already marked** (a post made sensitive while its video was processing, or detection flagged it): the worker uploads the real files to the sensitive bucket and the preview to the public bucket, and sets `Ready` with `IsRestricted`. After saving it checks the mark again, so a post tagged or untagged during processing is caught at once. A moderator approving a sensitive item from the review queue copies the files to the sensitive bucket and queues a job to make the preview.
- **Tagging or untagging later** queues a `Visibility` job on the existing media job queue (same transport, `MediaJobStep.Visibility`). It always works from the current mark, not the one at the time of queueing. Tagged: copy the public files to the sensitive bucket (if not there yet), make and publish the preview, save `IsRestricted`, then, only after a fresh read shows the media still marked, delete the public copies and call `IMediaCachePurger` for Cloudflare. Untagged: copy the files back to the public bucket (if missing), save, delete the preview. Every step is safe to repeat; a job that stops half way is simply run again; a save that clashes with another change starts again from the saved state; late or duplicate jobs do nothing. The private copies are kept after untagging (so two racing jobs can never lose the files) and deleted with the media. A job that keeps failing ends in the failed queue (`media.process.failed`) for replay; it never fails the media itself.
- **Cloudflare cache purge.** Published files are cached for a year, so after the public copies are deleted Cloudflare can keep serving them until purged. `IMediaCachePurger` is the hook; the default only logs a warning (event id 4801, `MediaCachePurgeNeeded`, naming the media id and file names). Purging (Cloudflare's purge-by-URL API, or a cache rule) is infra work: register a real `IMediaCachePurger` in the worker host before `AddMediaProcessing`, or alert on that event.
- **Who gets what.** `IMediaDirectory.GetAsync` returns restricted media with no playback or thumbnail address, only `PreviewUrl`; `GetWithSensitiveLinksAsync` returns signed links (`Media:SensitiveLinkLifetime`, 10 minutes, `LinksExpireAt`). The Posts module decides who may see them (age-verified adults, the author, moderators). `GET /api/media/{id}` blurs sensitive media (`isBlurred: true`, `previewUrl` only) for anyone but the uploader, moderators and age-verified adults; `GET /api/media/mine` always gives the uploader their own files; data exports give 24-hour signed links; the moderator review endpoints give signed links. Deleting media deletes its private copies and preview too.

### Automatic graphic content detection

The media worker can tag graphic content (blood, injuries, the aftermath of serious crashes) itself, so it's blurred before anyone sees it. Settings in `Media:SensitiveDetection` (worker only):

| Setting | Default | |
|---|---|---|
| `Enabled` | `false` | Off until a model has been chosen and tested. Once on, a missing or failing model sends every upload to review (fail safe) |
| `Provider` | `Onnx` | `Onnx` runs `ModelPath`; `Development` is a stand-in that flags mostly red pictures, refused outside the Development environment |
| `ModelPath` | | An image classification model in ONNX format |
| `Threshold` | `0.35` | Score at or above which media is marked sensitive. Low on purpose: over-flag, moderators confirm or undo |
| `SampleEvery` | `3` | Score every Nth frame the blurring pass analyses (it analyses every 5th): about 2 frames a second at 30 fps. Photos are always scored |
| `InputWidth`, `InputHeight`, `Mean`, `Std`, `Output`, `SensitiveClasses` | 224, 224, ImageNet mean/std, `Softmax`, `1` | How to feed the model (NCHW float RGB scaled to 0–1, then normalised) and read its first output (`[1, classes]`; `Softmax` for logits, `Sigmoid` for multi-label logits, `Probabilities` if it ends in softmax); `SensitiveClasses` lists the graphic class indices |

**How it works.** Scoring runs inside the existing blurring pass on frames already decoded and scaled down for face and plate detection (640 px for videos), so nothing is decoded twice; `ISensitiveContentClassifier` is the swappable interface (`OnnxSensitiveContentClassifier` on ONNX Runtime's CPU provider, like the detectors). The highest score of any frame (the max over segments for videos processed in parts) is kept on the media (`SensitiveScore`). At or above the threshold, the worker marks the media sensitive (`Automatic`) **before** publishing, so it goes straight to the sensitive bucket with a blurred preview and is never public; the Posts module then tags its post (`sensitiveSource: Automatic`) and queues it for a moderator. If the classifier is unavailable or fails on any frame, the media goes to `NeedsReview` ("The graphic content check failed.") instead of being published unchecked.

**The model.** No suitable openly licensed graphic-injury/gore classifier could be obtained and vetted from this build environment (no network access to model hubs), so none is bundled or downloaded: this ships the interface, the pluggable ONNX implementation (tested with a small model built in the tests) and the Development stand-in. Options to evaluate before enabling: a classifier fine-tuned (MobileNetV3 or EfficientNet-B0, Apache-2.0/BSD weights from torchvision or timm) on labelled dashcam frames, starting with the moderators' confirm/undo outcomes recorded here; or a zero-shot CLIP image encoder (OpenAI CLIP weights are MIT) scored against fixed text prompts, exported to ONNX as a two-class head. Check the licence of the weights **and** of the training data, test on real UK crash footage, and set `Threshold` from the recorded scores. Limitations of any such model: dashcam crashes rarely show injuries close up; red cars, brake lights, sunsets and fire engines cause false positives (hence moderator review); small or distant casualties will be missed (hence user and moderator tagging still matter).

**Latency.** A 224×224 MobileNet-class model takes roughly 5–15 ms per frame on one 4-vCPU worker (EfficientNet-B0 about 15–30 ms). At `SampleEvery=3` a 30-second clip at 30 fps has 60 frames scored (900 frames, every 5th analysed, every 3rd of those scored): about **0.3–0.9 s of CPU per clip** (1–2 s with EfficientNet-B0), but spread over the six 5-second segments processed in parallel, so roughly **0.1–0.3 s added to the time until the clip is live**, plus a one-off model load (under a second) per worker replica. Photos: one frame, about 10–30 ms. Not measured in CI, since no production model is configured; the Redaction timing step can be extended once one is chosen.

### Moderation of media (`/api/admin/media`, `bjk.media.manage` + Moderator/Admin with two-factor)

| Endpoint | What it does |
|---|---|
| `GET /api/admin/media/review?page=&pageSize=` | The review queue (`NeedsReview`), oldest first, with reasons, blur counts and signed preview links (15 minutes) |
| `GET /api/admin/media/{id}/review` | One item with signed links to its blurred preview and thumbnail (public links once Ready) |
| `GET /api/admin/media/{id}/original` | **Admins only.** A signed link (15 minutes) to the unblurred original. Each use is recorded in the moderation audit log (`ViewMediaOriginal`) |
| `POST /api/admin/media/{id}/approve` | Copies the blurred files to the public bucket and sets `Ready` (to the sensitive bucket, with the preview made by the worker, if the media is sensitive) |
| `POST /api/admin/media/{id}/reject` `{ "reason": "..." }` | Sets `Failed` with the reason (shown to the uploader) and removes the review copies |
| `POST /api/admin/media/{id}/redactions` `{ "regions": [{ "x":0.1, "y":0.4, "width":0.2, "height":0.1, "startSeconds":3, "endSeconds":8 }] }` | Adds blur regions (fractions of the picture from the top left; optional time range for videos) and processes the media again **from its current processed file** (copied aside to `{userId}/{mediaId}/rework/r{revision}.mp4|webp` in the originals bucket, since originals are deleted once the post is published): automatic detection plus every manual region, on top of the blurring already there. Works on `NeedsReview` or `Ready` media (e.g. after a report); a Ready item's public files are deleted at once (purge Cloudflare's cache too). The result comes back to the queue. Each re-processing gets new file names (`720p-r1.mp4`), because published files are cached as immutable |

Approvals, rejections and added regions are recorded in the moderation audit log too (`ApproveMedia`, `RejectMedia`, `AddMediaRedactions`, with before/after status). Re-processing never needs the original. The unblurred-original link (admins) works only while the original still exists.

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
| `POST /api/profiles/{handle}/reports` | Signed-in user: report a profile (served by the Posts module, which owns the reports queue) |
| `GET /api/admin/profiles/{handle}`, `POST /api/admin/profiles/{handle}/reset` | Moderator (`bjk.profiles.manage`): any profile; reset an offensive profile (reason required, audited) |

- **No personal details**: display names, bios and locations can't contain number plates, phone numbers, email addresses, street addresses or social handles (see "Online safety").
- **Blocking**: up to 1,000 people per user, in `profiles.Blocks`. The Posts module hides blocked users' posts and comments from the blocker and stops blocked users commenting on or reacting to the blocker's posts. Blocks go when either account is deleted.
- **Moderation reset**: the display name goes back to the handle and the bio, location and avatar are removed (the avatar's files are deleted). The handle itself isn't changed.

Configuration: `ConnectionStrings:<name>`, the name passed to `new ProfilesModule(...)`. Its tables are in the `profiles` schema of `badjockeys`. It needs the Media module registered too.

## Posts module

Posts of driving incidents, in the `posts` schema: `Posts`, `PostMedia`, `Reactions`, `Comments`, `CommentReactions`, `Reports`, `Appeals`, `ModerationAuditLog`.

- **Deactivated authors**: while an author's account is deactivated, their posts and comments are hidden from everyone (feed, post pages, comment lists and counts). Their reactions still count.
- **A post** has a title (up to 120 characters), an optional description (5,000), a category, when the incident happened (not in the future), a map pin (latitude/longitude, optional place name), and **1 to 10 photos or one video** (a video can't be mixed with anything else: "A post can have 1 to 10 photos or one video, not both. Post the video on its own.").
- **Categories**: RoadRage, DangerousOvertaking, Tailgating, MobilePhoneUse, Speeding, RedLightRunning, CuttingUp, Undertaking, LaneHogging, ClosePass, BadParking, Other.
- **Post now, publish when ready** (19 Oct 2026): the photos or video must be the author's own uploads, and the upload must be complete, but they don't have to have finished processing. If any is still `Processing` (or `NeedsReview`, waiting for a moderator), the post is saved with `mediaState: "Pending"` (`isProcessing: true`): only its author (in `GET /api/posts/mine` and on the post page) and moderators see it, and its `media` lists only the items that are ready. When the last item becomes `Ready`, the `media.ready` event publishes it automatically (`mediaState: "Ready"`), and it appears in the feed. If any item fails, is rejected by a moderator or is deleted, `media.failed` marks the post `mediaState: "Failed"` with `mediaFailureReason` (e.g. "Videos can be up to 30 seconds long..."), it's never published, and the author is emailed once ("Your post couldn't be published", template `EmailTemplates.PostNotPublished`) at the `email` claim their token had when they posted; that address is kept on the post only while it's pending (`posts.Posts.NotifyEmail`) and cleared as soon as it's published or they've been told. They can delete the post and try again. Media that finishes while the post is being saved is caught by a second check after saving. Posts whose media are already Ready are visible straight away, as before. Content screening, the sensitive tag, the age check, new-account limits and moderation all apply as before, whatever the media state: a post shows to others only when its status is `Published` **and** its media are `Ready`. Each photo or video can be in only one post. The author needs a profile.
- **Editing**: the author (or an admin) can change the details but not the media. Deleting a post also deletes its photos and videos. The row is kept, marked Deleted.
- **Reactions**: one per user per post (Like, Angry, Shocked, Laugh). Reacting again changes it. Posts show counts per reaction and the viewer's own reaction.
- **Comments and replies**: up to 2,000 characters. Replies go **one level deep**: a reply points to a top-level comment on the same post, and replying to a reply is refused with a 400 ("You can't reply to a reply..."). The author can edit; the author or a moderator can delete. Commenting needs a profile. Comment lists return top-level comments, oldest first, each with its reply count and first 3 replies; the rest come from the replies endpoint. A hidden or deleted top-level comment that still has replies is shown as a placeholder (`isRemoved: true`, no author, empty body) with its replies underneath. The post's comment count includes replies.
- **Comment reactions**: one per user per comment or reply (Like, Angry, Shocked, Laugh), changeable and removable, in `posts.CommentReactions`. Comments show counts per type and the viewer's own reaction.
- **Reports**: signed-in users can report a post, comment, reply or profile once, with a reason (DangerousOrIllegal, PersonalInformation, HarassmentOrHate, Spam, NotADrivingIncident, IllegalContent, Threat, InThisClip, Copyright, Other). Anyone can use the public form. Moderators work through the queue, urgent first. **Dismiss** leaves the content up. **HideContent** hides it (or resets a profile) and closes every open report about it. A hidden post looks missing to everyone except its author and moderators. See "Online safety" for priorities, emails and appeals.
- **Feed**: newest first, filterable by category, author's @handle, and area (a point plus a radius of up to 200 km).

| Endpoint | Access |
|---|---|
| `GET /api/posts`, `GET /api/posts/{id}` | Anyone, including anonymous callers (sensitive posts' media blurred unless the caller is age-verified, the author or a moderator) |
| `GET /api/posts/mine` | Signed-in user (`bjk.posts`): my posts, including hidden and held ones and ones waiting for their media (`mediaState`) |
| `POST /api/posts`, `PUT` / `DELETE /api/posts/{id}` | Signed-in user; edit and delete are for the author |
| `PUT` / `DELETE /api/posts/{id}/reaction` | Signed-in user |
| `GET /api/posts/{id}/comments`, `GET /api/posts/{id}/comments/{commentId}/replies` | Signed-in user (logged-out viewers see no comments) |
| `POST /api/posts/{id}/comments`, `POST /api/posts/{id}/comments/{commentId}/replies`, `PUT` / `DELETE /api/posts/{id}/comments/{commentId}` | Signed-in user |
| `PUT` / `DELETE /api/posts/{id}/comments/{commentId}/reaction` | Signed-in user |
| `POST /api/posts/{id}/reports`, `POST /api/posts/{id}/comments/{commentId}/reports`, `POST /api/profiles/{handle}/reports` | Signed-in user |
| `POST /api/posts/{id}/appeal`, `POST /api/posts/{id}/comments/{commentId}/appeal` | Signed-in author, once per decision |
| `POST /api/reports/public` | Anyone, with a Turnstile token in `X-Captcha-Token` |
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
    new MediaModule("AppDatabase"),
    new ProfilesModule("AppDatabase"),
    new PostsModule("AppDatabase"),
    new EmailModule()); // report and appeal emails
```

### Online safety

Built for the Online Safety Act design (architecture decisions, 18 Oct 2026).

- **No personal details in text.** Post titles, descriptions and place names, comments and replies, and profile display names, bios and locations are checked by `TextSafety` (Shared). Refused, with a 400 and a friendly message on the field: UK number plates in every format (current `AB12 CDE`, prefix `A123 BCD`, suffix `ABC 123D`, Northern Ireland `AXZ 99`, and dateless `ABC 123` / `123 ABC` when written in capitals), in any case and with or without the space; UK and international phone numbers (10 to 15 digits starting `0`, `00` or `+`); email addresses; street addresses (a house number, one or two name words, then a street type such as Road or Avenue); and social handles or profile links (`@handle`, `x.com/…`, `tiktok.com/@…`, `instagram.com/…`, `facebook.com/…` and similar). Road numbers, junctions, speeds and car models are deliberately allowed: "M62 J26", "A road", "doing 70 in a 30", "BMW 320d". A plate whose letters spell a common short word ("A56 THE") is missed rather than refusing ordinary text.
- **Content screening.** Every comment, reply and post text goes through `IContentScreener`, scoring 0 to 1. At or above `Screening:HoldThreshold` (0.8) it's **held for review**: only its author sees it (status `HeldForReview`) until a moderator approves or removes it. At or above `Screening:FlagThreshold` (0.5) it's published but **flagged** for a moderator. The default provider is a built-in keyword and phrase list (threats, slurs, abuse, and calls to find or confront people) with three severities; text is normalised so "k1lll" matches "kill". The `Http` provider also calls a classifier service, intended for an open-source Detoxify model in a sidecar container: `POST {"text": "..."}` returning `{"scores": {"toxicity": 0.97, ...}}`; the higher of the two scores wins, and if the service is down the keyword result is used (labelled `classifier_unavailable`). Edits are screened again; a flag stays until a moderator clears it.
- **Sensitive posts are shown blurred** (revised 19 Oct 2026, with the 13+ age policy). `isSensitive` (graphic injuries, serious crashes) is set by the author on create or edit, by a moderator, or by automatic detection in the media worker (see "Sensitive media" in the Media module). Sensitive posts appear for **everyone**, in the feed, profile lists and on the post page, with their title, description and details, but their photos and video come **blurred** unless the caller may see them: a signed-in user whose token has `age_verified` = `"true"` (they passed the on-demand age check), the post's author, or a moderator (moderator rights, with or without the claim). For everyone else (logged out, or not age-verified) the post has `requiresAgeVerification: true` and each `media` entry has `isBlurred: true`, `playbackUrl: null`, `thumbnailUrl: null` and only `previewUrl`, a heavily blurred low-resolution WebP (null for the few seconds until the worker makes it: show a placeholder). This holds even while the media's files are still being moved: the response never contains a real address for a blurred viewer. Callers who may see it get the real files: for sensitive media, short-lived signed links (`Media:SensitiveLinkLifetime`, 10 minutes) with `linksExpireAt`; fetch the post again for fresh links. `sensitiveSource` says who tagged it (`Author`, `Moderator`, `Automatic`). Authors can remove only a tag they set themselves, and not if detection also flagged the media (400). Tagging or untagging moves the media's files (Media module) within seconds.
- **Automatic sensitive tags and the review queue.** If the media worker's graphic content detection marked any of a post's media (`MediaSummary.isAutoSensitive`), the post is tagged sensitive with `sensitiveSource: "Automatic"` before it's published (when it's created with media already flagged, in the same save that publishes a post waiting for its media, or when a published post's media is processed again), recorded in the audit log (`AutoTagSensitive`, actor role `Automatic`) and put in the moderators' queue (`sensitiveReview: Pending`, `GET /api/admin/posts/sensitive-review`). A moderator confirms or undoes it (`POST /api/admin/posts/{id}/sensitive-review`, audited as `ReviewSensitiveTag`); undoing publishes the media again, and detection won't tag that post again. The outcome (`SensitiveReview` Confirmed/Undone, who, when) is kept on the post with the detection score (`SensitiveScore`), and every processed media keeps its own score (`media.Media.SensitiveScore`) whether or not it crossed the threshold, so the threshold can be tuned (or a model trained) against moderators' decisions, e.g. `SELECT p.SensitiveReview, m.SensitiveScore FROM posts.Posts p JOIN posts.PostMedia pm ON pm.PostId = p.Id JOIN media.Media m ON m.Id = pm.MediaId WHERE p.SensitiveReview IN ('Confirmed','Undone')`. A moderator setting the tag with `PUT .../sensitive` settles a pending review too.
- **No comments for logged-out viewers.** The comment and reply lists need a signed-in token. Post responses to logged-out callers carry `commentsRequireSignIn: true`; the comment count is still included (it shows activity without revealing anything), so the apps can show "Sign in to see 12 comments".
- **UK only at launch.** The pin as given, before rounding, must be inside an allowed region (`Posts:AllowedRegions`, default the UK), or the post is refused with a 400 on `Location`: "Bad Jockeys only accepts incidents in the UK for now." The outlines are simplified polygons embedded in code (`ServiceRegions`), drawn at roughly 5 to 15 km accuracy: mainland Great Britain; Northern Ireland following the land border with Ireland; and the islands (Isle of Wight, Anglesey, Scilly, Arran, the Inner and Outer Hebrides, Orkney, Fair Isle, Shetland and Rathlin). They're generous at sea and tight on the Irish border and the Dover Strait, so Ireland, France and the Netherlands are refused. The Isle of Man and the Channel Islands are outside the UK, so they're refused unless added (`IM`, `JE`, `GG`). Points within a kilometre or two of the Irish border can fall either way (for example, Belcoo and Blacklion face each other across a stream).
- **Location rounding.** Map pins are rounded to about 500 m when saved (`PinRounding`): latitude to a grid of 0.0045° (500 m north–south), then longitude to 0.0045° / cos(latitude) (about 500 m east–west). The API returns only the rounded pin and the place name. The OnlineSafety migration rounds existing rows the same way (an update; nothing is deleted).
- **Age check.** Viewing the real media of sensitive posts needs the token's `age_verified` claim (above). Writes (create and edit posts, comment, reply, react, report as a signed-in user) need the token's `age_verified` claim (`"true"`), issued by the identity server once the account has passed the age check, but only while `Safety:RequireAgeVerification` is on. It's **off by default** since the age policy changed on 19 Oct 2026 (13+ can post, comment and react; the age check is for viewing sensitive posts), and kept for future use. When it's on and the claim is missing the API returns 403 with `code: "age_verification_required"` (type `https://badjockeys.com/problems/age-verification-required`), so the apps can send the user to the age check.
- **New-account limits.** While a profile is younger than `Safety:NewAccounts:Period` (24 hours) it can create at most `MaxPosts` (3) posts and `MaxComments` (20) comments and replies in any rolling 24 hours, counting deleted ones. Over the limit: 429 with `code: "new_account_limit"`.
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
| `IProfileModeration` (Profiles) | Posts | Resetting a reported profile |
| `IModerationAuditLog` (Shared, stored by Posts) | Profiles, Media, Posts | Recording moderation actions |
| `media.ready` / `media.failed` events (Media, RabbitMQ `bjk.media`) | Posts | Publishing (or failing) posts made while their media were processing |

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
- **Sent straight away, not queued.** Each send calls the provider while the request waits, so the caller knows at once whether it worked (for example, the sign-in page can say "couldn't send the code"). There's deliberately no RabbitMQ queue for email or SMS.
- **Providers.** `Email:Provider` is `SendGrid` or `Log`; `SMS:Provider` is `Twilio` or `Log`. Both default to `Log`: nothing is sent, and in Development the whole message is written to the console (`DEVELOPMENT ONLY - email not sent` / `SMS not sent`). Outside Development, `Log` only records that a message was dropped, never its content.
- **Checked at startup.** Choosing SendGrid or Twilio without the keys they need stops the app starting, with a message naming the missing setting.
- **Retries.** Timeouts, connection failures, 429 and 5xx are retried 3 times with backoff (honouring `Retry-After`, up to 10 seconds), using `ProviderRetry` from Shared. A rejection (bad number, unverified sender, bad key) isn't retried. Both throw `MessageDeliveryException` (Shared), whose `IsTransient` says which it was.
- **No click tracking.** Our emails carry sign-in, confirmation and reset codes in links, so SendGrid click and open tracking are switched off for every email.
- **Privacy.** Outside Development, logs show masked addresses only (`r***@example.com`, `+44********23`) plus the provider's message id, so a delivery can be traced in SendGrid or Twilio without logging personal data.
- **Branded emails.** `BrandedEmail` builds matching plain-text and HTML bodies (heading, paragraphs, a button, a large code, small print). All text is HTML-encoded, and buttons only accept http(s) links.

Both providers are called over their REST APIs with `HttpClient`, so there are no vendor SDKs to keep up to date.

| Key | Notes |
|---|---|
| `Email:Provider` | `Log` (default) or `SendGrid` |
| `Email:FromAddress`, `Email:FromName` | Sender. The address (or its domain) must be verified in SendGrid. Name defaults to `Bad Jockeys`. |
| `Email:ReplyToAddress` | Optional, e.g. `support@badjockeys.com` |
| `Email:LogoUrl` | Optional absolute URL of a PNG logo for the top of branded emails (Gmail and Outlook don't show SVG) |
| `Email:SendGrid:ApiKey` | Secret. A key with the **Mail Send** permission only. |
| `Email:SendGrid:BaseUrl` | Default `https://api.sendgrid.com`; `https://api.eu.sendgrid.com` for an EU-pinned subuser |
| `Email:SendGrid:SandboxMode` | `true` = SendGrid checks each request but delivers nothing |
| `SMS:Provider` | `Log` (default) or `Twilio` |
| `SMS:Twilio:AccountSid` | `AC…` |
| `SMS:Twilio:ApiKeySid`, `SMS:Twilio:ApiKeySecret` | Secret. Recommended: an API key can be revoked without changing the account's auth token. |
| `SMS:Twilio:AuthToken` | Secret. Used only if no API key is set. |
| `SMS:Twilio:MessagingServiceSid` | `MG…`, recommended: Twilio picks the sender from the service's pool |
| `SMS:Twilio:FromNumber` | E.164 number or alphanumeric sender ID, used when there's no Messaging Service |

Phone numbers must be E.164 (`+447700900123`); use `PhoneNumbers.IsE164` to check numbers you collect.
