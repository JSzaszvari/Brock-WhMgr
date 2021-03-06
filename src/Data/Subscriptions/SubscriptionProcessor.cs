﻿namespace WhMgr.Data.Subscriptions
{
    using System;
    using System.Linq;
    using System.Threading;

    using DSharpPlus;
    using DSharpPlus.Entities;

    using WhMgr.Configuration;
    using WhMgr.Data.Subscriptions.Models;
    using WhMgr.Diagnostics;
    using WhMgr.Extensions;
    using WhMgr.Geofence;
    using WhMgr.Net.Models;
    using WhMgr.Net.Webhooks;

    public class SubscriptionProcessor
    {
        #region Variables

        private static readonly IEventLogger _logger = EventLogger.GetLogger();

        private readonly DiscordClient _client;
        private readonly WhConfig _whConfig;
        private readonly WebhookManager _whm;
        private readonly EmbedBuilder _embedBuilder;
        private readonly NotificationQueue _queue;

        #endregion

        #region Properties

        public SubscriptionManager Manager { get; }

        #endregion

        #region Constructor

        public SubscriptionProcessor(DiscordClient client, WhConfig config, WebhookManager whm, EmbedBuilder embedBuilder)
        {
            _logger.Trace($"SubscriptionProcessor::SubscriptionProcessor");

            _client = client;
            _whConfig = config;
            _whm = whm;
            _embedBuilder = embedBuilder;
            _queue = new NotificationQueue();

            Manager = new SubscriptionManager();

            ProcessQueue();
        }

        #endregion

        #region Public Methods

        public void ProcessPokemonSubscription(PokemonData pkmn)
        {
            if (!_whConfig.EnableSubscriptions)
                return;

            var db = Database.Instance;
            if (!db.Pokemon.ContainsKey(pkmn.Id))
                return;

            var loc = GetGeofence(pkmn.Latitude, pkmn.Longitude);
            if (loc == null)
            {
                _logger.Warn($"Failed to lookup city from coordinates {pkmn.Latitude},{pkmn.Longitude} {db.Pokemon[pkmn.Id].Name} {pkmn.IV}, skipping...");
                return;
            }

            var subscriptions = Manager.GetUserSubscriptionsByPokemonId(pkmn.Id);
            if (subscriptions == null)
            {
                _logger.Warn($"Failed to get subscriptions from database table.");
                return;
            }

            SubscriptionObject user;
            bool isSupporter;
            PokemonSubscription subscribedPokemon;
            var pokemon = db.Pokemon[pkmn.Id];
            bool matchesIV;
            bool matchesLvl;
            bool matchesGender;
            DiscordMember member = null;
            var embed = _embedBuilder.BuildPokemonMessage(pkmn, loc.Name);
            for (var i = 0; i < subscriptions.Count; i++)
            {
                try
                {
                    user = subscriptions[i];
                    if (user == null)
                        continue;

                    if (!user.Enabled)
                        continue;

                    try
                    {
                        member = _client.GetMemberById(_whConfig.GuildId, user.UserId);
                    }
                    catch (Exception ex)
                    {
                        _logger.Debug($"FAILED TO GET MEMBER BY ID {user.UserId}");
                        _logger.Error(ex);
                    }

                    if (member == null)
                    {
                        _logger.Warn($"Failed to find Discord member with id {user.UserId}.");
                        continue;
                    }

                    isSupporter = member.HasSupporterRole(_whConfig.SupporterRoleId);
                    if (!isSupporter)
                    {
                        _logger.Debug($"User {member.Username} is not a supporter, skipping pokemon {pokemon.Name}...");
                        continue;
                    }

                    subscribedPokemon = user.Pokemon.FirstOrDefault(x => x.PokemonId == pkmn.Id);
                    if (subscribedPokemon == null)
                    {
                        _logger.Info($"User {member.Username} not subscribed to Pokemon {pokemon.Name}.");
                        continue;
                    }

                    if (!member.Roles.Select(x => x.Name.ToLower()).Contains(loc.Name.ToLower()))
                    {
                        //_logger.Info($"User {member.Username} does not have city role {loc.Name}, skipping pokemon {pokemon.Name}.");
                        continue;
                    }

                    matchesIV = _whm.Filters.MatchesIV(pkmn.IV, subscribedPokemon.MinimumIV);
                    //var matchesCP = _whm.Filters.MatchesCpFilter(pkmn.CP, subscribedPokemon.MinimumCP);
                    matchesLvl = _whm.Filters.MatchesLvl(pkmn.Level, subscribedPokemon.MinimumLevel);
                    matchesGender = _whm.Filters.MatchesGender(pkmn.Gender, subscribedPokemon.Gender);

                    if (!(matchesIV && matchesLvl && matchesGender))
                        continue;

                    _logger.Debug($"Notifying user {member.Username} that a {pokemon.Name} {pkmn.CP}CP {pkmn.IV} IV L{pkmn.Level} has spawned...");

                    _queue.Enqueue(new Tuple<DiscordUser, string, DiscordEmbed>(member, pokemon.Name, embed));
                    Statistics.Instance.SubscriptionPokemonSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
            }
        }

        public void ProcessRaidSubscription(RaidData raid)
        {
            if (!_whConfig.EnableSubscriptions)
                return;

            var db = Database.Instance;
            if (!db.Pokemon.ContainsKey(raid.PokemonId))
                return;

            var loc = GetGeofence(raid.Latitude, raid.Longitude);
            if (loc == null)
            {
                _logger.Warn($"Failed to lookup city for coordinates {raid.Latitude},{raid.Longitude}, skipping...");
                return;
            }

            var subscriptions = Manager.GetUserSubscriptionsByRaidBossId(raid.PokemonId);
            if (subscriptions == null)
            {
                _logger.Warn($"Failed to get subscriptions from database table.");
                return;
            }

            bool isSupporter;
            SubscriptionObject user;
            var pokemon = db.Pokemon[raid.PokemonId];
            var embed = _embedBuilder.BuildRaidMessage(raid, loc.Name);
            for (int i = 0; i < subscriptions.Count; i++)
            {
                try
                {
                    user = subscriptions[i];
                    if (user == null)
                        continue;

                    if (!user.Enabled)
                        continue;

                    var member = _client.GetMemberById(_whConfig.GuildId, user.UserId);
                    if (member == null)
                    {
                        _logger.Warn($"Failed to find member with id {user.UserId}.");
                        continue;
                    }

                    isSupporter = member.HasSupporterRole(_whConfig.SupporterRoleId);
                    if (!isSupporter)
                    {
                        _logger.Info($"User {user.UserId} is not a supporter, skipping raid boss {pokemon.Name}...");
                        continue;
                    }

                    //if (!member.Roles.Select(x => x.Name.ToLower()).Contains(loc.Name.ToLower()))
                    //{
                    //    _logger.Debug($"[{loc.Name}] Skipping notification for user {member.DisplayName} ({member.Id}) for raid boss {pokemon.Name} because they do not have the city role '{loc.Name}'.");
                    //    continue;
                    //}

                    var distance = new Coordinates(user.Latitude, user.Longitude).DistanceTo(new Coordinates(raid.Latitude, raid.Longitude));
                    if (user.DistanceM > 0 && user.DistanceM < distance)
                    {
                        //Skip if distance is set and is not met.
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for raid boss {pokemon.Name}, raid is farther than set distance of '{user.DistanceM} meters.");
                        //continue;
                    }

                    if (user.Gyms.Count > 0 && user.Gyms.FirstOrDefault(x => raid.GymName.ToLower().Contains(x.Name.ToLower())) == null)
                    {
                        //Skip if list is not empty and gym is not in list.
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for raid boss {pokemon.Name}, raid '{raid.GymName}' is not in list of subscribed gyms.");
                        //continue;
                    }

                    var exists = user.Raids.FirstOrDefault(x => 
                        x.PokemonId == raid.PokemonId && 
                        (string.IsNullOrEmpty(x.City) || (!string.IsNullOrEmpty(x.City) && string.Compare(loc.Name, x.City, true) == 0))
                    ) != null;
                    if (!exists)
                    {
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for raid boss {pokemon.Name}, raid is in city '{loc.Name}'.");
                        continue;
                    }

                    _logger.Debug($"Notifying user {member.Username} that a {raid.PokemonId} raid is available...");

                    _queue.Enqueue(new Tuple<DiscordUser, string, DiscordEmbed>(member, pokemon.Name, embed));
                    Statistics.Instance.SubscriptionRaidsSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
            }
        }

        public void ProcessQuestSubscription(QuestData quest)
        {
            if (!_whConfig.EnableSubscriptions)
                return;

            var db = Database.Instance;
            var reward = quest.Rewards[0].Info;
            var rewardKeyword = quest.GetRewardString();
            var questName = quest.GetMessage();

            var loc = GetGeofence(quest.Latitude, quest.Longitude);
            if (loc == null)
            {
                _logger.Warn($"Failed to lookup city for coordinates {quest.Latitude},{quest.Longitude}, skipping...");
                return;
            }

            var subscriptions = Manager.GetUserSubscriptions();
            if (subscriptions == null)
            {
                _logger.Warn($"Failed to get subscriptions from database table.");
                return;
            }

            bool isSupporter;
            SubscriptionObject user;
            var embed = _embedBuilder.BuildQuestMessage(quest, loc.Name);
            for (int i = 0; i < subscriptions.Count; i++)
            {
                try
                {
                    user = subscriptions[i];
                    if (user == null)
                        continue;

                    if (!user.Enabled)
                        continue;

                    var member = _client.GetMemberById(_whConfig.GuildId, user.UserId);
                    if (member == null)
                    {
                        _logger.Warn($"Failed to find member with id {user.UserId}.");
                        continue;
                    }

                    isSupporter = member.HasSupporterRole(_whConfig.SupporterRoleId);
                    if (!isSupporter)
                    {
                        _logger.Info($"User {user.UserId} is not a supporter, skipping quest {questName}...");
                        continue;
                    }

                    var exists = user.Quests.FirstOrDefault(x => rewardKeyword.ToLower().Contains(x.RewardKeyword.ToLower()) &&
                    (
                        string.IsNullOrEmpty(x.City) || (!string.IsNullOrEmpty(x.City) && string.Compare(loc.Name, x.City, true) == 0)
                    )) != null;
                    if (!exists)
                    {
                        //_logger.Debug($"Skipping notification for user {member.DisplayName} ({member.Id}) for quest {questName} because the quest is in city '{loc.Name}'.");
                        continue;
                    }

                    //Check if time is passed user preset snooze time, if so save to db to be requested later, otherwise add to queue.
                    if (user.AlertTime.HasValue && user.AlertTime.Value.TimeOfDay > DateTime.Now.TimeOfDay)
                    {
                        var snoozedQuest = new SnoozedQuest
                        {
                            Date = DateTime.Now.Date,
                            UserId = user.UserId,
                            PokestopName = quest.PokestopName,
                            Latitude = quest.Latitude,
                            Longitude = quest.Longitude,
                            Quest = quest.GetMessage(),
                            Condition = quest.GetConditionName(),
                            Reward = quest.GetRewardString(),
                            RewardType = quest.Rewards[0]?.Type ?? QuestRewardType.Unset,
                            IconUrl = quest.GetIconUrl(),
                            City = loc.Name
                        };

                        _logger.Debug($"Snoozing quest {quest.GetMessage()} for user {user.UserId}.");
                        var result = Manager.AddSnoozedQuest(user.UserId, snoozedQuest);
                        if (!result)
                        {
                            _logger.Warn($"Could not add snoozed quest [{snoozedQuest.PokestopName}, {snoozedQuest.Quest}] to user {user.UserId} subscriptions.");
                        }

                        continue;
                    }

                    _logger.Debug($"Notifying user {member.Username} that a {rewardKeyword} quest is available...");
                    _queue.Enqueue(new Tuple<DiscordUser, string, DiscordEmbed>(member, questName, embed));
                    Statistics.Instance.SubscriptionQuestsSent++;
                    Thread.Sleep(5);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex);
                }
            }
        }

        #endregion

        #region Private Methods

        private void ProcessQueue()
        {
            _logger.Trace($"SubscriptionProcessor::ProcessQueue");

#pragma warning disable RECS0165 // Asynchronous methods should return a Task instead of void
            new Thread(async () =>
#pragma warning restore RECS0165 // Asynchronous methods should return a Task instead of void
            {
                while (true)
                {
                    if (_queue.Count == 0)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    var item = _queue.Dequeue();
                    await _client.SendDirectMessage(item.Item1, item.Item3);

                    _logger.Debug($"[WEBHOOK] Notified user {item.Item1.Username} of {item.Item2}.");
                    Thread.Sleep(50);
                }
            })
            { IsBackground = true }.Start();
        }

        private GeofenceItem GetGeofence(double latitude, double longitude)
        {
            var loc = _whm.GeofenceService.GetGeofence(_whm.Geofences.Select(x => x.Value).ToList(), new Location(latitude, longitude));
            return loc;
        }

        #endregion
    }
}