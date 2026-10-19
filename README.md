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
      Add/                       Register: sign-in account + account record
      Deactivate/  Reactivate/   Deactivate (self with password, or admin); reactivate within the grace period
      Delete/                    Admin "delete now"; the retention job that deletes expired accounts
      Reset/                     Forgot password: request token, confirm with new password
      Get/  List/                Read side (CQRS queries)
      AccountLifecycle.cs        Deactivate / reactivate / delete, each with an outbox event
    Infrastructure/Services/     IUserRepository, IIdentityService (implemented by the host)
    Infrastructure/Data/         UserDbContext (schema: users: Accounts, Outbox) and Migrations/
    Infrastructure/              AccountRetentionService (hourly), account data export
    Domain/                      UserAccount
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
    Contracts/                   IMediaDirectory: what other modules may ask of Media
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
    Infrastructure/              Moderation audit log (stored for every module), lifecycle and data export
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
| `MediatR:LicenseKey` | MediatR 13+ needs a licence key (free Community licence under $5M revenue). Store it in Key Vault. |
| `Database:AllowDestructiveMigrations` | Default `false`. See below. |
| `Safety:RequireAgeVerification` | Default `true`: posting, editing posts, commenting, reacting and reporting need the `age_verified` claim. `false` only for local development and tests. |
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
| `POST /api/users` | Anonymous + CAPTCHA: register (email, password) |
| `GET /api/users/me` | Signed-in user: email, status, dates |
| `POST /api/users/me/deactivate` | Signed-in user, confirming with their password |
| `POST /api/users/reactivate` | Anonymous + CAPTCHA: email and password of a deactivated account |
| `POST /api/users/password-reset`, `.../confirm` | Anonymous (+ CAPTCHA for the request) |
| `GET /api/users?status=`, `GET /api/users/{id}` | Admin (or yourself for `{id}`) |
| `POST /api/users/{id}/deactivate`, `POST /api/users/{id}/reactivate`, `DELETE /api/users/{id}` | Admin. `DELETE` deletes permanently now. |

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
| Identity server | `account`: id, email, status, dates |
| bjk-core | `profile`; `posts` (posts, comments, reactions, reports made); `media` (every upload, with a 24-hour download link for each original still kept, and the processed files) |

The apps call both endpoints and offer the two files (or one zip) as "Download my data". A deactivated user can't sign in, so they reactivate, download, then deactivate again if they want.

## Media module

Photos and videos follow the same flow; only the worker's processing differs.

