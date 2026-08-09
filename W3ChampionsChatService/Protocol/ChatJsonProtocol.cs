using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace W3ChampionsChatService.Protocol;

/// <summary>
/// The hub's JSON payload contract, extracted from <c>Startup</c> as a named unit so it can be unit
/// tested directly (see <c>ChatJsonProtocolTests</c>) instead of only through a DI bootstrap.
/// </summary>
public static class ChatJsonProtocol
{
    /// <summary>
    /// Omits null properties from every hub payload.
    /// <para>
    /// <see cref="JsonIgnoreCondition.WhenWritingNull"/> — deliberately NOT
    /// <c>WhenWritingDefault</c>, which would also drop <c>false</c> and <c>0</c>. Those are
    /// meaningful for flair: <c>isClassic: false</c> and <c>race: 0</c> (RnD) are real values the
    /// client renders, and dropping them would silently change avatars.
    /// </para>
    /// <para>
    /// Safe for the launcher: every flair field is already declared optional in
    /// <c>chat-protocol.types.ts</c> and every client read path uses optional chaining with a
    /// fallback, so an absent key and an explicit null are indistinguishable there.
    /// </para>
    /// </summary>
    public static void Configure(JsonHubProtocolOptions options)
    {
        options.PayloadSerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
    }
}
