using TradeFoundry.Publishing;

namespace TradeFoundry.Core;

public sealed record PublishingSettings(string DestinationUrl = "", string Title = "", string Introduction = "");
public sealed record PublishingState(PublishingSettings Settings, string ProtectedToken, PublicationUpload? Draft,
    PublicationStatus? RemoteStatus, DateTimeOffset? LastSuccessUtc);
