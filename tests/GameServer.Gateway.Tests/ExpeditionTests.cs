using System.Buffers.Binary;
using GameServer.Core;
using GameServer.Core.Accounts;
using GameServer.Core.Protocol.Instance;
using GameServer.Core.Protocol.Login;
using GameServer.Core.World;
using GameServer.Data.Terrain.Generation;
using GameServer.Modding;
using GameServer.Modules.Expedition;
using League = GameServer.Core.Accounts.League;

namespace GameServer.Gateway.Tests;

public sealed class ExpeditionTests
{
    // The level files (data/game-files.zip) generate the room: the official MapWorldsForbiddenWoods seed 1304805886 creates
    // its detonator at cell (839, 631) turned 3π/2: the room's own server object line, generated here with the plugin state,
    // and no such line without it. Then the encounter's lifecycle on that area: its owner, the plan made once, and every
    // player who enters, the later one too, replayed what stands.
    [GameFilesFact]
    public async Task Expedition_plugin_state_places_the_encounter_room_at_the_official_detonator()
    {
        ExpeditionModule.LoadData();
        byte[] kept = [0x04, 0x00];
        const string area = "MapWorldsForbiddenWoods";
        uint seed = ServerLevels.PlayableSeed(area, 1304805886, 0, null, LeagueCategory.None, [((ushort)0x086a, kept)]);
        Assert.Equal(1304805886u, seed);
        ushort hash = (ushort)ClientTables.Table("WorldAreas").First(r => r.TextOrNull("Id") == area).Int("HASH16");
        var layout = AreaLayouts.For(hash, seed);
        var room = ExpeditionEncounter.RoomDetonator(layout);
        Assert.NotNull(room);
        Assert.Equal((839, 631), room.Value.Cell);
        Assert.Equal(3 * Math.PI / 2, room.Value.Rotation, 4);
        Assert.NotNull(ExpeditionEncounter.RoomNpc(layout));
        Assert.Equal("Metadata/Monsters/LeagueExpedition/NPC/ExpeditionGwennen_", ExpeditionEncounter.WildNpcPath("Faction1"));
        Assert.Equal("Metadata/Monsters/LeagueExpedition/NPC/ExpeditionTujen", ExpeditionEncounter.WildNpcPath("Faction2"));
        Assert.Equal("Metadata/Monsters/LeagueExpedition/NPC/ExpeditionRog", ExpeditionEncounter.WildNpcPath("Faction3"));
        Assert.Equal("Metadata/Monsters/LeagueExpedition/NPC/ExpeditionDannig", ExpeditionEncounter.WildNpcPath("Faction4"));
        uint plain = ServerLevels.PlayableSeed(area, 1304805887, 0, null, LeagueCategory.None, null);
        Assert.Null(ExpeditionEncounter.RoomDetonator(AreaLayouts.For(hash, plain)));
        await Encounter_is_the_instances_and_every_entering_player_is_replayed_it();
    }

