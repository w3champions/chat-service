using System;

namespace W3ChampionsChatService.Mutes;

public class LoungeMuteRequest
{
    public string battleTag { get; set; }
    public string endDate { get; set; }
    public string author { get; set; }
    public string reason { get; set; }
    public bool isShadowBan { get; set; } = false;
}

public class LoungeMute : IIdentifiable
{
    /// <summary>
    /// The Mongo <c>_id</c> — the LOWERCASED battleTag. This is the immutable case-insensitive match key
    /// that also keeps back-compat with the existing all-lowercase prod rows: legacy documents were stored
    /// fully lowercased (both <c>_id</c> and <c>battleTag</c>), so keying <c>_id</c> off the lowercased tag
    /// makes every read/upsert/delete resolve the SAME document regardless of the moderator-entered casing.
    /// <para>
    /// The MongoDB driver automaps the member named <c>Id</c> to <c>_id</c> (the IdMemberMap), so a
    /// query like <c>c =&gt; c.Id == v</c> renders to <c>{ _id: v }</c> and this getter's value is written
    /// as <c>_id</c> on serialize. There is NO setter, so <c>_id</c> is ignored on deserialize — only the
    /// <see cref="battleTag"/> FIELD (which now carries the moderator-entered DISPLAY casing) populates
    /// object state. The <see cref="LoungeMute"/> collection is SHARED WITH PROD and its element set is
    /// unchanged (<c>_id, battleTag, endDate, insertDate, author, reason, isShadowBan</c>); decoupling
    /// <c>_id</c> from the display <c>battleTag</c> is a pure semantics change, not a shape change.
    /// </para>
    /// </summary>
    // ToLowerInvariant (not ToLower): the _id key must be culture-INDEPENDENT so a mute can never be
    // evaded by a Turkish/Azeri dotted/dotless-I fold mismatch between the write and read sides, and so it
    // matches the ToLowerInvariant key convention used everywhere else in the service (MembershipRepository,
    // MentionFanOut, mention inbox). Prod _ids are pure-lowercase ASCII, so this is byte-identical for real
    // battle.net tags and changes no existing key.
    public string Id => battleTag?.ToLowerInvariant();
    public string battleTag { get; set; }
    public DateTime endDate { get; set; }
    public DateTime insertDate { get; set; }
    public string author { get; set; }
    public string reason { get; set; }
    public bool isShadowBan { get; set; } = false;

    /// <summary>
    /// Returns true if this mute is still active at <paramref name="now"/>
    /// (i.e. its end date is in the future). An expired mute is treated as no mute.
    /// </summary>
    public bool IsActive(DateTime now) => DateTime.Compare(endDate, now) > 0;
}
