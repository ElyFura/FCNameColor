﻿using Dalamud.Data;
using Dalamud.Game;
using Dalamud.Game.ClientState;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.Command;
using Dalamud.Game.Gui;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Game.Text.SeStringHandling.Payloads;
using Dalamud.IoC;
using Dalamud.Logging;
using Dalamud.Plugin;
using FFXIVClientStructs.FFXIV.Client.Game.Group;
using NetStone;
using NetStone.Model.Parseables.FreeCompany.Members;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Timers;
using XivCommon;
using XivCommon.Functions.NamePlates;

namespace FCNameColor
{
    public class Plugin : IDalamudPlugin
    {
        public string Name => "FC Name Color";
        private const string CommandName = "/fcnc";

        [PluginService] public static DalamudPluginInterface Pi { get; private set; }

        private readonly Configuration config;

        [PluginService] public static ClientState ClientState { get; private set; }
        [PluginService] public static ChatGui Chat { get; private set; }
        [PluginService] public static Condition Condition { get; private set; }
        [PluginService] public static ObjectTable Objects { get; private set; }
        [PluginService] public static CommandManager Commands { get; private set; }
        [PluginService] public static Framework Framework { get; private set; }


        private readonly XivCommonBase xivCommonBase;
        private LodestoneClient lodestoneClient;
        private readonly FCNameColorProvider fcNameColorProvider;
        private PluginUI UI { get; }
        private bool loggingIn;
        private bool firstTime = false;
        private readonly Timer timer = new() {Interval = 1000};
        private List<FCMember> members;
        public bool Loading;
        public const int CooldownTime = 10;
        public int Cooldown = 0;
        public bool NotInFC;
        public bool Error;

        public Plugin(DataManager dataManager)
        {
            config = Pi.GetPluginConfig() as Configuration;
            timer.Elapsed += delegate
            {
                Cooldown -= 1;
                if (Cooldown <= 0)
                {
                    timer.Stop();
                }
            };
            if (config == null)
            {
                firstTime = true;
                config = new Configuration();
            }

            config.Initialize(Pi);

            xivCommonBase = new XivCommonBase(Hooks.NamePlates);
            xivCommonBase.Functions.NamePlates.OnUpdate += NamePlates_OnUpdate;

            UI = new PluginUI(config, dataManager, this);

            Commands.AddHandler(CommandName, new CommandInfo(OnCommand)
            {
                HelpMessage = "Opens the FCNameColor Config."
            });

            ClientState.Login += OnLogin;
            Framework.Update += OnFrameworkUpdate;
            Pi.UiBuilder.Draw += DrawUI;
            Pi.UiBuilder.OpenConfigUi += DrawConfigUI;

            _ = FetchData();

            this.fcNameColorProvider = new FCNameColorProvider(Pi, new FCNameColorAPI(this.config));
        }

        private void OnCommand(string command, string args)
        {
            UI.Visible = true;
        }

        private void DrawConfigUI()
        {
            UI.Visible = true;
        }

        private void OnLogin(object sender, EventArgs e)
        {
            // LocalPlayer is still null at this point, so we just set a flag that indicates we're logging in.
            loggingIn = true;
            members = null;
        }

        private void OnFrameworkUpdate(Framework framework)
        {
            if (!loggingIn || ClientState.LocalPlayer == null) return;
            loggingIn = false;

            if (string.IsNullOrEmpty(ClientState.LocalPlayer.CompanyTag.TextValue)) return;
            PluginLog.Debug(
                $"Logged in as {ClientState.LocalPlayer.Name} @ {ClientState.LocalPlayer.HomeWorld.GameData.Name}.");
            _ = FetchData();
        }

        private void DrawUI()
        {
            UI.Draw();
        }

        public void Reload()
        {
            _ = FetchData();
        }

