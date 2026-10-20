using System.ComponentModel.DataAnnotations;

namespace BJK.Modules.Media;

/// <summary>Limits and timings for uploads. Section <c>Media</c>; every value has a sensible default.</summary>
public sealed class MediaOptions
{
    public const string SectionName = "Media";

    /// <summary>
    /// Largest video upload. 150 MB leaves plenty of room for 30 seconds of 4K dashcam footage (about 80 to 100 MB at
    /// typical bit rates); the apps shrink videos on the phone before uploading anyway.
    /// </summary>
    [Range(1, long.MaxValue)]
    public long MaxVideoBytes { get; set; } = 150L * 1024 * 1024;

    [Range(1, long.MaxValue)]
    public long MaxImageBytes { get; set; } = 25L * 1024 * 1024;

    /// <summary>
    /// Longest video, in seconds (30 since 19 Oct 2026). Checked by the worker once ffmpeg has read the video, with
    /// <see cref="DurationTolerance"/> to spare, because a clip trimmed to exactly 30 seconds is often a few
    /// hundredths over once it's in a file.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MaxVideoDurationSeconds { get; set; } = 30;

    /// <summary>How far over <see cref="MaxVideoDurationSeconds"/> a video may run before it's refused.</summary>
    public static readonly decimal DurationTolerance = 0.5m;

    /// <summary>True if a video of this length is over the limit.</summary>
    /// <param name="seconds">The video's length.</param>
    public bool IsTooLong(decimal seconds) => seconds > MaxVideoDurationSeconds + DurationTolerance;

    /// <summary>The message the uploader sees when a video is too long, e.g. "Videos can be up to 30 seconds long...".</summary>
    /// <param name="seconds">The video's length.</param>
    public string TooLongMessage(decimal seconds) =>
        $"Videos can be up to {Describe(MaxVideoDurationSeconds)} long, and this one is {Describe((int)Math.Ceiling(seconds))}. Trim it to {Describe(MaxVideoDurationSeconds)} or less and upload it again.";

    /// <summary>The message the uploader sees when a file is too big, e.g. "Videos can be up to 150 MB...".</summary>
    /// <param name="type">Video or photo.</param>
    /// <param name="sizeBytes">The file's size, if known.</param>
    public string TooBigMessage(Domain.MediaType type, long? sizeBytes = null)
    {
        var limit = MaxBytesFor(type) / (1024 * 1024);
        var what = type == Domain.MediaType.Video ? "Videos" : "Photos";
        var size = sizeBytes is { } bytes ? $", and this one is {Math.Max(1, (bytes + (1024 * 1024) - 1) / (1024 * 1024))} MB" : string.Empty;
        var hint = type == Domain.MediaType.Video
            ? $" Trim it to {Describe(MaxVideoDurationSeconds)} or export it at a lower quality, then upload it again."
            : " Export it at a smaller size, then upload it again.";
        return $"{what} can be up to {limit} MB{size}.{hint}";
    }

    /// <summary>"30 seconds", "1 minute", "2 minutes 30 seconds".</summary>
    /// <param name="seconds">A whole number of seconds.</param>
    public static string Describe(int seconds)
    {
        var minutes = seconds / 60;
        var rest = seconds % 60;
        var parts = new List<string>();
        if (minutes > 0)
        {
            parts.Add(minutes == 1 ? "1 minute" : $"{minutes} minutes");
        }

        if (rest > 0 || minutes == 0)
        {
            parts.Add(rest == 1 ? "1 second" : $"{rest} seconds");
        }

        return string.Join(" ", parts);
    }

    public TimeSpan UploadUrlLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long the signed links to sensitive media work (for age-verified adults, the uploader and moderators). Short,
    /// so a copied link soon stops working; the apps fetch the post again for fresh links.
    /// </summary>
    public TimeSpan SensitiveLinkLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Originals are deleted as soon as they're no longer needed: when the post using the media is published (or the
    /// media is deleted). This is the most they're kept otherwise, counted from upload: failed media (until the user
    /// uploads again), media never posted, and abandoned uploads. Media still processing or waiting for a moderator
    /// keeps its original. Replaces the 90-day retention (owner's decision, 19 Oct 2026).
    /// </summary>
    public TimeSpan UnusedOriginalRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How often the API host's sweeper deletes originals past <see cref="UnusedOriginalRetention"/>.</summary>
    public TimeSpan OriginalSweepInterval { get; set; } = TimeSpan.FromHours(1);

