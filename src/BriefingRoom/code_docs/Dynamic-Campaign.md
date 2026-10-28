# Dynamic Campaign

A turn-based campaign where every mission changes the next one. The theater has a
persistent state (both coalitions' assets and the supply network linking them); each
turn the player flies one mission generated from that state, uploads the `dcs.log`,
and the result plus a simulated day of war (logistics, AI air operations, ground war)
produce the next turn. Single player, not real time.

Code: `src/BriefingRoom/DynamicCampaign/`, UI: `src/CommonGUI/Pages/DynamicCampaign.razor`,
mission script: `Include/Lua/DynamicCampaign/Reporter.lua`, texts:
`Database/Language/DynamicCampaign/*.ini`, tests: `src/Tests/DynamicCampaignTests.cs`.

## Turn loop

```
DynamicCampaignService.Create(template)        DynamicCampaignBuilder: initial layout
        |
        v
GetSorties(state)                              one sortie per operational enemy asset
        |
GenerateMission(state, sortie) -> DCSMission   DynamicCampaignMissionMaker (sets state.Pending)
        |   player flies it in DCS
        v
ParseLog(state, dcs.log) -> DCMissionResult    DynamicCampaignLogParser
        |
ResolveTurn(state, result) / SkipTurn(state)   DynamicCampaignSimulator -> DCTurnReport
        |
        +--> next turn (Turn++, Date += 1-2 days)
```

`DynamicCampaignStore` saves each campaign as `<id>.json` (plus the pending mission as
`<id>.miz`) in `BR_CAMPAIGNS_DIR`, or `DynamicCampaigns/` under the BriefingRoom root.

## State (`DynamicCampaignModels.cs`)

`DynamicCampaignState` holds the campaign template (`.cbrt` INI, reused for every
mission), the situation, the front line, the assets and the turn reports.

| Asset (`DCAssetKind`) | Role | Strength means |
| --- | --- | --- |
| Factory | produces supplies (`FACTORY_OUTPUT`) | production share |
| Depot | stores supplies, feeds front units, SAM sites and rear airbases | damage |
| FrontUnit | brigade on the front line, needs upkeep by convoy | combat power |
| AirDefense | SAM site, covers assets within `AIR_DEFENSE_COVER_KM` | damage (radars) |
| Airbase | air readiness, needs supplies | readiness |
| GroundRoute | convoys factory > depot and depot > front unit | trucks left |
| AirRoute | cargo flights rear airbase > forward airbase | aircraft left |

Strength and supplies are 0-100; an asset under `DESTROYED_THRESHOLD` is destroyed.
Routes are never destroyed: they get cut (`CutTurns`) and recover. All tuning values
are in `DynamicCampaignRules.cs`.

## Weapons and aircraft (`DynamicCampaignInventory.cs`)

Factories, depots and airbases hold `Weapons` by category (air-to-air, anti-radiation,
air-to-ground missiles, guided bombs, bombs, rockets) and airbases hold `Aircraft`:
AI fleets by role (`Fighter`, `Attack`) and the player's aircraft types (from the
template's player flight groups, `state.PlayerAircraft`).

- Production: each factory adds weapons and fractional aircraft per turn, scaled by its
  strength; whole aircraft are ferried to the airbase that has the fewest of that type.
- Distribution: weapons go factory > depot with the convoys, depot > rear airbase by road,
  and to forward airbases with the cargo flights (shares scaled by route strength). Cutting a
  route stops them like the supplies.
- Player missions: the departure airbase is the closest one with enough aircraft of every
  player type; a sortie is unavailable without them or without at least two weapons of the
  categories it can use (`SORTIE_WEAPONS`). The departure stock goes in the briefing and in
  the reporter script, which warns in game when the flight carries more than the stock.
- Results: the reporter snapshots the weapons of the player's groups (player and AI
  wingmen) at the first takeoff, classifies them from `Unit:getAmmo()` descriptors and
  reports what was spent and which aircraft types were lost; both are taken from the
  departure airbase.
