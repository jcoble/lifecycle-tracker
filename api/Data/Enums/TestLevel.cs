using System.Text.Json.Serialization;

namespace Lifecycle.Data.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TestLevel
{
    Smoke = 1,
    Functional = 2,
    Comprehensive = 3,
    FullE2E = 4
}
