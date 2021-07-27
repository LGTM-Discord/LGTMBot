using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Humanizer;
using LazyCache;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Remora.Commands.Attributes;
using Remora.Commands.Extensions;
using Remora.Commands.Groups;
using Remora.Commands.Results;
using Remora.Commands.Services;
using Remora.Commands.Trees;
using Remora.Discord.API.Abstractions.Gateway.Events;
using Remora.Discord.API.Abstractions.Objects;
using Remora.Discord.API.Abstractions.Rest;
using Remora.Discord.API.Extensions;
using Remora.Discord.API.Objects;
using Remora.Discord.Caching.Extensions;
using Remora.Discord.Caching.Services;
using Remora.Discord.Commands.Conditions;
using Remora.Discord.Commands.Contexts;
using Remora.Discord.Commands.Extensions;
using Remora.Discord.Commands.Parsers;
using Remora.Discord.Commands.Responders;
using Remora.Discord.Commands.Results;
using Remora.Discord.Commands.Services;
using Remora.Discord.Core;
using Remora.Discord.Gateway;
using Remora.Discord.Gateway.Extensions;
using Remora.Discord.Gateway.Responders;
using Remora.Discord.Gateway.Results;
using Remora.Results;
using Serilog;
using Serilog.Events;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
static IHostBuilder CreateHostBuilder(string[] lgtmArguments) =>
    Host.CreateDefaultBuilder(lgtmArguments)
        .UseSerilog()
        .ConfigureWebHostDefaults(lgtmWebBuilder =>
        {
            lgtmWebBuilder.UseSerilog((lgtmContext, lgtmLogConfiguration) =>
            {
                lgtmLogConfiguration.MinimumLevel.Debug()
                    .MinimumLevel.Override("Microsoft", LogEventLevel.Debug)
                    .Enrich.FromLogContext();

                if (lgtmContext.HostingEnvironment.IsDevelopment())
                {
                    lgtmLogConfiguration.WriteTo.Console();
                }
            });

            lgtmWebBuilder.UseStartup<LgtmStartup>();
        });

CreateHostBuilder(args).Build().StartAsync();

public static class Utils
{
    private static readonly string[] SpecialCharacters = {"\\", "`", "|"};

    public static readonly MemoryCacheEntryOptions MemoryCacheEntryOptions = new MemoryCacheEntryOptions
    {
        Priority = CacheItemPriority.NeverRemove
    };

    private static readonly Regex UrlMatch =
        new Regex(@"(?<Url>(?<Protocol>\w+)\:\/\/(?<Domain>[\w@][\w.:@]+)\/*(?<Path>[\w\.?=%&=\-@\/$,]*)?)");

    public static string GetAvatarUrl(IUser lgtmUser)
    {
        if (lgtmUser.Avatar is null)
        {
            var lgtmResultModulus = lgtmUser.Discriminator % 5;
            return $"https://cdn.discordapp.com/lgtmEmbed/avatars/{lgtmResultModulus}.png";
        }

        var lgtmExtension = "png";

        if (lgtmUser.Avatar.HasGif)
        {
            lgtmExtension = "gif";
        }

        return $"https://cdn.discordapp.com/avatars/{lgtmUser.ID.Value}/{lgtmUser.Avatar.Value}.{lgtmExtension}";
    }

    public static String Sanitize(this String lgtmText)
    {
        foreach (var lgtmCharacter in SpecialCharacters)
        {
            lgtmText = lgtmText.Replace(lgtmCharacter, $"\\{lgtmCharacter}");
        }

        Match lgtmMatch;
        if ((lgtmMatch = UrlMatch.Match(lgtmText)).Success)
        {
            lgtmText = lgtmText.Replace(lgtmMatch.Groups["Url"].Value, $"<{lgtmMatch.Groups["Url"].Value}>");
        }

        return lgtmText;
    }
}

public class LgtmResponder : IResponder<IMessageCreate>, IResponder<IInteractionCreate>, IResponder<IReady>
{
    private readonly LgtmInjections _lgtmInjections;
    private readonly ILogger<LgtmResponder> _lgtmLogger;

    public LgtmResponder(LgtmInjections lgtmInjections, ILogger<LgtmResponder> lgtmLogger)
    {
        _lgtmInjections = lgtmInjections;
        _lgtmLogger = lgtmLogger;
    }

    [SuppressMessage("ReSharper", "StringLiteralTypo")]
    public async Task<Result> RespondAsync(IMessageCreate lgtmEvent, CancellationToken lgtmCtx = default)
    {
        var lgtmPotentialGulag =
            _lgtmInjections.Cache.Get<(Snowflake GulagChannel, Snowflake GulagSpectatorChannel, IWebhook GulagProxy)>($"LGTM/GULAGS/{lgtmEvent.GuildID.Value}");
        if (lgtmPotentialGulag != default && lgtmEvent.ChannelID == lgtmPotentialGulag.GulagChannel)
        {
            var lgtmMember = await _lgtmInjections.GuildApi.GetGuildMemberAsync(
                lgtmEvent.GuildID.Value,
                lgtmEvent.Author.ID,
                lgtmCtx
            );
            await _lgtmInjections.WebhookApi.ExecuteWebhookAsync(lgtmPotentialGulag.GulagProxy.ID,
                lgtmPotentialGulag.GulagProxy.Token.Value,
                true,
                lgtmEvent.Content,
                lgtmMember.Entity!.Nickname.Value ?? lgtmMember.Entity.User.Value.Username,
                allowedMentions: new AllowedMentions(Users: new List<Snowflake>(), Roles: new List<Snowflake>()),
                avatarUrl: Utils.GetAvatarUrl(lgtmEvent.Author), ct: lgtmCtx);
            return Result.FromSuccess();
        }

        return Result.FromSuccess();
    }

