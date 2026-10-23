# quivr-server

A replacement backend for **[QuiVr](https://store.steampowered.com/app/489380/QuiVr/)**, the VR archery game by Alvios, Inc.

QuiVr keeps every player's progress (relic fragments, items, outfit, titles) on an online server. That server is gone, so the game now shows

> Error Saving — User Data Corrupted, please restart

whenever you earn something, and nothing is ever saved. Your local files are fine; there is just nobody to save to.

This project brings that server back: a [Parse Server](https://parseplatform.org/) with the game's database layout, its four server-side functions and the original item catalog, plus a small patcher that points the game at it.

> Not affiliated with Alvios, Inc. Old accounts and items from the official server cannot be recovered — everyone starts fresh.

## What works

- Login (automatic, via your Steam ID), saving relic fragments, items, outfit, titles, stats
- Random item rewards after a run, with the original 41-item loot table
- Reforging / disenchanting, Hallowed and Primordial rolls
- Achievement and leaderboard bows (handled by the game itself)
- Multiplayer (runs on Photon, which was never down)

Not included: the website leaderboards (quivr.net) and the PvP matchmaking queue.

## Quick start (play on your own PC)

Requirements: [Docker](https://docs.docker.com/get-docker/) with Compose, and the [.NET 8 SDK](https://dotnet.microsoft.com/download) for the patcher.

```sh
git clone <this repo> quivr-server
cd quivr-server

# 1. Start the server (listens on 127.0.0.1:4040, restarts with Docker)
docker compose up -d

# 2. Point the game at it (finds the Steam install automatically)
dotnet run --project patcher
```

Then start QuiVr. The "Server Offline" message should be gone, and items/fragments stay after a restart.

The patcher keeps the original files as `*.orig` next to them. To undo:

```sh
dotnet run --project patcher -- --restore
```

Steam's *Verify integrity of game files* also restores the original files (then just run the patcher again).

If the game is installed somewhere unusual, pass the folder: `dotnet run --project patcher -- "/path/to/QuiVr"`.

## Hosting a server for other players

1. Copy `.env.example` to `.env`, set `BIND_ADDRESS=0.0.0.0` and `PUBLIC_SERVER_URL` to the address players will use.
2. `docker compose up -d`
3. Players patch their game with your address:
   ```sh
   dotnet run --project patcher -- --url http://your-host:4040/parse/
   ```

The game talks plain HTTP and sends the Steam ID as both username and password (that is how the original game was built), so treat accounts on a public server as low-security. Putting the server behind an HTTPS reverse proxy and patching with an `https://` URL works too.

## How it works

The game uses the Parse SDK. Its connection settings are compiled into `Assembly-CSharp-firstpass.dll` (`ParseInitBehavior`: app ID `QuiVr`, server `http://34.198.149.161:4040/parse/`), and a second "is the server online" check reads the address from a scene asset. The patcher (`patcher/`, using Mono.Cecil) replaces that URL literal and the online-check address; nothing else in the game is changed.

The server (`server/`) is Parse Server with:

| Piece | Purpose |
|---|---|
| `_User` fields | `Resource`, `Inventory`, `Outfit`, `PlayerTitles`, `Wings`, `EquippedCosmetics`, stats — written by the game directly |
| `Items` class | loot table, read by the game; seeded from `server/data/items.json` |
| `VersionInfo`, `ArcadeUser` classes | session counter and VR-arcade profiles, written by the game |
| Config | `ServerMessage`, `BetaMessage`, `WeeklySeed`, `RespawnTime`, `InvincibleTime`, `DiffMult` |
| `AddItem` | append item(s) to the inventory string |
| `ReplaceItemV3` | reforge (replace) or disenchant (remove) an item |
| `GetItemV2` | pick a random item of a rarity, preferring unowned items in the slot you have least of |
| `FixInventories` | drop malformed / duplicate inventory entries |

A `beforeSave` hook also works around a client bug: the game creates the `NxtLgdr` (next-legendary counter) field as an array but reads it as a number, which would break item rewards from the second run on.

### Where the item data comes from

- `server/data/items.json` is the drop table `Assets/Resources/ItemDropData.asset` from the developer's public [QuiVr-PSVR source](https://github.com/KeithKirby/QuiVr-PSVR), plus *Tenser's Ocular Jacket*, which is defined in the Steam build's `resources.assets` but was added after that table.
- *Battle Shell* appears on an armory rack in the Steam build, but its effect is not recorded anywhere, so it is not in the loot table.
- Bows were never random drops; they are achievement / leaderboard / Early Access rewards granted by the client.

## Operating

```sh
docker compose logs -f server     # server log
docker compose down               # stop (data is kept in the mongo-data volume)
docker compose down -v            # stop and delete all player data
```

Change a config value (e.g. show a message to players; it must be longer than 5 characters):

```sh
docker compose exec -T mongo mongosh quivr --quiet \
  --eval 'db.getCollection("_GlobalConfig").updateOne({_id:1},{$set:{"params.ServerMessage":"Welcome back, archers!"}})'
```

## License

MIT, see [LICENSE](LICENSE). QuiVr and its assets belong to Alvios, Inc.; this repository contains no game files.
