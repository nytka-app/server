using System.Text.Json;

namespace OmiPlatform.Omi;

public static class OmiJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
