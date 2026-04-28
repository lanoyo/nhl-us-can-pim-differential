namespace NHLFetcher2._0;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

public class Program2
{
    // ------------------------------------------------------------
    // CONFIGURATION
    // ------------------------------------------------------------

    // First season to download.
    // Example: 2021 means the 2021-2022 NHL season.
    private const int StartYear = 1963;

    // Null means "auto-detect the current NHL season start year."
    //
    // Example:
    // If today is during the 2025-2026 NHL season, this will detect 2025.
    //
    // To stop at a specific season manually, use something like:
    // private static readonly int? EndYearOverride = 2023;
    private static readonly int? EndYearOverride = 1977;

    // How many playoff game candidates can be processed at the same time.
    //
    // Keep this small. 2 is a safe starting point.
    // 3 would probably still be reasonable, but I would test 2 first.
    private const int MaxConcurrentGames = 4;

    // Minimum delay between STARTING any two API requests.
    //
    // This is global across the whole program, not per task.
    // So even with MaxConcurrentGames = 2, this prevents requests from starting too rapidly.
    private const int MinDelayBetweenRequestStartsMs = 100;

    // How many times to retry when the API says we are being rate limited.
    private const int MaxRequestAttempts = 3;

    // First retry delay after a 429 response.
    // Later retries multiply this by the attempt number.
    private const int RateLimitRetryBaseDelayMs = 2_000;

    private const string BaseUrl = "https://api-web.nhle.com/v1/";

    private const string OutputFolder = "/home/lanoyo/Documents/NHL_Stats/API Stats/Input";

    // ------------------------------------------------------------
    // MAIN PROGRAM
    // ------------------------------------------------------------