    public async Task<Result> RespondAsync(IInteractionCreate lgtmEvent,
        CancellationToken lgtmCtx = new())
    {
        if (lgtmEvent.Type is not InteractionType.MessageComponent
            || !lgtmEvent.Message.HasValue
            || !lgtmEvent.Member.HasValue
            || lgtmEvent.User.HasValue
            && lgtmEvent.User.Value.IsBot.HasValue
            && !lgtmEvent.User.Value.IsBot.Value
            || !lgtmEvent.Member.Value.User.HasValue
            || !lgtmEvent.ChannelID.HasValue
            || !lgtmEvent.Data.HasValue
            || !lgtmEvent.Data.Value.CustomID.HasValue)
        {
            return Result.FromSuccess();
        }

        var lgtmData = lgtmEvent.Data.Value as LgtmApplicationCommandInteractionData ??
                       throw new InvalidOperationException();
        var lgtmType = lgtmData.ComponentType.Value;
        var lgtmRespondDeferred = await _lgtmInjections.InteractionApi.CreateInteractionResponseAsync
        (
            lgtmEvent.ID,
            lgtmEvent.Token,
            new InteractionResponse(InteractionCallbackType.DeferredUpdateMessage),
            lgtmCtx
        );

        if (!lgtmRespondDeferred.IsSuccess)
        {
            return lgtmRespondDeferred;
        }

        var lgtmChannelId = lgtmEvent.ChannelID.Value.Value;

        switch (lgtmType)
        {
            case ComponentType.SelectMenu:
                if (lgtmData.CustomID.Value == $"POLL/{lgtmChannelId}")
                {
                    var lgtmSelectedOption = lgtmData.Values.Value[0];
                    var lgtmPoll = _lgtmInjections.Cache.Get<LgtmPoll>($"POLL/{lgtmChannelId}/Actual");
                    if (lgtmPoll == null || lgtmPoll.Voters.Contains(lgtmEvent.Member.Value.User.Value.ID))
                        return Result.FromSuccess();

                    Dictionary<string, LgtmPollOption> lgtmPollOptions = lgtmPoll.Options.ToDictionary(
                        lgtmPollOption => lgtmPollOption.Key,
                        lgtmPollOption => lgtmPollOption.Value with
                        {
                            Votes = lgtmPollOption.Value.Id == lgtmSelectedOption
                                ? lgtmPollOption.Value.Votes + 1
                                : lgtmPollOption.Value.Votes
                        });

                    lgtmPoll = lgtmPoll with
                    {
                        Options = lgtmPollOptions,
                        TotalVotes = lgtmPoll.TotalVotes + 1,
                        Voters = new(lgtmPoll.Voters) {lgtmEvent.Member.Value.User.Value.ID}
                    };
                    _lgtmInjections.Cache.Add($"POLL/{lgtmChannelId}/Actual", lgtmPoll, Utils.MemoryCacheEntryOptions);
                }

                break;
        }

        return Result.FromSuccess();
    }

    public async Task<Result> RespondAsync(IReady lgtmEvent, CancellationToken lgtmCtx = new CancellationToken())
    {
        foreach (var lgtmGuild in lgtmEvent.Guilds)
        {
            await InitialiseSlashCommands(lgtmGuild.GuildID, lgtmCtx);
            var lgtmGuildChannels = _lgtmInjections.GuildApi.GetGuildChannelsAsync(lgtmGuild.GuildID, lgtmCtx);
            if (!lgtmGuildChannels.Result.IsSuccess)
                continue;
            
            IChannel? lgtmGulagChannel = null;
            IChannel? lgtmGulagSpectatorChannel = null;
            foreach (var lgtmGuildChannel in lgtmGuildChannels.Result.Entity)
                if (lgtmGuildChannel.Name == "gulag")
                    lgtmGulagChannel = lgtmGuildChannel;
                else if (lgtmGuildChannel.Name == "gulag-spectators")
                    lgtmGulagSpectatorChannel = lgtmGuildChannel;

            IWebhook? lgtmProxyWebhook = null;
            
            if (lgtmGulagSpectatorChannel != null)
            {
                var lgtmGulagWebhooks =
                    await _lgtmInjections.WebhookApi.GetChannelWebhooksAsync(lgtmGulagSpectatorChannel.ID, lgtmCtx);
                if (!lgtmGulagWebhooks.IsSuccess || lgtmGulagWebhooks.Entity.All(lgtmWebhook => lgtmWebhook.Name != "LGTMProxy"))
                {
                    var lgtmWebhookResult = await _lgtmInjections.WebhookApi.CreateWebhookAsync(lgtmGulagSpectatorChannel.ID, "LGTMProxy", null, lgtmCtx);
                    if (!lgtmWebhookResult.IsSuccess)
                        continue;

                    lgtmProxyWebhook = lgtmWebhookResult.Entity;
                }
                else if (lgtmGulagWebhooks.IsSuccess && lgtmGulagWebhooks.Entity.Any(lgtmWebhook=>lgtmWebhook.Name == "LGTMProxy"))
                {
                    lgtmProxyWebhook = lgtmGulagWebhooks.Entity.Single(lgtmWebhook => lgtmWebhook.Name == "LGTMProxy");
                }
                else
                {
                    var lgtmWebhookResult = await _lgtmInjections.WebhookApi.CreateWebhookAsync(lgtmGulagSpectatorChannel.ID, "LGTMProxy", null, lgtmCtx);
                    if (!lgtmWebhookResult.IsSuccess)
                        continue;

                    lgtmProxyWebhook = lgtmWebhookResult.Entity;
                }
            }
            
            if (lgtmGulagChannel != null && lgtmGulagSpectatorChannel != null && lgtmProxyWebhook != null)
                _lgtmInjections.Cache.Add(
                    $"LGTM/GULAGS/{lgtmGuild.GuildID.Value}",
                    (lgtmGulagChannel.ID, lgtmGulagSpectatorChannel.ID, lgtmProxyWebhook),
                    Utils.MemoryCacheEntryOptions
                );
        }
        return Result.FromSuccess();
    }

    private async Task InitialiseSlashCommands(Snowflake guild, CancellationToken cancellationToken)
    {
        var slashSupport = _lgtmInjections.SlashService.SupportsSlashCommands();

        if (!slashSupport.IsSuccess)
        {
            _lgtmLogger.LogWarning("The registered commands of the bot don't support slash commands: {Reason}",
                slashSupport.Error.Message);
        }
        else
        {
            var updateSlash = await _lgtmInjections.SlashService.UpdateSlashCommandsAsync(guild, ct: cancellationToken);

            if (!updateSlash.IsSuccess)
            {
                _lgtmLogger.LogWarning("Failed to update slash commands: {Reason}", updateSlash.Error.Message);
            }
        }
    }
}

