using GBX.NET;
using GBX.NET.Engines.Game;
using ManiaAPI.TMX;
using ManiaAPI.XmlRpc;
using NostalgicCompletionist;
using Polly;
using Polly.Retry;
using System.Buffers;
using System.Net;

var invalidFileNameCharSearchValues = SearchValues.Create([
    '\"', '<', '>', '|', '\0',
    (char)1, (char)2, (char)3, (char)4, (char)5, (char)6, (char)7, (char)8, (char)9, (char)10,
    (char)11, (char)12, (char)13, (char)14, (char)15, (char)16, (char)17, (char)18, (char)19, (char)20,
    (char)21, (char)22, (char)23, (char)24, (char)25, (char)26, (char)27, (char)28, (char)29, (char)30,
    (char)31, ':', '*', '?', '\\', '/'
]);

var connectionPipeline = new ResiliencePipelineBuilder()
    .AddRetry(new RetryStrategyOptions
    {
        Delay = TimeSpan.FromSeconds(1),
        MaxRetryAttempts = int.MaxValue,
        BackoffType = DelayBackoffType.Linear
    })
    .Build();

GameTitle game;
int count;

while (true)
{
    Console.Write($"Select a game ({string.Join(", ", Enum.GetNames<GameTitle>())}): ");
    var input = Console.ReadLine()?.Trim();

    if (Enum.TryParse(input, ignoreCase: true, out game))
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

long? lastTrackId = null;

for (var i = 0; i <= count / 10; i++)
{
    var tracks = await tmx.SearchTracksAsync(new()
    {
        Count = 100,
        InHasRecord = false,
        PrimaryType = TrackType.Race, // only Race works properly with old LAN server
        ETag = [TrackStyle.Laps], // multilap in TimeAttack doesnt quite work
        After = lastTrackId,
    });

    trackIds.AddRange(tracks.Results
        .Shuffle()
        .Take(10)
        .Select(t => t.TrackId));

    if (!tracks.HasMoreItems)
    {
        break;
    }

    lastTrackId = tracks.Results.Last().TrackId;
}

var fileNames = new List<string>();

Directory.CreateDirectory("Tracks");

foreach (var trackIdBatch in trackIds.Chunk(10))
{
    await foreach (var responseTask in Task.WhenEach(trackIdBatch.Select(x => tmx.GetTrackGbxResponseAsync(x))))
    {
        using var response = await responseTask;

        var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
            ?? response.Content.Headers.ContentDisposition?.FileName ?? (response.RequestMessage?.RequestUri?.Segments.Last() + ".Gbx");

        var validFileName = GetValidFileName(fileName);

        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var fileStream = File.Create(Path.Combine("Tracks", validFileName));

        await stream.CopyToAsync(fileStream);

        fileNames.Add(validFileName);
    }
}

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

var dateId = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");

var tracksDirectory = await client.CallAsync<string>("GetTracksDirectory");
var nostalgicCompletionistDirPath = Path.Combine("_NostalgicCompletionist", dateId);
var nostalgicCompletionistAbsoluteDirPath = Path.Combine(tracksDirectory, nostalgicCompletionistDirPath);
Directory.CreateDirectory(nostalgicCompletionistAbsoluteDirPath);

foreach (var fileName in fileNames)
{
    var localFilePath = Path.Combine("Tracks", fileName);
    var remoteFilePath = Path.Combine(nostalgicCompletionistAbsoluteDirPath, fileName);

    File.Copy(localFilePath, remoteFilePath, overwrite: true);
}

while (true)
{
    var addedTracks = await client.CallAsync<int>("InsertChallengeList", [fileNames.Select(x => Path.Combine(nostalgicCompletionistDirPath, x))]);

    if (addedTracks > 0)
    {
        break;
    }

    Console.WriteLine("Now press the Refresh button in track browser...");
    await Task.Delay(1000);
}

await client.CallAsync("SetGameMode", 1);
await client.CallAsync("SetTimeAttackLimit", 0);
await client.CallAsync("SetChatTime", 0);
await client.CallAsync("StartServerLan");
await client.CallAsync("ChatSend", "NOTE: you might be in Spectator mode!");

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

        if (replay.Xml?.Contains("validable=\"1\"") == true)
        {
            var replayDir = Path.Combine("Replays", dateId);
            Directory.CreateDirectory(replayDir);
            File.Copy(e.FullPath, Path.Combine(replayDir, Path.GetFileName(e.FullPath)), overwrite: true);

            await client.CallAsync("NextChallenge");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex.Message);
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