    public static async Task Main()
    {
        int endYear = EndYearOverride ?? GetCurrentSeasonStartYear();

        Directory.CreateDirectory(OutputFolder);

        JsonSerializerOptions outputOptions = new()
        {
            WriteIndented = true
        };

        using HttpClient client = new()
        {
            BaseAddress = new Uri(BaseUrl),
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd("NhlPlayoffPimScraper/1.0");
        client.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json")
        );

        RequestLimiter requestLimiter = new(MinDelayBetweenRequestStartsMs);

        int totalMatchCount = 0;

        Console.WriteLine($"Scanning NHL playoff games from {StartYear}-{StartYear + 1} through {endYear}-{endYear + 1}...");
        Console.WriteLine($"Max concurrent game checks: {MaxConcurrentGames}");
        Console.WriteLine($"Minimum delay between request starts: {MinDelayBetweenRequestStartsMs}ms");
        Console.WriteLine();

        for (int seasonStartYear = StartYear; seasonStartYear <= endYear; seasonStartYear++)
        {
            ConcurrentBag<PlayoffPimGame> seasonResults = new();

            List<PlayoffGameCandidate> candidates =
                GeneratePlayoffGameCandidates(seasonStartYear).ToList();

            Console.WriteLine($"Scanning season {seasonStartYear}-{seasonStartYear + 1}...");

            ParallelOptions parallelOptions = new()
            {
                MaxDegreeOfParallelism = MaxConcurrentGames
            };

            await Parallel.ForEachAsync(
                candidates,
                parallelOptions,
                async (candidate, cancellationToken) =>
                {
                    PlayoffPimGame? game = await TryCreatePlayoffPimGameAsync(
                        client,
                        requestLimiter,
                        candidate,
                        cancellationToken
                    );

                    if (game is null)
                    {
                        return;
                    }

                    seasonResults.Add(game);

                    Console.WriteLine(
                        $"{game.Season} | {game.GameId} | {game.AwayTeam.Abbrev} @ {game.HomeTeam.Abbrev} | " +
                        $"PIM: {game.AwayTeam.Pim}-{game.HomeTeam.Pim}"
                    );
                }
            );

            List<PlayoffPimGame> orderedSeasonResults = seasonResults
                .OrderBy(game => game.GameId)
                .ToList();

            totalMatchCount += orderedSeasonResults.Count;

            string seasonOutputFileName = $"{seasonStartYear}-{seasonStartYear + 1}_playoff_pim.json";
            string outputPath = Path.Combine(OutputFolder, seasonOutputFileName);

            string outputJson = JsonSerializer.Serialize(orderedSeasonResults, outputOptions);
            await File.WriteAllTextAsync(outputPath, outputJson);

            Console.WriteLine();
            Console.WriteLine($"Season {seasonStartYear}-{seasonStartYear + 1}: found {orderedSeasonResults.Count} US/Canada playoff games.");
            Console.WriteLine("Saved to:");
            Console.WriteLine(outputPath);
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine($"Done. Saved {totalMatchCount} total games across all seasons.");
        Console.WriteLine("Press Enter to exit.");
        Console.ReadLine();
    }

    // ------------------------------------------------------------
    // SINGLE GAME PROCESSING
    // ------------------------------------------------------------

    private static async Task<PlayoffPimGame?> TryCreatePlayoffPimGameAsync(
        HttpClient client,
        RequestLimiter requestLimiter,
        PlayoffGameCandidate candidate,
        CancellationToken cancellationToken)
    {
        JsonNode? landingJson = await GetJsonOrNullAsync(
            client,
            requestLimiter,
            $"gamecenter/{candidate.GameId}/landing",
            cancellationToken
        );

        if (landingJson is null)
        {
            return null;
        }

        int? gameType = ReadInt(landingJson["gameType"]);

        // Game type 3 = playoffs.
        if (gameType is not null && gameType != 3)
        {
            return null;
        }

        string gameState = ReadText(landingJson["gameState"]);

        // Future games will not have final PIM data.
        if (gameState.Equals("FUT", StringComparison.OrdinalIgnoreCase) ||
            gameState.Equals("PRE", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        TeamBasic awayTeam = ExtractTeam(landingJson["awayTeam"]);
        TeamBasic homeTeam = ExtractTeam(landingJson["homeTeam"]);

        string awayCountry = GetTeamCountry(awayTeam);
        string homeCountry = GetTeamCountry(homeTeam);

        bool isUsVsCanada =
            (awayCountry == "Canada" && homeCountry == "USA") ||
            (awayCountry == "USA" && homeCountry == "Canada");

        if (!isUsVsCanada)
        {
            return null;
        }

        TeamPim? pim = TryExtractTeamGameStatsPim(landingJson);

        // If the landing endpoint did not expose team PIM clearly,
        // try the boxscore endpoint and sum player PIM as a fallback.
        if (pim is null)
        {
            JsonNode? boxscoreJson = await GetJsonOrNullAsync(
                client,
                requestLimiter,
                $"gamecenter/{candidate.GameId}/boxscore",
                cancellationToken
            );

            pim =
                TryExtractTeamGameStatsPim(boxscoreJson) ??
                TrySumPlayerPimFromBoxscore(boxscoreJson);
        }

        if (pim is null)
        {
            Console.WriteLine($"Found US/Canada game {candidate.GameId}, but could not find PIM.");
            return null;
        }

        return new PlayoffPimGame
        {
            Season = $"{candidate.SeasonStartYear}-{candidate.SeasonStartYear + 1}",
            SeasonStartYear = candidate.SeasonStartYear,
            GameId = candidate.GameId,
            GameDate = ReadText(landingJson["gameDate"]),
            GameState = gameState,

            Round = candidate.Round,
            SeriesMatchup = candidate.Matchup,
            SeriesGame = candidate.GameNumber,

            AwayTeam = new TeamPimResult
            {
                Abbrev = awayTeam.Abbrev,
                PlaceName = awayTeam.PlaceName,
                CommonName = awayTeam.CommonName,
                FullName = awayTeam.FullName,
                Country = awayCountry,
                Pim = pim.AwayPim
            },

            HomeTeam = new TeamPimResult
            {
                Abbrev = homeTeam.Abbrev,
                PlaceName = homeTeam.PlaceName,
                CommonName = homeTeam.CommonName,
                FullName = homeTeam.FullName,
                Country = homeCountry,
                Pim = pim.HomePim
            },

            TotalPim = (pim.AwayPim ?? 0) + (pim.HomePim ?? 0),
            PimSource = pim.Source
        };
    }

    // ------------------------------------------------------------
    // GAME ID GENERATION
    // ------------------------------------------------------------

    private static IEnumerable<PlayoffGameCandidate> GeneratePlayoffGameCandidates(int seasonStartYear)
    {
        // NHL playoff game IDs generally look like:
        //
        // 2023030111
        //
        // 2023 = season start year
        // 03   = playoff game type
        // 0111 = playoff-specific number
        //
        // In the final 4 digits:
        // 0 = padding
        // 1 = round
        // 1 = matchup within that round
        // 1 = game number within that series

        for (int round = 1; round <= 4; round++)
        {
            int matchupsInRound = round switch
            {
                1 => 8,
                2 => 4,
                3 => 2,
                4 => 1,
                _ => 0
            };

            for (int matchup = 1; matchup <= matchupsInRound; matchup++)
            {
                for (int gameNumber = 1; gameNumber <= 7; gameNumber++)
                {
                    int gameId = int.Parse($"{seasonStartYear}030{round}{matchup}{gameNumber}");

                    yield return new PlayoffGameCandidate
                    {
                        GameId = gameId,
                        SeasonStartYear = seasonStartYear,
                        Round = round,
                        Matchup = matchup,
                        GameNumber = gameNumber
                    };
                }
            }
        }
    }

    private static int GetCurrentSeasonStartYear()
    {
        DateTime today = DateTime.Today;

        // NHL seasons usually start in the fall.
        // If it is Jan-Aug, the current season started the previous calendar year.
        return today.Month >= 9 ? today.Year : today.Year - 1;
    }

    // ------------------------------------------------------------
    // HTTP / JSON HELPERS
    // ------------------------------------------------------------

    private static async Task<JsonNode?> GetJsonOrNullAsync(
        HttpClient client,
        RequestLimiter requestLimiter,
        string relativeUrl,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; attempt <= MaxRequestAttempts; attempt++)
        {
            try
            {
                await requestLimiter.WaitForTurnAsync(cancellationToken);

                using HttpResponseMessage response = await client.GetAsync(
                    relativeUrl,
                    cancellationToken
                );

                if (response.StatusCode == HttpStatusCode.NotFound)
                {
                    return null;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    int retryDelayMs = GetRetryDelayMs(response, attempt);

                    Console.WriteLine(
                        $"Rate limited while requesting {relativeUrl}. " +
                        $"Waiting {retryDelayMs}ms before retry {attempt}/{MaxRequestAttempts}."
                    );

                    await Task.Delay(retryDelayMs, cancellationToken);
                    continue;
                }

                string body = await response.Content.ReadAsStringAsync(cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"Request failed: {relativeUrl}");
                    Console.WriteLine($"Status: {(int)response.StatusCode} {response.StatusCode}");
                    return null;
                }

                string trimmed = body.TrimStart();

                if (!trimmed.StartsWith("{") && !trimmed.StartsWith("["))
                {
                    Console.WriteLine($"Non-JSON response from: {relativeUrl}");
                    return null;
                }

                return JsonNode.Parse(body);
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Console.WriteLine($"Request timed out: {relativeUrl}");

                if (attempt < MaxRequestAttempts)
                {
                    int retryDelayMs = RateLimitRetryBaseDelayMs * attempt;
                    await Task.Delay(retryDelayMs, cancellationToken);
                    continue;
                }

                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error requesting {relativeUrl}: {ex.Message}");
                return null;
            }
        }

        Console.WriteLine($"Giving up after {MaxRequestAttempts} attempts: {relativeUrl}");
        return null;
    }

    private static int GetRetryDelayMs(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is TimeSpan retryAfterDelta)
        {
            return Math.Max(1_000, (int)retryAfterDelta.TotalMilliseconds);
        }

        if (response.Headers.RetryAfter?.Date is DateTimeOffset retryAfterDate)
        {
            TimeSpan delay = retryAfterDate - DateTimeOffset.UtcNow;
            return Math.Max(1_000, (int)delay.TotalMilliseconds);
        }

        return RateLimitRetryBaseDelayMs * attempt;
    }