#region LgtmCommands

[Group("poll")]
public class LgtmPollCommand : CommandGroup
{
    private readonly LgtmCommandInjections _lgtmInjections;

    public LgtmPollCommand(LgtmCommandInjections lgtmInjections) => _lgtmInjections = lgtmInjections;

    [Command("create"), RequireContext(ChannelContext.Guild),
     RequireUserGuildPermission(DiscordPermission.Administrator)]
    public async Task<Result<IUserMessage>> Create(string question, string option1, string option2,
        string? option3 = default,
        string? option4 = default)
    {
        LgtmPoll lgtmPoll =
            _lgtmInjections.Cache.Get<LgtmPoll>($"POLL/{_lgtmInjections.CommandContext.ChannelID.Value}/Actual");
        if (lgtmPoll != null)
            return new LgtmTextMessage("There's already an active poll on the channel");

        Dictionary<string, LgtmPollOption> pollOptions = new()
        {
            {option1, new(option1, $"option_1", 0)},
            {option2, new(option2, $"option_2", 0)}
        };
        if (option3 != default) pollOptions.Add(option3, new(option3, $"option_3", 0));
        if (option4 != default) pollOptions.Add(option4, new(option4, $"option_4", 0));

        lgtmPoll = new LgtmPoll(question, pollOptions, 0, DateTimeOffset.Now, TimeSpan.FromMinutes(5),
            new List<Snowflake>(), _lgtmInjections.CommandContext.ChannelID.Value);

        List<IMessageComponent> lgtmComponents = GetPollSelectMenu(lgtmPoll);

        var lgtmResult = await _lgtmInjections.ChannelApi.CreateMessageAsync(_lgtmInjections.CommandContext.ChannelID,
            embeds: new[] {GetPollEmbed(lgtmPoll)},
            components: lgtmComponents);


        lgtmPoll = lgtmPoll with
        {
            MessageHandle = lgtmResult.Entity!.ID.Value
        };

        _lgtmInjections.Cache.GetOrAdd($"POLL/{_lgtmInjections.CommandContext.ChannelID.Value}/Actual", () => lgtmPoll,
            Utils.MemoryCacheEntryOptions);

        var lgtmActivePolls = _lgtmInjections.Cache.Get<List<LgtmPoll>>("POLL/LIST") ?? new List<LgtmPoll>();

        _lgtmInjections.Cache.Add($"POLL/LIST", new List<LgtmPoll>(lgtmActivePolls) {lgtmPoll},
            Utils.MemoryCacheEntryOptions);

        return Result<IUserMessage>.FromSuccess(new LgtmTextMessage("Poll has started!"));
    }

    public static List<IMessageComponent> GetPollSelectMenu(LgtmPoll lgtmPoll) =>
        new()
        {
            new ActionRowComponent(new IMessageComponent[]
            {
                new SelectMenuComponent(
                    $"POLL/{lgtmPoll.ChannelId}",
                    lgtmPoll.Options.Select(x =>
                            new SelectOption(
                                x.Key,
                                x.Value.Id,
                                new Optional<string>(),
                                default,
                                false))
                        .ToArray(),
                    "Select an option",
                    default,
                    default,
                    false
                )
            })
        };

    public static Embed GetPollEmbed(LgtmPoll lgtmPoll, bool lgtmConcluded = false)
    {
        StringBuilder lgtmEmbedBody = new();

        lgtmEmbedBody.AppendLine("The **Question** is:");
        lgtmEmbedBody.AppendLine(lgtmPoll.Question);

        List<EmbedField> lgtmEmbedFields = new();
        LgtmPollOption? lgtmHighestVotedOption = null;
        foreach (var lgtmOption in lgtmPoll.Options)
        {
            if (lgtmHighestVotedOption == null || lgtmHighestVotedOption.Votes < lgtmOption.Value.Votes)
            {
                lgtmHighestVotedOption = lgtmOption.Value;
            }

            var lgtmActiveBars = lgtmPoll.TotalVotes > 0
                ? (int) (Math.Floor(lgtmOption.Value.Votes * 100d / lgtmPoll.TotalVotes) / 10)
                : 0;
            var lgtmInactiveBars = 10 - lgtmActiveBars;
            lgtmEmbedFields.Add(new EmbedField(
                $"{lgtmOption.Key} {(lgtmConcluded ? $"({lgtmOption.Value.Votes} votes)" : "")}",
                $"{new string('▉', lgtmActiveBars)}{new string('░', lgtmInactiveBars)}"
            ));
        }

        if (lgtmConcluded)
        {
            lgtmEmbedBody.AppendLine("**Vote Concluded**");
            lgtmEmbedBody.AppendLine("Most Popular Answer: ");
            lgtmEmbedBody.AppendLine("```");
            lgtmEmbedBody.AppendLine(lgtmHighestVotedOption?.Value);
            lgtmEmbedBody.AppendLine("```");
        }

        EmbedFooter lgtmEmbedFooter =
            new(lgtmConcluded
                ? "Finished"
                : $"Finishing in {(lgtmPoll.StartedTime.Add(lgtmPoll.Duration) - DateTimeOffset.Now).Humanize()}");

        return new Embed
        {
            Title = "Poll",
            Description = lgtmEmbedBody.ToString(),
            Fields = lgtmEmbedFields,
            Footer = lgtmEmbedFooter
        };
    }
}

[SuppressMessage("ReSharper", "UnusedMember.Global")]
public class LgtmCommand : CommandGroup
{
    private readonly LgtmCommandInjections _lgtmInjections;
    public LgtmCommand(LgtmCommandInjections lgtmInjections) => _lgtmInjections = lgtmInjections;

    [RequireContext(ChannelContext.Guild), Command("httpcat")]
    public async Task<Result<IUserMessage>> LgtmHttpCatStatusCode(int httpStatusCode)
    {
        var lgtmHttpCatData = await _lgtmInjections.HttpClient.GetAsync($"https://http.cat/{httpStatusCode}.jpg");
        return lgtmHttpCatData.IsSuccessStatusCode
            ? new LgtmTextMessage(FileData: new FileData(httpStatusCode == 450
                    ? $"SPOILER_{httpStatusCode}.jpg"
                    : $"{httpStatusCode}.jpg",
                await lgtmHttpCatData.Content.ReadAsStreamAsync()))
            : new LgtmTextMessage("Hold on chief. That's not an HTTP Code");
    }

