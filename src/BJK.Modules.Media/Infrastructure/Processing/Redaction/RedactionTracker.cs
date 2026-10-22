namespace BJK.Modules.Media.Infrastructure.Processing.Redaction;

/// <summary>
/// Follows faces and plates through a video. Detection runs on every Nth frame; this links the boxes from one
/// detection pass to the next into tracks (by overlap, or by distance for fast-moving objects), then gives a box for
/// every frame in between by interpolation. A track missed by a few passes keeps its blur across the gap, and each
/// track is extended by one detection interval before its first and after its last detection (following its motion),
/// so blurs don't flicker, drop out, or appear late.
/// </summary>
/// <remarks>
/// Two things keep a moving plate's blur continuous. Detections are matched to where a track is heading (its last
/// box moved on by its speed), not where it was last seen, so a car that moved a long way during a missed pass is
/// still the same track and the gap is interpolated. And a weak detection (below the score needed to start a track,
/// e.g. a plate turning side-on) can still continue an existing track, so the track isn't dropped while the plate is
/// hard to see.
/// </remarks>
public sealed class RedactionTracker
{
    private readonly List<Track> _tracks = [];
    private readonly float _matchIoU;
    private readonly int _maxMissed;
    private readonly Func<RedactionTarget, float>? _startAt;
    private int _lastFrame = -1;

    /// <param name="maxMissed">Detection passes a track may miss in a row and still be continued.</param>
    /// <param name="matchIoU">Minimum overlap for a detection to continue a track.</param>
    /// <param name="startAt">
    /// The score a detection needs to start a new track, per target. Weaker detections only continue tracks; unmatched
    /// ones are dropped. Null: every detection may start a track.
    /// </param>
    public RedactionTracker(int maxMissed, float matchIoU = 0.2f, Func<RedactionTarget, float>? startAt = null)
    {
        _maxMissed = maxMissed;
        _matchIoU = matchIoU;
        _startAt = startAt;
    }

    public IReadOnlyList<Track> Tracks => _tracks;

    /// <summary>Adds one detection pass. Frames must be added in increasing order.</summary>
    /// <param name="frame">Frame number (from 0).</param>
    /// <param name="detections">What the detectors found in it.</param>
    public void Add(int frame, IReadOnlyList<DetectionBox> detections)
    {
        if (frame <= _lastFrame)
        {
            throw new ArgumentException("Frames must be added in increasing order.", nameof(frame));
        }

        _lastFrame = frame;
        var active = _tracks.Where(t => t.Missed <= _maxMissed).ToList();
        var matched = new HashSet<Track>();

        foreach (var detection in detections.OrderByDescending(d => d.Confidence))
        {
            Track? best = null;
            var bestScore = 0f;
            foreach (var track in active)
            {
                if (track.Target != detection.Target || matched.Contains(track))
                {
                    continue;
                }

                var score = MatchScore(track.PredictAt(frame), detection);
                if (score > bestScore)
                {
                    (best, bestScore) = (track, score);
                }
            }

            if (best is null)
            {
                if (_startAt is not null && detection.Confidence < _startAt(detection.Target))
                {
                    // Too weak to be a new face or plate on its own: only good for continuing one.
                    continue;
                }

                best = new Track(detection.Target);
                _tracks.Add(best);
            }

            best.Add(frame, detection);
            matched.Add(best);
        }

        foreach (var track in active.Where(t => !matched.Contains(t)))
        {
            track.Missed++;
        }
    }

    /// <summary>
    /// The boxes to blur in one frame.
    /// </summary>
    /// <param name="frame">Frame number.</param>
    /// <param name="interval">Frames between detection passes; tracks are extended by this much at each end.</param>
    public List<DetectionBox> BoxesAt(int frame, int interval)
    {
        var boxes = new List<DetectionBox>();
        foreach (var track in _tracks)
        {
            if (track.BoxAt(frame, interval) is { } box)
            {
                boxes.Add(box);
            }
        }

        return boxes;
    }

