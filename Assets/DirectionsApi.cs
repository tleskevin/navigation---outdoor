using Newtonsoft.Json;

// 重新命名：避免與 Unity 內部 GeoLocation 或 Location 相關的衝突
public class ApiGeoLocation
{
    public double lat { get; set; }
    public double lng { get; set; }
}

public class ApiStep
{
    // 使用新的命名
    public ApiGeoLocation end_location { get; set; }
    public string html_instructions { get; set; }
}

public class ApiLeg
{
    public ApiStep[] steps { get; set; }
}

public class ApiRouteData
{
    public ApiLeg[] legs { get; set; }
}

// 主響應類別
public class DirectionsApiData
{
    [JsonProperty("routes")]
    public ApiRouteData[] routes { get; set; }

    [JsonProperty("status")]
    public string status { get; set; }
}