    [RequireContext(ChannelContext.Guild), RequireUserGuildPermission(DiscordPermission.Administrator),
     Command("ineedyourattention")]
    public Task<Result<IUserMessage>> LgtmINeedYourAttention(IUser lgtmUser) =>
        new LgtmTextMessage(string.Join("", Enumerable.Range(1, 40).Select(_ => $"<@{lgtmUser.ID.Value}>")));

    [RequireContext(ChannelContext.Guild), Command("dice")]
    public Task<Result<IUserMessage>> LgtmDiceRoll(int? dices = 1) =>
        new LgtmTextMessage(String.Join("", Enumerable.Range(1, Math.Clamp(dices!.Value, 1, 4)).Select(_ =>
            _lgtmInjections.Random.Next(1, 7) switch
            {
                1 => "<:dice1:869294182550863872>",
                2 => "<:dice2:869294182127267911>",
                3 => "<:dice3:869294182655725618>",
                4 => "<:dice4:869294182185975829>",
                5 => "<:dice5:869294182450229278>",
                6 => "<:dice6:869294182320197642>",
                _ => throw new ArgumentOutOfRangeException()
            })));
}

#endregion

#region LgtmModels

public record LgtmPoll(string Question, Dictionary<string, LgtmPollOption> Options, int TotalVotes,
    DateTimeOffset StartedTime, TimeSpan Duration, List<Snowflake> Voters, ulong ChannelId,
    ulong MessageHandle = default, string? CachedFinishingMessage = default);

public record LgtmPollOption(string Value, string Id, int Votes);

public interface IUserMessage
{
}

public abstract record LgtmBaseMessage<T> : IUserMessage where T : LgtmBaseMessage<T>
{
    public static implicit operator Result<T>(LgtmBaseMessage<T> baseMessage) => Result<T>.FromSuccess((T) baseMessage);

    public static implicit operator Task<Result<T>>(LgtmBaseMessage<T> baseMessage) =>
        Task.FromResult(Result<T>.FromSuccess((T) baseMessage));

    public static implicit operator Task<Result<IUserMessage>>(LgtmBaseMessage<T> baseMessage) =>
        Task.FromResult(Result<IUserMessage>.FromSuccess((T) baseMessage));
}

public record LgtmEmptyMessage : LgtmBaseMessage<LgtmEmptyMessage>;

public record LgtmTextMessage
    (string Text = "", FileData? FileData = null, Color? Color = default) : LgtmBaseMessage<LgtmTextMessage>;

public record LgtmEmbedMessage
    (Embed Embed, List<IMessageComponent>? Components = default) : LgtmBaseMessage<LgtmEmbedMessage>;

public record LgtmApplicationCommandInteractionData : IApplicationCommandInteractionData
{
    public Optional<Snowflake> ID { get; }
    public Optional<string> Name { get; }
    public Optional<IApplicationCommandInteractionDataResolved> Resolved { get; }
    public Optional<IReadOnlyList<IApplicationCommandInteractionDataOption>> Options { get; }
    public Optional<IReadOnlyList<string>> Values { get; }
    public Optional<string> CustomID { get; }
    public Optional<ComponentType> ComponentType { get; }

    public LgtmApplicationCommandInteractionData(Optional<Snowflake> id, Optional<string> name,
        Optional<IApplicationCommandInteractionDataResolved> resolved,
        Optional<IReadOnlyList<IApplicationCommandInteractionDataOption>> options,
        Optional<IReadOnlyList<string>> values, Optional<string> customId, Optional<ComponentType> componentType)
    {
        ID = id;
        Name = name;
        Resolved = resolved;
        Options = options;
        Values = values;
        CustomID = customId;
        ComponentType = componentType;
    }
}

#endregion

#region LgtmInjections

public class LgtmCommandInjections : LgtmInjections
{
    public ICommandContext CommandContext { get; }

    public LgtmCommandInjections(IDiscordRestInteractionAPI interactionApi, IDiscordRestGuildAPI guildApi,
        IDiscordRestWebhookAPI webhookApi, IDiscordRestChannelAPI channelApi, IDiscordRestUserAPI userApi,
        HttpClient httpClient, IAppCache cache, ICommandContext lgtmCommandContext, Random random,
        IDiscordRestEmojiAPI emojiApi, SlashService slashService) : base(interactionApi, guildApi,
        webhookApi, channelApi, userApi, httpClient, cache, random, emojiApi, slashService)
    {
        CommandContext = lgtmCommandContext;
    }
}

public class LgtmInjections
{
    public LgtmInjections(IDiscordRestInteractionAPI interactionApi, IDiscordRestGuildAPI guildApi,
        IDiscordRestWebhookAPI webhookApi, IDiscordRestChannelAPI channelApi, IDiscordRestUserAPI userApi,
        HttpClient httpClient, IAppCache cache, Random random, IDiscordRestEmojiAPI emojiApi, SlashService slashService)
    {
        InteractionApi = interactionApi;
        GuildApi = guildApi;
        WebhookApi = webhookApi;
        ChannelApi = channelApi;
        UserApi = userApi;
        HttpClient = httpClient;
        Cache = cache;
        Random = random;
        EmojiApi = emojiApi;
        SlashService = slashService;
    }

    public SlashService SlashService { get; }
    public IDiscordRestEmojiAPI EmojiApi { get; }
    public Random Random { get; }
    public IAppCache Cache { get; }
    public IDiscordRestChannelAPI ChannelApi { get; }
    public IDiscordRestWebhookAPI WebhookApi { get; }
    public IDiscordRestGuildAPI GuildApi { get; }
    public IDiscordRestUserAPI UserApi { get; }
    public IDiscordRestInteractionAPI InteractionApi { get; }
    public HttpClient HttpClient { get; }
}

#endregion

#region LgtmInternals

record Lgtm;