    private float MatchScore(DetectionBox previous, DetectionBox detection)
    {
        var iou = BoxMath.IoU(previous, detection);
        if (iou >= _matchIoU)
        {
            return 1 + iou;
        }

        // Fast movers barely overlap from one pass to the next: accept a similar-sized box close by.
        var size = Math.Max(Math.Max(previous.Width, previous.Height), 1);
        var dx = (previous.X + (previous.Width / 2)) - (detection.X + (detection.Width / 2));
        var dy = (previous.Y + (previous.Height / 2)) - (detection.Y + (detection.Height / 2));
        var distance = MathF.Sqrt((dx * dx) + (dy * dy)) / size;
        var sizeRatio = detection.Area <= 0 || previous.Area <= 0 ? 0 : Math.Min(detection.Area, previous.Area) / Math.Max(detection.Area, previous.Area);
        return distance <= 1.5f && sizeRatio >= 0.25f ? 1 - (distance / 1.5f) : 0;
    }

    /// <summary>One face or plate followed through the video.</summary>
    public sealed class Track(RedactionTarget target)
    {
        private readonly List<(int Frame, DetectionBox Box)> _keys = [];

        public RedactionTarget Target { get; } = target;

        /// <summary>Highest score of any detection in the track.</summary>
        public float MaxConfidence { get; private set; }

        public int FirstFrame => _keys[0].Frame;

        public int LastFrame => _keys[^1].Frame;

        /// <summary>Detections in the track.</summary>
        public int Count => _keys.Count;

        internal DetectionBox Last => _keys[^1].Box;

        /// <summary>Where the track is expected to be at a later frame: its last box moved on by its latest speed.</summary>
        internal DetectionBox PredictAt(int frame) =>
            _keys.Count < 2 || frame <= LastFrame ? Last : Extrapolate(_keys.Count - 1, _keys.Count - 2, frame);

        internal int Missed { get; set; }

        internal void Add(int frame, DetectionBox box)
        {
            _keys.Add((frame, box));
            MaxConfidence = Math.Max(MaxConfidence, box.Confidence);
            Missed = 0;
        }

        /// <summary>The interpolated box at a frame, or null outside the track's (extended) span.</summary>
        /// <param name="frame">Frame number.</param>
        /// <param name="interval">Frames between detection passes.</param>
        public DetectionBox? BoxAt(int frame, int interval)
        {
            if (frame < FirstFrame - interval || frame > LastFrame + interval)
            {
                return null;
            }

            if (frame <= FirstFrame)
            {
                return Extrapolate(0, 1, frame);
            }

            if (frame >= LastFrame)
            {
                return Extrapolate(_keys.Count - 1, _keys.Count - 2, frame);
            }

            // Binary search for the keys either side.
            var low = 0;
            var high = _keys.Count - 1;
            while (high - low > 1)
            {
                var mid = (low + high) / 2;
                if (_keys[mid].Frame <= frame)
                {
                    low = mid;
                }
                else
                {
                    high = mid;
                }
            }

            var (a, b) = (_keys[low], _keys[high]);
            return a.Frame == frame
                ? a.Box
                : BoxMath.Lerp(a.Box, b.Box, (frame - a.Frame) / (float)(b.Frame - a.Frame));
        }

        /// <summary>Moves the end box along the track's motion (position only; the size is kept).</summary>
        private DetectionBox Extrapolate(int end, int neighbour, int frame)
        {
            var box = _keys[end].Box;
            if (neighbour < 0 || neighbour >= _keys.Count || frame == _keys[end].Frame)
            {
                return box;
            }

            var other = _keys[neighbour];
            var t = (frame - _keys[end].Frame) / (float)(_keys[end].Frame - other.Frame);
            return box with
            {
                X = box.X + ((box.X - other.Box.X) * t),
                Y = box.Y + ((box.Y - other.Box.Y) * t),
            };
        }
    }
}