- AI: strike sorties need attack aircraft and air-to-ground weapons at an airbase and
  consume them; the defender intercepts with its fighters (spending air-to-air missiles)
  depending on its fighter power, which also sets the enemy/friendly CAP levels of the
  generated missions. Airbase strikes destroy parked aircraft; destroyed depots and
  factories lose their weapons, captured airbases lose everything.

DCS warehouses are not limited: the `.miz` needs internal weapon ids (`wsType`) that the
database does not have, so the campaign stock is enforced when picking missions and
warned about in game, and consumption is read back from DCS.

## Initial layout (`DynamicCampaignBuilder.cs`)

1. Situation: the template one if it has zones for both sides, else a random one of the
   theater. Most situations have no front line: it is computed where the two sides'
   spawn points meet (`CreateFrontLine`).
2. Per coalition, on theater spawn points inside its zone: factories deep in the rear,
   depots 35-100 km behind the front, brigades along the front, SAM sites near depots
   and brigades, and the airbases of its zone closest to the front.
3. Each consumer is supplied by its nearest depot; convoy routes link factory > depot >
   brigade; cargo routes link the rearmost airbase to the forward ones (which only get
   supplies by air).

## Missions (`DynamicCampaignMissionMaker.cs`)

The sortie kind sets the objective (preset, target, behavior, task); the objective uses
the asset position as `CoordinateHint` (routes: a point along the route) and the asset
strength as target count. The campaign also sets the departure airbase (closest friendly
one), the front line (`ContextCustomFrontLine`), the season and date, and the air defense
and air force levels of both sides (from the SAM cover near the target and the airbase
readiness). If generation fails twice, the last attempt adds `SpawnAnywhere`.

After generation the reporter script is appended to `ScriptMissionFeatures` with a
random token, and two briefing remarks explain the campaign impact of the target.

## Results (`Reporter.lua`, `DynamicCampaignLogParser.cs`)

The script tracks objective units (events plus `getByName`/`getLife`) and player
aircraft lost, and writes to `dcs.log` at start, every 60 s, on each objective
completion and at mission end:

```
BRDC|<token>|<sequence>|<missionTime>|<missionComplete 0/1>|<playerAircraftLost>|<dead>/<total>/<complete>,...|W:<category>=<used>;...|L:<type>=<lost>;...
```

The parser keeps the highest sequence of the pending mission's token. No changes to
the DCS installation are needed (`env.info` works in the sanitized environment).

## Turn resolution (`DynamicCampaignSimulator.cs`)

1. Player result: damage proportional to the targets destroyed; convoys/cargo cut when
   at least half are destroyed; SAM radars destroyed put the site at 25%; player aircraft
   lost lower the departure airbase readiness.
2. Logistics (both sides): routes recover, factories deliver to depots by convoy, depots
   supply rear airbases, cargo flights supply forward airbases, airbases consume supplies
   (readiness up or down), brigades get upkeep and reinforcements by convoy (attrition
   without), SAM sites are repaired, structures repair slowly.
3. Commander (both sides): consumers of a lost depot are re-routed to the nearest one
   (new convoy route), depots without a factory route get one, lost SAM sites may be
   rebuilt, spare supplies reinforce the weakest brigade.
4. AI air operations (both sides): sorties from airbase readiness, targets weighted
   toward supply routes, depots and brigades (SAM site first when the target is well
   covered); success and losses depend on SAM cover and both sides' readiness.
5. Ground war: each brigade fights the nearest enemy one; the front line moves toward
   the weaker side (up to `FRONT_MAX_SHIFT_KM`), brigades move with it, airbases left on
   the wrong side are captured and other installations overrun.
6. Outcome: a side without factories and depots, or without brigades, loses.

The turn uses its own `Random(seed + turn * 7919)`, so it is reproducible.