    private static string ReadText(JsonNode? node)
    {
        if (node is null)
        {
            return "";
        }

        if (node is JsonValue)
        {
            return node.ToString();
        }

        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("default", out JsonNode? defaultValue))
            {
                return ReadText(defaultValue);
            }

            if (obj.TryGetPropertyValue("en", out JsonNode? enValue))
            {
                return ReadText(enValue);
            }
        }

        return node.ToJsonString();
    }

    private static int? ReadInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        string text = ReadText(node);

        if (int.TryParse(text, out int result))
        {
            return result;
        }

        return null;
    }

    // ------------------------------------------------------------
    // TEAM EXTRACTION
    // ------------------------------------------------------------

    private static TeamBasic ExtractTeam(JsonNode? teamNode)
    {
        if (teamNode is not JsonObject teamObj)
        {
            return new TeamBasic();
        }

        string abbrev = ReadFirstExistingText(
            teamObj,
            "abbrev",
            "triCode",
            "teamAbbrev"
        );

        string placeName = ReadFirstExistingText(
            teamObj,
            "placeName",
            "locationName"
        );

        string commonName = ReadFirstExistingText(
            teamObj,
            "commonName",
            "teamName",
            "name"
        );

        string fullName = ReadFirstExistingText(
            teamObj,
            "fullName",
            "name"
        );

        if (string.IsNullOrWhiteSpace(fullName))
        {
            fullName = $"{placeName} {commonName}".Trim();
        }

        return new TeamBasic
        {
            Abbrev = abbrev,
            PlaceName = placeName,
            CommonName = commonName,
            FullName = fullName
        };
    }

    private static string ReadFirstExistingText(JsonObject obj, params string[] propertyNames)
    {
        foreach (string propertyName in propertyNames)
        {
            if (obj.TryGetPropertyValue(propertyName, out JsonNode? value))
            {
                string text = ReadText(value);

                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        return "";
    }

    private static string GetTeamCountry(TeamBasic team)
    {
        // Current and common historical Canadian NHL abbreviations.
        // You can add to this list if you run into old historical edge cases.
        HashSet<string> canadianAbbrevs = new(StringComparer.OrdinalIgnoreCase)
        {
            "MTL", // Montreal Canadiens
            "MON", // Older Montreal abbreviation sometimes seen in historical data
            "TOR",
            "VAN",
            "CGY",
            "EDM",
            "OTT",
            "WPG",
            "WIN", // Older Winnipeg abbreviation
            "QUE", // Quebec Nordiques / Bulldogs
            "HAM", // Hamilton Tigers
            "MMR", // Montreal Maroons, depending on data source
            "MWN"  // Montreal Wanderers, depending on data source
        };

        HashSet<string> canadianPlaceNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "Montreal",
            "Montréal",
            "Toronto",
            "Vancouver",
            "Calgary",
            "Edmonton",
            "Ottawa",
            "Winnipeg",
            "Quebec",
            "Québec",
            "Hamilton"
        };

        if (canadianAbbrevs.Contains(team.Abbrev))
        {
            return "Canada";
        }

        if (canadianPlaceNames.Contains(team.PlaceName))
        {
            return "Canada";
        }

        // NHL teams are US or Canada. If the API gave us any recognizable team,
        // and it was not Canadian, classify it as USA.
        if (!string.IsNullOrWhiteSpace(team.Abbrev) ||
            !string.IsNullOrWhiteSpace(team.PlaceName) ||
            !string.IsNullOrWhiteSpace(team.FullName))
        {
            return "USA";
        }

        return "Unknown";
    }

    // ------------------------------------------------------------
    // PIM EXTRACTION
    // ------------------------------------------------------------

    private static TeamPim? TryExtractTeamGameStatsPim(JsonNode? root)
    {
        JsonObject? pimStat = FindStatObjectByCategory(root, "pim");

        if (pimStat is null)
        {
            return null;
        }

        int? awayPim = null;
        int? homePim = null;

        if (pimStat.TryGetPropertyValue("awayValue", out JsonNode? awayValue))
        {
            awayPim = ReadInt(awayValue);
        }

        if (pimStat.TryGetPropertyValue("homeValue", out JsonNode? homeValue))
        {
            homePim = ReadInt(homeValue);
        }

        if (awayPim is null && pimStat.TryGetPropertyValue("away", out JsonNode? away))
        {
            awayPim = ReadInt(away);
        }

        if (homePim is null && pimStat.TryGetPropertyValue("home", out JsonNode? home))
        {
            homePim = ReadInt(home);
        }

        if (awayPim is null && homePim is null)
        {
            return null;
        }

        return new TeamPim
        {
            AwayPim = awayPim,
            HomePim = homePim,
            Source = "teamGameStats"
        };
    }

    private static JsonObject? FindStatObjectByCategory(JsonNode? node, string category)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("category", out JsonNode? categoryNode))
            {
                string foundCategory = ReadText(categoryNode);

                if (foundCategory.Equals(category, StringComparison.OrdinalIgnoreCase))
                {
                    return obj;
                }
            }

            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                JsonObject? found = FindStatObjectByCategory(property.Value, category);

                if (found is not null)
                {
                    return found;
                }
            }
        }

        if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                JsonObject? found = FindStatObjectByCategory(item, category);

                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static TeamPim? TrySumPlayerPimFromBoxscore(JsonNode? root)
    {
        JsonNode? playerByGameStats = FindPropertyRecursive(root, "playerByGameStats");

        if (playerByGameStats is not JsonObject statsObj)
        {
            return null;
        }

        int? awayPim = null;
        int? homePim = null;

        if (statsObj.TryGetPropertyValue("awayTeam", out JsonNode? awayTeamStats))
        {
            awayPim = SumPimFields(awayTeamStats);
        }

        if (statsObj.TryGetPropertyValue("homeTeam", out JsonNode? homeTeamStats))
        {
            homePim = SumPimFields(homeTeamStats);
        }

        if (awayPim is null && homePim is null)
        {
            return null;
        }

        return new TeamPim
        {
            AwayPim = awayPim,
            HomePim = homePim,
            Source = "playerByGameStats sum"
        };
    }

    private static JsonNode? FindPropertyRecursive(JsonNode? node, string propertyName)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue(propertyName, out JsonNode? value))
            {
                return value;
            }

            foreach (KeyValuePair<string, JsonNode?> property in obj)
            {
                JsonNode? found = FindPropertyRecursive(property.Value, propertyName);

                if (found is not null)
                {
                    return found;
                }
            }
        }

        if (node is JsonArray arr)
        {
            foreach (JsonNode? item in arr)
            {
                JsonNode? found = FindPropertyRecursive(item, propertyName);

                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static int? SumPimFields(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        int total = 0;
        bool foundAny = false;

        void Walk(JsonNode? current)
        {
            if (current is null)
            {
                return;
            }

            if (current is JsonObject obj)
            {
                if (obj.TryGetPropertyValue("pim", out JsonNode? pimNode))
                {
                    int? value = ReadInt(pimNode);

                    if (value is not null)
                    {
                        total += value.Value;
                        foundAny = true;
                    }
                }

                foreach (KeyValuePair<string, JsonNode?> property in obj)
                {
                    Walk(property.Value);
                }
            }
            else if (current is JsonArray arr)
            {
                foreach (JsonNode? item in arr)
                {
                    Walk(item);
                }
            }
        }

        Walk(node);

        return foundAny ? total : null;
    }
}

// ------------------------------------------------------------
// REQUEST LIMITER
// ------------------------------------------------------------

public class RequestLimiter
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _minimumDelayBetweenRequests;
    private DateTimeOffset _lastRequestStartTime = DateTimeOffset.MinValue;

    public RequestLimiter(int minimumDelayBetweenRequestStartsMs)
    {
        _minimumDelayBetweenRequests = TimeSpan.FromMilliseconds(minimumDelayBetweenRequestStartsMs);
    }

    public async Task WaitForTurnAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            TimeSpan timeSinceLastRequest = DateTimeOffset.UtcNow - _lastRequestStartTime;
            TimeSpan remainingDelay = _minimumDelayBetweenRequests - timeSinceLastRequest;

            if (remainingDelay > TimeSpan.Zero)
            {
                await Task.Delay(remainingDelay, cancellationToken);
            }

            _lastRequestStartTime = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }
}

