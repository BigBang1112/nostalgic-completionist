using GBX.NET;
using GBX.NET.Engines.Game;
using ManiaAPI.TMX;
using ManiaAPI.XmlRpc;
using NostalgicCompletionist;
using Polly;
using Polly.Retry;
using System.Buffers;
using System.Net;
using TmEssentials;

const string Reset = "\u001b[0m";
const string Red = "\u001b[31m";
const string Green = "\u001b[32m";
const string Yellow = "\u001b[33m";

Directory.CreateDirectory("Bans");
foreach (var gameBans in Enum.GetValues<GameTitle>())
{
    var bansFilePath = Path.Combine("Bans", $"{gameBans}.txt");

    if (!File.Exists(bansFilePath))
    {
        File.WriteAllText(bansFilePath, string.Empty);
    }
}

GameTitle game;
CompletionMode completionMode;
int count;

while (true)
{
    Console.Write($"Select a game ({string.Join(", ", Enum.GetNames<GameTitle>().Select(name => $"{Yellow}{name}{Reset}"))}): ");
    var input = Console.ReadLine()?.Trim();

    if (Enum.TryParse(input, ignoreCase: true, out game))
    {
        break;
    }

    Console.WriteLine($"'{input}' isn't a recognized value.");
}

while (true)
{
    Console.Write($"Completion mode ({string.Join(", ", Enum.GetNames<CompletionMode>().Select(name => $"{Yellow}{name}{Reset}"))}): ");
    var input = Console.ReadLine()?.Trim();

    if (Enum.TryParse(input, ignoreCase: true, out completionMode))
    {
        break;
    }

    Console.WriteLine($"'{input}' isn't a recognized value.");
}

while (true)
{
    Console.Write("How many tracks? ");
    var input = Console.ReadLine()?.Trim();

    if (int.TryParse(input, out count) && count > 0)
    {
        break;
    }

    Console.WriteLine($"'{input}' isn't a valid number.");
}

var tmx = new TMX(game switch
{
    GameTitle.TMN => TmxSite.Nations,
    GameTitle.TMS => TmxSite.Sunrise,
    GameTitle.TMO => TmxSite.Original,
    _ => throw new NotImplementedException()
});

var trackIds = new List<long>();

var bannedTrackIds = File.ReadAllLines(Path.Combine("Bans", $"{game}.txt"))
    .Select(x =>
    {
        var spaceIndex = x.IndexOf(' ');
        return spaceIndex == -1 ? x : x.Substring(0, spaceIndex);
    })
    .Where(x => !string.IsNullOrWhiteSpace(x))
    .Select(long.Parse)
    .ToHashSet();

long? lastTrackId = null;

for (var i = 0; i <= count / 10; i++)
{
    var tracks = await tmx.SearchTracksAsync(new()
    {
        Count = 100,
        InHasRecord = completionMode == CompletionMode.Finish ? false : null,
        InAuthorTimeBeaten = completionMode == CompletionMode.AuthorMedal ? false : null,
        PrimaryType = TrackType.Race, // only Race works properly with old LAN server
        ETag = [TrackStyle.Laps], // multilap in TimeAttack doesnt quite work
        After = lastTrackId,
        Fields = TrackItemFields.All with { AuthorScore = false }
    });

    trackIds.AddRange(tracks.Results
        .Where(t => !bannedTrackIds.Contains(t.TrackId))
        .Shuffle()
        .Take(Math.Min(10, count - trackIds.Count))
        .Select(t => t.TrackId));

    if (!tracks.HasMoreItems)
    {
        break;
    }

    lastTrackId = tracks.Results.Last().TrackId;
}

var fileNames = new List<string>();

Directory.CreateDirectory("Tracks");

