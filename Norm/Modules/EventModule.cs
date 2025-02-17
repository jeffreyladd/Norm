﻿using DSharpPlus;
using DSharpPlus.CommandsNext;
using DSharpPlus.CommandsNext.Attributes;
using DSharpPlus.Entities;
using DSharpPlus.EventArgs;
using DSharpPlus.Interactivity;
using DSharpPlus.Interactivity.Enums;
using DSharpPlus.Interactivity.Extensions;
using HandyHangoutStudios.Parsers;
using Hangfire;
using MediatR;
using NodaTime;
using Norm.Attributes;
using Norm.Database.Entities;
using Norm.Database.Requests;
using Norm.Services;
using Norm.Utilities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Norm.Modules
{
    [Group("event")]
    [Description("The event functionality's submodule.")]
    [BotCategory(BotCategory.EventsAndAnnouncements)]
    [RequireGuild]
    public class EventModule : BaseCommandModule
    {
        private static readonly Random Random = new();

        private readonly IDateTimeZoneProvider timeZoneProvider;

        private readonly IClock clock;

        private readonly IMediator mediator;

        public EventModule(IMediator mediator, IDateTimeZoneProvider timeZoneProvider, IClock clock)
        {
            this.mediator = mediator;
            this.timeZoneProvider = timeZoneProvider;
            this.clock = clock;
        }

        [Command("random")]
        [Description("The bot will randomly choose an event and announce it to the specified role or `@everyone`!")]
        [RequirePermissions(Permissions.MentionEveryone)]
        public async Task RandomEventForRole(CommandContext context,
            [Description("Role to mention")]
            DiscordRole role)
        {
            DiscordMessage msg = await context.RespondAsync(
                $":wave: Hi, {context.User.Mention}! You want to `@everyone` and announce a random event?");
            await msg.CreateReactionAsync(DiscordEmoji.FromName(context.Client, ":regional_indicator_y:"));
            await msg.CreateReactionAsync(DiscordEmoji.FromName(context.Client, ":regional_indicator_n:"));

            InteractivityExtension interactivity = context.Client.GetInteractivity();

            InteractivityResult<MessageReactionAddEventArgs> interactivityResult =
                await interactivity.WaitForReactionAsync(msg, context.User);

            if (interactivityResult.TimedOut ||
                !interactivityResult.Result.Emoji.Equals(
                    DiscordEmoji.FromName(context.Client, ":regional_indicator_y:")))
            {
                await context.RespondAsync($"{context.User.Mention}, well then why did you get my attention! Thanks for wasting my time.");
                return;
            }

            List<GuildEvent> guildEvents = (await this.mediator.Send(new GuildEvents.GetGuildEvents(context.Guild))).Value.ToList();
            GuildEvent selectedEvent = guildEvents[Random.Next(guildEvents.Count)];
            DiscordEmbedBuilder eventEmbedBuilder = new();
            eventEmbedBuilder
                .WithTitle(selectedEvent.EventName)
                .WithDescription(selectedEvent.EventDesc);
            await context.RespondAsync(role.Mention, embed: eventEmbedBuilder.Build());
        }

        [Command("random")]
        public async Task RandomEvent(CommandContext context)
        {
            await this.RandomEventForRole(context, context.Guild.EveryoneRole);
        }

        [Command("cschedule")]
        [Description("Create and schedule an event for the time given announced to the role given or the `@everyone` role if no role is specified.")]
        [RequirePermissions(Permissions.MentionEveryone)]
        [BotCategory(BotCategory.Scheduling)]
        public async Task CreateAndScheduleGuildEvent(
            CommandContext context,
            [Description("The channel to announce the event in")]
            DiscordChannel announcementChannel,
            [Description("The role to announce the event to")]
            DiscordRole role,
            [Description("The date to schedule the event for")]
            [RemainingText]
            string datetimeString
        )
        {
            (bool success, DateTimeZone schedulerTimeZone) = await context.User.TryGetDateTimeZoneAsync(this.mediator, this.timeZoneProvider);

            if (!success)
            {
                await context.RespondAsync(NoTimeZoneErrorMessage);
                return;
            }

            if (!announcementChannel.PermissionsFor(context.Guild.CurrentMember).HasPermission(Permissions.SendMessages | Permissions.MentionEveryone))
            {
                await context.RespondAsync($"{context.Member.Mention}, I don't have permission to send messages and mention `@everyone` in that channel.");
                return;
            }

            ZonedDateTime zonedMessageDateTime = ZonedDateTime.FromDateTimeOffset(context.Message.CreationTimestamp);
            DateTime senderRefTime = zonedMessageDateTime.WithZone(schedulerTimeZone).ToDateTimeOffset().DateTime;

            LocalDateTime datetime = Recognizers.RecognizeDateTime(datetimeString, senderRefTime, DateTimeV2Type.DateTime)
                .First()
                .Values
                .Select(value => (LocalDateTime)value.Value)
                .Where(value => value >= LocalDateTime.FromDateTime(senderRefTime))
                .OrderBy(key => key)
                .First();
            DiscordMessage msg = await context.RespondAsync($":wave: Hi, {context.User.Mention}! You want to schedule an event for {datetime:g} in your timezone?");

            InteractivityExtension interactivity = context.Client.GetInteractivity();
            Reaction reaction = await interactivity.AddAndWaitForYesNoReaction(msg, context.User);
            if (reaction != Reaction.Yes)
            {
                return;
            }

            CustomResult<DiscordMessage> addResult = await this.AddGuildEventInteractive(context, interactivity);

            if (addResult.TimedOut || addResult.Cancelled)
            {
                return;
            }

            GuildEvent selectedEvent = (await this.mediator.Send(new GuildEvents.GetGuildEvents(context.Guild))).Value.OrderByDescending(e => e.Id).First();

            Instant eventDateTime = datetime.InZoneStrictly(schedulerTimeZone).ToInstant();
            DiscordEmbed embed = new DiscordEmbedBuilder()
                .WithAuthor(context.Member.DisplayName, iconUrl: context.Member.AvatarUrl)
                .WithDescription(selectedEvent.EventDesc)
                .WithTitle(selectedEvent.EventName)
                .Build();

            await Task.Delay(2000);
            await addResult.Result.ModifyAsync($"You have scheduled the following event for {datetime:g} in your time zone to be output in the {announcementChannel.Mention} channel.", embed: embed);
            await this.ScheduleEventsForRoleAsync(context, announcementChannel, selectedEvent, eventDateTime, role);
        }

        [Command("cschedule")]
        public async Task CreateAndScheduleGuildEventNoRole(
            CommandContext context,
            [Description("The channel to announce the event in")]
            DiscordChannel announcementChannel,
            [Description("The date to schedule the event for")]
            [RemainingText]
            string datetimeString
        )
        {
            await this.CreateAndScheduleGuildEvent(context, announcementChannel, null, datetimeString);
        }

        [Command("schedule")]
        [Description("Schedule an event from the list of events defined for this guild that will be announced to the role given.")]
        [RequirePermissions(Permissions.MentionEveryone)]
        [BotCategory(BotCategory.Scheduling)]
        public async Task ScheduleGuildEvent(
            CommandContext context,
            [Description("The channel to announce the event in")]
            DiscordChannel announcementChannel,
            [Description("The role to announce the event to")]
            DiscordRole role,
            [Description("The date to schedule the event for")]
            [RemainingText]
            string datetimeString
        )
        {
            (bool success, DateTimeZone schedulerTimeZone) = await context.User.TryGetDateTimeZoneAsync(this.mediator, this.timeZoneProvider);

            if (!success)
            {
                await context.RespondAsync(NoTimeZoneErrorMessage);
                return;
            }

            if (!announcementChannel.PermissionsFor(context.Guild.CurrentMember).HasPermission(Permissions.SendMessages | Permissions.MentionEveryone))
            {
                await context.RespondAsync($"{context.Member.Mention}, I don't have permission to send messages and mention `@everyone` in that channel.");
                return;
            }

            ZonedDateTime zonedMessageDateTime = ZonedDateTime.FromDateTimeOffset(context.Message.CreationTimestamp);
            DateTime senderRefTime = zonedMessageDateTime.WithZone(schedulerTimeZone).ToDateTimeOffset().DateTime;

            LocalDateTime datetime = Recognizers.RecognizeDateTime(datetimeString, senderRefTime, DateTimeV2Type.DateTime)
                .First()
                .Values
                .Select(value => (LocalDateTime)value.Value)
                .Where(value => value >= LocalDateTime.FromDateTime(senderRefTime))
                .OrderBy(key => key)
                .First();
            DiscordMessage msg = await context.RespondAsync($":wave: Hi, {context.User.Mention}! You want to schedule an event for {datetime:g} in your timezone?");

            InteractivityExtension interactivity = context.Client.GetInteractivity();
            Reaction reaction = await interactivity.AddAndWaitForYesNoReaction(msg, context.User);
            if (reaction != Reaction.Yes)
            {
                return;
            }

            DiscordEmbedBuilder scheduleEmbedBase = new DiscordEmbedBuilder()
                .WithTitle("Select an event by typing: <event number>")
                .WithColor(context.Member.Color);

            msg = await context.RespondAsync("Loading...");
            await context.TriggerTypingAsync();
            await Task.Delay(1000);
            GuildEvent selectedEvent = await this.SelectPredefinedEvent(context, msg, interactivity, scheduleEmbedBase);

            Instant eventDateTime = datetime.InZoneStrictly(schedulerTimeZone).ToInstant();
            DiscordEmbed embed = new DiscordEmbedBuilder()
                .WithAuthor(context.Member.DisplayName, iconUrl: context.Member.AvatarUrl)
                .WithDescription(selectedEvent.EventDesc)
                .WithTitle(selectedEvent.EventName)
                .Build();
            await context.RespondAsync($"You have scheduled the following event for {datetime:g} in your time zone to be output in the {announcementChannel.Mention} channel.", embed: embed);
            await this.ScheduleEventsForRoleAsync(context, announcementChannel, selectedEvent, eventDateTime, role);
        }

        [Command("schedule")]
        public async Task ScheduleGuildEventNoRole(
            CommandContext context,
            [Description("The channel to announce the event in")]
            DiscordChannel announcementChannel,
            [Description("The date to schedule the event for")]
            [RemainingText]
            string datetimeString
        )
        {
            await this.ScheduleGuildEvent(context, announcementChannel, null, datetimeString);
        }

        private async Task ScheduleEventsForRoleAsync(CommandContext context, DiscordChannel announcementChannel, GuildEvent selectedEvent, Instant eventDateTime, DiscordRole role)
        {
            Duration eventScheduleDuration = eventDateTime - this.clock.GetCurrentInstant();
            string scheduledJobId = BackgroundJob.Schedule<EventService>(
                eventService =>
                    eventService.SendEmbedWithMessageToChannelAsUser(
                            context.Guild.Id,
                            context.Member.Id,
                            announcementChannel.Id,
                            $"{(role == null ? "@everyone" : role.Mention)}, this event is starting now!",
                            selectedEvent.EventName,
                            selectedEvent.EventDesc),
                eventScheduleDuration.ToTimeSpan());

            await this.mediator.Send(new GuildBackgroundJobs.Add(scheduledJobId, context.Guild.Id, $"{selectedEvent.EventName} - Announcement", eventDateTime, GuildJobType.SCHEDULED_EVENT));

            scheduledJobId = BackgroundJob.Schedule<EventService>(
                eventService
                    => eventService.SendEmbedWithMessageToChannelAsUser(
                            context.Guild.Id,
                            context.Member.Id,
                            announcementChannel.Id,
                            $"{(role == null ? "@everyone" : role.Mention)}, this event is starting in 10 minutes!",
                            selectedEvent.EventName,
                            selectedEvent.EventDesc
                        ),
                (eventScheduleDuration - Duration.FromMinutes(10)).ToTimeSpan());

            await this.mediator.Send(new GuildBackgroundJobs.Add(scheduledJobId, context.Guild.Id, $"{selectedEvent.EventName} - 10 Min Announcement", eventDateTime - Duration.FromMinutes(10), GuildJobType.SCHEDULED_EVENT));
        }

        private async Task<GuildEvent> SelectPredefinedEvent(CommandContext context, DiscordMessage msg, InteractivityExtension interactivity, DiscordEmbedBuilder scheduleEmbedBase)
        {

            List<GuildEvent> guildEvents = (await this.mediator.Send(new GuildEvents.GetGuildEvents(context.Guild))).Value.ToList();
            IEnumerable<Page> pages = GetGuildEventsPages(guildEvents, interactivity, scheduleEmbedBase);
            CustomResult<int> result = await context.WaitForMessageAndPaginateOnMsg(pages,
                PaginationMessageFunction.CreateWaitForMessageWithIntInRange(context.User, context.Channel, 1, guildEvents.Count + 1),
                msg: msg
            );
            if (result.TimedOut || result.Cancelled)
            {
                await context.RespondAsync("You never gave me a valid input. Thanks for wasting my time. :triumph:");
                return null;
            }

            return guildEvents[result.Result - 1];
        }

        [Command("unschedule")]
        [Description("Start the interactive unscheduling prompt.")]
        [BotCategory(BotCategory.Scheduling)]
        [RequirePermissions(Permissions.MentionEveryone)]
        public async Task UnscheduleGuildEvent(CommandContext context)
        {
            InteractivityExtension interactivity = context.Client.GetInteractivity();

            DiscordMessage msg = await context.RespondAsync(
                $":wave: Hi, {context.User.Mention}! You want to unschedule an event for your guild?");
            Reaction reaction = await interactivity.AddAndWaitForYesNoReaction(msg, context.User);

            if (reaction != Reaction.Yes)
            {
                await context.RespondAsync("Ok, I'll just go back to doing nothing then.");
                return;
            }


            DateTimeZone memberTimeZone = this.timeZoneProvider[(await this.mediator.Send(new UserTimeZones.GetUsersTimeZone(context.User))).Value.TimeZoneId];

            List<GuildBackgroundJob> guildEventJobs = (await this.mediator.Send(new GuildBackgroundJobs.GetGuildJobs(context.Guild)))
                .Value
                .Where(x => x.GuildJobType == GuildJobType.SCHEDULED_EVENT)
                .OrderBy(x => x.ScheduledTime)
                .ToList();

            DiscordEmbedBuilder removeEventEmbed = new DiscordEmbedBuilder()
                .WithTitle("Select an event to unschedule by typing: <event number>")
                .WithColor(context.Member.Color);

            CustomResult<int> result = await context.WaitForMessageAndPaginateOnMsg(
                GetScheduledEventsPages(guildEventJobs, memberTimeZone, interactivity, removeEventEmbed),
                PaginationMessageFunction.CreateWaitForMessageWithIntInRange(context.User, context.Channel, 1, guildEventJobs.Count + 1));

            if (result.TimedOut || result.Cancelled)
            {
                await context.RespondAsync("You never gave me a valid input. Thanks for wasting my time. :triumph:");
                return;
            }

            GuildBackgroundJob job = guildEventJobs[result.Result - 1];

            msg = await context.RespondAsync($"{context.User.Mention}, are you sure you want to unschedule this event?", embed: null);
            reaction = await interactivity.AddAndWaitForYesNoReaction(msg, context.User);

            if (reaction != Reaction.Yes)
            {
                await context.RespondAsync("Ok, I'll just go back to doing nothing then.");
                return;
            }

            BackgroundJob.Delete(job.HangfireJobId);
            await this.mediator.Send(new GuildBackgroundJobs.Delete(job));
            await msg.ModifyAsync("Ok, I've unscheduled that event!", embed: null);
        }

        [Command("add")]
        [Description("Starts the set-up process for a new event to be added to the guild events for this server.")]
        [RequireUserPermissions(Permissions.ManageGuild)]
        public async Task AddGuildEvent(CommandContext context)
        {
            DiscordMessage msg = await context.RespondAsync($":wave: Hi, {context.User.Mention}! You wanted to create a new event?");
            InteractivityExtension interactivity = context.Client.GetInteractivity();
            Reaction result = await interactivity.AddAndWaitForYesNoReaction(msg, context.User);

            if (result != Reaction.Yes)
            {
                await context.RespondAsync("Well, thanks for wasting my time. Have a good day.");
                return;
            }

            await this.AddGuildEventInteractive(context, interactivity, msg);
        }

        private async Task<CustomResult<DiscordMessage>> AddGuildEventInteractive(CommandContext context, InteractivityExtension interactivity, DiscordMessage msg = null)
        {

            if (msg == null)
            {
                await context.RespondAsync(content: "Ok, what do you want the event name to be?");
            }
            else
            {
                await msg.ModifyAsync(content: "Ok, what do you want the event name to be?");
            }

            InteractivityResult<DiscordMessage> result = await interactivity.WaitForMessageAsync(xm => xm.Author.Equals(context.User) && xm.Channel.Equals(context.Channel));

            if (result.TimedOut)
            {
                DiscordMessage snark = await context.RespondAsync(
                    content: "You failed to provide a valid event title within the time limit, so thanks for wasting a minute of myyyy time. :triumph:");
                return new CustomResult<DiscordMessage>(timedOut: true);
            }

            string eventName = result.Result.Content;
            await context.RespondAsync("What do you want the event description to be?");
            result = await interactivity.WaitForMessageAsync(xm => xm.Author.Equals(context.User) && xm.Channel.Equals(context.Channel), timeoutoverride: TimeSpan.FromMinutes(3));

            if (result.TimedOut)
            {
                DiscordMessage snark = await context.RespondAsync(
                    content: "You failed to provide a valid event description within the time limit, so thanks for wasting a minute of myyyy time. :triumph:");
                return new CustomResult<DiscordMessage>(timedOut: true);
            }

            string eventDesc = result.Result.Content;

            await this.mediator.Send(new GuildEvents.Add(context.Guild.Id, eventName, eventDesc));
            DiscordEmbed embed = new DiscordEmbedBuilder()
                .WithAuthor(context.Client.CurrentUser.Username, iconUrl: context.Client.CurrentUser.AvatarUrl)
                .WithDescription(eventDesc)
                .WithTitle(eventName)
                .Build();

            return new CustomResult<DiscordMessage>(await context.RespondAsync("You have added the following event to your guild:", embed: embed));

        }

        [Command("remove")]
        [Description("Removes an event from the guild's events.")]
        [RequireUserPermissions(Permissions.ManageGuild)]
        public async Task RemoveGuildEvent(CommandContext context)
        {
            DiscordMessage msg = await context.RespondAsync(
                $":wave: Hi, {context.User.Mention}! You want to remove an event from your guild list?");
            await msg.CreateReactionAsync(DiscordEmoji.FromName(context.Client, ":regional_indicator_y:"));
            await msg.CreateReactionAsync(DiscordEmoji.FromName(context.Client, ":regional_indicator_n:"));
            InteractivityExtension interactivity = context.Client.GetInteractivity();

            InteractivityResult<MessageReactionAddEventArgs> interactivityResult =
                await interactivity.WaitForReactionAsync(msg, context.User);

            if (interactivityResult.TimedOut ||
                !interactivityResult.Result.Emoji.Equals(
                    DiscordEmoji.FromName(context.Client, ":regional_indicator_y:")))
            {
                DiscordMessage snark = await context.RespondAsync("Well, thanks for wasting my time. Have a good day.");
                return;
            }

            DiscordEmbedBuilder removeEventEmbed = new DiscordEmbedBuilder()
                .WithTitle("Select an event to remove by typing: <event number>")
                .WithColor(context.Member.Color);

            Task<(bool, int)> messageValidationAndReturn(MessageCreateEventArgs messageE)
            {
                if (messageE.Author.Equals(context.User) && int.TryParse(messageE.Message.Content, out int eventToChoose))
                {
                    return Task.FromResult((true, eventToChoose));
                }
                else
                {
                    return Task.FromResult((false, -1));
                }
            }


            List<GuildEvent> guildEvents = (await this.mediator.Send(new GuildEvents.GetGuildEvents(context.Guild))).Value.ToList();

            CustomResult<int> result = await context.WaitForMessageAndPaginateOnMsg(
                GetGuildEventsPages(guildEvents, interactivity, removeEventEmbed),
                messageValidationAndReturn);

            if (result.TimedOut || result.Cancelled)
            {
                await context.RespondAsync("You never gave me a valid input. Thanks for wasting my time. :triumph:");
                return;
            }

            GuildEvent selectedEvent = guildEvents[result.Result - 1];

            await this.mediator.Send(new GuildEvents.Delete(selectedEvent));
            await context.RespondAsync($"You have deleted the \"{selectedEvent.EventName}\" event from the guild's event list.", embed: null);
        }

        [Command("show")]
        [Description("Shows a listing of all events currently available for this guild.")]
        public async Task ShowGuildEvents(CommandContext context)
        {
            await context.Client.GetInteractivity().SendPaginatedMessageAsync(context.Channel, context.User,
                GetGuildEventsPages((await this.mediator.Send(new GuildEvents.GetGuildEvents(context.Guild))).Value, context.Client.GetInteractivity()),
                behaviour: PaginationBehaviour.WrapAround, deletion: PaginationDeletion.DeleteMessage, timeoutoverride: TimeSpan.FromMinutes(1));
        }

        private static IEnumerable<Page> GetGuildEventsPages(IEnumerable<GuildEvent> guildEvents, InteractivityExtension interactivity, DiscordEmbedBuilder pageEmbedBase = null)
        {
            StringBuilder guildEventsStringBuilder = new();

            int count = 1;
            foreach (GuildEvent guildEvent in guildEvents)
            {
                guildEventsStringBuilder.AppendLine($"{count}. {guildEvent.EventName}");
                count++;
            }

            if (!guildEvents.Any())
            {
                guildEventsStringBuilder.AppendLine("This guild doesn't have any defined events.");
            }

            return interactivity.GeneratePagesInEmbed(guildEventsStringBuilder.ToString(), SplitType.Line, embedbase: pageEmbedBase);
        }

        private static IEnumerable<Page> GetScheduledEventsPages(IEnumerable<GuildBackgroundJob> guildEventJobs, DateTimeZone timeZone, InteractivityExtension interactivity, DiscordEmbedBuilder pageEmbedBase = null)
        {
            StringBuilder guildEventsStringBuilder = new();

            int count = 1;
            foreach (GuildBackgroundJob job in guildEventJobs)
            {
                guildEventsStringBuilder.AppendLine($"{count}. {job.JobName} - {job.ScheduledTime.InZone(timeZone).LocalDateTime:f}");
                count++;
            }

            if (!guildEventJobs.Any())
            {
                guildEventsStringBuilder.AppendLine("This guild doesn't have any scheduled events.");
            }

            return interactivity.GeneratePagesInEmbed(guildEventsStringBuilder.ToString(), SplitType.Line, embedbase: pageEmbedBase);
        }

        private static readonly string NoTimeZoneErrorMessage = "You must have a time zone set up to use this command. Set up your time zone using `time init`.";
    }
}