    public long MaxBytesFor(Domain.MediaType type) =>
        type == Domain.MediaType.Video ? MaxVideoBytes : MaxImageBytes;
}

/// <summary>Cloudflare R2 (or any S3-compatible store). Section <c>Media:Storage</c>.</summary>
public sealed class MediaStorageOptions
{
    public const string SectionName = "Media:Storage";

    /// <summary>e.g. https://&lt;account id&gt;.r2.cloudflarestorage.com</summary>
    [Required]
    public Uri? ServiceUrl { get; set; }

    [Required]
    public string? AccessKeyId { get; set; }

    /// <summary>Secret: user secrets locally, a container secret when deployed.</summary>
    [Required]
    public string? SecretAccessKey { get; set; }

    /// <summary>Private bucket for uploaded originals.</summary>
    [Required]
    public string? OriginalsBucket { get; set; }

    /// <summary>Public bucket for processed files.</summary>
    [Required]
    public string? PublicBucket { get; set; }

    /// <summary>Address the public bucket is served from through Cloudflare, e.g. https://media.badjockeys.com/</summary>
    [Required]
    public Uri? PublicBaseUrl { get; set; }

    /// <summary>
    /// Private bucket for the real processed files of sensitive media (posts tagged sensitive), served only through
    /// short-lived signed links. Optional: if not set, they go in <see cref="OriginalsBucket"/> under a
    /// <c>sensitive/</c> folder. Set it wherever the originals bucket has the 90-day deletion rule, or sensitive posts
    /// would lose their media after 90 days. It needs no lifecycle rule, CORS or public access.
    /// </summary>
    public string? SensitiveBucket { get; set; }

    /// <summary>The bucket sensitive files go in: <see cref="SensitiveBucket"/>, or the originals bucket.</summary>
    public string EffectiveSensitiveBucket => string.IsNullOrWhiteSpace(SensitiveBucket) ? OriginalsBucket! : SensitiveBucket.Trim();
}

/// <summary>The processing queue. Section <c>Media:Queue</c>. It is a table in the messaging schema (see <c>Messaging</c>).</summary>
public sealed class MediaQueueOptions
{
    public const string SectionName = "Media:Queue";

    [Required]
    public string QueueName { get; set; } = "media.process";
}

/// <summary>The processing worker. Section <c>Media:Processing</c>; only the worker host reads it.</summary>
public sealed class MediaProcessingOptions
{
    public const string SectionName = "Media:Processing";

    /// <summary>ffmpeg executable: a name on the PATH or a full path.</summary>
    [Required]
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>ffprobe executable: a name on the PATH or a full path.</summary>
    [Required]
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Where each job's temporary files go; each job gets its own folder, removed when it finishes.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>
    /// Jobs one worker runs at once. Each job (a photo, a short video, or one segment of a longer video) already uses
    /// every core for detection and encoding, so keep 1 and scale out with more worker replicas instead.
    /// </summary>
    [Range(1, 16)]
    public int MaxConcurrentJobs { get; set; } = 1;

    /// <summary>
    /// Threads each ffmpeg process may use for decoding and encoding. 0 (the default) means every core this worker has
    /// (<see cref="Environment.ProcessorCount"/>, which respects the container's CPU limit; ffmpeg's own automatic
    /// choice doesn't).
    /// </summary>
    [Range(0, 64)]
    public int FfmpegThreads { get; set; }

    /// <summary>
    /// Videos longer than this are split into segments of this many seconds, each blurred and encoded as a separate
    /// queue job, so several worker replicas can work on one video at once; a final job joins them. Shorter videos are
    /// processed in one job. 0 turns splitting off.
    /// </summary>
    [Range(0, 300)]
    public int SegmentSeconds { get; set; } = 5;

    /// <summary>
    /// Extra video each segment job decodes and runs detection on, before and after its own part, so faces and plates
    /// crossing a segment boundary are tracked as if the video were in one piece and blurs don't drop at the joins.
    /// Should cover <c>Media:Redaction:DetectEveryNthFrame</c> x (<c>MaxMissedDetections</c> + 1) frames (20 frames,
    /// two thirds of a second at 30 fps, by default).
    /// </summary>
    [Range(0.0, 10.0)]
    public double SegmentOverlapSeconds { get; set; } = 1.0;

