using Newtonsoft.Json;

namespace Palisades.Models;

public class ContributorModel
{
    [JsonProperty("login")]
    public string Login { get; set; } = string.Empty;

    [JsonProperty("avatar_url")]
    public string AvatarUrl { get; set; } = string.Empty;

    [JsonProperty("contributions")]
    public int Contributions { get; set; }
}
