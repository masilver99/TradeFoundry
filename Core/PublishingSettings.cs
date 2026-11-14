using TradeFoundry.Publishing;

namespace TradeFoundry.Core;

/// <param name="IncludeTearSheet">Publish selected tear-sheet indicators (risk, trade dynamics, calendar and R-multiple groups).</param>
public sealed record PublishingSettings(string DestinationUrl = "", string Title = "", string Introduction = "", string GoogleTagScript = "", bool IncludeTearSheet = false);
public sealed record PublishingState(PublishingSettings Settings, string ProtectedToken, PublicationUpload? Draft,
    PublicationStatus? RemoteStatus, DateTimeOffset? LastSuccessUtc);
