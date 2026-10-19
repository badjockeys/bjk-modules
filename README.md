# bjk-modules

Feature modules for the Bad Jockeys platform. Each module is a self-contained vertical slice (API, use cases, infrastructure, domain), published as a NuGet package to GitHub Packages and loaded by a host: `bjk-identity-server` or `bjk-core`.

## Layout

```
src/
  BJK.Modules.Shared/            Module registration, MediatR validation pipeline, error handling,
                                 current user, safe startup migrations, RabbitMQ connection and outbox,
                                 provider retries and MessageDeliveryException (Email, SMS),
                                 user lifecycle events, GET /api/me/data-export
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
    Contracts/                   IProfileDirectory: author names and avatars for other modules
    Infrastructure/Data/         ProfilesDbContext (schema: profiles) and Migrations/
    Domain/                      Profile, Handle rules
    ProfilesModule.cs            Module entry point
  BJK.Modules.Posts/             Hosted by bjk-core
    API/Controllers/             PostsController (/api/posts), PostReportsController (/api/posts/reports)
    UseCases/
      Create/  Update/  Delete/  Publish, edit and delete posts
      Get/                       One post, the public feed, my posts
      Reactions/  Comments/      React, comment
      Reports/                   Report a post or comment; admin review queue
    Infrastructure/Data/         PostsDbContext (schema: posts) and Migrations/
    Domain/                      Post, PostMedia, Reaction, Comment, Report
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
4. The **media worker** (bjk-core's worker container, using `services.AddMediaProcessing(...)` from this module) takes the job, blurs faces and number plates, converts the file and publishes it to the **public** bucket, then sets `Ready`. If automatic blurring failed or wasn't sure, it keeps the blurred files private and sets `NeedsReview` instead; a moderator approves (then `Ready`) or rejects (then `Failed`). Only `Ready` media is visible to other users or can be posted.

### Processing

| | Video | Photo |
|---|---|---|
| Output | `720p.mp4`: H.264, no sound (see `Media:Redaction:RemoveAudio`), at most 720 pixels high and 30 fps, fast start for streaming | `image.webp`: at most 2048 pixels on its longest side |
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
| `FfmpegPath`, `FfprobePath` | `ffmpeg`, `ffprobe` on the PATH (ffmpeg 5.1 or later) |
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
| `DELETE /api/media/{id}` | Owner, or admin (`bjk.media.manage`) |

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

### Automatic blurring (faces and number plates)

Every photo and video is blurred by the worker before anything is published (Online Safety Act design, 18 Oct 2026).

**How it works**

1. ffmpeg decodes the picture (photos: upright, at most 2048 px; videos: at most 720p and 30 fps) into memory over a pipe. Nothing unblurred is written out.
2. Open-source detection models run on the CPU with ONNX Runtime (`Microsoft.ML.OnnxRuntime`, MIT): YuNet for faces (each frame at full size and again at 640 px for faces close to the camera) and LPD-YuNet for number plates (the whole frame plus a 2x2 grid of overlapping zoomed tiles for small, distant plates).
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

The blurred files are still produced, but kept in the **private** bucket under `{userId}/{mediaId}/review/` (originals bucket, so the 90-day lifecycle rule removes them too) until a moderator approves them. They're never public before that, and the media can't be posted (Posts only accepts `Ready` media). The reason is in `ReviewReason`.

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
| `InferenceThreads` | 0 (ONNX Runtime decides: all cores) |
| `VideoTimeout` | 25 minutes per video |

**Timing** (measured in CI on 2-vCPU GitHub runners, 60-second 1080p30 clips, the Media tests running alone; two runs, which varied by about 25%):

| | Per minute of video | 5-minute clip | Of which detection |
|---|---|---|---|
| Moving test pattern, no faces (baseline) | 141–172 s | 12–14 min | 67–84 s/min |
| Crowd photo panning, ~200 faces a frame (worst case for blurring) | 106–131 s | 9–11 min | 67–85 s/min |

Per 720p frame analysed: faces 43–55 ms, plates 129–159 ms with tiles (25–31 ms whole frame only). The rest is ffmpeg decoding and encoding. Speed scales roughly with cores, so a 4-vCPU worker should take about half as long. To go faster at some cost in safety: `PlateTiles=false` (saves about 3.5 minutes per 5-minute clip on 2 vCPUs) or `DetectEveryNthFrame=10` (about 3 minutes). `VideoTimeout` (25 minutes) stops a runaway job before RabbitMQ's 30-minute acknowledgement timeout.

### Moderation of media (`/api/admin/media`, `bjk.media.manage` + Moderator/Admin with two-factor)

| Endpoint | What it does |
|---|---|
| `GET /api/admin/media/review?page=&pageSize=` | The review queue (`NeedsReview`), oldest first, with reasons, blur counts and signed preview links (15 minutes) |
| `GET /api/admin/media/{id}/review` | One item with signed links to its blurred preview and thumbnail (public links once Ready) |
| `GET /api/admin/media/{id}/original` | **Admins only.** A signed link (15 minutes) to the unblurred original. Each use is logged as `AUDIT media.original.viewed` with the admin's and media's ids (no shared audit log exists in this module yet) |
| `POST /api/admin/media/{id}/approve` | Copies the blurred files to the public bucket and sets `Ready` |
| `POST /api/admin/media/{id}/reject` `{ "reason": "..." }` | Sets `Failed` with the reason (shown to the uploader) and removes the review copies |
| `POST /api/admin/media/{id}/redactions` `{ "regions": [{ "x":0.1, "y":0.4, "width":0.2, "height":0.1, "startSeconds":3, "endSeconds":8 }] }` | Adds blur regions (fractions of the picture from the top left; optional time range for videos) and processes the media again from the original: automatic detection plus every manual region. Works on `NeedsReview` or `Ready` media (e.g. after a report); a Ready item's public files are deleted at once (purge Cloudflare's cache too). The result comes back to the queue. Each re-processing gets new file names (`720p-r1.mp4`), because published files are cached as immutable |

Approvals, rejections and added regions are also logged with an `AUDIT` prefix. The original must still exist (90 days) to re-process.

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

Configuration: `ConnectionStrings:<name>`, the name passed to `new ProfilesModule(...)`. Its tables are in the `profiles` schema of `badjockeys`. It needs the Media module registered too.

## Posts module

Posts of driving incidents, in the `posts` schema: `Posts`, `PostMedia`, `Reactions`, `Comments`, `Reports`.

- **Deactivated authors**: while an author's account is deactivated, their posts and comments are hidden from everyone (feed, post pages, comment lists and counts). Their reactions still count.
- **A post** has a title (up to 120 characters), an optional description (5,000), a category, when the incident happened (not in the future), a map pin (latitude/longitude, optional place name), and 1 to 10 photos or videos.
- **Categories**: RoadRage, DangerousOvertaking, Tailgating, MobilePhoneUse, Speeding, RedLightRunning, CuttingUp, Undertaking, LaneHogging, ClosePass, BadParking, Other.
- **Publishing**: the photos and videos must be the author's own uploads and already Ready. The app uploads them, waits for processing, then creates the post, which everyone can see straight away. Each photo or video can be in only one post. The author needs a profile.
- **Editing**: the author (or an admin) can change the details but not the media. Deleting a post also deletes its photos and videos. The row is kept, marked Deleted.
- **Reactions**: one per user per post (Like, Angry, Shocked, Laugh). Reacting again changes it. Posts show counts per reaction and the viewer's own reaction.
- **Comments**: flat (no replies), up to 2,000 characters. The author can edit; the author or an admin can delete. Commenting needs a profile.
- **Reports**: any signed-in user can report a post or comment once, with a reason (DangerousOrIllegal, PersonalInformation, HarassmentOrHate, Spam, NotADrivingIncident, Other). Admins work through the queue. **Dismiss** leaves the content up. **HideContent** hides it and closes every open report about it. A hidden post looks missing to everyone except its author and admins. Admins can unhide it.
- **Feed**: newest first, filterable by category, author's @handle, and area (a point plus a radius of up to 200 km).

| Endpoint | Access |
|---|---|
| `GET /api/posts`, `GET /api/posts/{id}`, `GET /api/posts/{id}/comments` | Anyone, including anonymous callers |
| `GET /api/posts/mine` | Signed-in user (`bjk.posts`): my posts, including hidden ones |
| `POST /api/posts`, `PUT` / `DELETE /api/posts/{id}` | Signed-in user; edit and delete are for the author or an admin |
| `PUT` / `DELETE /api/posts/{id}/reaction` | Signed-in user |
| `POST /api/posts/{id}/comments`, `PUT` / `DELETE /api/posts/{id}/comments/{commentId}` | Signed-in user |
| `POST /api/posts/{id}/reports`, `POST /api/posts/{id}/comments/{commentId}/reports` | Signed-in user |
| `GET /api/posts/reports`, `POST /api/posts/reports/{id}/resolve`, `POST /api/posts/{id}/unhide` | Admin (`bjk.posts.manage`) |

Configuration: `ConnectionStrings:<name>`, the name passed to `new PostsModule(...)`. Its tables are in the `posts` schema of `badjockeys`. It needs the Media and Profiles modules registered too:

```csharp
builder.Services.AddBjkModules(builder.Configuration,
    new MediaModule("AppDatabase"),
    new ProfilesModule("AppDatabase"),
    new PostsModule("AppDatabase"));
```

### How modules talk to each other

A module never reads another module's tables. Each module that others depend on publishes a small interface in its `Contracts` folder, registered by the module itself:

| Contract | Used by | For |
|---|---|---|
| `IMediaDirectory` (Media) | Profiles, Posts | Checking media is the user's own and Ready, getting its URLs, deleting it with a post or old avatar |
| `IProfileDirectory` (Profiles) | Posts | Authors' handles, names and avatars; does the user have a profile |

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