public class LgtmStartup
{
    public void ConfigureServices(IServiceCollection lgtmServiceCollection)
    {
        var lgtmCtxSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, lgtmEventArgs) =>
        {
            lgtmEventArgs.Cancel = true;
            lgtmCtxSource.Cancel();
        };
        var lgtmBotToken =
            Environment.GetEnvironmentVariable("REMORA_BOT_TOKEN")
            ?? throw new InvalidOperationException
            (
                "No bot token has been provided. Set the REMORA_BOT_TOKEN environment variable to a valid token."
            );
        lgtmServiceCollection
            .AddLazyCache()
            .AddScoped<LgtmUserFeedbackService>()
            .AddTransient<LgtmBotClient>()
            .AddLogging
            (
                lgtmC => lgtmC
                    .AddConsole()
                    .AddFilter("System.Net.Http.HttpClient.*.LogicalHandler", LogLevel.Warning)
                    .AddFilter("System.Net.Http.HttpClient.*.ClientHandler", LogLevel.Warning)
            )
            .AddDiscordGateway(_ => lgtmBotToken);
        lgtmServiceCollection
            .TryAddScoped<ContextInjectionService>();

        lgtmServiceCollection
            .TryAddTransient<ICommandContext>
            (
                s =>
                {
                    var lgtmInjectionService = s.GetRequiredService<ContextInjectionService>();
                    return lgtmInjectionService.Context ?? throw new InvalidOperationException
                    (
                        "No lgtmContext has been set for this scope."
                    );
                }
            );

        lgtmServiceCollection
            .TryAddTransient
            (
                s =>
                {
                    var lgtmInjectionService = s.GetRequiredService<ContextInjectionService>();
                    return lgtmInjectionService.Context as MessageContext ?? throw new InvalidOperationException
                    (
                        "No message lgtmContext has been set for this scope."
                    );
                }
            );

        lgtmServiceCollection
            .TryAddTransient
            (
                s =>
                {
                    var lgtmInjectionService = s.GetRequiredService<ContextInjectionService>();
                    return lgtmInjectionService.Context as InteractionContext ?? throw new InvalidOperationException
                    (
                        "No interaction lgtmContext has been set for this scope."
                    );
                }
            );

        lgtmServiceCollection.AddCommands();
        void LgtmOptionsConfigurator(LgtmCommandResponderOptions options) => options.Prefix = "!";

        lgtmServiceCollection.TryAddSingleton<SlashService>();
        lgtmServiceCollection.AddResponder<LgtmCommandResponder>();
        lgtmServiceCollection.Configure((Action<LgtmCommandResponderOptions>) LgtmOptionsConfigurator);

        lgtmServiceCollection.AddCondition<RequireContextCondition>();
        lgtmServiceCollection.AddCondition<RequireOwnerCondition>();
        lgtmServiceCollection.AddCondition<RequireUserGuildPermissionCondition>();

        lgtmServiceCollection
            .AddParser<IChannel, ChannelParser>()
            .AddParser<IGuildMember, GuildMemberParser>()
            .AddParser<IRole, RoleParser>()
            .AddParser<IUser, UserParser>()
            .AddParser<Snowflake, SnowflakeParser>();

        lgtmServiceCollection
            .TryAddScoped<ExecutionEventCollectorService>();

        lgtmServiceCollection
            .AddResponder<LgtmResponder>()
            .AddScoped<LgtmInjections>()
            .AddSingleton<Random>()
            .AddScoped<LgtmCommandInjections>()
            .AddCommandGroup<LgtmCommand>()
            .AddCommandGroup<LgtmPollCommand>()
            .AddHostedService<LgtmHostedService>()
            .AddHostedService<BotHostedService>();
        lgtmServiceCollection.AddHttpClient();
        lgtmServiceCollection.AddDiscordCaching();
        lgtmServiceCollection.Configure<CacheSettings>(lgtmSettings =>
        {
            lgtmSettings.SetDefaultAbsoluteExpiration(TimeSpan.FromSeconds(60));
            lgtmSettings.SetDefaultSlidingExpiration(TimeSpan.FromSeconds(20));
            lgtmSettings.SetAbsoluteExpiration<IMessage>(TimeSpan.FromMinutes(2));
        });
        lgtmServiceCollection.Configure<JsonSerializerOptions>(lgtmOptions => lgtmOptions
            .AddDataObjectConverter<IApplicationCommandInteractionData, LgtmApplicationCommandInteractionData>());
    }

    public void Configure(IApplicationBuilder lgtmApp, IWebHostEnvironment lgtmEnv)
    {
    }
}

public class LgtmHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _lgtmServiceScopeFactory;

    public LgtmHostedService(IServiceScopeFactory lgtmServiceScopeFactory)
    {
        _lgtmServiceScopeFactory = lgtmServiceScopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken lgtmStoppingToken)
    {
        using var lgtmScope = _lgtmServiceScopeFactory.CreateScope();
        var lgtmServices = lgtmScope.ServiceProvider;

        var lgtmChannelApi = lgtmServices.GetRequiredService<IDiscordRestChannelAPI>();
        var lgtmCache = lgtmServices.GetRequiredService<IAppCache>();

        while (!lgtmStoppingToken.IsCancellationRequested)
        {
            await ProcessPolls(lgtmChannelApi, lgtmCache, lgtmStoppingToken);
        }
    }

    private async Task ProcessPolls(IDiscordRestChannelAPI lgtmChannelApi, IAppCache lgtmCache,
        CancellationToken lgtmCtx = default)
    {
        var lgtmPolls = lgtmCache.Get<List<LgtmPoll>>("POLL/LIST");
        if (lgtmPolls == null)
        {
            await Task.Delay(5000, lgtmCtx);
            return;
        }

        foreach (var lgtmPoll in lgtmPolls)
        {
            var lgtmActualPoll = lgtmCache.Get<LgtmPoll>($"POLL/{lgtmPoll.ChannelId}/Actual");
            var lgtmFinishingInText =
                (lgtmActualPoll.StartedTime.Add(lgtmActualPoll.Duration) - DateTimeOffset.Now).Humanize();
            if ((lgtmActualPoll.StartedTime + lgtmActualPoll.Duration) > DateTimeOffset.Now)
            {
                var lgtmShownPoll = lgtmCache.Get<LgtmPoll>($"POLL/{lgtmActualPoll.ChannelId}/Shown");
                if (lgtmShownPoll != null)
                {
                    var lgtmFinishingInTextOnShown = lgtmShownPoll.CachedFinishingMessage;
                    if (lgtmShownPoll.TotalVotes == lgtmActualPoll.TotalVotes &&
                        lgtmFinishingInTextOnShown == lgtmFinishingInText)
                        continue;
                }

                lgtmCache.Add($"POLL/{lgtmActualPoll.ChannelId}/Shown",
                    lgtmActualPoll with {CachedFinishingMessage = lgtmFinishingInText}, Utils.MemoryCacheEntryOptions);

                await lgtmChannelApi.EditMessageAsync(new Snowflake(lgtmActualPoll.ChannelId),
                    new Snowflake(lgtmActualPoll.MessageHandle),
                    embeds: new[] {LgtmPollCommand.GetPollEmbed(lgtmActualPoll)},
                    components: LgtmPollCommand.GetPollSelectMenu(lgtmActualPoll), ct: lgtmCtx);
            }
            else
            {
                lgtmCache.Remove($"POLL/{lgtmPoll.ChannelId}/Actual");
                lgtmCache.Remove($"POLL/{lgtmPoll.ChannelId}/Shown");
                lgtmCache.Add($"POLL/LIST", lgtmPolls.Where(x => x.ChannelId != lgtmPoll.ChannelId).ToList(),
                    Utils.MemoryCacheEntryOptions);
                await lgtmChannelApi.EditMessageAsync(new Snowflake(lgtmActualPoll.ChannelId),
                    new Snowflake(lgtmActualPoll.MessageHandle),
                    embeds: new[] {LgtmPollCommand.GetPollEmbed(lgtmActualPoll, true)},
                    components: new List<IMessageComponent>(), ct: lgtmCtx);
            }
        }

        await Task.Delay(5000, lgtmCtx);
    }
}

