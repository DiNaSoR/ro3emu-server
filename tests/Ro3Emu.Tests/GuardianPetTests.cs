using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Romsg;
using Ro3Emu.Bot;
using Ro3Emu.Server.Game;
using Ro3Emu.Server.Data;
using Ro3Emu.Server.Maps;

namespace Ro3Emu.Tests;

/// <summary>Invented content: guardian admission and lifetime checks run in public CI without a client export.</summary>
public sealed class GuardianPetTests
{
    private static readonly int[] DescriptionProbe = [12, 3, 4];
    private static readonly GuardianPetRules.Reaction[] UnrelatedReactions = [GuardianPetRules.Reaction.Heal, GuardianPetRules.Reaction.Slow, GuardianPetRules.Reaction.Defence];
    private static readonly int[] PrimaryChildCandidates = [OtherChild, Child];
    private static readonly int[] PrimaryChildGroup = [199];
    private static readonly int[] BuffCategory = [3011];
    private static readonly int[] PrimaryBuffTuple = [Buff, 10000, 5000, 3001];
    private static readonly string[] DurationDescription = ["2.75"];
    private static readonly int[] SecondChildList = [SecondChild];
    private static readonly int[] SecondChildGroup = [699];
    private static readonly int[] BuffAttackChange = [103, 17];
    private static readonly int[] FightAttackChange = [103, 13];
    private static readonly int[] FightHpChange = [101, 5000];
    private static readonly int[] AssistAttackChange = [103, 7];
    private static readonly int[] UnlockCondition = [1002, 33];
    private static readonly int[] HealDescription = [6];
    private static readonly int[] ShieldDescription = [60, 7, 4];
    private static readonly int[] DamageDescription = [12, 3];
    private static readonly int[] BothChildren = [Child, SecondChild];
    private static readonly int[] SlowChange = [1131, -1200];
    private static readonly int[] ReactionController = [60, 99006, 1234];
    private static readonly int[] DefenceChange = [1052, 700];
    private static readonly int[] MagicDefenceChange = [1062, 700];
    private static readonly int[] DamageCategory = [3010];

    private const int Wrapper = 99001, Child = 99002, OtherChild = 99003, Buff = 99004, Pet = 99, Passive = 99005;
    private const int SecondWrapper = 99101, SecondChild = 99102, SecondPet = 100;

    [Theory]
    [InlineData("SenLinCiFu")]
    [InlineData("Pet_Salamander_23462")]
    [InlineData("yemanhudun")]
    public void NewlySupportedActivesCannotBorrowAnotherFamilysReactiveBuff(string script)
    {
        var data = new GameData(Microsoft.Extensions.Options.Options.Create(new Ro3Emu.Server.ServerOptions
        { GameDataDir = SyntheticData.Dir }), Microsoft.Extensions.Logging.Abstractions.NullLogger<GameData>.Instance);
        var effect = JsonSerializer.SerializeToElement(new { _iID = Child, _kScriptLogic = script });
        var wrapper = JsonSerializer.SerializeToElement(new { _iID = Wrapper });
        var passive = JsonSerializer.SerializeToElement(new { _iID = Passive, _kDescData = DescriptionProbe });
        var kind = GuardianPetRules.Classify(effect);
        Assert.NotNull(kind);
        var skill = new GuardianPetRules.Skill(2, Pet, 1, 0, 0, wrapper, effect, passive, kind.Value);
        Assert.DoesNotContain(skill.Reactive, UnrelatedReactions);
        Assert.Equal(0, GuardianPetRules.ReactiveBuff(data, skill));
    }

    private sealed class Fixture(string reaction = "Buff") : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ro3emu-guardian-" + Guid.NewGuid().ToString("N"));
        public SyntheticHost Host { get; private set; } = null!;
        public GameSession Session => Host.Service<SessionRegistry>().All().Single(s => s.Role != null);
        public GameLoop Loop => Host.Service<GameLoop>();
        public CharacterService Character => Host.Service<CharacterService>();
        public CombatService Combat => Host.Service<CombatService>();