    /// <summary>The ffmpeg thread count to use: <see cref="FfmpegThreads"/>, or every core if 0.</summary>
    public int EffectiveFfmpegThreads => FfmpegThreads > 0 ? FfmpegThreads : Environment.ProcessorCount;

    /// <summary>Height of the playback video. Smaller videos keep their size.</summary>
    [Range(240, 2160)]
    public int VideoHeight { get; set; } = 720;

    /// <summary>x264 quality (lower is better and bigger). 23 is a good balance for dashcam footage.</summary>
    [Range(15, 35)]
    public int VideoCrf { get; set; } = 23;

    /// <summary>Longest side of processed photos, in pixels.</summary>
    [Range(320, 8192)]
    public int ImageMaxSize { get; set; } = 2048;

    /// <summary>
    /// Photos: longest side of the feed-size copy (<c>feed.webp</c>, same quality as the full image), which feeds load;
    /// the full <see cref="ImageMaxSize"/> image opens on tap. Never enlarged.
    /// </summary>
    [Range(320, 4096)]
    public int FeedImageMaxSize { get; set; } = 1080;

    /// <summary>Longest side of thumbnails, in pixels.</summary>
    [Range(64, 1920)]
    public int ThumbnailMaxSize { get; set; } = 640;

    /// <summary>
    /// Sensitive media's public preview: the thumbnail is shrunk so its longest side is this many pixels, which
    /// destroys detail, then blurred, enlarged to <see cref="SensitivePreviewSize"/> and blurred again.
    /// </summary>
    [Range(8, 256)]
    public int SensitivePreviewSourceSize { get; set; } = 64;

    /// <summary>Longest side of sensitive media's blurred public preview (WebP), in pixels.</summary>
    [Range(64, 1280)]
    public int SensitivePreviewSize { get; set; } = 480;

    /// <summary>Gives up on one ffmpeg run after this long.</summary>
    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromMinutes(15);
}

/// <summary>
/// Automatic blurring of faces and number plates. Section <c>Media:Redaction</c>; only the worker host reads it.
/// If a model is missing or fails, or a detection is uncertain, the media goes to <c>NeedsReview</c> instead of
/// <c>Ready</c>, so nothing unchecked is published.
/// </summary>
public sealed class MediaRedactionOptions
{
    public const string SectionName = "Media:Redaction";

    /// <summary>
    /// The YuNet face detection model (ONNX), e.g. <c>/opt/bjk/models/face_detection_yunet_2026may.onnx</c>. Not set or
    /// missing: faces can't be detected, so every upload goes to review.
    /// </summary>
    public string? FaceModelPath { get; set; }

    /// <summary>
    /// The LPD-YuNet number plate detection model (ONNX), e.g.
    /// <c>/opt/bjk/models/license_plate_detection_lpd_yunet_2023mar.onnx</c>. Not set or missing: every upload goes to review.
    /// </summary>
    public string? PlateModelPath { get; set; }

    /// <summary>Faces scoring at least this are blurred.</summary>
    [Range(0.05, 1.0)]
    public double FaceMinConfidence { get; set; } = 0.5;

    /// <summary>A face (or, in a video, a face's whole track) that never scores this much is uncertain: the media goes to review.</summary>
    [Range(0.05, 1.0)]
    public double FaceConfidentAt { get; set; } = 0.8;

    /// <summary>Number plates scoring at least this are blurred.</summary>
    [Range(0.05, 1.0)]
    public double PlateMinConfidence { get; set; } = 0.5;

    /// <summary>A plate (or a plate's whole track) that never scores this much is uncertain: the media goes to review.</summary>
    [Range(0.05, 1.0)]
    public double PlateConfidentAt { get; set; } = 0.8;

    /// <summary>Extra margin added to every side of a detected box, as a fraction of its width or height.</summary>
    [Range(0.0, 1.0)]
    public double BoxPadding { get; set; } = 0.25;

    /// <summary>
    /// Videos: run the detectors on every Nth frame and follow each face or plate in between, so blurs neither
    /// flicker nor drop out. Lower is safer and slower.
    /// </summary>
    [Range(1, 30)]
    public int DetectEveryNthFrame { get; set; } = 5;

    /// <summary>
    /// Videos: a face or plate missed by this many detection passes in a row still counts as the same one if it
    /// reappears, and stays blurred in between.
    /// </summary>
    [Range(0, 30)]
    public int MaxMissedDetections { get; set; } = 3;