// ------------------------------------------------------------
// DATA TYPES
// ------------------------------------------------------------

public class PlayoffGameCandidate
{
    public int GameId { get; set; }
    public int SeasonStartYear { get; set; }
    public int Round { get; set; }
    public int Matchup { get; set; }
    public int GameNumber { get; set; }
}

public class TeamBasic
{
    public string Abbrev { get; set; } = "";
    public string PlaceName { get; set; } = "";
    public string CommonName { get; set; } = "";
    public string FullName { get; set; } = "";
}

public class TeamPim
{
    public int? AwayPim { get; set; }
    public int? HomePim { get; set; }
    public string Source { get; set; } = "";
}

public class PlayoffPimGame
{
    public string Season { get; set; } = "";
    public int SeasonStartYear { get; set; }
    public int GameId { get; set; }
    public string GameDate { get; set; } = "";
    public string GameState { get; set; } = "";

    public int Round { get; set; }
    public int SeriesMatchup { get; set; }
    public int SeriesGame { get; set; }

    public TeamPimResult AwayTeam { get; set; } = new();
    public TeamPimResult HomeTeam { get; set; } = new();

    public int TotalPim { get; set; }
    public string PimSource { get; set; } = "";
}

public class TeamPimResult
{
    public string Abbrev { get; set; } = "";
    public string PlaceName { get; set; } = "";
    public string CommonName { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Country { get; set; } = "";
    public int? Pim { get; set; }
}