var invalidFileNameCharSearchValues = SearchValues.Create([
    '\"', '<', '>', '|', '\0',
    (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
    (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
    (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
    (char)31, ':', '*', '?', '\\', '/'
]);

foreach (var trackIdBatch in trackIds.Chunk(10))
{
    await foreach (var responseTask in Task.WhenEach(trackIdBatch.Select(x => tmx.GetTrackGbxResponseAsync(x))))
    {
        try
        {
            using var response = await responseTask;

            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName ?? (response.RequestMessage?.RequestUri?.Segments.Last() + ".Gbx");

            var validFileName = GetValidFileName(fileName);

            await using var stream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = File.Create(Path.Combine("Tracks", validFileName));

            await stream.CopyToAsync(fileStream);

            fileStream.Position = 0;

            var map = Gbx.ParseHeaderNode<CGameCtnChallenge>(fileStream);

            if (map.Xml is null || !map.Xml.Contains("nblaps=\"0\""))
            {
                Console.WriteLine($"Skipping track {Red}{TextFormatter.Deformat(map.MapName)}{Reset} because it is a multilap track.");
                continue;
            }

            fileNames.Add(validFileName);

            Console.WriteLine($"Downloaded track {Green}{TextFormatter.Deformat(map.MapName)}{Reset} ({validFileName})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to setup track: {Red}{ex.Message}{Reset}");
        }
    }
}

var connectionPipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        Delay = TimeSpan.FromSeconds(1),
        MaxRetryAttempts = int.MaxValue,
        BackoffType = DelayBackoffType.Linear
    })
    .Build();

await using var client = await connectionPipeline.ExecuteAsync(async token =>
{
    try
    {
        return await XmlRpcClient.ConnectAsync(IPAddress.Loopback, 5000, cancellationToken: token);
    }
    catch
    {
        Console.WriteLine($"Please open {game}...");
        throw;
    }
});

var authResult = await client.CallAsync<bool>("Authenticate", ["SuperAdmin", "SuperAdmin"]);

if (!authResult)
{
    throw new Exception("Failed to authenticate.");
}

Console.WriteLine("Authenticated successfully!");

var dateId = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

var tracksDirectory = await client.CallAsync<string>("GetTracksDirectory");
var nostalgicCompletionistDirPath = Path.Combine("Challenges", "Downloaded", "_NostalgicCompletionist", dateId);
var nostalgicCompletionistAbsoluteDirPath = Path.Combine(tracksDirectory, nostalgicCompletionistDirPath);
Directory.CreateDirectory(nostalgicCompletionistAbsoluteDirPath);

foreach (var fileName in fileNames)
{
    var localFilePath = Path.Combine("Tracks", fileName);
    var remoteFilePath = Path.Combine(nostalgicCompletionistAbsoluteDirPath, fileName);

    File.Copy(localFilePath, remoteFilePath, overwrite: true);
}

Console.WriteLine($"Copied {fileNames.Count} tracks.");

// Clear current challenge list
var challengeList = await client.CallAsync<List<object>>("GetChallengeList", int.MaxValue, 0);
await client.CallAsync("RemoveChallengeList", challengeList.Cast<Dictionary<string, object>>().Select(x => (string)x["FileName"]));

// Add new tracks to challenge list once possible
while (true)
{
    var addedTracks = await client.CallAsync<int>("AddChallengeList", [fileNames.Select(x => Path.Combine(nostalgicCompletionistDirPath, x))]);

    if (addedTracks > 0)
    {
        break;
    }

    Console.WriteLine($"{Yellow}Now press the Refresh button in track browser...{Reset}");
    await Task.Delay(1000);
}

await client.CallAsync("SetGameMode", 1); // TimeAttack
await client.CallAsync("SetTimeAttackLimit", 0); // no time limit
await client.CallAsync("SetChatTime", 0); // immediate skip to next track
await client.CallAsync("StartServerLan");
await client.CallAsync("ChatSend", "NOTE: you might be in Spectator mode!");

Console.WriteLine($"{Green}Ready!{Reset}");

using var fileSystemWatcher = new FileSystemWatcher(tracksDirectory)
{
    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
    IncludeSubdirectories = true,
    EnableRaisingEvents = true,
};

fileSystemWatcher.Created += async (sender, e) =>
{
    try
    {
        var replay = Gbx.ParseHeaderNode<CGameCtnReplayRecord>(e.FullPath);

        var challengeInfo = await client.CallAsync<Dictionary<string, object>>("GetCurrentChallengeInfo");
        var challengeFileName = (string)challengeInfo["FileName"];

        var map = Gbx.ParseHeaderNode<CGameCtnChallenge>(Path.Combine(tracksDirectory, challengeFileName));

        if (replay.MapInfo?.Id != map.MapUid)
        {
            return;
        }

        Console.WriteLine($"Received replay for {Green}{TextFormatter.Deformat(map.MapName)}{Reset} ({Path.GetFileName(e.FullPath)})");

        if (replay.Xml is null || !replay.Xml.Contains("validable=\"1\"/>"))
        {
            Console.WriteLine($"{Red}Replay is not validable, continuing...{Reset}");
            return;
        }

        var replayDir = Path.Combine("Replays", dateId);

        Console.WriteLine($"Replay is validable, copying to {Green}{replayDir}{Reset}..");

        Directory.CreateDirectory(replayDir);
        File.Copy(e.FullPath, Path.Combine(replayDir, Path.GetFileName(e.FullPath)), overwrite: true);

        // the EventsDuration approach is a HACK but it works and is the simplest way to deal with it
        if (completionMode == CompletionMode.AuthorMedal && replay.EventsDuration > map.AuthorTime)
        {
            Console.WriteLine($"{Green}Replay copied, but you didn't beat the author time, continuing...{Reset}");
            return;
        }

        Console.WriteLine($"{Green}Replay copied, next track!{Reset}");
        await client.CallAsync("NextChallenge");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"{Red}{ex.Message}{Reset}");
    }
};

await client.WaitForCloseAsync();

string GetValidFileName(string fileName)
{
    var buffer = ArrayPool<char>.Shared.Rent(fileName.Length);
    var bufferIndex = 0;

    foreach (var c in fileName)
    {
        buffer[bufferIndex++] = invalidFileNameCharSearchValues.Contains(c) ? '_' : c;
    }

    var result = new string(buffer, 0, bufferIndex);
    ArrayPool<char>.Shared.Return(buffer);

    return result;
}