    /// <summary>
    /// Also look for plates in zoomed-in tiles of each frame, not just the whole frame. Finds small, distant plates
    /// that the whole-frame pass misses, at about six times the plate detection cost.
    /// </summary>
    public bool PlateTiles { get; set; } = true;

    /// <summary>Remove the soundtrack from every video (voices, music, in-car conversation). On by default at launch.</summary>
    public bool RemoveAudio { get; set; } = true;

    /// <summary>Send every upload to review even when detection was confident. For launch or incident periods.</summary>
    public bool ReviewEverything { get; set; }

    /// <summary>
    /// Gives up blurring one video after this long (then retried, and Failed after the last attempt). The queue keeps
    /// renewing the job's lease meanwhile, so another worker doesn't pick it up.
    /// </summary>
    public TimeSpan VideoTimeout { get; set; } = TimeSpan.FromMinutes(25);

    /// <summary>
    /// Threads ONNX Runtime uses within one detection (intra-op). 0 (the default) means every core this worker has
    /// (<see cref="Environment.ProcessorCount"/>, which respects the container's CPU limit).
    /// </summary>
    [Range(0, 64)]
    public int InferenceThreads { get; set; }

    /// <summary>
    /// Detection runs on a copy of each frame scaled down so its longest side is at most this many pixels (boxes are
    /// mapped back to the full picture before blurring). Much faster for faces; plates are searched in zoomed tiles of
    /// the smaller copy, so their cost barely changes. Faces smaller than about 10 pixels in the copy (20 pixels in a
    /// 720p frame) may be missed. 0 runs detection at full resolution.
    /// </summary>
    [Range(0, 4096)]
    public int DetectionMaxSize { get; set; } = 640;

    /// <summary>
    /// Where the detection models run. <see cref="RedactionExecutionProvider.Cpu"/> (the default) works everywhere.
    /// <see cref="RedactionExecutionProvider.Cuda"/> is for a future GPU worker image built with the
    /// <c>Microsoft.ML.OnnxRuntime.Gpu</c> package; with the standard CPU package the detectors report themselves
    /// unavailable, so media goes to review rather than being published unblurred.
    /// </summary>
    public RedactionExecutionProvider ExecutionProvider { get; set; } = RedactionExecutionProvider.Cpu;

    /// <summary>The GPU to use when <see cref="ExecutionProvider"/> is Cuda.</summary>
    [Range(0, 16)]
    public int CudaDeviceId { get; set; }

    /// <summary>The ONNX Runtime thread count to use: <see cref="InferenceThreads"/>, or every core if 0.</summary>
    public int EffectiveInferenceThreads => InferenceThreads > 0 ? InferenceThreads : Environment.ProcessorCount;
}

/// <summary>Hardware the detection models run on (<c>Media:Redaction:ExecutionProvider</c>).</summary>
public enum RedactionExecutionProvider
{
    /// <summary>ONNX Runtime's CPU provider, in the standard <c>Microsoft.ML.OnnxRuntime</c> package.</summary>
    Cpu,

    /// <summary>
    /// NVIDIA GPUs through ONNX Runtime's CUDA provider. Reserved for a GPU worker image: needs the
    /// <c>Microsoft.ML.OnnxRuntime.Gpu</c> package (same API) and CUDA libraries in the image.
    /// </summary>
    Cuda,
}

/// <summary>
/// Automatic detection of graphic content (blood, injuries, the aftermath of serious crashes). Section
/// <c>Media:SensitiveDetection</c>; only the worker host reads it. When enabled, the media worker scores frames already
/// decoded (and scaled down) for blurring with an image classifier; a photo or video scoring at least
/// <see cref="Threshold"/> is marked sensitive automatically before it's published, so it's never public unblurred,
/// and its post goes to the moderators' sensitive-review queue. If the classifier is missing or fails, the media goes
/// to review instead of being published unchecked.
/// </summary>
public sealed class SensitiveDetectionOptions
{
    public const string SectionName = "Media:SensitiveDetection";

