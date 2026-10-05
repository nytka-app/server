namespace Nytka.Storage;

/// <summary>
/// The speech kind of a transcript line (docs/specs/speech-kind.md): <c>person</c> (the wearer included), <c>media</c> (a TV,
/// a video, a podcast, music, a voice assistant) or <c>call</c> (the far side of a call on a loudspeaker). A guess may also be
/// <c>unsure</c>, which every reader treats as <c>person</c>. The SQL here is the one rule every reader follows, over a segment
/// aliased <c>s</c>; readers ask <see cref="IsMedia"/> and <see cref="IsCall"/> of the stored <c>speech_kind</c>.
/// </summary>
public static class SpeechKinds
{
    public const string Person = "person";
    public const string Media = "media";
    public const string Call = "call";

    /// <summary>A guess only: it applies as <see cref="Person"/>.</summary>
    public const string Unsure = "unsure";

    /// <summary><c>speech.mode</c>: <c>off</c> makes no guess, <c>shadow</c> guesses and shows it, <c>on</c> lets the guess decide.</summary>
    public const string Off = "off";

    public const string Shadow = "shadow";

    public const string On = "on";

    /// <summary>A conversation counts as media from this share of its speech time (<c>mediaShare</c>).</summary>
    public const double MediaShareLimit = 0.8;

    /// <summary>The kinds an owner may mark a line with.</summary>
    public static bool IsMark(string? kind) => kind is Person or Media or Call;

    public static bool IsMode(string? mode) => mode is Off or Shadow or On;

    /// <summary>SQL for whether the kind that applies to a segment aliased <c>s</c> is media.</summary>
    public const string IsMedia = "s.speech_kind = 'media'";

    /// <summary>SQL for whether the kind that applies to a segment aliased <c>s</c> is call.</summary>
    public const string IsCall = "s.speech_kind = 'call'";

    /// <summary>SQL for the mode <c>speech_kind</c> and <c>speech_guess</c> follow, as stored.</summary>
    public const string AppliedMode = "(select applied_mode from speech_state where id = 1)";

    /// <summary>
    /// SQL for the guess a stored score gives at <c>@threshold</c> (T), for a segment aliased <c>s</c>: media from T, unsure from
    /// T - 0.15, else person. A call and a segment without a score keep their guess (the wearer's lines have none). Compared as
    /// decimals, so a score of 0.65 at T = 0.8 sits on the line the table says, not beside it by a float's error.
    /// </summary>
    public const string Guess =
        "case when s.speech_score is null or s.speech_guess = 'call' then s.speech_guess "
        + "when cast(s.speech_score as numeric) >= cast(@threshold as numeric) then 'media' "
        + "when cast(s.speech_score as numeric) >= cast(@threshold as numeric) - 0.15 then 'unsure' "
        + "else 'person' end";

    /// <summary>
    /// SQL for the kind that applies to a segment aliased <c>s</c> (docs/specs/speech-kind.md, Which kind wins): the owner's
    /// mark, else with the mode <c>on</c> the guess with <c>unsure</c> read as <c>person</c>, else null. <paramref name="manual"/> is
    /// an SQL expression for the mark and <paramref name="mode"/> one for the mode, so an update can pass the mark it is setting.
    /// </summary>
    public static string Rule(string manual, string mode) =>
        $"coalesce({manual}, case when {mode} = 'on' then "
        + "case s.speech_guess when 'media' then 'media' when 'call' then 'call' when 'person' then 'person' when 'unsure' then 'person' end end)";

    /// <summary>
    /// SQL for a lateral join, aliased <c>m</c>, that puts in <c>m.share</c> the share of the speech time of the conversation
    /// aliased <c>c</c> whose kind is media: 0 for a conversation with no speech.
    /// </summary>
    public const string MediaShareJoin = $"""
        left join lateral (
            select coalesce(
                sum(extract(epoch from s.ended_at - s.started_at)::float8) filter (where {IsMedia})
                / nullif(sum(extract(epoch from s.ended_at - s.started_at)::float8), 0), 0) as share
            from segments s where s.conversation_id = c.id) m on true
        """;

    /// <summary>
    /// SQL for the speaker label of a segment aliased <c>s</c>, after <see cref="SpeakerLabel.Joins"/>, for a reader that tells
    /// the kind: <c>Media</c> for media, the label and <c>(call)</c> for a call, else the label.
    /// </summary>
    public const string Speaker =
        $"case s.speech_kind when 'media' then 'Media' when 'call' then coalesce({SpeakerLabel.Column} || ' (call)', '(call)') else {SpeakerLabel.Column} end";
}