        private void HandleError()
        {
            PluginLog.Debug("Running HandleError");
            Error = true;
            Cooldown = CooldownTime * 2;
            timer.Start();

            void OnFinish(object sender, ElapsedEventArgs e)
            {
                if (Cooldown > 0) return;

                PluginLog.Debug("HandleError: Retrying FetchData");
                _ = FetchData();
                timer.Elapsed -= OnFinish;
            }

            timer.Elapsed += OnFinish;
        }

        private async Task FetchData()
        {
            Loading = true;
            Cooldown = CooldownTime;
            timer.Start();
            PluginLog.Debug("Fetching data");

            if (firstTime)
            {
                Chat.Print(
                    "[FCNameColor]: First-time setup - Fetching FC members from Lodestone. Plugin will work once this is done.");
            }

            lodestoneClient ??= await LodestoneClient.GetClientAsync();

            var playerCacheName = $"{ClientState.LocalPlayer?.Name}@{ClientState.LocalPlayer?.HomeWorld.GameData.Name}";
            config.PlayerIDs.TryGetValue(playerCacheName, out var playerId);

            if (string.IsNullOrEmpty(playerId))
            {
                PluginLog.Debug("Fetching character ID");
                var playerSearch = await lodestoneClient.SearchCharacter(
                    new NetStone.Search.Character.CharacterSearchQuery()
                    {
                        World = ClientState.LocalPlayer.HomeWorld.GameData.Name,
                        CharacterName = ClientState.LocalPlayer.Name.TextValue
                    });
                playerId = playerSearch.Results
                    .FirstOrDefault(entry => entry.Name == ClientState.LocalPlayer.Name.TextValue)?.Id;
                if (string.IsNullOrEmpty(playerId))
                {
                    PluginLog.Error("Could not find player on Lodestone");
                    HandleError();
                    return;
                }

                config.PlayerIDs[playerCacheName] = playerId;
            }

            var fcFetched = config.PlayerFCs.TryGetValue(playerId, out var cachedFC);
            if (fcFetched)
            {
                PluginLog.Debug($"Loading {cachedFC.Members.Length} cached FC members");
                members = cachedFC.Members.ToList();
            }

            PluginLog.Debug("Fetching FC ID via character page");
            var player = await lodestoneClient.GetCharacter(playerId);
            if (player.FreeCompany == null)
            {
                PluginLog.Debug("Player is not in an FC.");
                NotInFC = true;
                return;
            }

            var fc = new FC {ID = player.FreeCompany.Id, Name = player.FreeCompany.Name};

            // Fetch the first page of FC members.
            // This will also contain the amount of additional pages of members that may have to be retrieved.
            PluginLog.Debug("Fetching members page 1");
            try
            {
                var fcMemberResult = await lodestoneClient.GetFreeCompanyMembers(fc.ID);
                members = new List<FCMember>();
                members.AddRange(fcMemberResult.Members.Select(res => new FCMember {ID = res.Id, Name = res.Name}));

                // Fire off async requests for fetching members for each remaining page
                if (fcMemberResult.NumPages > 1)
                {
                    var taskList = new List<Task<FreeCompanyMembers>>();
                    foreach (var index in Enumerable.Range(2, fcMemberResult.NumPages - 1))
                    {
                        PluginLog.Debug($"Fetching members page {index}");
                        taskList.Add(lodestoneClient.GetFreeCompanyMembers(fc.ID, index));
                    }

                    await Task.WhenAll(taskList);
                    taskList.ForEach(task =>
                        members.AddRange(
                            task.Result.Members.Select(res => new FCMember() {ID = res.Id, Name = res.Name})));
                }

                fc.Members = members.ToArray();
                config.PlayerFCs[playerId] = fc;
                config.Save();
                Loading = false;
                PluginLog.Debug($"Finished fetching data. Fetched {members.Count} members.");

                if (firstTime)
                {
                    Chat.Print($"[FCNameColor]: First-time setup finished. Fetched {members.Count} members.");
                    firstTime = false;
                }
            }
            catch (Exception)
            {
                HandleError();
            }
        }