        public async Task Start()
        {
            foreach (string file in Directory.EnumerateFiles(SyntheticData.Dir, "*", SearchOption.AllDirectories))
            {
                string dest = Path.Combine(_root, Path.GetRelativePath(SyntheticData.Dir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
            Write("SkillConfig", new Dictionary<string, object>
            {
                [Wrapper.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = Wrapper, _iSkillID = 99, _iMode = 1, _iSystemType = 8, _iBasicType = 1, _kScriptLogic = "petyinyue", _iCD = 30000,
                    _iCdModeGroup = -1, _iAutoUse = 1, _kAutoLearnSkill = PrimaryChildCandidates,
                    _kLogicData = PrimaryChildGroup, // the second candidate is the actual guardian, not an extra proc
                },
                [Child.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = Child, _iSkillID = 199, _iSystemType = 41, _iBasicType = 1, _iActionTime = 300,
                    _kScriptLogic = "PetSkill_2_2_QiaoKeHou", _kSkillCategory = BuffCategory,
                    _kBuff1 = new[] { PrimaryBuffTuple },
                },
                [OtherChild.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = OtherChild, _iSkillID = 299, _iSystemType = 41,
                    _kScriptLogic = "UnrecoveredGuardianProc",
                },
                [Passive.ToString(CultureInfo.InvariantCulture)] = new { _iID = Passive, _kDescData = DurationDescription },
                [SecondWrapper.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = SecondWrapper, _iSkillID = 599, _iSystemType = 8, _iBasicType = 1,
                    _kScriptLogic = "petyinyue", _iCD = 25000, _iCdModeGroup = -1,
                    _kAutoLearnSkill = SecondChildList, _kLogicData = SecondChildGroup,
                },
                [SecondChild.ToString(CultureInfo.InvariantCulture)] = new
                {
                    _iID = SecondChild, _iSkillID = 699, _iSystemType = 41, _iBasicType = 1, _iActionTime = 300,
                    _kScriptLogic = "PetSkill_2_2_QiaoKeHou", _kSkillCategory = BuffCategory,
                    _kBuff1 = new[] { PrimaryBuffTuple },
                },
            });
            Write("BuffConfig", new Dictionary<string, object>
            {
                [Buff.ToString(CultureInfo.InvariantCulture)] = new { _iID = Buff, _iBuffID = 399, _iTime = 5000, _kChangeAttri = new[] { BuffAttackChange } },
            });
            Write("PetBaseConfig", new Dictionary<string, object>
            {
                [Pet.ToString(CultureInfo.InvariantCulture)] = new { _iID = Pet }, [SecondPet.ToString(CultureInfo.InvariantCulture)] = new { _iID = SecondPet },
            });
            Write("PetStarConfig", new[]
            {
                new { _iPetID = Pet, _iStar = 1, _iStage = 0, _iProtectSkill = Wrapper, _iPassiveMain = Passive,
                    _kFightSkill = new[] { FightAttackChange, FightHpChange }, _kAssistSkill = new[] { AssistAttackChange } },
                new { _iPetID = SecondPet, _iStar = 1, _iStage = 0, _iProtectSkill = SecondWrapper, _iPassiveMain = 0,
                    _kFightSkill = Array.Empty<int[]>(), _kAssistSkill = Array.Empty<int[]>() },
            });
            Write("PetSetLimitConfig", new Dictionary<string, object>
            {
                ["1"] = new
                {
                    _iID = 1, _iCondition1 = 1, _iCondition2 = 1,
                    _iFightAttrAddition = 10000, _iAssistAttrAddition = 10000
                },
                ["2"] = new
                {
                    _iID = 2, _iCondition1 = 1, _iCondition2 = 1,
                    _iFightAttrAddition = 10000, _iAssistAttrAddition = 10000
                },
            });
            Write("LC_ConditionList", new Dictionary<string, object>
            {
                ["1"] = new { m_kSubConditionList = new[] { new { m_kData = UnlockCondition } } },
            });
            ConfigureReaction();
            Host = await SyntheticHost.StartAsync(o => o.GameDataDir = _root);
        }

        private void ConfigureReaction()
        {
            if (reaction == "Buff") return;
            var skills = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "config", "SkillConfig.json")))?.AsObject()
                ?? throw new InvalidOperationException("Missing synthetic skills.");
            var buffs = JsonNode.Parse(File.ReadAllText(Path.Combine(_root, "config", "BuffConfig.json")))?.AsObject()
                ?? throw new InvalidOperationException("Missing synthetic buffs.");
            var (script, category, description) = reaction switch
            {
                "Heal" => ("YaoGun", 3013, HealDescription),
                "Shield" => ("BoLiPaoPao", 3014, ShieldDescription),
                "Damage" => ("Pet_WildRose_23552", 3010, DamageDescription),
                _ => throw new ArgumentException("Unknown reaction."),
            };
            foreach (int id in BothChildren)
            {
                var row = skills[id.ToString(CultureInfo.InvariantCulture)]?.AsObject() ?? throw new InvalidOperationException("Missing synthetic child.");
                row["_kScriptLogic"] = script;
                row["_kSkillCategory"] = new JsonArray(category);
                row["_kDamageParam1"] = new JsonArray(8500);
                row["_kRange"] = new JsonArray(750);
                row["_iTargetMax"] = 3;
                if (reaction != "Shield") row["_kBuff1"] = new JsonArray();
            }
            skills[Passive.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(new { _iID = Passive, _kDescData = description });
            var wrapper = skills[SecondWrapper.ToString(CultureInfo.InvariantCulture)]?.AsObject() ?? throw new InvalidOperationException("Missing synthetic wrapper.");
            wrapper["_kDescData"] = new JsonArray(77);
            buffs[Wrapper.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(new { _iID = Wrapper, _iBuffID = 990, _kChangeAttri = new[] { SlowChange } });
            buffs[Passive.ToString(CultureInfo.InvariantCulture)] = JsonSerializer.SerializeToNode(new { _iID = Passive, _kLogicData = ReactionController });
            buffs["99006"] = JsonSerializer.SerializeToNode(new { _iID = 99006, _iBuffID = 991, _kChangeAttri = new[] { DefenceChange, MagicDefenceChange } });
            File.WriteAllText(Path.Combine(_root, "config", "SkillConfig.json"), skills.ToJsonString());
            File.WriteAllText(Path.Combine(_root, "config", "BuffConfig.json"), buffs.ToJsonString());
        }

        private void Write(string name, object value) =>
            File.WriteAllText(Path.Combine(_root, "config", name + ".json"), JsonSerializer.Serialize(value));

        public Task Equip() => Loop.InvokeAsync(async () =>
        {
            Session.Progress!.Level = 40;
            Session.Pets = new DBPetInfo { CurrentPlan = 0 };
            Session.Pets.ListPet[Pet] = new PetData { PetId = Pet, Star = 1 };
            Session.Pets.SetupPlans[0] = new DBPetSetUpPlan { SetUpPets = { [1] = Pet } };
            Session.Pets.SetupPlans[1] = new DBPetSetUpPlan();
            await Character.RefreshStatsAsync(Session);
        });

        public async ValueTask DisposeAsync()
        {
            if (Host != null) await Host.DisposeAsync();
            Directory.Delete(_root, true);
        }
    }

    private static async Task<CastSkillRsp> Reply(BotClient bot)
    {
        var packet = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.UseSkill), TimeSpan.FromSeconds(5));
        return CastSkillRsp.Parser.ParseFrom(packet.Body);
    }

    [Fact]
    public async Task WrapperResolvesPrimaryChildAndConcurrentRequestsShareOneCooldown()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        long before = f.Character.Stats(f.Session)[103];
        var request = new CastSkillReq { SkillId = Wrapper, TargetId = f.Session.Role!.Id };
        await bot.SendGameAsync(CombatService.UseSkill, request);
        await bot.SendGameAsync(CombatService.UseSkill, request);
        var replies = new[] { await Reply(bot), await Reply(bot) };
        Assert.Single(replies, r => r.Result == 0);
        Assert.Single(replies, r => r.Result == ErrorCodes.SkillCooldown);
        var animation = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.ExtraUseInSkill), TimeSpan.FromSeconds(5));
        var extra = UseInSkillInfo.Parser.ParseFrom(animation.Body);
        Assert.Equal(Child, extra.SkillID);
        Assert.Equal(Pet, extra.PetConfigId);
        await bot.Inbox.WaitAsync(e => e.IsGame(BuffService.BuffUpdate), TimeSpan.FromSeconds(5));
        Assert.Single(f.Session.Buffs, b => b.ConfigId == Buff);
        Assert.Equal(5000, Assert.Single(f.Session.Buffs, b => b.ConfigId == Buff).DurationMs); // primary does not activate its reactive passive
        Assert.Equal(before + 17, f.Character.Stats(f.Session)[103]);
        Assert.Null(f.Session.SkillCast);
    }

    [Fact]
    public async Task OnlyAnOwnedPetInAnUnlockedFightSlotCanCastOrContributeFightStats()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        long deployed = f.Character.Stats(f.Session)[103];
        async Task Refused(int id = Wrapper)
        {
            await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = id, TargetId = f.Session.Role!.Id });
            Assert.Equal(ErrorCodes.SkillNotLearned, (await Reply(bot)).Result);
        }
        await Refused(Child);
        await f.Loop.InvokeAsync(() =>
        {
            var plan = f.Session.Pets!.SetupPlans[0];
            plan.SetUpPets.Remove(1);
            plan.SetUpPets[2] = Pet;
            return Task.CompletedTask;
        });
        await Refused(); // utility slots keep their passive/stat role but cannot cast Protect
        await f.Loop.InvokeAsync(async () =>
        {
            var plan = f.Session.Pets!.SetupPlans[0];
            plan.SetUpPets.Clear();
            plan.SupportPets[1] = Pet;
            await f.Character.RefreshStatsAsync(f.Session);
        });
        Assert.Equal(deployed - 13 + 7, f.Character.Stats(f.Session)[103]);
        await Refused();
        await f.Loop.InvokeAsync(async () =>
        {
            var plan = f.Session.Pets!.SetupPlans[0];
            plan.SupportPets.Clear();
            plan.SetUpPets[99] = Pet; // invalid persisted slot must not grant bonuses or guardian admission
            await f.Character.RefreshStatsAsync(f.Session);
        });
        Assert.Equal(deployed - 13, f.Character.Stats(f.Session)[103]);
        await Refused();
        await f.Loop.InvokeAsync(() =>
        {
            f.Session.Pets!.SetupPlans[0].SetUpPets[1] = Pet;
            f.Session.Pets.ListPet.Clear();
            return Task.CompletedTask;
        });
        await Refused();
    }

    [Fact]
    public async Task WheelKeepsPrimaryDuringCooldownAndPlanSwitchPublishesRemoval()
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        var skills = f.Host.Service<SkillService>();
        int slot = (int)SkillWheelSlotType.SkillWheelPet;
        await f.Loop.InvokeAsync(async () =>
        {
            f.Session.Pets!.ListPet[SecondPet] = new PetData { PetId = SecondPet, Star = 1 };
            f.Session.Pets.SetupPlans[0].SetUpPets[2] = SecondPet;
            await skills.SyncAsync(f.Session);
            Assert.Equal(Wrapper, skills.Build(f.Session).SkillWheel.WhellSkills[slot].SkillConfigId);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = Wrapper });
        Assert.Equal(0, (await Reply(bot)).Result);
        await f.Loop.InvokeAsync(() =>
        {
            Assert.Equal(Wrapper, skills.Build(f.Session).SkillWheel.WhellSkills[slot].SkillConfigId);
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = SecondWrapper });
        Assert.Equal(ErrorCodes.SkillNotLearned, (await Reply(bot)).Result);
        await bot.SendGameAsync(PetService.SwitchPlan, new PetSwitchPlanReq { TargetIndex = 1 });
        await bot.Inbox.WaitAsync(e => e.IsGame(PetService.SwitchPlan)
            && PetSwitchPlanResp.Parser.ParseFrom(e.Body).Result == 0, TimeSpan.FromSeconds(5));
        await bot.Inbox.WaitAsync(e => e.IsGame(SkillService.SkillDataSync)
            && DBSkillDataIncrement.Parser.ParseFrom(e.Body).SkillWheel?.Inc?.WhellSkills.GetValueOrDefault(slot)?.Del == true,
            TimeSpan.FromSeconds(5));
        await f.Loop.InvokeAsync(() =>
        {
            Assert.DoesNotContain(slot, skills.Build(f.Session).OriginSkillWheel.WhellSkills.Keys);
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData(false, 7750)]
    [InlineData(true, 5000)]
    public async Task ReactivePassiveAffectsPrimaryOnlyFromUtilityAndNotAssistSlots(bool support, int expectedMs)
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        await f.Loop.InvokeAsync(() =>
        {
            f.Session.Pets!.ListPet[SecondPet] = new PetData { PetId = SecondPet, Star = 1 };
            var plan = f.Session.Pets.SetupPlans[0];
            plan.SetUpPets[1] = SecondPet;
            if (support) plan.SupportPets[1] = Pet;
            else plan.SetUpPets[2] = Pet;
            return Task.CompletedTask;
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = SecondWrapper });
        Assert.Equal(0, (await Reply(bot)).Result);
        await bot.Inbox.WaitAsync(e => e.IsGame(BuffService.BuffUpdate), TimeSpan.FromSeconds(5));
        Assert.Equal(expectedMs, Assert.Single(f.Session.Buffs, b => b.ConfigId == Buff).DurationMs);
    }

    [Theory]
    [InlineData("Heal")]
    [InlineData("Shield")]
    public async Task UtilityReactionsApplyAfterAnotherPetsPrimaryEffect(string reaction)
    {
        await using var f = new Fixture(reaction);
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        await f.Loop.InvokeAsync(async () =>
        {
            f.Session.Pets!.ListPet[SecondPet] = new PetData { PetId = SecondPet, Star = 1 };
            f.Session.Pets.SetupPlans[0].SetUpPets[1] = SecondPet;
            f.Session.Pets.SetupPlans[0].SetUpPets[2] = Pet;
            await f.Character.SetHpSpAsync(f.Session, 1, null);
        });
        await bot.SendGameAsync(CombatService.UseSkill, new CastSkillReq { SkillId = SecondWrapper });
        Assert.Equal(0, (await Reply(bot)).Result);
        if (reaction == "Heal")
        {
            var packet = await bot.Inbox.WaitAsync(e => e.IsGame(CombatService.SkillHit)
                && SkillHitInfo.Parser.ParseFrom(e.Body).SkillId == Passive, TimeSpan.FromSeconds(5));
            Assert.Contains(SkillHitInfo.Parser.ParseFrom(packet.Body).Dsts, d => d.DamageType == DamageType.Heal && d.Damage > 0);
        }
        else
        {
            await bot.Inbox.WaitAsync(e => e.IsGame(BuffService.BuffUpdate)
                && BuffUpdateNotify.Parser.ParseFrom(e.Body).Buffs.Any(b => b.ConfigId == 99006), TimeSpan.FromSeconds(5));
            Assert.Equal(4000, Assert.Single(f.Session.Buffs, b => b.ConfigId == 99006).DurationMs);
        }
    }

    [Fact]
    public async Task UtilityOffensiveReactionReducesActualMonsterSpeed()
    {
        await using var f = new Fixture("Damage");
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        await f.Loop.InvokeAsync(async () =>
        {
            f.Session.Pets!.SetupPlans[0].SetUpPets.Remove(1);
            f.Session.Pets.SetupPlans[0].SetUpPets[2] = Pet;
            var monsters = f.Host.Service<MonsterService>();
            var template = monsters.Template(16320001) ?? throw new InvalidOperationException("Missing synthetic monster.");
            var target = await monsters.SpawnSingleAsync(f.Session.SceneEntry, 16320001,
                new MapPoint(f.Session.Position.X, f.Session.Position.Y, f.Session.Position.Z, 0), f.Session.InstanceId,
                customTemplate: template with { SpeedCm = 250, RoamDistance = 0 })
                ?? throw new InvalidOperationException("Synthetic monster could not spawn.");
            // Exercise the shared positive-hit callback used by ordinary guardian damage.
            var row = JsonSerializer.SerializeToElement(new { _iID = SecondChild, _iSystemType = 41, _kSkillCategory = DamageCategory });
            foreach (var handler in f.Combat.DealtAsync) await handler(f.Session, target, row, 19);
            Assert.Equal(1200, target.SlowMovementBp);
            Assert.InRange(target.SlowMovementExpireMs - MonsterService.NowMs, 2000, 3000);
            Assert.Equal(220, target.SpeedCm(MonsterService.NowMs));
        });
    }

    [Theory]
    [InlineData("plan")]
    [InlineData("unequip")]
    [InlineData("star")]
    [InlineData("scene")]
    [InlineData("stage")]
    [InlineData("slot-locked")]
    [InlineData("moved-to-utility")]
    public async Task PendingEffectIsDiscardedWhenItsSourceChanges(string change)
    {
        await using var f = new Fixture();
        await f.Start();
        await using var bot = await f.Host.InWorldAsync("Guardian");
        await f.Equip();
        await f.Loop.InvokeAsync(async () =>
        {
            await f.Combat.CastAsync(f.Session, new CastSkillReq { SkillId = Wrapper, TargetId = f.Session.Role!.Id });
            switch (change)
            {
                case "plan": f.Session.Pets!.CurrentPlan = 1; break;
                case "unequip": f.Session.Pets!.SetupPlans[0].SetUpPets.Clear(); break;
                case "star": f.Session.Pets!.ListPet[Pet].Star++; break;
                case "scene": f.Session.SceneGeneration++; break;
                case "stage": f.Session.Pets!.ListPet[Pet].StarProgress++; break;
                case "slot-locked": f.Session.Progress!.Level = 1; break;
                case "moved-to-utility":
                    f.Session.Pets!.SetupPlans[0].SetUpPets.Remove(1);
                    f.Session.Pets.SetupPlans[0].SetUpPets[2] = Pet;
                    break;
            }
        });
        Assert.Equal(0, (await Reply(bot)).Result);
        await Task.Delay(600);
        await f.Loop.InvokeAsync(() =>
        {
            Assert.DoesNotContain(f.Session.Buffs, b => b.ConfigId == Buff);
            return Task.CompletedTask;
        });
    }
}