    // What the plugin state, the roll and the monster pools are: no level file is read.
    static void The_roll_the_state_and_the_pools()
    {
        ExpeditionModule.LoadData();
        Assert.Equal((ushort)0x086a, ExpeditionTables.PluginId);
        Assert.Equal((byte)116, ExpeditionTables.RulesetRow);
        Assert.Equal("ExpeditionPlugin", AreaTerrainPlugins.NameOf(0x086a));
        Assert.Equal("ExpeditionEncounter", LevelGenerator.LeaguePluginFeature("ExpeditionPlugin", [0x04, 0x00]));
        Assert.Null(LevelGenerator.LeaguePluginFeature("ExpeditionPlugin", [0x06, 0x00]));
        Assert.Null(LevelGenerator.LeaguePluginFeature("UltimatumPlugin", [0x00]));

        // The roll: never in The Coast (no chance), about 8 in 100 maps; the state names one of the four factions.
        var rng = new Random(1);
        Assert.All(Enumerable.Range(0, 200), _ => Assert.Null(ExpeditionEncounter.PluginStates("1_1_2", rng)));
        var rolled = Enumerable.Range(0, 1000).Select(_ => ExpeditionEncounter.PluginStates("MapWorldsForbiddenWoods", rng))
            .Where(s => s is not null).Select(s => Assert.Single(s!)).ToList();
        Assert.InRange(rolled.Count, 50, 120);
        Assert.All(rolled, s => Assert.Equal((ushort)0x086a, s.Plugin));
        Assert.Equal(4, rolled.Select(s => ExpeditionTables.FactionOf(s.State)).Distinct().Count());
        Assert.Equal(1, ExpeditionTables.FactionOf([0x04, 0x00]));
        // A map's switches: the league's own bit of MapDisableLeagues (not any bit), nothing-more maps, the keystone, Blighted
        // maps and the league's disable stat each stop the roll; a test's forced chance reaches no area without a chance.
        var forest = ExpeditionEncounter.PluginStates("MapWorldsForbiddenWoods", new Random(2), AreaStatsOf((Stat.MapExtraContentHasNoBaseChanceToSpawn, 1),
            (Stat.MapLeagueContentAdditionalChancePct, 100)));
        Assert.NotNull(forest);
        Assert.NotNull(ExpeditionEncounter.PluginStates("MapWorldsForbiddenWoods", rng, AreaStatsOf(
            (Stat.MapExtraContentHasNoBaseChanceToSpawn, 1), (Stat.MapExpeditionLeague, 1))));
        Assert.NotNull(ExpeditionEncounter.PluginStates("MapWorldsForbiddenWoods", rng, AreaStatsOf(
            (Stat.MapExtraContentHasNoBaseChanceToSpawn, 1), (Stat.MapExpeditionEncounterAdditionalChancePct, 50),
            (Stat.MapExpeditionEncounterChancePlusPct, 100))));
        foreach (var (stat, value) in new (uint, int)[] { (Stat.MapDisableLeagues, 1 << LeagueChances.Expedition), (Stat.MapNukeEverything, 1),
                     (Stat.AtlasKeystoneNoExtraContent, 1), (Stat.IsBlightedMap, 1), (Stat.MapDisableExpeditionFromChance, 1) })
            Assert.Null(ExpeditionEncounter.PluginStates("MapWorldsForbiddenWoods", new Random(2), AreaStatsOf((Stat.MapLeagueContentAdditionalChancePct, 100), (stat, value))));
        Assert.NotNull(ExpeditionEncounter.PluginStates("MapWorldsForbiddenWoods", new Random(2), AreaStatsOf((Stat.MapLeagueContentAdditionalChancePct, 100),
            (Stat.MapDisableLeagues, 1 << LeagueChances.Breach))));

        // Every GGG-like league lists the module; its state is kept by the composition, one the generator does not port
        // is dropped.
        var leagues = LeagueCatalog.FromFolder("Leagues");
        var standard = leagues.Find("Standard")!;
        Assert.All(leagues.All.Where(l => l.Modules.Contains("Core/Content/Anarchy")), l => Assert.Contains("Core/Content/Expedition", l.Modules));
        var request = new AreaStatsRequest("Exile", "Standard", 0x7ad2, null);
        ushort mapHash = (ushort)ClientTables.Table("WorldAreas").First(r => r.TextOrNull("Id") == "MapWorldsForbiddenWoods").Int("HASH16");
        var areaStats = Game.ComposeMapAreaStats(MapMods.None, 1,
            AreaStatsOf((Stat.MapLeagueContentAdditionalChancePct, 100)), new HashSet<string>(), -1);
        var mapRequest = request with { Area = mapHash, AreaStats = areaStats };
        Assert.NotNull(ExpeditionModule.PlanTerrain(mapRequest));
        areaStats[Stat.AtlasKeystoneNoExtraContent] = 1;
        Assert.Null(ExpeditionModule.PlanTerrain(mapRequest));
        var runtime = new LeagueRuntime(standard, ServerLeagues.ModulesOf(standard));
        byte[] kept = [0x04, 0x00];
        using (InstanceEvents.TerrainPluginStates.Add("Expedition", _ => [((ushort)0x086a, kept)]))
            Assert.Equal(kept, Assert.Single(InstanceEvents.ComposeTerrainPluginStates(runtime, request)).State);
        using (InstanceEvents.TerrainPluginStates.Add("Expedition", _ => [((ushort)0x086a, [0x06, 0x00])]))
            Assert.Empty(InstanceEvents.ComposeTerrainPluginStates(runtime, request));


        // The monster pools come from the faction's MonsterPacks rows, never from names: five regular packs, two elite packs
        // per faction, and none of the 173 pinnacle bosses, daemons, totems, spectres or Kalandra monsters.
        Assert.Equal(5, ExpeditionTables.RegularPacks.Count);
        Assert.All(ExpeditionTables.FactionIds, f => Assert.Equal(2, ExpeditionTables.ElitePacks(f).Count));
        Assert.NotEmpty(ExpeditionTables.VarietiesOf("Faction2"));
        Assert.All(ExpeditionTables.VarietiesOf("Faction2"), v =>
            Assert.DoesNotMatch("Boss|Daemon|Totem|Spectre|LeagueLake|Orb", ClientTables.Table("MonsterVarieties").First(r => r.Index == v).Text("Id")));
    }

