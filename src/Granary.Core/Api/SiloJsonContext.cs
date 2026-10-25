using System.Text.Json;
using System.Text.Json.Serialization;
using Granary.Core.Api.Accounts;
using Granary.Core.Api.Auth;
using Granary.Core.Api.Catalog;
using Granary.Core.Api.Discovery;
using Granary.Core.Api.Images;
using Granary.Core.Api.Profiles;
using Granary.Core.Api.Settings;

namespace Granary.Core.Api;

/// <summary>
/// Source-generated serializer settings for the Silo v2 wire format: snake_case members, optional
/// members omitted instead of sent as null, unknown members ignored on read.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = false)]
[JsonSerializable(typeof(ApiProblem))]
[JsonSerializable(typeof(EffectiveSettings))]
[JsonSerializable(typeof(SettingValueRequest))]
[JsonSerializable(typeof(SystemInfo))]
[JsonSerializable(typeof(ServerIdentity))]
[JsonSerializable(typeof(ServerConnections))]
[JsonSerializable(typeof(SetupStatus))]
[JsonSerializable(typeof(Branding))]
[JsonSerializable(typeof(LoginRequest))]
[JsonSerializable(typeof(TokenPair))]
[JsonSerializable(typeof(Account))]
[JsonSerializable(typeof(RefreshRequest))]
[JsonSerializable(typeof(RefreshedTokens))]
[JsonSerializable(typeof(AuthProviderCollection))]
[JsonSerializable(typeof(DeviceLoginCapability))]
[JsonSerializable(typeof(StartDeviceLoginRequest))]
[JsonSerializable(typeof(DeviceLoginStart))]
[JsonSerializable(typeof(DeviceCodeRequest))]
[JsonSerializable(typeof(DeviceLoginPoll))]
[JsonSerializable(typeof(DeviceLoginCancel))]
[JsonSerializable(typeof(ProfileCollection))]
[JsonSerializable(typeof(ProfilePinCheck))]
[JsonSerializable(typeof(ProfileVerification))]
[JsonSerializable(typeof(ChangePasswordRequest))]
[JsonSerializable(typeof(AccountPasswordCapability))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SectionCollection))]
[JsonSerializable(typeof(Section))]
[JsonSerializable(typeof(CatalogItem))]
[JsonSerializable(typeof(CatalogItemUserState))]
[JsonSerializable(typeof(CatalogItemOverlay))]
[JsonSerializable(typeof(CatalogItemCollection))]
[JsonSerializable(typeof(PageInfo))]
[JsonSerializable(typeof(HomeDismissalRequest))]
[JsonSerializable(typeof(LibraryCollection))]
[JsonSerializable(typeof(Library))]
[JsonSerializable(typeof(CatalogBrowseCollection))]
[JsonSerializable(typeof(CatalogEffectiveSort))]
[JsonSerializable(typeof(CatalogLetters))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(CatalogFilters))]
[JsonSerializable(typeof(CatalogTechnicalFilters))]
[JsonSerializable(typeof(CatalogItemDetail))]
[JsonSerializable(typeof(CatalogRating))]
[JsonSerializable(typeof(CastCredit))]
[JsonSerializable(typeof(CrewCredit))]
[JsonSerializable(typeof(WatchRollup))]
[JsonSerializable(typeof(FileVersion))]
[JsonSerializable(typeof(ItemVideo))]
[JsonSerializable(typeof(ItemExtra))]
[JsonSerializable(typeof(SeasonCollection))]
[JsonSerializable(typeof(Season))]
[JsonSerializable(typeof(EpisodeCollection))]
[JsonSerializable(typeof(Episode))]
[JsonSerializable(typeof(EpisodeFile))]
[JsonSerializable(typeof(CatalogSearchCapabilities))]
[JsonSerializable(typeof(PersonCollection))]
[JsonSerializable(typeof(RecommendationRowCollection))]
[JsonSerializable(typeof(RecommendationRow))]
[JsonSerializable(typeof(TasteProfile))]
[JsonSerializable(typeof(CalendarWeek))]
[JsonSerializable(typeof(Person))]
[JsonSerializable(typeof(ImageCapabilities))]
[JsonSerializable(typeof(ImageSizeWidths))]
public sealed partial class SiloJsonContext : JsonSerializerContext;