        private SeString BuildSeString(string content)
        {
            return new SeString(new Payload[]
            {
                new UIForegroundPayload(Convert.ToUInt16(config.UiColor)),
                new UIGlowPayload(config.Glow ? Convert.ToUInt16(config.UiColor) : (ushort) 0),
                new TextPayload(content),
                UIGlowPayload.UIGlowOff,
                UIForegroundPayload.UIForegroundOff
            });
        }

        private unsafe void NamePlates_OnUpdate(NamePlateUpdateEventArgs args)
        {
            if (!config.Enabled || members == null)
            {
                return;
            }

            if (args.Type != PlateType.Player || args.ObjectId == 0)
            {
                return;
            }

            var objectID = args.ObjectId;
            var target = (PlayerCharacter) Objects.SearchById(objectID);

            var isLocalPlayer = ClientState.LocalPlayer.ObjectId == objectID;
            var isPartyMember = GroupManager.Instance()->IsObjectIDInAlliance(objectID);
            var isInDuty = Condition[ConditionFlag.BoundByDuty56];

            if (isInDuty && isLocalPlayer)
            {
                return;
            }

            if (!isInDuty && isLocalPlayer && !config.IncludeSelf)
            {
                return;
            }

            // Skip any player who is dead, colouring the name of dead characters makes them harder to recognize.
            if (target.CurrentHp == 0)
            {
                return;
            }

            if (target.HomeWorld.Id != ClientState.LocalPlayer.HomeWorld.Id)
            {
                return;
            }

            if (!members.Exists(member => member.Name == target.Name.TextValue) ||
                this.config.IgnoredPlayers.ContainsKey(target.Name.TextValue))
            {
                return;
            }

            if (isInDuty && config.IncludeDuties)
            {
                PluginLog.Debug($"Overriding player nameplate for {args.Name.TextValue} (ObjectID {objectID})");
                args.Colour = new RgbaColour
                {
                    A = (byte) (config.Color.W * 255), R = (byte) (config.Color.X * 255),
                    G = (byte) (config.Color.Y * 255), B = (byte) (config.Color.Z * 255)
                };
                return;
            }

            var shouldReplaceName = !config.OnlyColorFCTag && !isPartyMember && !isLocalPlayer;
            PluginLog.Debug(
                $"Name: {args.Name.TextValue}, shouldReplaceName: {shouldReplaceName}, IsInDuty: {isInDuty}, OnlyColorFCTag: {config.OnlyColorFCTag}, isPartyMember: {isPartyMember}, isLocalPlayer: {isLocalPlayer}");

            if (!isInDuty && !shouldReplaceName)
            {
                var newFCString = BuildSeString(args.FreeCompany.TextValue);
                args.FreeCompany = newFCString;
            }

            if (shouldReplaceName)
            {
                args.Colour = new()
                {
                    A = (byte) (config.Color.W * 255), R = (byte) (config.Color.X * 255),
                    G = (byte) (config.Color.Y * 255), B = (byte) (config.Color.Z * 255)
                };
            }
            else
            {
                var newFCString = BuildSeString(args.FreeCompany.TextValue);
                args.FreeCompany = newFCString;
            }

            PluginLog.Debug($"Overriding player nameplate for {args.Name.TextValue} (ObjectID {objectID})");
        }


        protected virtual void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    UI.Dispose();

                    this.fcNameColorProvider.Dispose();

                    Commands.RemoveHandler(CommandName);
                    Framework.Update -= OnFrameworkUpdate;
                    ClientState.Login -= OnLogin;
                    Pi.Dispose();

                    xivCommonBase.Functions.NamePlates.OnUpdate -= NamePlates_OnUpdate;
                    xivCommonBase.Dispose();
                }
            }
            catch (Exception ex)
            {
                PluginLog.Error(ex, "Failed to dispose properly.");
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}