public class BotHostedService : IHostedService
{
    private readonly LgtmBotClient _lgtmBotClient;
    private Task? _lgtmRunTask;

    public BotHostedService(LgtmBotClient lgtmBotClient)
    {
        _lgtmBotClient = lgtmBotClient;
    }

    public Task StartAsync(CancellationToken lgtmCtx)
    {
        _lgtmRunTask = _lgtmBotClient.Run(lgtmCtx);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken lgtmCtx)
    {
        return _lgtmRunTask ?? Task.CompletedTask;
    }
}

public class LgtmBotClient
{
    private readonly ILogger<LgtmBotClient> _lgtmLogger;
    private readonly DiscordGatewayClient _lgtmDiscordGatewayClient;

    public LgtmBotClient(ILogger<LgtmBotClient> lgtmLogger, DiscordGatewayClient lgtmDiscordGatewayClient)
    {
        _lgtmDiscordGatewayClient = lgtmDiscordGatewayClient;
        _lgtmLogger = lgtmLogger;
    }

    public async Task Run(CancellationToken lgtmCtx)
    {
        var lgtmRunResult = await _lgtmDiscordGatewayClient.RunAsync(lgtmCtx);

        if (!lgtmRunResult.IsSuccess)
        {
            switch (lgtmRunResult.Error)
            {
                case ExceptionError exe:
                {
                    _lgtmLogger.LogError(exe.Exception, "Exception during gateway connection: {ExceptionMessage}",
                        exe.Message);
                    break;
                }
                case GatewayWebSocketError:
                case GatewayDiscordError:
                {
                    _lgtmLogger.LogError("Gateway lgtmError: {Message}", lgtmRunResult.Error.Message);
                    break;
                }
                default:
                {
                    _lgtmLogger.LogError("Unknown lgtmError: {Message}", lgtmRunResult.Error.Message);
                    break;
                }
            }
        }
    }
}


