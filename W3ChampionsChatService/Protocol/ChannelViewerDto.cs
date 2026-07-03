namespace W3ChampionsChatService.Protocol;

/// <summary>
/// Active-viewer roster entry for <see cref="FocusChannelResult"/> (Task 1) and later focus/roster
/// tasks (Task 9) that reuse this shape.
/// </summary>
public record ChannelViewerDto(string BattleTag, string Name);
