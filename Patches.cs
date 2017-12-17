﻿using Harmony;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Profile;

namespace Multiplayer
{
    [HarmonyPatch(typeof(OptionListingUtility))]
    [HarmonyPatch(nameof(OptionListingUtility.DrawOptionListing))]
    public static class MainMenuPatch
    {
        static void Prefix(Rect rect, List<ListableOption> optList)
        {
            int newColony = optList.FindIndex(opt => opt.label == "NewColony".Translate());
            if (newColony != -1)
                optList.Insert(newColony + 1, new ListableOption("Connect to server", () =>
                {
                    Find.WindowStack.Add(new ConnectWindow());
                }));

            int reviewScenario = optList.FindIndex(opt => opt.label == "ReviewScenario".Translate());
            if (reviewScenario != -1)
                AddHostButton(optList);

            if (Multiplayer.client != null && Multiplayer.server == null)
                optList.RemoveAll(opt => opt.label == "Save".Translate());
        }

        public static void AddHostButton(List<ListableOption> buttons)
        {
            if (Multiplayer.server != null)
                buttons.Insert(0, new ListableOption("Server info", () =>
                {
                    Find.WindowStack.Add(new ServerInfoWindow());
                }));
            else if (Multiplayer.client == null)
                buttons.Insert(0, new ListableOption("Host a server", () =>
                {
                    Find.WindowStack.Add(new HostWindow());
                }));
        }
    }

    [HarmonyPatch(typeof(UIRoot))]
    [HarmonyPatch(nameof(UIRoot.UIRootOnGUI))]
    public static class UIRootPatch
    {
        static bool firstRun = true;

        static void Prefix()
        {
            if (firstRun)
            {
                GUI.skin.font = Text.fontStyles[1].font;
                firstRun = false;
            }
        }
    }

    [HarmonyPatch(typeof(SavedGameLoader))]
    [HarmonyPatch(nameof(SavedGameLoader.LoadGameFromSaveFile))]
    [HarmonyPatch(new Type[] { typeof(string) })]
    public static class LoadPatch
    {
        static bool Prefix(string fileName)
        {
            if (Multiplayer.savedWorld != null && fileName == "server")
            {
                ScribeUtil.StartLoading(Multiplayer.savedWorld);

                if (Multiplayer.mapsData.Length > 0)
                {
                    XmlDocument mapsXml = new XmlDocument();
                    using (MemoryStream stream = new MemoryStream(Multiplayer.mapsData))
                        mapsXml.Load(stream);

                    XmlNode gameNode = Scribe.loader.curXmlParent["game"];
                    gameNode.RemoveChildIfPresent("maps");
                    gameNode["taleManager"]["tales"].RemoveAll();

                    XmlNode newMaps = gameNode.OwnerDocument.ImportNode(mapsXml.DocumentElement["maps"], true);
                    gameNode.AppendChild(newMaps);
                }

                ScribeMetaHeaderUtility.LoadGameDataHeader(ScribeMetaHeaderUtility.ScribeHeaderMode.Map, false);

                if (Scribe.EnterNode("game"))
                {
                    Current.Game = new Game();
                    Current.Game.InitData = new GameInitData();
                    Prefs.PauseOnLoad = false;
                    Current.Game.LoadGame(); // calls Scribe.loader.FinalizeLoading()
                    Prefs.PauseOnLoad = true;
                    Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
                }

                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Log.Message("Client maps: " + Current.Game.Maps.Count());

                    Multiplayer.savedWorld = null;
                    Multiplayer.mapsData = null;

                    if (!Current.Game.Maps.Any())
                    {
                        MemoryUtility.UnloadUnusedUnityAssets();
                        Find.World.renderer.RegenerateAllLayersNow();
                    }

                    /*Find.WindowStack.Add(new CustomSelectLandingSite()
                    {
                        nextAct = () => Settle()
                    });*/

                    Multiplayer.client.SetState(new ClientPlayingState(Multiplayer.client));
                    Multiplayer.client.Send(Packets.CLIENT_WORLD_LOADED);

                    Multiplayer.client.Send(Packets.CLIENT_ENCOUNTER_REQUEST, new object[] { Find.WorldObjects.Settlements.First(s => Find.World.GetComponent<MultiplayerWorldComp>().playerFactions.ContainsValue(s.Faction)).Tile });
                });

                return false;
            }