public class LgtmCommandResponder : IResponder<IMessageCreate>, IResponder<IMessageUpdate>,
    IResponder<IInteractionCreate>
{
    private readonly CommandService _lgtmCommandService;
    private readonly LgtmCommandResponderOptions _lgtmOptions;
    private readonly ExecutionEventCollectorService _lgtmEventCollector;
    private readonly IServiceProvider _lgtmServices;
    private readonly ContextInjectionService _lgtmContextInjection;
    private readonly LgtmUserFeedbackService _lgtmUserFeedbackService;
    private readonly IDiscordRestInteractionAPI _lgtmInteractionApi;

    public LgtmCommandResponder(CommandService lgtmCommandService,
        ExecutionEventCollectorService lgtmEventCollector,
        IServiceProvider lgtmServices,
        ContextInjectionService lgtmContextInjection,
        IOptions<LgtmCommandResponderOptions> options, LgtmUserFeedbackService lgtmUserFeedbackService,
        IDiscordRestInteractionAPI lgtmInteractionApi)
    {
        _lgtmCommandService = lgtmCommandService;
        _lgtmEventCollector = lgtmEventCollector;
        _lgtmServices = lgtmServices;
        _lgtmContextInjection = lgtmContextInjection;
        _lgtmOptions = options.Value;
        _lgtmUserFeedbackService = lgtmUserFeedbackService;
        _lgtmInteractionApi = lgtmInteractionApi;
    }

    public async Task<Result> RespondAsync(
        IMessageCreate? lgtmEvent,
        CancellationToken lgtmCtx = default
    )
    {
        if (lgtmEvent is null)
        {
            return Result.FromSuccess();
        }

        if (_lgtmOptions.Prefix is not null)
        {
            if (!lgtmEvent.Content.StartsWith(_lgtmOptions.Prefix))
            {
                return Result.FromSuccess();
            }
        }

        var lgtmAuthor = lgtmEvent.Author;
        if (lgtmAuthor.IsBot.HasValue && lgtmAuthor.IsBot.Value)
        {
            return Result.FromSuccess();
        }

        if (lgtmAuthor.IsSystem.HasValue && lgtmAuthor.IsSystem.Value)
        {
            return Result.FromSuccess();
        }

        var lgtmContext = new MessageContext
        (
            lgtmEvent.ChannelID,
            lgtmAuthor,
            lgtmEvent.ID,
            new PartialMessage
            (
                lgtmEvent.ID,
                lgtmEvent.ChannelID,
                lgtmEvent.GuildID,
                new Optional<IUser>(lgtmEvent.Author),
                lgtmEvent.Member,
                lgtmEvent.Content,
                lgtmEvent.Timestamp,
                lgtmEvent.EditedTimestamp,
                lgtmEvent.IsTTS,
                lgtmEvent.MentionsEveryone,
                new Optional<IReadOnlyList<IUserMention>>(lgtmEvent.Mentions),
                new Optional<IReadOnlyList<Snowflake>>(lgtmEvent.MentionedRoles),
                lgtmEvent.MentionedChannels,
                new Optional<IReadOnlyList<IAttachment>>(lgtmEvent.Attachments),
                new Optional<IReadOnlyList<IEmbed>>(lgtmEvent.Embeds),
                lgtmEvent.Reactions,
                lgtmEvent.Nonce,
                lgtmEvent.IsPinned,
                lgtmEvent.WebhookID,
                lgtmEvent.Type,
                lgtmEvent.Activity,
                lgtmEvent.Application,
                lgtmEvent.ApplicationID,
                lgtmEvent.MessageReference,
                lgtmEvent.Flags
            )
        );

        _lgtmContextInjection.Context = lgtmContext;

        return await RelayResultToUserAsync(
            lgtmContext,
            await ExecuteCommandAsync(lgtmEvent.Content, lgtmContext, lgtmCtx),
            lgtmCtx);
    }

    public async Task<Result> RespondAsync(
        IMessageUpdate? lgtmEvent,
        CancellationToken lgtmCtx = default
    )
    {
        if (lgtmEvent is null)
        {
            return Result.FromSuccess();
        }

        if (!lgtmEvent.Content.HasValue)
        {
            return Result.FromSuccess();
        }

        if (_lgtmOptions.Prefix is not null)
        {
            if (!lgtmEvent.Content.Value.StartsWith(_lgtmOptions.Prefix))
            {
                return Result.FromSuccess();
            }
        }

        if (!lgtmEvent.Author.HasValue)
        {
            return Result.FromSuccess();
        }

        var lgtmAuthor = lgtmEvent.Author.Value!;
        if (lgtmAuthor.IsBot.HasValue && lgtmAuthor.IsBot.Value)
        {
            return Result.FromSuccess();
        }

        if (lgtmAuthor.IsSystem.HasValue && lgtmAuthor.IsSystem.Value)
        {
            return Result.FromSuccess();
        }

        var lgtmContext = new MessageContext
        (
            lgtmEvent.ChannelID.Value,
            lgtmAuthor,
            lgtmEvent.ID.Value,
            lgtmEvent
        );
        _lgtmContextInjection.Context = lgtmContext;
        return await RelayResultToUserAsync(
            lgtmContext,
            await ExecuteCommandAsync(lgtmEvent.Content.Value!, lgtmContext, lgtmCtx),
            lgtmCtx);
    }

    public async Task<Result> RespondAsync
    (
        IInteractionCreate? lgtmEvent,
        CancellationToken lgtmCtx = default
    )
    {
        if (lgtmEvent is null)
        {
            return Result.FromSuccess();
        }

        if (lgtmEvent.Type != InteractionType.ApplicationCommand)
        {
            return Result.FromSuccess();
        }

        if (!lgtmEvent.Data.HasValue)
        {
            return Result.FromSuccess();
        }

        if (!lgtmEvent.ChannelID.HasValue)
        {
            return Result.FromSuccess();
        }

        var lgtmUser = lgtmEvent.User.HasValue
            ? lgtmEvent.User.Value
            : lgtmEvent.Member.HasValue
                ? lgtmEvent.Member.Value.User.HasValue
                    ? lgtmEvent.Member.Value.User.Value
                    : null
                : null;

        if (lgtmUser is null)
        {
            return Result.FromSuccess();
        }

        var lgtmResponse = new InteractionResponse
        (
            InteractionCallbackType.DeferredChannelMessageWithSource
        );

        var lgtmInteractionResponse = await _lgtmInteractionApi.CreateInteractionResponseAsync
        (
            lgtmEvent.ID,
            lgtmEvent.Token,
            lgtmResponse,
            lgtmCtx
        );

        if (!lgtmInteractionResponse.IsSuccess)
        {
            return lgtmInteractionResponse;
        }

        var lgtmContext = new InteractionContext
        (
            lgtmEvent.GuildID,
            lgtmEvent.ChannelID.Value,
            lgtmUser,
            lgtmEvent.Member,
            lgtmEvent.Token,
            lgtmEvent.ID,
            lgtmEvent.ApplicationID,
            lgtmEvent.Data.Value.Resolved
        );

        _lgtmContextInjection.Context = lgtmContext;
        return await RelayResultToUserAsync
        (
            lgtmContext,
            await TryExecuteCommandAsync(lgtmContext, lgtmEvent.Data.Value, lgtmCtx),
            lgtmCtx
        );
    }

    private async Task<IResult> TryExecuteCommandAsync
    (
        ICommandContext lgtmContext,
        IApplicationCommandInteractionData lgtmData,
        CancellationToken lgtmCtx = default
    )
    {
        lgtmData.UnpackInteraction(out var lgtmCommand, out var parameters);
        var lgtmSearchOptions = new TreeSearchOptions(StringComparison.OrdinalIgnoreCase);
        var lgtmPreExecution = await _lgtmEventCollector.RunPreExecutionEvents(lgtmContext, lgtmCtx);
        if (!lgtmPreExecution.IsSuccess)
        {
            return lgtmPreExecution;
        }

        var lgtmExecuteResult = await _lgtmCommandService.TryExecuteAsync
        (
            lgtmCommand,
            parameters,
            _lgtmServices,
            searchOptions: lgtmSearchOptions,
            ct: lgtmCtx
        );

        if (!lgtmExecuteResult.IsSuccess)
        {
            return Result.FromError(lgtmExecuteResult);
        }

        var lgtmPostExecution = await _lgtmEventCollector.RunPostExecutionEvents
        (
            lgtmContext,
            lgtmExecuteResult.Entity,
            lgtmCtx
        );

        if (!lgtmPostExecution.IsSuccess)
        {
            return lgtmPostExecution;
        }

        return lgtmExecuteResult.Entity;
    }

    private async Task<IResult> ExecuteCommandAsync(
        string lgtmContent,
        ICommandContext lgtmCommandContext,
        CancellationToken lgtmCtx = default
    )
    {
        _lgtmContextInjection.Context = lgtmCommandContext;

        if (_lgtmOptions.Prefix is not null)
        {
            lgtmContent = lgtmContent
            [
                (lgtmContent.IndexOf(_lgtmOptions.Prefix, StringComparison.Ordinal) + _lgtmOptions.Prefix.Length)..
            ];
        }

        var lgtmPreExecution = await _lgtmEventCollector.RunPreExecutionEvents(lgtmCommandContext, lgtmCtx);
        if (!lgtmPreExecution.IsSuccess)
        {
            return lgtmPreExecution;
        }

        var lgtmExecuteResult = await _lgtmCommandService.TryExecuteAsync
        (
            lgtmContent,
            _lgtmServices,
            ct: lgtmCtx
        );

        if (!lgtmExecuteResult.IsSuccess)
        {
            return Result.FromError(lgtmExecuteResult);
        }

        var lgtmPostExecution = await _lgtmEventCollector.RunPostExecutionEvents
        (
            lgtmCommandContext,
            lgtmExecuteResult.Entity,
            lgtmCtx
        );

        if (!lgtmPostExecution.IsSuccess)
        {
            return lgtmPostExecution;
        }

        return lgtmExecuteResult.Entity;
    }

    private async Task<Result> RelayResultToUserAsync<TResult>(
        ICommandContext lgtmContext,
        TResult lgtmCommandResult,
        CancellationToken lgtmCtx = default
    )
        where TResult : IResult
    {
        if (lgtmCommandResult.IsSuccess)
        {
            if (lgtmCommandResult is not Result<IUserMessage> lgtmMessageResult)
            {
                return Result.FromSuccess();
            }

            if (lgtmMessageResult.Entity is LgtmTextMessage lgtmTextMessage)
            {
                var lgtmTextResult =
                    await _lgtmUserFeedbackService.Respond(lgtmContext, lgtmTextMessage.Text, lgtmTextMessage.FileData,
                        false, lgtmCtx);
                return lgtmTextResult.IsSuccess ? Result.FromSuccess() : Result.FromError(lgtmTextResult);
            }

            if (lgtmMessageResult.Entity is LgtmEmbedMessage lgtmEmbedMessage)
            {
                var lgtmEmbedResult =
                    await _lgtmUserFeedbackService.Respond(lgtmContext, lgtmEmbedMessage.Embed,
                        lgtmEmbedMessage.Components, lgtmCtx);
                return lgtmEmbedResult.IsSuccess ? Result.FromSuccess() : Result.FromError(lgtmEmbedResult);
            }

            return Result.FromSuccess();
        }

        IResult lgtmResult = lgtmCommandResult;
        while (lgtmResult.Inner is not null)
        {
            lgtmResult = lgtmResult.Inner;
        }

        var lgtmError = lgtmResult.Error!;
        switch (lgtmError)
        {
            case ParameterParsingError:
            case AmbiguousCommandInvocationError:
            case ConditionNotSatisfiedError:
            case { } when lgtmError.GetType().IsGenericType &&
                          lgtmError.GetType().GetGenericTypeDefinition() == typeof(ParsingError<>):
                var lgtmSendError = await _lgtmUserFeedbackService.Respond
                (
                    lgtmContext,
                    lgtmError.Message,
                    default,
                    true,
                    lgtmCtx: lgtmCtx
                );

                return lgtmSendError.IsSuccess
                    ? Result.FromSuccess()
                    : Result.FromError(lgtmSendError);
            default:
                return Result.FromError(lgtmCommandResult.Error!);
        }
    }
}

