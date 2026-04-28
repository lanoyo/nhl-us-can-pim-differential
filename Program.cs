namespace NHLPIMSorter;

using System.Text;
using System.Text.Json;

// ------------------------------------------------------------
// CONFIGURATION
// ------------------------------------------------------------

// This should be the same folder where your scraper saved the season JSON files.

internal class Program
{
    public static async Task Main(string[] args)
    {
        const string InputFolder = "/home/lanoyo/Documents/NHL_Stats/API Stats/Input/Post-93";

// This is the CSV file this program will create.
        const string OutputFileName = "playoff_pim_by_team_game.csv";
        const string OutputFolder =  "/home/lanoyo/Documents/NHL_Stats/API Stats/Output/Post-93";

// ------------------------------------------------------------
// MAIN PROGRAM
// ------------------------------------------------------------

        string outputPath = Path.Combine(OutputFolder, OutputFileName);

        List<TeamGamePimRow> rows = new();

        string[] jsonFiles = Directory
            .GetFiles(InputFolder, "*_playoff_pim.json")
            .OrderBy(file => file)
            .ToArray();

        if (jsonFiles.Length == 0)
        {
            Console.WriteLine("No playoff PIM JSON files were found.");
            Console.WriteLine($"Checked folder: {InputFolder}");
            Console.WriteLine("Press Enter to exit.");
            Console.ReadLine();
            return;
        }

        Console.WriteLine($"Found {jsonFiles.Length} JSON files.");
        Console.WriteLine();

        JsonSerializerOptions jsonOptions = new()
        {
            PropertyNameCaseInsensitive = true
        };

        foreach (string filePath in jsonFiles)
        {
            Console.WriteLine($"Reading {Path.GetFileName(filePath)}...");

            string json = await File.ReadAllTextAsync(filePath);

            List<PlayoffPimGame>? games = JsonSerializer.Deserialize<List<PlayoffPimGame>>(
                json,
                jsonOptions
            );

            if (games is null)
            {
                Console.WriteLine($"Could not parse {Path.GetFileName(filePath)}.");
                continue;
            }

            foreach (PlayoffPimGame game in games)
            {
                AddTeamRow(rows, game, isHomeTeam: false);
                AddTeamRow(rows, game, isHomeTeam: true);
            }
        }

        rows = rows
            .OrderBy(row => row.SeasonStartYear)
            .ThenBy(row => row.GameDate)
            .ThenBy(row => row.GameId)
            .ThenBy(row => row.IsHomeTeam)
            .ToList();

        string csv = BuildCsv(rows);

        await File.WriteAllTextAsync(outputPath, csv, Encoding.UTF8);

        Console.WriteLine();
        Console.WriteLine($"Done. Created {rows.Count} team-game rows.");
        Console.WriteLine("Saved CSV to:");
        Console.WriteLine(outputPath);
        Console.WriteLine();
        Console.WriteLine("Press Enter to exit.");
        Console.ReadLine();

// ------------------------------------------------------------
// ROW CREATION
// ------------------------------------------------------------

        static void AddTeamRow(
            List<TeamGamePimRow> rows,
            PlayoffPimGame game,
            bool isHomeTeam)
        {
            TeamPimResult team = isHomeTeam ? game.HomeTeam : game.AwayTeam;
            TeamPimResult opponent = isHomeTeam ? game.AwayTeam : game.HomeTeam;

            rows.Add(new TeamGamePimRow
            {
                Season = game.Season,
                SeasonStartYear = game.SeasonStartYear,
                GameDate = game.GameDate,
                GameId = game.GameId,
                GameState = game.GameState,

                Round = game.Round,
                SeriesMatchup = game.SeriesMatchup,
                SeriesGame = game.SeriesGame,

                TeamAbbrev = team.Abbrev,
                TeamName = team.FullName,
                TeamCountry = team.Country,
                IsHomeTeam = isHomeTeam,

                OpponentAbbrev = opponent.Abbrev,
                OpponentName = opponent.FullName,
                OpponentCountry = opponent.Country,

                TeamPim = team.Pim ?? 0,
                OpponentPim = opponent.Pim ?? 0,
                TotalGamePim = game.TotalPim,

                PimSource = game.PimSource
            });
        }

// ------------------------------------------------------------
// CSV CREATION
// ------------------------------------------------------------

        static string BuildCsv(List<TeamGamePimRow> rows)
        {
            StringBuilder sb = new();

            sb.AppendLine(
                "Season," +
                "SeasonStartYear," +
                "GameDate," +
                "GameId," +
                "GameState," +
                "Round," +
                "SeriesMatchup," +
                "SeriesGame," +
                "TeamAbbrev," +
                "TeamName," +
                "TeamCountry," +
                "IsHomeTeam," +
                "OpponentAbbrev," +
                "OpponentName," +
                "OpponentCountry," +
                "TeamPim," +
                "OpponentPim," +
                "TotalGamePim," +
                "PimSource"
            );

            foreach (TeamGamePimRow row in rows)
            {
                sb.AppendLine(string.Join(",", new[]
                {
                    Csv(row.Season),
                    row.SeasonStartYear.ToString(),
                    Csv(row.GameDate),
                    row.GameId.ToString(),
                    Csv(row.GameState),
                    row.Round.ToString(),
                    row.SeriesMatchup.ToString(),
                    row.SeriesGame.ToString(),
                    Csv(row.TeamAbbrev),
                    Csv(row.TeamName),
                    Csv(row.TeamCountry),
                    row.IsHomeTeam.ToString(),
                    Csv(row.OpponentAbbrev),
                    Csv(row.OpponentName),
                    Csv(row.OpponentCountry),
                    row.TeamPim.ToString(),
                    row.OpponentPim.ToString(),
                    row.TotalGamePim.ToString(),
                    Csv(row.PimSource)
                }));
            }

            return sb.ToString();
        }

        static string Csv(string? value)
        {
            value ??= "";

            bool mustQuote =
                value.Contains(',') ||
                value.Contains('"') ||
                value.Contains('\n') ||
                value.Contains('\r');

            if (!mustQuote)
            {
                return value;
            }

            return $"\"{value.Replace("\"", "\"\"")}\"";
        }
    }
}

// ------------------------------------------------------------
// OUTPUT ROW TYPE
// ------------------------------------------------------------

public class TeamGamePimRow
{
    public string Season { get; set; } = "";
    public int SeasonStartYear { get; set; }
    public string GameDate { get; set; } = "";
    public int GameId { get; set; }
    public string GameState { get; set; } = "";

    public int Round { get; set; }
    public int SeriesMatchup { get; set; }
    public int SeriesGame { get; set; }

    public string TeamAbbrev { get; set; } = "";
    public string TeamName { get; set; } = "";
    public string TeamCountry { get; set; } = "";
    public bool IsHomeTeam { get; set; }

    public string OpponentAbbrev { get; set; } = "";
    public string OpponentName { get; set; } = "";
    public string OpponentCountry { get; set; } = "";

    public int TeamPim { get; set; }
    public int OpponentPim { get; set; }
    public int TotalGamePim { get; set; }

    public string PimSource { get; set; } = "";
}

// ------------------------------------------------------------
// INPUT JSON TYPES
// ------------------------------------------------------------

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