    // The encounter belongs to the instance: its owner has the client's IsMapOwner in the area (not the character's saved
    // flag), the plan is made once at the area's generation, and every player who enters, the one that came later too, is sent
    // the monster types and the ruleset's messages as the encounter stands.
    static async Task Encounter_is_the_instances_and_every_entering_player_is_replayed_it()
    {
        ExpeditionModule.LoadData();
        const string area = "MapWorldsForbiddenWoods";
        byte[] kept = [0x04, 0x00];
        uint seed = ServerLevels.PlayableSeed(area, 1304805886, 0, null, LeagueCategory.None, [((ushort)0x086a, kept)]);
        ushort hash = (ushort)ClientTables.Table("WorldAreas").First(r => r.TextOrNull("Id") == area).Int("HASH16");
        var layout = AreaLayouts.For(hash, seed);
        var instance = new InstanceState();
        await using var first = await Wire.Open(layout, seed, instance, "ExpeditionOwner");
        await using var second = await Wire.Open(layout, seed, instance, "ExpeditionGuest");
        using var encounter = new ExpeditionEncounter();

        Assert.Equal(["IsMapOwner"], encounter.QuestFlags(first.Player));
        Assert.Empty(encounter.QuestFlags(second.Player));
        // The check the combat code runs for every quest flag it reads (GameEvents.HasAreaQuestFlag).
        using (GameEvents.AreaQuestFlags.Add("Expedition", encounter.QuestFlags))
        {
            Assert.True(GameEvents.HasAreaQuestFlag(first.Player, "IsMapOwner"));
            Assert.False(GameEvents.HasAreaQuestFlag(second.Player, "IsMapOwner"));
            Assert.False(GameEvents.HasAreaQuestFlag(first.Player, "SomeOtherFlag"));
        }
        Assert.Single(encounter.RulesetMessages(second.Player));

        await encounter.OnAreaGenerated(_ => { }, first.Player);
        await encounter.OnPlayerEntered(_ => { }, first.Player);
        await encounter.OnPlayerEntered(_ => { }, second.Player);
        byte[] one = await first.Finish(), two = await second.Finish();
        foreach (var wire in new[] { one, two })
        {
            Assert.True(Contains(wire, [0x00, 0x20]), "0x0020 monster types");
            Assert.True(Contains(wire, [0x02, 0xc9, 116]), "0x02c9 ruleset data");
        }
        // The same messages, so the late player sees what the first does.
        Assert.Equal(one.Length, two.Length);
    }

    static Dictionary<uint, int> AreaStatsOf(params (uint Stat, int Value)[] stats) => stats.ToDictionary(s => s.Stat, s => s.Value);

    static bool Contains(byte[] bytes, byte[] pattern) => bytes.AsSpan().IndexOf(pattern) >= 0;

