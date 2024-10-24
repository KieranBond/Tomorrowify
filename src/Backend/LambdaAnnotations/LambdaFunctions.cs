using Amazon.Lambda.Core;
using Amazon.Lambda.Annotations;
using Amazon.Lambda.Annotations.APIGateway;
using Tomorrowify.Repositories;
using Serilog;
using Microsoft.AspNetCore.Http;
using Tomorrowify.Configuration;
using SpotifyAPI.Web;
using Tomorrowify;
using Amazon.Lambda.CloudWatchEvents.ScheduledEvents;
using static SpotifyAPI.Web.PlaylistRemoveItemsRequest;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Tomorrowify.Dto;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace LambdaAnnotations;

public class LambdaFunctions(IRefreshTokenRepository tokenRepository, TomorrowifyConfiguration configuration, IAmazonCloudWatch amazonCloudWatch)
{
    private ILogger _logger = Log.ForContext<LambdaFunctions>();

    private readonly IAmazonCloudWatch _amazonCloudWatch = amazonCloudWatch;
    private readonly IRefreshTokenRepository _tokenRepository = tokenRepository;
    private readonly TomorrowifyConfiguration _configuration = configuration;

    [LambdaFunction()]
    public async Task<IResult> UpdatePlaylistsForAllUsers(ScheduledEvent _, ILambdaContext context)
    {
        _logger = _logger.ForContext("LambdaContext", context, true);
        var tokenDtos = await _tokenRepository.GetAllTokens();

        await Parallel.ForEachAsync(tokenDtos, async (tokenDto, _) =>
        {
            try
            {
                await UpdatePlaylistsForUser(tokenDto, _configuration);
                _logger.Information("Completed updating playlists for {user}", tokenDto.Key);
            }
            catch(Exception e)
            {
                _logger.Error(e, "Failure to update playlist for {user}", tokenDto.Key);
            }
        });

        return Results.Ok();
    }

    private async Task<IResult> UpdatePlaylistsForUser(RefreshTokenDto refreshTokenDto, TomorrowifyConfiguration configuration)
    {
        var refreshToken = refreshTokenDto.Token;
        _logger.Information("Received {refreshToken} for update playlists request", refreshTokenDto);

        // Use the original refresh token to re-auth and get fresh token
        var response = await new OAuthClient()
            .RequestToken(
                new AuthorizationCodeRefreshRequest(
                    Constants.ClientId,
                    configuration.ClientSecret!,
                    refreshToken));

        var spotify = new SpotifyClient(response.AccessToken);
        var user = await spotify.UserProfile.Current() ?? throw new InvalidUserException(refreshTokenDto.Key);

        _logger.Information("Found {@user} for update playlists request using {refreshToken}", user, refreshToken);

        var userPlaylists = await spotify.PaginateAll(await spotify.Playlists.CurrentUsers());

        var tomorrowPlaylist =
            userPlaylists.FirstOrDefault(p => p.Name == "Tomorrow") ??
            await spotify.Playlists.Create(user.Id, new PlaylistCreateRequest("Tomorrow") { Description = Constants.TomorrowPlaylistDescription });

        var todayPlaylist =
            userPlaylists.FirstOrDefault(p => p.Name == "Today") ??
            await spotify.Playlists.Create(user.Id, new PlaylistCreateRequest("Today") { Description = Constants.TodayPlaylistDescription });

        var tomorrowTracks =
            (await spotify.PaginateAll(await spotify.Playlists.GetItems(tomorrowPlaylist.Id!)))
            .Select(t => t.Track as FullTrack)
            .Where(t => t?.Id != null);

        if (!tomorrowTracks.Any())
            return Results.Ok();

        await PublishMetric("TomorrowTracks", tomorrowTracks.Count(), new[] { ("User", user.Id.ToString()) });

        var firstTracksUris = tomorrowTracks.Take(100).Select(t => t!.Uri).ToList();

        await spotify.Playlists.ReplaceItems(todayPlaylist.Id!,
            new PlaylistReplaceItemsRequest(firstTracksUris)
        );

        await spotify.Playlists.RemoveItems(tomorrowPlaylist.Id!,
                    new PlaylistRemoveItemsRequest()
                    {
                        Tracks = firstTracksUris.Select(uri => new Item() { Uri = uri }).ToList(),
                    }
                );

        if (tomorrowTracks.Count() > 100)
        {
            var remainingTracks = tomorrowTracks.Skip(100);
            var tracksToAdd = remainingTracks.Chunk(100);
            foreach (var chunk in tracksToAdd)
            {
                var trackUris = chunk.Select(t => t!.Uri).ToList();
                await spotify.Playlists.AddItems(todayPlaylist.Id!,
                    new PlaylistAddItemsRequest(trackUris)
                );

                await spotify.Playlists.RemoveItems(tomorrowPlaylist.Id!,
                    new PlaylistRemoveItemsRequest()
                    {
                        Tracks = trackUris.Select(uri => new Item() { Uri = uri }).ToList(),
                    }
                );
            }
        }


        _logger.Information("Completed swap of playlist for {@user}", user);

        return Results.Ok();
    }

    private async Task<PutMetricDataResponse> PublishMetric(string metricName, int metricValue, (string name, string value)[] tags = null)
    {
        var dimensions = new List<Dimension>();
        
        foreach((var name, var value) in tags)
        {
            dimensions.Add(new Dimension() { Name = name, Value = value });
        }

        var metric = await _amazonCloudWatch.PutMetricDataAsync(new PutMetricDataRequest
        {

            MetricData = new List<MetricDatum>
                {
                    new MetricDatum
                    {
                        MetricName = metricName,
                        Value = metricValue,
                        TimestampUtc = DateTime.UtcNow,
                        Dimensions = dimensions,
                    }
                },
            Namespace = "TomorrowifyMetrics"
        });
        return metric;
    }
}