1. `POST /api/media/uploads` with `{ fileName, contentType, sizeBytes }` creates a `media.Media` row (`Uploading`) and returns a signed link, valid for 15 minutes, to PUT the file straight into the private **originals** bucket. The API never handles the file.
2. The app PUTs the file to that link, sending the `Content-Type` header it was given.
3. `POST /api/media/{id}/complete` checks the file is in storage and within the size limit, sets `Processing`, and puts a job on the RabbitMQ queue.
4. The **media worker** (bjk-core's worker container, using `services.AddMediaProcessing(...)` from this module) takes the job, converts the file and publishes it to the **public** bucket, then sets `Ready`. Only `Ready` media is visible to other users.

### Processing

| | Video | Photo |
|---|---|---|
| Output | `720p.mp4`: H.264 + AAC, at most 720 pixels high and 30 fps, fast start for streaming | `image.webp`: at most 2048 pixels on its longest side |
| Thumbnail | `thumbnail.jpg`, from one second in | `thumbnail.webp`, at most 640 pixels |
| Checks | Must contain a picture; no longer than `Media:MaxVideoDurationSeconds` (5 minutes) | Must contain a picture |

- **Metadata is stripped** from every published file, so no GPS position, device or owner details go public. The original keeps them, in the private bucket, until it's deleted after 90 days.
- **SHA-256** of each original is recorded, for spotting duplicates later.
- **Failures.** A file ffmpeg can't read, with no picture, or too long fails straight away, with a reason the app can show. Anything temporary (storage or database unavailable) is retried; the job is delivered at most 3 times, and the last attempt marks the media `Failed`, so nothing stays at `Processing`.
- **Deleted meanwhile.** If the owner deletes the media while it's being processed, the worker removes what it published.
- ffmpeg runs as a child process with its arguments passed as a list, never through a shell.
- The worker doesn't load the Media API, migrations or the user lifecycle consumer. The API does those, so account events aren't split between the two.

| Setting (`Media:Processing:*`, worker only) | Default |
|---|---|
| `FfmpegPath`, `FfprobePath` | `ffmpeg`, `ffprobe` on the PATH |
| `MaxConcurrentJobs` | 1 (encoding already uses every core) |
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
| `Media:Queue:QueueName` | Default `media.process`; failed jobs go to `media.process.failed` |
| `Media:MaxVideoBytes`, `Media:MaxImageBytes`, `Media:MaxVideoDurationSeconds`, `Media:UploadUrlLifetime`, `Media:OriginalRetention` | Defaults: 500 MB, 25 MB, 300 s, 15 min, 90 days |

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
- **A post** has a title (up to 120 characters), an optional description (5,000), a category, when the incident happened (not in the future), a map pin (latitude/longitude, optional place name), and 1 to 10 photos or videos.
- **Categories**: RoadRage, DangerousOvertaking, Tailgating, MobilePhoneUse, Speeding, RedLightRunning, CuttingUp, Undertaking, LaneHogging, ClosePass, BadParking, Other.
- **Publishing**: the photos and videos must be the author's own uploads and already Ready. The app uploads them, waits for processing, then creates the post, which everyone can see straight away. Each photo or video can be in only one post. The author needs a profile.
- **Editing**: the author (or an admin) can change the details but not the media. Deleting a post also deletes its photos and videos. The row is kept, marked Deleted.
- **Reactions**: one per user per post (Like, Angry, Shocked, Laugh). Reacting again changes it. Posts show counts per reaction and the viewer's own reaction.
- **Comments and replies**: up to 2,000 characters. Replies go **one level deep**: a reply points to a top-level comment on the same post, and replying to a reply is refused with a 400 ("You can't reply to a reply..."). The author can edit; the author or a moderator can delete. Commenting needs a profile. Comment lists return top-level comments, oldest first, each with its reply count and first 3 replies; the rest come from the replies endpoint. A hidden or deleted top-level comment that still has replies is shown as a placeholder (`isRemoved: true`, no author, empty body) with its replies underneath. The post's comment count includes replies.
- **Comment reactions**: one per user per comment or reply (Like, Angry, Shocked, Laugh), changeable and removable, in `posts.CommentReactions`. Comments show counts per type and the viewer's own reaction.
- **Reports**: signed-in users can report a post, comment, reply or profile once, with a reason (DangerousOrIllegal, PersonalInformation, HarassmentOrHate, Spam, NotADrivingIncident, IllegalContent, Threat, InThisClip, Copyright, Other). Anyone can use the public form. Moderators work through the queue, urgent first. **Dismiss** leaves the content up. **HideContent** hides it (or resets a profile) and closes every open report about it. A hidden post looks missing to everyone except its author and moderators. See "Online safety" for priorities, emails and appeals.
- **Feed**: newest first, filterable by category, author's @handle, and area (a point plus a radius of up to 200 km).

| Endpoint | Access |
|---|---|
| `GET /api/posts`, `GET /api/posts/{id}` | Anyone, including anonymous callers (never sensitive posts for them) |
| `GET /api/posts/mine` | Signed-in user (`bjk.posts`): my posts, including hidden and held ones |
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
- **Sensitive posts.** `isSensitive` (graphic injuries, serious crashes) is set by the author on create or edit, or by a moderator. Logged-out callers never get sensitive posts: they're left out of the feed and profile lists, and the post page is a 404.
- **No comments for logged-out viewers.** The comment and reply lists need a signed-in token. Post responses to logged-out callers carry `commentsRequireSignIn: true`; the comment count is still included (it shows activity without revealing anything), so the apps can show "Sign in to see 12 comments".
- **UK only at launch.** The pin as given, before rounding, must be inside an allowed region (`Posts:AllowedRegions`, default the UK), or the post is refused with a 400 on `Location`: "Bad Jockeys only accepts incidents in the UK for now." The outlines are simplified polygons embedded in code (`ServiceRegions`), drawn at roughly 5 to 15 km accuracy: mainland Great Britain; Northern Ireland following the land border with Ireland; and the islands (Isle of Wight, Anglesey, Scilly, Arran, the Inner and Outer Hebrides, Orkney, Fair Isle, Shetland and Rathlin). They're generous at sea and tight on the Irish border and the Dover Strait, so Ireland, France and the Netherlands are refused. The Isle of Man and the Channel Islands are outside the UK, so they're refused unless added (`IM`, `JE`, `GG`). Points within a kilometre or two of the Irish border can fall either way (for example, Belcoo and Blacklion face each other across a stream).
- **Location rounding.** Map pins are rounded to about 500 m when saved (`PinRounding`): latitude to a grid of 0.0045° (500 m north–south), then longitude to 0.0045° / cos(latitude) (about 500 m east–west). The API returns only the rounded pin and the place name. The OnlineSafety migration rounds existing rows the same way (an update; nothing is deleted).
- **Age check.** Writes (create and edit posts, comment, reply, react, report as a signed-in user) need the token's `age_verified` claim (`"true"`), issued by the identity server once the account has passed the age check, while `Safety:RequireAgeVerification` is on (the default). Without it the API returns 403 with `code: "age_verification_required"` (type `https://badjockeys.com/problems/age-verification-required`), so the apps can send the user to the age check.
- **New-account limits.** While a profile is younger than `Safety:NewAccounts:Period` (24 hours) it can create at most `MaxPosts` (3) posts and `MaxComments` (20) comments and replies in any rolling 24 hours, counting deleted ones. Over the limit: 429 with `code: "new_account_limit"`.
- **Reports.** In the app: posts, comments, replies and profiles. The public form `POST /api/reports/public` ("Report content / I'm in this clip") needs no account: the post id or link (plus an optional comment id), a reason (InThisClip, PersonalInformation, IllegalContent, Threat, HarassmentOrHate, Copyright, Other), details, the reporter's name and email, consent, and a Cloudflare Turnstile token in `X-Captcha-Token` (`ICaptchaVerifier`; a failed token is a 400, an unreachable Turnstile a 503). It returns a reference (`R-1A2B3C4D`), the priority and when a decision is due. **Priority**: `Urgent` for IllegalContent and Threat, with a 4-hour target; `Normal` otherwise, 48 hours. The queue lists urgent reports first, then by due time. **Emails** (Email module, sent straight away): an acknowledgement when the report is made and the outcome when it's resolved, to the public form's address or, for signed-in reporters, the `email` claim in their token if it has one. A failed email is logged and recorded (`acknowledgedAt` / `outcomeNotifiedAt` stay empty); it never undoes the report or decision.
- **Appeals.** An author can appeal a moderator's decision to hide their post or comment, once (`posts.Appeals`). Appeals have a 48-hour target and their own queue; overturning shows the content again. The author is emailed when the appeal is received and decided (if their token carries an email).
- **Moderation audit log.** Every moderator or admin action in Posts (resolve report, unhide, delete post or comment, approve or remove screened content, sensitive tag, appeal decision), Profiles (reset) and Media (delete) is recorded in `posts.ModerationAuditLog`: actor id and role (Admin, Moderator, or Service for machine tokens), action, target type and id, reason, before/after summaries (states only, never content or personal details) and time. A reason or note is required on every action. `GET /api/admin/audit` (admins only) filters by actor, module, action, target and time. Other modules write through Shared's `IModerationAuditLog`, which the Posts module implements; Posts actions are saved together with their audit entry.
- **Blocking.** See the Profiles module. Blocked users' posts are left out of the blocker's feed and look missing; their comments and replies are left out; and they can't comment on, reply to or react to the blocker's posts and comments.

### How modules talk to each other

A module never reads another module's tables. Each module that others depend on publishes a small interface in its `Contracts` folder, registered by the module itself:

| Contract | Used by | For |
|---|---|---|
| `IMediaDirectory` (Media) | Profiles, Posts | Checking media is the user's own and Ready, getting its URLs, deleting it with a post or old avatar |
| `IProfileDirectory` (Profiles) | Posts | Authors' handles, names, avatars and profile age; does the user have a profile |
| `IBlockDirectory` (Profiles) | Posts | Who the viewer has blocked; has the author blocked this user |
| `IProfileModeration` (Profiles) | Posts | Resetting a reported profile |
| `IModerationAuditLog` (Shared, stored by Posts) | Profiles, Media, Posts | Recording moderation actions |

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