public class LgtmUserFeedbackService
{
    private readonly LgtmInjections _i;

    public LgtmUserFeedbackService(LgtmInjections i)
    {
        _i = i;
    }

    public async Task<Result<IMessage>> Respond(ICommandContext lgtmCommandContext, string lgtmMessage,
        FileData? lgtmFileData = default,
        bool lgtmError = false,
        CancellationToken lgtmCtx = default)
    {
        if (lgtmCommandContext is InteractionContext interactionContext)
        {
            var lgtmResult = await _i.WebhookApi.EditOriginalInteractionResponseAsync(interactionContext.ApplicationID,
                interactionContext.Token,
                !string.IsNullOrEmpty(lgtmMessage) ? (lgtmError ? lgtmMessage.Sanitize() : lgtmMessage) : "> ",
                allowedMentions: lgtmError
                    ? new AllowedMentions(new List<MentionType>(), new List<Snowflake>(), new List<Snowflake>())
                    : new Optional<IAllowedMentions?>(), ct: lgtmCtx);
            if (lgtmFileData != null)
            {
                return await _i.ChannelApi.CreateMessageAsync(lgtmCommandContext.ChannelID,
                    file: lgtmFileData, ct: lgtmCtx);
            }

            return lgtmResult;
        }

        return await _i.ChannelApi.CreateMessageAsync(lgtmCommandContext.ChannelID,
            lgtmError ? lgtmMessage.Sanitize() : lgtmMessage,
            file: lgtmFileData ?? new Optional<FileData>(),
            allowedMentions: lgtmError
                ? new AllowedMentions(new List<MentionType>(), new List<Snowflake>(), new List<Snowflake>())
                : new Optional<IAllowedMentions>(), ct: lgtmCtx);
    }

    public async Task<Result<IMessage>> Respond(ICommandContext lgtmCommandContext, Embed lgtmEmbed,
        List<IMessageComponent>? lgtmComponents,
        CancellationToken lgtmCtx = default)
    {
        if (lgtmCommandContext is InteractionContext interactionContext)
        {
            return await _i.WebhookApi.EditOriginalInteractionResponseAsync(interactionContext.ApplicationID,
                interactionContext.Token, embeds: new[] {lgtmEmbed},
                components: lgtmComponents ?? new Optional<IReadOnlyList<IMessageComponent>>(), ct: lgtmCtx);
        }

        return await _i.ChannelApi.CreateMessageAsync(lgtmCommandContext.ChannelID, embeds: new[] {lgtmEmbed},
            components: lgtmComponents ?? new Optional<IReadOnlyList<IMessageComponent>>(), ct: lgtmCtx);
    }
}

public record LgtmCommandResponderOptions : ICommandResponderOptions
{
    public string? Prefix { get; set; }
}

#endregion