            return true;
        }

        private static void Settle()
        {
            byte[] extra = ScribeUtil.WriteSingle(new LongActionGenerating() { username = Multiplayer.username });
            Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.LONG_ACTION_SCHEDULE, extra });

            Find.GameInitData.mapSize = 150;
            Find.GameInitData.startingPawns.Add(StartingPawnUtility.NewGeneratedStartingPawn());
            Find.GameInitData.startingPawns.Add(StartingPawnUtility.NewGeneratedStartingPawn());
            Find.GameInitData.PrepForMapGen();
            Find.Scenario.PreMapGenerate(); // creates the FactionBase WorldObject
            IntVec3 intVec = new IntVec3(Find.GameInitData.mapSize, 1, Find.GameInitData.mapSize);
            FactionBase factionBase = Find.WorldObjects.FactionBases.First(faction => faction.Faction == Faction.OfPlayer);
            Map visibleMap = MapGenerator.GenerateMap(intVec, factionBase, factionBase.MapGeneratorDef, factionBase.ExtraGenStepDefs, null);
            Find.World.info.initialMapSize = intVec;
            PawnUtility.GiveAllStartingPlayerPawnsThought(ThoughtDefOf.NewColonyOptimism);
            Current.Game.VisibleMap = visibleMap;
            Find.CameraDriver.JumpToVisibleMapLoc(MapGenerator.PlayerStartSpot);
            Find.CameraDriver.ResetSize();
            Current.Game.InitData = null;

            Log.Message("New map: " + visibleMap.GetUniqueLoadID());

            ClientPlayingState.SyncClientWorldObj(factionBase);

            Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.LONG_ACTION_END, extra });
        }
    }

    [HarmonyPatch(typeof(MainButtonsRoot))]
    [HarmonyPatch(nameof(MainButtonsRoot.MainButtonsOnGUI))]
    public static class MainButtonsPatch
    {
        static bool Prefix()
        {
            Text.Font = GameFont.Small;
            string text = Find.TickManager.TicksGame.ToString();

            if (Find.VisibleMap != null)
            {
                text += " r:" + Find.VisibleMap.reservationManager.AllReservedThings().Count();

                if (Find.VisibleMap.GetComponent<MultiplayerMapComp>().factionHaulables.TryGetValue(Find.VisibleMap.info.parent.Faction.GetUniqueLoadID(), out ListerHaulables haul))
                    text += " h:" + haul.ThingsPotentiallyNeedingHauling().Count;

                if (Find.VisibleMap.GetComponent<MultiplayerMapComp>().factionSlotGroups.TryGetValue(Find.VisibleMap.info.parent.Faction.GetUniqueLoadID(), out SlotGroupManager groups))
                    text += " sg:" + groups.AllGroupsListForReading.Count;
            }

            Rect rect = new Rect(80f, 60f, 330f, Text.CalcHeight(text, 330f));
            Widgets.Label(rect, text);

            return Find.Maps.Count > 0;
        }
    }

    [HarmonyPatch(typeof(TickManager))]
    [HarmonyPatch(nameof(TickManager.TickManagerUpdate))]
    public static class TickUpdatePatch
    {
        private static TimeSpeed lastSpeed;

        static bool Prefix()
        {
            if (Multiplayer.client != null && Find.TickManager.CurTimeSpeed != lastSpeed)
            {
                ServerAction action = Find.TickManager.CurTimeSpeed == TimeSpeed.Paused ? ServerAction.PAUSE : ServerAction.UNPAUSE;
                Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { action, new byte[0] });
                Log.Message("client request at: " + Find.TickManager.TicksGame);

                Find.TickManager.CurTimeSpeed = lastSpeed;
                return false;
            }

            lastSpeed = Find.TickManager.CurTimeSpeed;
            return true;
        }

        public static void SetSpeed(TimeSpeed speed)
        {
            Find.TickManager.CurTimeSpeed = speed;
            lastSpeed = speed;
        }
    }

    [HarmonyPatch(typeof(GameDataSaveLoader))]
    [HarmonyPatch(nameof(GameDataSaveLoader.SaveGame))]
    public static class SavePatch
    {
        static bool Prefix()
        {
            if (Multiplayer.client == null || Multiplayer.server != null)
                return true;

            ScribeUtil.StartWriting();
            Scribe.EnterNode("savedMaps");
            List<Map> list = Current.Game.Maps.FindAll(map => map.IsPlayerHome);
            Scribe_Collections.Look(ref list, "maps", LookMode.Deep);
            byte[] data = ScribeUtil.FinishWriting();
            Multiplayer.client.Send(Packets.CLIENT_QUIT_MAPS, data);

            return false;
        }
    }

    [HarmonyPatch(typeof(ScribeLoader))]
    [HarmonyPatch(nameof(ScribeLoader.FinalizeLoading))]
    public static class FinalizeLoadingPatch
    {
        static void Prefix(ref string __state)
        {
            __state = Scribe.loader.curXmlParent.Name;
        }

        // called after cross refs and right before map finalization
        static void Postfix(string __state)
        {
            ScribeUtil.loading = false;

            if (Current.ProgramState != ProgramState.MapInitializing || __state != "game") return;

            RegisterCrossRefs();

            if (Multiplayer.client == null || Multiplayer.server != null) return;

            FinalizeFactions();
        }

        static void RegisterCrossRefs()
        {
            foreach (Faction f in Find.FactionManager.AllFactions)
                ScribeUtil.crossRefs.RegisterLoaded(f);

            foreach (Map map in Find.Maps)
                ScribeUtil.crossRefs.RegisterLoaded(map);
        }

        static void FinalizeFactions()
        {
            MultiplayerWorldComp comp = Find.World.GetComponent<MultiplayerWorldComp>();

            Faction.OfPlayer.def = FactionDefOf.Outlander;
            Faction clientFaction = comp.playerFactions[Multiplayer.username];
            clientFaction.def = FactionDefOf.PlayerColony;
            Find.GameInitData.playerFaction = clientFaction;

            // todo actually handle relations
            foreach (Faction current in Find.FactionManager.AllFactionsListForReading)
            {
                if (current == clientFaction) continue;
                current.TryMakeInitialRelationsWith(clientFaction);
            }

            Log.Message("Client faction: " + clientFaction.Name + " / " + clientFaction.GetUniqueLoadID());
        }
    }

    [HarmonyPatch(typeof(CaravanArrivalAction_AttackSettlement))]
    [HarmonyPatch(nameof(CaravanArrivalAction_AttackSettlement.Arrived))]
    public static class AttackSettlementPatch
    {
        static FieldInfo settlementField = typeof(CaravanArrivalAction_AttackSettlement).GetField("settlement", BindingFlags.NonPublic | BindingFlags.Instance);

        static bool Prefix(CaravanArrivalAction_AttackSettlement __instance, Caravan caravan)
        {
            if (Multiplayer.client == null) return true;

            Settlement settlement = (Settlement)settlementField.GetValue(__instance);
            string username = Find.World.GetComponent<MultiplayerWorldComp>().GetUsername(settlement.Faction);
            if (username == null) return true;

            Multiplayer.client.Send(Packets.CLIENT_ENCOUNTER_REQUEST, new object[] { settlement.Tile });

            return false;
        }
    }

    [HarmonyPatch(typeof(Settlement))]
    [HarmonyPatch(nameof(Settlement.ShouldRemoveMapNow))]
    public static class ShouldRemoveMap
    {
        static void Postfix(ref bool __result)
        {
            __result = false;
        }
    }

    [HarmonyPatch(typeof(FactionBaseDefeatUtility))]
    [HarmonyPatch("IsDefeated")]
    public static class IsDefeated
    {
        static void Postfix(ref bool __result)
        {
            __result = false;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker))]
    [HarmonyPatch(nameof(Pawn_JobTracker.StartJob))]
    public static class JobTrackerPatch
    {
        public static ReservationManager normalRes;
        public static bool dontHandle;

        public static FieldInfo pawnField = typeof(Pawn_JobTracker).GetField("pawn", BindingFlags.Instance | BindingFlags.NonPublic);

        static void Postfix(Pawn_JobTracker __instance, Job newJob)
        {
            Pawn pawn = (Pawn)pawnField.GetValue(__instance);
            Log.Message(Multiplayer.username + " start job " + newJob + " " + pawn);
        }

        public static bool IsPawnOwner(Pawn pawn)
        {
            return (pawn.Faction != null && pawn.Faction == Faction.OfPlayer) || pawn.Map.IsPlayerHome;
        }
    }

    public class JobRequest : AttributedExposable
    {
        [ExposeDeep]
        public Job job;
        [ExposeValue]
        public int mapId;
        [ExposeValue]
        public int pawnId;
    }

    [HarmonyPatch(typeof(Pawn_JobTracker))]
    [HarmonyPatch(nameof(Pawn_JobTracker.EndCurrentJob))]
    public static class JobTrackerEnd
    {
        static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            Pawn pawn = (Pawn)JobTrackerPatch.pawnField.GetValue(__instance);

            if (Multiplayer.client == null) return;

            Log.Message(Multiplayer.client.username + " end job: " + pawn + " " + __instance.curJob + " " + condition);

            MultiplayerThingComp comp = pawn.GetComp<MultiplayerThingComp>();
            if (comp.actualJob != null)
                Log.Message("actual job: " + comp.actualJob);
        }
    }

    [HarmonyPatch(typeof(JobDriver))]
    [HarmonyPatch(nameof(JobDriver.DriverTick))]
    public static class JobDriverPatch
    {
        static FieldInfo startField = typeof(JobDriver).GetField("startTick", BindingFlags.Instance | BindingFlags.NonPublic);

        static bool Prefix(JobDriver __instance)
        {
            if (__instance.job.expiryInterval != -2) return true;

            __instance.job.startTick = Find.TickManager.TicksGame;
            startField.SetValue(__instance, Find.TickManager.TicksGame);

            return false;
        }
    }

    [HarmonyPatch(typeof(Pawn_JobTracker))]
    [HarmonyPatch("CleanupCurrentJob")]
    public static class Clear
    {
        static void Prefix(Pawn_JobTracker __instance, JobCondition condition)
        {
            Pawn pawn = (Pawn)JobTrackerPatch.pawnField.GetValue(__instance);

            if (__instance.curJob != null && __instance.curJob.expiryInterval == -2)
                Log.Warning(Multiplayer.username + " cleanup " + JobTrackerPatch.dontHandle + " " + __instance.curJob + " " + condition + " " + pawn + " " + __instance.curJob.expiryInterval);
        }
    }

    [HarmonyPatch(typeof(Thing))]
    [HarmonyPatch(nameof(Thing.SpawnSetup))]
    public static class ThingSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            if (__instance.def.HasThingIDNumber)
                ScribeUtil.crossRefs.RegisterLoaded(__instance);
        }
    }

    [HarmonyPatch(typeof(Thing))]
    [HarmonyPatch(nameof(Thing.DeSpawn))]
    public static class ThingDeSpawnPatch
    {
        static void Postfix(Thing __instance)
        {
            ScribeUtil.crossRefs.Unregister(__instance);
        }
    }

    [HarmonyPatch(typeof(WorldObject))]
    [HarmonyPatch(nameof(WorldObject.SpawnSetup))]
    public static class WorldObjectSpawnPatch
    {
        static void Postfix(WorldObject __instance)
        {
            ScribeUtil.crossRefs.RegisterLoaded(__instance);
        }
    }

    [HarmonyPatch(typeof(WorldObject))]
    [HarmonyPatch(nameof(WorldObject.PostRemove))]
    public static class WorldObjectRemovePatch
    {
        static void Postfix(WorldObject __instance)
        {
            ScribeUtil.crossRefs.Unregister(__instance);
        }
    }

    [HarmonyPatch(typeof(FactionManager))]
    [HarmonyPatch(nameof(FactionManager.Add))]
    public static class FactionAddPatch
    {
        static void Postfix(Faction faction)
        {
            ScribeUtil.crossRefs.RegisterLoaded(faction);

            foreach (Map map in Find.Maps)
                map.pawnDestinationReservationManager.RegisterFaction(faction);
        }
    }

    [HarmonyPatch(typeof(Game))]
    [HarmonyPatch(nameof(Game.AddMap))]
    public static class AddMapPatch
    {
        static void Postfix(Map map)
        {
            ScribeUtil.crossRefs.RegisterLoaded(map);
        }
    }

    [HarmonyPatch(typeof(MapDeiniter))]
    [HarmonyPatch(nameof(MapDeiniter.Deinit))]
    public static class DeinitMapPatch
    {
        static void Prefix(Map map)
        {
            ScribeUtil.crossRefs.UnregisterFromMap(map);
        }
    }

    [HarmonyPatch(typeof(MemoryUtility))]
    [HarmonyPatch(nameof(MemoryUtility.ClearAllMapsAndWorld))]
    public static class ClearAllPatch
    {
        static void Postfix()
        {
            ScribeUtil.crossRefs = null;
            Log.Message("Removed all cross refs");
        }
    }

    [HarmonyPatch(typeof(Game))]
    [HarmonyPatch("FillComponents")]
    public static class FillComponentsPatch
    {
        static void Postfix()
        {
            ScribeUtil.crossRefs = new CrossRefSupply();
            Log.Message("New cross refs");
        }
    }

    [HarmonyPatch(typeof(LoadedObjectDirectory))]
    [HarmonyPatch(nameof(LoadedObjectDirectory.RegisterLoaded))]
    public static class LoadedObjectsRegisterPatch
    {
        static bool Prefix(LoadedObjectDirectory __instance, ILoadReferenceable reffable)
        {
            if (!(__instance is CrossRefSupply)) return true;
            if (reffable == null) return false;

            string key = reffable.GetUniqueLoadID();
            if (ScribeUtil.crossRefs.GetDict().ContainsKey(key)) return false;

            if (Scribe.mode == LoadSaveMode.ResolvingCrossRefs)
                ScribeUtil.crossRefs.tempKeys.Add(key);

            ScribeUtil.crossRefs.GetDict().Add(key, reffable);

            return false;
        }
    }

    [HarmonyPatch(typeof(LoadedObjectDirectory))]
    [HarmonyPatch(nameof(LoadedObjectDirectory.Clear))]
    public static class LoadedObjectsClearPatch
    {
        static bool Prefix(LoadedObjectDirectory __instance)
        {
            if (!(__instance is CrossRefSupply)) return true;

            ScribeUtil.crossRefsField.SetValue(Scribe.loader.crossRefs, ScribeUtil.defaultCrossRefs);

            foreach (string temp in ScribeUtil.crossRefs.tempKeys)
                ScribeUtil.crossRefs.Unregister(temp);
            ScribeUtil.crossRefs.tempKeys.Clear();

            return false;
        }
    }

    [HarmonyPatch(typeof(CompressibilityDeciderUtility))]
    [HarmonyPatch(nameof(CompressibilityDeciderUtility.IsSaveCompressible))]
    public static class SaveCompressible
    {
        static void Postfix(ref bool __result)
        {
            if (Multiplayer.savingForEncounter)
                __result = false;
        }
    }

    [HarmonyPatch(typeof(GenSpawn))]
    [HarmonyPatch(nameof(GenSpawn.Spawn))]
    [HarmonyPatch(new Type[] { typeof(Thing), typeof(IntVec3), typeof(Map), typeof(Rot4), typeof(bool) })]
    public static class GenSpawnPatch
    {
        public static bool spawningThing;

        static bool Prefix(Thing newThing, IntVec3 loc, Map map, Rot4 rot)
        {
            if (Multiplayer.client == null || Current.ProgramState == ProgramState.MapInitializing || spawningThing) return true;

            if (newThing is Blueprint)
            {
                byte[] data = ScribeUtil.WriteSingle(new Info(newThing, loc, map, rot));
                Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.SPAWN_THING, data });
                return false;
            }

            return true;
        }

        public class Info : AttributedExposable
        {
            [ExposeDeep]
            public Thing thing;
            [ExposeValue]
            public IntVec3 loc;
            [ExposeReference]
            public Map map;
            [ExposeValue]
            public Rot4 rot;

            public Info() { }

            public Info(Thing thing, IntVec3 loc, Map map, Rot4 rot)
            {
                this.thing = thing;
                this.loc = loc;
                this.map = map;
                this.rot = rot;
            }
        }
    }

    [HarmonyPatch(typeof(UIRoot_Play))]
    [HarmonyPatch(nameof(UIRoot_Play.UIRootOnGUI))]
    public static class OnGuiPatch
    {
        static bool Prefix()
        {
            if (OnMainThread.currentLongAction == null) return true;
            if (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseUp || Event.current.type == EventType.KeyDown || Event.current.type == EventType.KeyUp || Event.current.type == EventType.ScrollWheel) return false;
            return true;
        }

        static void Postfix()
        {
            if (OnMainThread.currentLongAction == null) return;

            string text = OnMainThread.GetActionsText();
            Vector2 size = Text.CalcSize(text);
            int width = Math.Max(240, (int)size.x + 40);
            int height = Math.Max(50, (int)size.y + 20);
            Rect rect = new Rect((UI.screenWidth - width) / 2, (UI.screenHeight - height) / 2, width, height);
            rect.Rounded();

            Widgets.DrawShadowAround(rect);
            Widgets.DrawWindowBackground(rect);
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(rect, text);
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    [HarmonyPatch(typeof(Pawn))]
    [HarmonyPatch(nameof(Pawn.Tick))]
    public static class PawnContext
    {
        public static Pawn current;

        static void Prefix(Pawn __instance)
        {
            if (Multiplayer.client == null) return;

            current = __instance;

            if (current.Faction == null || current.Map == null) return;

            FactionContext.Set(__instance.Map, __instance.Faction);

            Faction.OfPlayer.def = FactionDefOf.Outlander;
            __instance.Faction.def = FactionDefOf.PlayerColony;
        }

        static void Postfix(Pawn __instance)
        {
            if (Multiplayer.client == null) return;

            current = null;

            if (__instance.Map == null || __instance.Faction == null) return;

            __instance.Faction.def = FactionDefOf.Outlander;
            Find.World.GetComponent<MultiplayerWorldComp>().playerFactions.GetValueSafe(Multiplayer.username).def = FactionDefOf.PlayerColony;

            FactionContext.Reset(__instance.Map);
        }
    }

    [HarmonyPatch(typeof(GameEnder))]
    [HarmonyPatch(nameof(GameEnder.CheckOrUpdateGameOver))]
    public static class GameEnderPatch
    {
        static bool Prefix()
        {
            return false;
        }
    }

    [HarmonyPatch(typeof(UniqueIDsManager))]
    [HarmonyPatch("GetNextID")]
    public static class UniqueIdsPatch
    {
        static void Postfix(ref int __result)
        {
            if (Multiplayer.mainBlock == null) return;
            __result = Multiplayer.mainBlock.NextId();
        }
    }

    [HarmonyPatch(typeof(DesignationManager))]
    [HarmonyPatch(nameof(DesignationManager.AddDesignation))]
    public static class DesignationAddPatch
    {
        static bool Prefix(DesignationManager __instance, Designation newDes)
        {
            if (Multiplayer.client == null) return true;
            if (!ProcessDesignatorsPatch.processingDesignators && !DrawGizmosPatch.drawingGizmos) return true;

            byte[] extra = Server.GetBytes(0, __instance.map.GetUniqueLoadID(), Faction.OfPlayer.GetUniqueLoadID(), ScribeUtil.WriteSingle(newDes));
            Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.DESIGNATION, extra });

            return false;
        }
    }

    [HarmonyPatch(typeof(DesignationManager))]
    [HarmonyPatch(nameof(DesignationManager.RemoveDesignation))]
    public static class DesignationRemovePatch
    {
        static bool Prefix(DesignationManager __instance, Designation des)
        {
            if (Multiplayer.client == null) return true;
            if (!ProcessDesignatorsPatch.processingDesignators && !DrawGizmosPatch.drawingGizmos) return true;

            byte[] extra = Server.GetBytes(1, __instance.map.GetUniqueLoadID(), Faction.OfPlayer.GetUniqueLoadID(), ScribeUtil.WriteSingle(des));
            Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.DESIGNATION, extra });

            return false;
        }
    }

    [HarmonyPatch(typeof(DesignationManager))]
    [HarmonyPatch(nameof(DesignationManager.RemoveAllDesignationsOn))]
    public static class DesignationRemoveThingPatch
    {
        public static bool dontHandle;

        static bool Prefix(DesignationManager __instance, Thing t, bool standardCanceling)
        {
            if (Multiplayer.client == null || dontHandle) return true;
            if (!ProcessDesignatorsPatch.processingDesignators && !DrawGizmosPatch.drawingGizmos) return true;

            byte[] extra = Server.GetBytes(2, __instance.map.GetUniqueLoadID(), Faction.OfPlayer.GetUniqueLoadID(), t.GetUniqueLoadID(), standardCanceling);
            Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.DESIGNATION, extra });

            return false;
        }
    }

    [HarmonyPatch(typeof(GizmoGridDrawer))]
    [HarmonyPatch(nameof(GizmoGridDrawer.DrawGizmoGrid))]
    public static class DrawGizmosPatch
    {
        public static bool drawingGizmos;

        static void Prefix()
        {
            drawingGizmos = true;
        }

        static void Postfix()
        {
            drawingGizmos = false;
        }
    }

    [HarmonyPatch(typeof(DesignatorManager))]
    [HarmonyPatch(nameof(DesignatorManager.ProcessInputEvents))]
    public static class ProcessDesignatorsPatch
    {
        public static bool processingDesignators;

        static void Prefix()
        {
            processingDesignators = true;
        }

        static void Postfix()
        {
            processingDesignators = false;
        }
    }

    [HarmonyPatch(typeof(Pawn_DraftController))]
    [HarmonyPatch(nameof(Pawn_DraftController.Drafted), PropertyMethod.Setter)]
    public static class DraftSetPatch
    {
        public static bool dontHandle;

        static bool Prefix(Pawn_DraftController __instance, bool value)
        {
            if (Multiplayer.client == null || dontHandle) return true;
            if (!DrawGizmosPatch.drawingGizmos) return true;

            byte[] extra = Server.GetBytes(__instance.pawn.Map.GetUniqueLoadID(), __instance.pawn.GetUniqueLoadID(), value);
            Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.DRAFT, extra });

            return false;
        }
    }

    [HarmonyPatch(typeof(PawnComponentsUtility))]
    [HarmonyPatch(nameof(PawnComponentsUtility.AddAndRemoveDynamicComponents))]
    public static class AddAndRemoveCompsPatch
    {
        static void Prefix(Pawn pawn)
        {
            if (Multiplayer.client == null || pawn.Faction == null) return;

            Faction.OfPlayer.def = FactionDefOf.Outlander;
            pawn.Faction.def = FactionDefOf.PlayerColony;
        }

        static void Postfix(Pawn pawn, bool actAsIfSpawned)
        {
            if (Multiplayer.client == null || pawn.Faction == null) return;

            pawn.Faction.def = FactionDefOf.Outlander;
            Find.World.GetComponent<MultiplayerWorldComp>().playerFactions.GetValueSafe(Multiplayer.username).def = FactionDefOf.PlayerColony;
        }
    }

    [HarmonyPatch(typeof(ThingIDMaker))]
    [HarmonyPatch(nameof(ThingIDMaker.GiveIDTo))]
    public static class GiveThingId
    {
        static void Postfix(Thing t)
        {
            if (PawnContext.current != null && PawnContext.current.Map != null)
            {
                IdBlock block = PawnContext.current.Map.GetComponent<MultiplayerMapComp>().encounterIdBlock;
                if (block != null && !(t is Mote))
                {
                    Log.Message(Multiplayer.client.username + ": new thing id pawn " + t + " " + PawnContext.current);
                }
            }
        }
    }

    [HarmonyPatch(typeof(UniqueIDsManager))]
    [HarmonyPatch(nameof(UniqueIDsManager.GetNextThingID))]
    public static class GetNextThingIdPatch
    {
        static void Postfix(ref int __result)
        {
            if (PawnContext.current != null && PawnContext.current.Map != null)
            {
                IdBlock block = PawnContext.current.Map.GetComponent<MultiplayerMapComp>().encounterIdBlock;
                if (block != null)
                {
                    __result = block.NextId();
                }
            }
        }
    }

    [HarmonyPatch(typeof(UniqueIDsManager))]
    [HarmonyPatch(nameof(UniqueIDsManager.GetNextJobID))]
    public static class GetNextJobIdPatch
    {
        static void Postfix(ref int __result)
        {
            if (PawnContext.current != null && PawnContext.current.Map != null)
            {
                IdBlock block = PawnContext.current.Map.GetComponent<MultiplayerMapComp>().encounterIdBlock;
                if (block != null)
                {
                    __result = block.NextId();
                }
            }
        }
    }

    [HarmonyPatch(typeof(ThingWithComps))]
    [HarmonyPatch(nameof(ThingWithComps.InitializeComps))]
    public static class InitCompsPatch
    {
        static void Postfix(ThingWithComps __instance)
        {
            MultiplayerThingComp comp = new MultiplayerThingComp() { parent = __instance };
            __instance.AllComps.Add(comp);
            comp.Initialize(null);
        }
    }

    [HarmonyPatch(typeof(Building))]
    [HarmonyPatch(nameof(Building.GetGizmos))]
    public static class GetGizmos
    {
        static void Postfix(Building __instance, ref IEnumerable<Gizmo> __result)
        {
            __result = __result.Concat(new Command_Action
            {
                defaultLabel = "Set faction",
                action = () =>
                {
                    __instance.SetFaction(Faction.OfSpacerHostile);
                }
            });
        }
    }

    [HarmonyPatch(typeof(WorldObject))]
    [HarmonyPatch(nameof(WorldObject.GetGizmos))]
    public static class WorldObjectGizmos
    {
        static void Postfix(ref IEnumerable<Gizmo> __result)
        {
            __result = __result.Concat(new Command_Action
            {
                defaultLabel = "Jump to",
                action = () =>
                {
                    Find.WindowStack.Add(new Dialog_JumpTo(i =>
                    {
                        Find.WorldCameraDriver.JumpTo(i);
                        Find.WorldSelector.selectedTile = i;
                    }));
                }
            });
        }
    }

    [HarmonyPatch(typeof(ListerHaulables))]
    [HarmonyPatch(nameof(ListerHaulables.ListerHaulablesTick))]
    public static class HaulablesTickPatch
    {
        static bool Prefix()
        {
            return Multiplayer.client == null || MultiplayerMapComp.tickingFactions;
        }
    }

    [HarmonyPatch(typeof(ResourceCounter))]
    [HarmonyPatch(nameof(ResourceCounter.ResourceCounterTick))]
    public static class ResourcesTickPatch
    {
        static bool Prefix()
        {
            return Multiplayer.client == null || MultiplayerMapComp.tickingFactions;
        }
    }

    [HarmonyPatch(typeof(CompForbiddable))]
    [HarmonyPatch(nameof(CompForbiddable.PostSplitOff))]
    public static class ForbiddableSplitPatch
    {
        static bool Prefix(CompForbiddable __instance, Thing piece)
        {
            if (Multiplayer.client == null) return true;
            piece.SetForbidden(__instance.parent.IsForbidden(Faction.OfPlayer));
            return false;
        }
    }

    [HarmonyPatch(typeof(ForbidUtility))]
    [HarmonyPatch(nameof(ForbidUtility.IsForbidden))]
    [HarmonyPatch(new Type[] { typeof(Thing), typeof(Faction) })]
    public static class IsForbiddenPatch
    {
        static void Postfix(Thing t, ref bool __result)
        {
            if (Multiplayer.client == null || Current.ProgramState != ProgramState.Playing) return;

            ThingWithComps thing = t as ThingWithComps;
            if (thing == null) return;

            MultiplayerThingComp comp = thing.GetComp<MultiplayerThingComp>();
            CompForbiddable forbiddable = thing.GetComp<CompForbiddable>();
            if (comp == null || forbiddable == null) return;

            string factionId = FactionContext.Current.GetUniqueLoadID();
            if (comp.factionForbidden.TryGetValue(factionId, out bool forbidden))
                __result = forbidden;
            else if (!t.Spawned)
                __result = false;
            else if (factionId == t.Map.ParentFaction.GetUniqueLoadID())
                __result = forbiddable.Forbidden;
            else
                __result = true;
        }
    }

    [HarmonyPatch(typeof(CompForbiddable))]
    [HarmonyPatch(nameof(CompForbiddable.Forbidden), PropertyMethod.Setter)]
    public static class ForbidSetPatch
    {
        private static FieldInfo forbiddenField = typeof(CompForbiddable).GetField("forbiddenInt", BindingFlags.NonPublic | BindingFlags.Instance);

        static bool Prefix(CompForbiddable __instance, bool value)
        {
            if (Multiplayer.client == null) return true;

            ThingWithComps thing = __instance.parent;
            MultiplayerThingComp comp = thing.GetComp<MultiplayerThingComp>();

            string factionId = FactionContext.Current.GetUniqueLoadID();
            if (comp.factionForbidden.TryGetValue(factionId, out bool forbidden) && forbidden == value) return false;

            if (DrawGizmosPatch.drawingGizmos || ProcessDesignatorsPatch.processingDesignators)
            {
                byte[] extra = Server.GetBytes(thing.Map.GetUniqueLoadID(), thing.GetUniqueLoadID(), Faction.OfPlayer.GetUniqueLoadID(), value);
                Multiplayer.client.Send(Packets.CLIENT_ACTION_REQUEST, new object[] { ServerAction.FORBID, extra });
                return false;
            }

            if (factionId == Faction.OfPlayer.GetUniqueLoadID())
                forbiddenField.SetValue(__instance, value);

            comp.factionForbidden[factionId] = value;

            if (thing.Spawned)
            {
                if (value)
                    thing.Map.listerHaulables.Notify_Forbidden(thing);
                else
                    thing.Map.listerHaulables.Notify_Unforbidden(thing);
            }

            return false;
        }
    }

}