    /// <summary>
    /// Turns detection on. Off by default until a model has been chosen and tested: once on, a missing or broken model
    /// sends every upload to review (fail safe).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// <see cref="SensitiveClassifierProvider.Onnx"/> (a model file, <see cref="ModelPath"/>) or
    /// <see cref="SensitiveClassifierProvider.Development"/> (a stand-in that flags strongly red pictures, for local
    /// testing only; refused outside the Development environment).
    /// </summary>
    public SensitiveClassifierProvider Provider { get; set; } = SensitiveClassifierProvider.Onnx;

    /// <summary>The image classification model (ONNX), e.g. <c>/opt/bjk/models/graphic-content.onnx</c>.</summary>
    public string? ModelPath { get; set; }

    /// <summary>
    /// Score (0 to 1) at or above which media is marked sensitive. Deliberately low, to over-flag: a moderator confirms
    /// or undoes every automatic tag, and a missed one is far worse than an extra one. Tune with the recorded outcomes.
    /// </summary>
    [Range(0.01, 1.0)]
    public double Threshold { get; set; } = 0.35;

    /// <summary>
    /// Score every Nth frame the blurring pass analyses (it analyses every <c>Media:Redaction:DetectEveryNthFrame</c>th
    /// frame, 5 by default): 3 means about two frames a second at 30 fps. Photos are always scored. Lower is safer and slower.
    /// </summary>
    [Range(1, 100)]
    public int SampleEvery { get; set; } = 3;

    /// <summary>The model's input width in pixels (frames are resized to it, ignoring aspect ratio).</summary>
    [Range(16, 2048)]
    public int InputWidth { get; set; } = 224;

    /// <summary>The model's input height in pixels.</summary>
    [Range(16, 2048)]
    public int InputHeight { get; set; } = 224;

    /// <summary>
    /// Per-channel mean (R,G,B), comma-separated, subtracted after pixels are scaled to 0 to 1. ImageNet's by default,
    /// which most classifiers fine-tuned from ImageNet expect.
    /// </summary>
    public string Mean { get; set; } = "0.485,0.456,0.406";

    /// <summary>Per-channel standard deviation (R,G,B), comma-separated, divided by after the mean is subtracted.</summary>
    public string Std { get; set; } = "0.229,0.224,0.225";

    /// <summary>How to read the model's output (its first output, shape [1, classes]).</summary>
    public SensitiveModelOutput Output { get; set; } = SensitiveModelOutput.Softmax;

    /// <summary>
    /// Which output classes mean graphic content, comma-separated indices (e.g. "1" for a [safe, graphic] model). With
    /// Softmax or Probabilities their probabilities are added; with Sigmoid the highest is taken.
    /// </summary>
    public string SensitiveClasses { get; set; } = "1";

    /// <summary>The parsed <see cref="Mean"/>.</summary>
    public float[] MeanValues => ParseFloats(Mean, nameof(Mean));

    /// <summary>The parsed <see cref="Std"/>.</summary>
    public float[] StdValues => ParseFloats(Std, nameof(Std));

    /// <summary>The parsed <see cref="SensitiveClasses"/>.</summary>
    public int[] SensitiveClassIndices =>
        SensitiveClasses.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => int.Parse(v, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

    /// <summary>True if the comma-separated settings parse and make sense.</summary>
    public bool IsValid()
    {
        try
        {
            return MeanValues.Length == 3 && StdValues.Length == 3 && StdValues.All(v => v > 0)
                && SensitiveClassIndices.Length > 0 && SensitiveClassIndices.All(i => i >= 0);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static float[] ParseFloats(string value, string name) =>
        (value ?? throw new FormatException($"{name} is empty."))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => float.Parse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
}

/// <summary>Which graphic content classifier the worker uses (<c>Media:SensitiveDetection:Provider</c>).</summary>
public enum SensitiveClassifierProvider
{
    /// <summary>An ONNX image classification model from <c>Media:SensitiveDetection:ModelPath</c>.</summary>
    Onnx,

    /// <summary>A stand-in for local development that scores the share of strongly red pixels. Development only.</summary>
    Development,
}

/// <summary>How the classifier model's output is turned into a score (<c>Media:SensitiveDetection:Output</c>).</summary>
public enum SensitiveModelOutput
{
    /// <summary>Logits: softmax over all classes, then the sensitive classes' probabilities added.</summary>
    Softmax,

    /// <summary>Independent logits: sigmoid of each sensitive class, the highest taken (multi-label models).</summary>
    Sigmoid,

    /// <summary>Already probabilities (the model ends in softmax): the sensitive classes' values added.</summary>
    Probabilities,
}