    sealed class Wire(System.Net.Sockets.TcpClient writer, System.Net.Sockets.TcpClient reader, Player player, MemoryStream captured, Task drain)
        : IAsyncDisposable
    {
        public Player Player => player;

        public static async Task<Wire> Open(AreaLayout layout, uint seed, InstanceState instance, string name)
        {
            using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            listener.Start();
            var accepting = listener.AcceptTcpClientAsync();
            var writer = new System.Net.Sockets.TcpClient();
            await writer.ConnectAsync(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
            var reader = await accepting;
            var captured = new MemoryStream();
            var clientCiphers = new ConnectionCiphers(new byte[64], true);
            var player = new Player(layout, 1, seed)
            {
                Character = { Name = name }, Instance = instance, PlayerLife = 5000,
                Connection = { PlayerSent = true, Session = new("expedition-test", writer.GetStream(), new ConnectionCiphers(new byte[64], false)) },
            };
            var drain = Task.Run(async () =>
            {
                byte[] buf = new byte[4096];
                try
                {
                    int n;
                    while ((n = await reader.GetStream().ReadAsync(buf)) > 0)
                    {
                        clientCiphers.Receive.Process(buf.AsSpan(0, n));
                        lock (captured) captured.Write(buf, 0, n);
                    }
                }
                catch { }
            });
            return new(writer, reader, player, captured, drain);
        }

        public async Task<byte[]> Finish()
        {
            try { writer.Client.Shutdown(System.Net.Sockets.SocketShutdown.Send); } catch { }
            await drain;
            lock (captured) return captured.ToArray();
        }

        public async ValueTask DisposeAsync()
        {
            player.Closed = true;
            writer.Dispose();
            await drain;
            reader.Dispose();
        }
    }

    // The bodies and messages are the official captures': the detonator (cell (1023, 1229) in the level-73 capture), a
    // marker and an elite marker (their model in Animated, the elite's 1.5 scale), the dug chest LeagueFaction2Low
    // (eid 0x910, 43 bytes at level 73), the fuse eid 0x8e6 (pole 0x8e4 to explosive 0x8e0) and the summary after one
    // covered remnant (600 → 15 and 14364 → 80, 1 of 7). The rotation f32 is left out where the official's is the room's
    // own angle or the marker's.
    [Fact]
    public void Expedition_creates_and_messages_carry_the_official_bytes()
    {
        ExpeditionModule.LoadData();
        The_roll_the_state_and_the_pools();
        byte[] official = Convert.FromHexString("0000ff030000cd040000c8cb964000000000400103000000000000000000");
        byte[]? detonator = ExpeditionWire.Object(ExpeditionEncounter.DetonatorType, (1023, 1229), 3 * Math.PI / 2,
            animated: 0x40, interaction: 0x01);
        Assert.NotNull(detonator);
        Assert.Equal(0x02, detonator[0]); // the create wakes it (the official's 0x00 is the wake zone's)
        Assert.Equal(Convert.ToHexString(official[1..10]), Convert.ToHexString(detonator[1..10]));
        Assert.Equal(Convert.ToHexString(official[14..]), Convert.ToHexString(detonator[14..]));

        const string ao = "Metadata/Terrain/Doodads/Leagues/Expedition/monstermarker_02.ao";
        byte[]? marker = ExpeditionWire.Object("Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker", (966, 1132), 5.1990, ao: ao);
        Assert.NotNull(marker);
        Assert.Equal(2 + 16 + 1 + 4 + ao.Length * 2 + 1 + 1, marker.Length);
        Assert.Equal(0x00, marker[16]);
        Assert.Equal(0x01, marker[18]);
        Assert.Equal(ao, System.Text.Encoding.Unicode.GetString(marker, 23, ao.Length * 2));
        byte[]? elite = ExpeditionWire.Object("Metadata/MiscellaneousObjects/Expedition/ExpeditionMarker", (1005, 1133), 5.5743,
            ao: ao, scale: ExpeditionTables.EliteMarkerScale);
        Assert.NotNull(elite);
        Assert.Equal(0x20, elite[16]);
        Assert.Equal(1.5f, BinaryPrimitives.ReadSingleLittleEndian(elite.AsSpan(18)));
        Assert.Equal(0x01, elite[30]);

        string chestPath = ExpeditionTables.ChestPath("Faction2", ExpeditionTables.ChestTier.Low);
        Assert.Equal("Metadata/Chests/LeaguesExpedition/LeagueFaction2Low", chestPath);
        Assert.Equal(0x99aeb430u, InstanceEntities.ObjectTypeHash(chestPath));
        official = Convert.FromHexString("020022040000ac040000d58bd33c0000000000004900000000000000004040010800000000000100010000");
        byte[]? chest = ExpeditionWire.Object(chestPath, (1058, 1196), 0, level: 73, stateValues: [1]);
        Assert.NotNull(chest);
        Assert.Equal(Convert.ToHexString(official[..10]), Convert.ToHexString(chest[..10]));
        Assert.Equal(Convert.ToHexString(official[14..]), Convert.ToHexString(chest[14..]));

        official = Convert.FromHexString(
            "020014040000bf040000000000000000000000c801b70c000b653146d36f4e46e4080000090000004e4333467a3f4d46e0080000060000000100000000");
        byte[]? fuse = ExpeditionWire.Object("Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosiveFuse", (1044, 1215), 0,
            beam: new ExpeditionWire.Beam(new ExpeditionWire.BeamEnd(0x8e4, (1044, 1215)), ExpeditionWire.BeamPole,
                new ExpeditionWire.BeamEnd(0x8e0, (1055, 1208)), ExpeditionWire.BeamExplosive));
        Assert.Equal(Convert.ToHexString(official), Convert.ToHexString(fuse!));
        // A chest's StateMachine goes on as the full layout (state count, timer count, compact value, flags), never one byte.
        byte[]? ready = ExpeditionWire.StateUpdate(0x910, chestPath, [2], true);
        Assert.Equal("01000202", Convert.ToHexString(ready![^4..]));
        Assert.Equal("01000302", Convert.ToHexString(ExpeditionWire.StateUpdate(0x910, chestPath, [3], true)![^4..]));
        Assert.Null(ExpeditionWire.StateUpdate(0x910, chestPath, [3, 3], true));
        // The blast's effect names its .ao as the official create does, and the lattice never wraps below 0.
        byte[]? blast = ExpeditionWire.Object(ExpeditionTables.EffectType, (1055, 1208), 0, ao: ExpeditionTables.BlastAo, baseData: ExpeditionTables.EffectBaseData);
        Assert.True(blast.AsSpan().IndexOf(System.Text.Encoding.Unicode.GetBytes(ExpeditionTables.BlastAo)) > 0);
        Assert.Equal(0, ExpeditionWire.Dirt([(-3, 400, 1)])[3]);
        Assert.Null(ExpeditionWire.Object("Metadata/MiscellaneousObjects/Expedition/ExpeditionExplosiveFuse", (1044, 1215), 0));

        Assert.Equal("02C9740D" + "030282580FB81C805001000700",
            Convert.ToHexString(InstanceRulesets.Data(116, ExpeditionWire.Summary([(600, 15), (14364, 80)], 1, 7))));
        // A placement (C→S 0x2ca, type 00, the cell and the explosives left).
        byte[] placement = [0x02, 0xca, 116, 10, 0x00, .. BitConverter.GetBytes(1055), .. BitConverter.GetBytes(1208), 4];
        var (row, payload) = InstanceRulesets.ReadAction(placement);
        Assert.Equal((byte)116, row);
        Assert.Equal((1055, 1208), ExpeditionWire.ReadPlacement(payload));

        // Detonator StateMachine carries three shared states [activated, light_colour, explosives_placed] with explicit override flags.
        byte[]? detonatorPlacing = ExpeditionWire.StateUpdate(0x3ff, ExpeditionEncounter.DetonatorType, [0, 1, 1], true);
        Assert.NotNull(detonatorPlacing);
        Assert.Equal("0300000201020102", Convert.ToHexString(detonatorPlacing![^8..]));
    }

    [GameFilesFact]
    public async Task Expedition_registers_before_enter_places_detonator_state_and_spawns_npc()
    {
        const string area = "MapWorldsForbiddenWoods";
        byte[] kept = [0x04, 0x00]; // Faction2 -> ExpeditionTujen
        uint seed = ServerLevels.PlayableSeed(area, 1304805886, 0, null, LeagueCategory.None, [((ushort)0x086a, kept)]);
        ushort hash = (ushort)ClientTables.Table("WorldAreas").First(r => r.TextOrNull("Id") == area).Int("HASH16");
        var layout = AreaLayouts.For(hash, seed);
        var instance = new InstanceState();

        TestEnvironment.OpenOwnSaves("expedition-npc");
        var previousLogic = Program.GameLogicOrNull;
        var previousLifetime = Game.CombatLifetime;
        var lifetime = new ModuleLifetime("Game");
        Game.CombatLifetime = lifetime;
        Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));
        var module = new ModuleLifetime("Expedition");
        new ExpeditionModule().Load(Context(module));
        await using var owner = await Wire.Open(layout, seed, instance, "ExpeditionNpcOwner");
        owner.Player.LeagueModule = new(new League("expedition-test", "Standard", false, false),
            new HashSet<string>(["Game", "Expedition"]));
        instance.World.Runner = owner.Player;
        Program.InGame.TryAdd(owner.Player, 0);
        try
        {
            var before = GameEvents.RunBeforeEnterMessages(owner.Player).ToList();
            byte row = ExpeditionTables.RulesetRow!.Value;
            Assert.Contains(before, m => m.Message.AsSpan().SequenceEqual(InstanceProtocol.AddRuleset(row)));
            Assert.DoesNotContain(GameEvents.RunEnterMessages(owner.Player),
                m => m.Message.AsSpan().SequenceEqual(InstanceProtocol.AddRuleset(row)));

            await GameEvents.RunAreaGenerated(owner.Player);
            var detonator = ExpeditionEncounter.RoomDetonator(layout)!.Value;
            owner.Player.Walk.Teleport(detonator.Cell.X, detonator.Cell.Y, Game.AreaTime(owner.Player));
            var work = GameEvents.AreaTickWork(owner.Player);
            await GameEvents.RunAreaTick(work, owner.Player, 0.1);

            Assert.Contains(owner.Player.Area.Monsters.Values,
                m => m.Variety?.Id == ExpeditionEncounter.WildNpcPath("Faction2") && !m.Hostile && m.Scripted);
        }
        finally
        {
            Program.InGame.TryRemove(owner.Player, out _);
            PlayerInventories.Forget(owner.Player.Character.Name!);
            module.Stop(ex => throw ex);
            GameEventOwners.Release("Expedition", GameEventOwners.ModuleRank);
            Game.CombatLifetime = previousLifetime;
            Program.SetGameLogic(previousLogic!);
            TestEnvironment.ReleaseOwnSaves();
        }
    }

    static IModuleContext Context(IModuleLifetime lifetime)
    {
        var context = System.Reflection.DispatchProxy.Create<IModuleContext, ModuleHook>();
        ((ModuleHook)(object)context).Lifetime = lifetime;
        return context;
    }

    public class ModuleHook : System.Reflection.DispatchProxy
    {
        public IModuleLifetime Lifetime = null!;
        protected override object? Invoke(System.Reflection.MethodInfo? method, object?[]? args) => method!.Name switch
        {
            "get_Lifetime" => Lifetime,
            "get_Manifest" => new ModManifest(Lifetime.Id, "0", ModKind.Feature, [], Lifetime.Id + ".dll"),
            "Log" => null,
            _ => throw new NotSupportedException(method.Name),
        };
    }

    [GameFilesFact]
    public async Task Expedition_completion_drops_undetonated_remnants_and_undug_markers()
    {
        await using var run = await SingleChestRun.StartAsync();
        await run.FinishEncounterAsync();

        var afterIds = JoinSnapshot(run.First.Player.Area, new Player(run.Layout, 1, run.Seed)).Select(r => r.Entity).ToHashSet();
        Assert.Contains(run.DetonatorId, afterIds);

        var undetonatedRemnants = Enumerable.Range(0, run.Plan.Remnants.Count)
            .Where(i => Distance(run.Plan.Remnants[i].Cell, run.PlaceCell) >= ExpeditionTables.BlastRadius)
            .Select(i => run.RemnantId(i))
            .ToList();
        var undugMarkers = Enumerable.Range(0, run.Plan.Markers.Count)
            .Where(i => Distance(run.Plan.Markers[i].Cell, run.PlaceCell) >= ExpeditionTables.BlastRadius)
            .Select(i => run.MarkerId(i))
            .ToList();
        Assert.NotEmpty(undetonatedRemnants);
        Assert.NotEmpty(undugMarkers);

        foreach (uint id in undetonatedRemnants) Assert.DoesNotContain(id, afterIds);
        foreach (uint id in undugMarkers) Assert.DoesNotContain(id, afterIds);

        byte[] wire = await run.First.Finish();
        Assert.True(Contains(wire, InstanceProtocol.RemoveObject(undetonatedRemnants[0])), "0x02bd removal for un-detonated remnant");
        Assert.True(Contains(wire, InstanceProtocol.RemoveObject(undugMarkers[0])), "0x02bd removal for un-dug marker");

        // A player joining after Stage.Over is not sent any dropped remnant or marker creates.
        await using var late = await Wire.Open(run.Layout, run.Seed, run.Instance, "ExpeditionLate");
        late.Player.Connection.LockstepClient = false;
        await run.Encounter.OnPlayerEntered(_ => { }, late.Player);
        var replayIds = JoinSnapshot(run.First.Player.Area, late.Player).Select(r => r.Entity).ToHashSet();
        foreach (uint id in undetonatedRemnants) Assert.DoesNotContain(id, replayIds);
        foreach (uint id in undugMarkers) Assert.DoesNotContain(id, replayIds);
    }

    [GameFilesFact]
    public async Task Expedition_excavated_chest_registers_blocker_and_opens_on_interact()
    {
        await using var run = await SingleChestRun.StartAsync();
        await run.FinishEncounterAsync();

        var afterIds = JoinSnapshot(run.First.Player.Area, new Player(run.Layout, 1, run.Seed)).Select(r => r.Entity).ToHashSet();
        var chestIds = afterIds.Where(id => !run.BeforeIds.Contains(id) && run.First.Player.StationaryPosition(id) is not null).ToList();
        Assert.NotEmpty(chestIds);
        uint chestId = chestIds[0];
        var chestCell = run.First.Player.StationaryPosition(chestId)!.Value;
        Assert.Equal(2, run.First.Player.StationaryInteractionSize(chestId));

        // A later entrant in the same area also receives the chest blocker.
        await using var guest = await Wire.Open(run.Layout, run.Seed, run.Instance, "ExpeditionGuest");
        guest.Player.Connection.LockstepClient = false;
        await run.Encounter.OnPlayerEntered(_ => { }, guest.Player);
        Assert.Equal(chestCell, guest.Player.StationaryPosition(chestId));
        Assert.Equal(2, guest.Player.StationaryInteractionSize(chestId));

        // Refused click (dead player) leaves the chest closed, then an in-range click opens it.
        run.First.Player.PlayerLife = 0;
        Assert.True(await run.Encounter.OnUse(run.First.Player, new InstanceTransfer.UseSkillOnObject(chestId, 1, 1, 0), 0));
        run.First.Player.PlayerLife = 5000;
        run.First.Player.Walk.Teleport(chestCell.X, chestCell.Y, 0);
        Assert.True(await run.Encounter.OnUse(run.First.Player, new InstanceTransfer.UseSkillOnObject(chestId, 1, 2, 0), 0));
        // Clicking an already-opened chest is handled without throwing.
        Assert.True(await run.Encounter.OnUse(run.First.Player, new InstanceTransfer.UseSkillOnObject(chestId, 1, 3, 0), 0));

        byte[] wire = await run.First.Finish();
        string chestType = run.Plan.Markers.First(m => m.Kind == ExpeditionTables.MarkerKind.Chest && m.Cell == chestCell).ChestType;
        Assert.True(Contains(wire, ExpeditionWire.ComponentUpdate(chestId, chestType, "ObjectMagicProperties", 0x08, 0x00, 0x00, 0x00)!));
        Assert.True(Contains(wire, ExpeditionWire.StateUpdate(chestId, chestType, [3], true)!));
    }

    static double Distance((int X, int Y) a, (int X, int Y) b) =>
        Math.Sqrt((double)(a.X - b.X) * (a.X - b.X) + (double)(a.Y - b.Y) * (a.Y - b.Y));

    static List<AreaDelta.Record> JoinSnapshot(GameArea area, Player probe)
    {
        var owner = GameArea.Own(area);
        try
        {
            area.Objects.Join(probe);
            List<AreaDelta.Record> all = [];
            for (int frame = 0; frame < 6; frame++) all.AddRange(area.Delta.Take());
            return all;
        }
        finally { GameArea.Own(owner); }
    }

    sealed class SingleChestRun : IAsyncDisposable
    {
        readonly IGameLogic? _previousLogic;
        readonly IModuleLifetime? _previousLifetime;
        readonly ModuleLifetime _lifetime;

        public required AreaLayout Layout { get; init; }
        public required uint Seed { get; init; }
        public required InstanceState Instance { get; init; }
        public required Wire First { get; init; }
        public required ExpeditionEncounter Encounter { get; init; }
        public required ExpeditionEncounter.Plan Plan { get; init; }
        public required ExpeditionEncounter.Marker ChestMarker { get; init; }
        public required (int X, int Y) PlaceCell { get; init; }
        public required uint DetonatorId { get; init; }
        public required HashSet<uint> BeforeIds { get; init; }

        SingleChestRun(IGameLogic? previousLogic, IModuleLifetime? previousLifetime, ModuleLifetime lifetime)
        {
            _previousLogic = previousLogic;
            _previousLifetime = previousLifetime;
            _lifetime = lifetime;
        }

        public uint MarkerId(int index) => DetonatorId + 1 + (uint)index;
        public uint RemnantId(int index) => DetonatorId + 1 + (uint)Plan.Markers.Count + (uint)index;

        public static async Task<SingleChestRun> StartAsync()
        {
            ExpeditionModule.LoadData();
            var previousLogic = Program.GameLogicOrNull;
            var previousLifetime = Game.CombatLifetime;
            var lifetime = new ModuleLifetime("Game");
            Game.CombatLifetime = lifetime;
            Program.SetGameLogic(new GameServer.Modules.Game.GameLogic(lifetime));

            const string area = "MapWorldsForbiddenWoods";
            byte[] kept = [0x04, 0x00];
            uint seed = ServerLevels.PlayableSeed(area, 1304805886, 0, null, LeagueCategory.None, [((ushort)0x086a, kept)]);
            ushort hash = (ushort)ClientTables.Table("WorldAreas").First(r => r.TextOrNull("Id") == area).Int("HASH16");
            var layout = AreaLayouts.For(hash, seed);
            var instance = new InstanceState();
            var first = await Wire.Open(layout, seed, instance, "ExpeditionOwner");
            first.Player.Connection.LockstepClient = false;
            var standard = LeagueCatalog.FromFolder("Leagues").Find("Standard")!;
            first.Player.LeagueModule = new(standard, ServerLeagues.ModulesOf(standard));
            Program.InGame[first.Player] = 0;

            var encounter = new ExpeditionEncounter();
            var plan = ExpeditionEncounter.MakePlan(first.Player, ServerLevels.AreaLevel(area))!;
            Assert.NotNull(plan);

            await encounter.OnAreaGenerated(_ => { }, first.Player);
            await encounter.OnPlayerEntered(_ => { }, first.Player);
            first.Player.Walk.Teleport(plan.Detonator.X, plan.Detonator.Y, 0);
            await encounter.TickFor(first.Player)!(first.Player, 0.1);

            var beforeIds = JoinSnapshot(first.Player.Area, new Player(layout, 1, seed)).Select(r => r.Entity).ToHashSet();
            uint detonatorId = beforeIds.Min();
            var chestMarker = plan.Markers.First(m => m.Kind == ExpeditionTables.MarkerKind.Chest);
            var placeCell = FindWalkablePlacement(layout, plan.Detonator, chestMarker.Cell);

            return new(previousLogic, previousLifetime, lifetime)
            {
                Layout = layout,
                Seed = seed,
                Instance = instance,
                First = first,
                Encounter = encounter,
                Plan = plan,
                ChestMarker = chestMarker,
                PlaceCell = placeCell,
                DetonatorId = detonatorId,
                BeforeIds = beforeIds,
            };
        }

        public async Task FinishEncounterAsync()
        {
            using (GameEvents.AreaQuestFlags.Add("Expedition", Encounter.QuestFlags))
            {
                byte[] place = [0x02, 0xca, ExpeditionTables.RulesetRow!.Value, 10, 0x00,
                    .. BitConverter.GetBytes(PlaceCell.X), .. BitConverter.GetBytes(PlaceCell.Y), 4];
                Assert.True(await Encounter.RulesetAction(First.Player, place));
            }
            Assert.True(await Encounter.OnUse(First.Player, new InstanceTransfer.UseSkillOnObject(DetonatorId, 1, 1, 0), 0));
            var tick = Encounter.TickFor(First.Player)!;
            for (int step = 0; step < 100; step++)
            {
                await tick(First.Player, 0.1);
                foreach (var monster in First.Player.Area.Monsters.Values)
                    Encounter.OnKill(new(First.Player, monster, "", (0, 0)));
            }
        }

        static (int X, int Y) FindWalkablePlacement(AreaLayout layout, (int X, int Y) detonator, (int X, int Y) target)
        {
            for (int r = 0; r < (int)ExpeditionTables.BlastRadius; r++)
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                    {
                        var cell = (target.X + dx, target.Y + dy);
                        if (cell.Item1 >= 0 && cell.Item2 >= 0 && cell.Item1 < layout.GridW && cell.Item2 < layout.GridH
                            && layout.WalkableFor(cell.Item1, cell.Item2, 0)
                            && Distance(cell, detonator) <= ExpeditionTables.PlaceRange
                            && Distance(cell, target) < ExpeditionTables.BlastRadius)
                            return cell;
                    }
            throw new InvalidOperationException("No walkable placement found near chest marker.");
        }

        public async ValueTask DisposeAsync()
        {
            Program.InGame.TryRemove(First.Player, out _);
            Encounter.Dispose();
            await First.DisposeAsync();
            _lifetime.Stop(ex => throw ex);
            Game.CombatLifetime = _previousLifetime!;
            Program.SetGameLogic(_previousLogic!);
        }
    }
}
