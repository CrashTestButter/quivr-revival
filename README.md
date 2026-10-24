# QuiVr Revival

Brings back saving in **[QuiVr](https://store.steampowered.com/app/489380/QuiVr/)**, the VR archery game by Alvios, Inc.

QuiVr kept every player's items, relic fragments and leaderboard scores on an online server. That server is gone, so the game shows

> Error Saving — User Data Corrupted, please restart

and nothing you earn is kept. QuiVr Revival is a small replacement server plus a one-time change to the game so it uses it. You can run it on your own PC (it starts with the game) or share one with friends.

> Not affiliated with Alvios, Inc. Accounts from the official server can't be recovered; everyone starts fresh.

## For players

### Windows

1. Download `QuiVrRevival-…-windows.zip` from [Releases](../../releases) and unzip it anywhere.
2. Double-click **QuiVrRevival.exe**. If Windows says *"Windows protected your PC"*, click *More info* → *Run anyway* (the program isn't code-signed).
3. Press **Enter** (option **1 – Play on this PC**).
4. Start QuiVr from Steam as usual.

That's all. The server starts and stops together with the game, and your progress is stored in `%APPDATA%\QuiVrRevival`.

### Linux (Proton)

1. Download `QuiVrRevival-…-linux.zip`, unzip, run `./QuiVrRevival` in a terminal and choose **1**.
   Keep `QuiVrRevival.exe` next to it — under Proton the game starts that one itself.
2. Start QuiVr from Steam as usual.

Progress is stored in the game's Proton prefix (`steamapps/compatdata/489380/pfx/drive_c/users/steamuser/AppData/Roaming/QuiVrRevival`), so it is deleted if you delete that prefix.

### Playing together

Players who want to share leaderboards (and see each other's scores) use one server:

- **The host** runs QuiVrRevival and chooses **3 – Host a server for others**. The window lists the address to hand out; keep it open while people play. For players outside your home network, forward TCP port **4040** on your router. The host can play too: choose **1** once, and the game uses the running server.
- **Everyone else** chooses **2 – Play on someone's server** and enters that address.

Multiplayer matches themselves never needed this server and work either way.

### Undo / after game updates

Choose **4 – Uninstall**, or use Steam's *Verify integrity of game files*. Your progress is kept. If Steam ever restores the game files, run QuiVrRevival again and choose 1 (or 2).

### What works

- Items, relic fragments, outfit, titles, stats — saved and loaded
- Random item rewards (the original 41-item loot table), reforging, Hallowed/Primordial rolls
- In-game leaderboards and the temporary leaderboard bows
- Not included: the old quivr.net website leaderboards and PvP matchmaking

## Hosting on a Linux server (Docker)

```sh
docker compose up -d        # listens on 127.0.0.1:4040
```

Edit `docker-compose.yml` to `"4040:4040"` to accept other players, who then choose option 2 with your address. Data lives in the `quivr-data` volume. Without Docker: `./QuiVrRevival serve --host 0.0.0.0` (the Linux release binary).

### Server settings

The data folder contains:

| File | |
|---|---|
| `quivr.db` | all player data (single file; back it up while the server is stopped) |
| `game-config.json` | values the game reads: `ServerMessage` (shown in the lobby if longer than 5 characters), `RespawnTime`, `InvincibleTime`, `DiffMult`, … Edited live. |
| `items.json` | optional: put a modified copy of [`items.json`](src/QuiVrRevival/Resources/items.json) here to change the loot table |
| `server.log` | log; set `QUIVR_TRACE=1` to log every request |

`QuiVrRevival serve` options: `--port 4040`, `--host 127.0.0.1`, `--data DIR` (or `QUIVR_PORT`, `QUIVR_HOST`, `QUIVR_DATA`).

**Security note for public servers:** the game logs in with your SteamID as both username *and* password (that is how it was built), and SteamIDs are public. On a shared server, someone determined could log in as another player. The server only lets each account change its own data and scores, but it cannot tell who is really behind an account. Private servers are unaffected.

## How it works

The game uses the 2016 Parse SDK. QuiVr Revival is a single self-contained .NET 8 program that contains both

- **a server** implementing the part of the Parse REST API the game uses (login, user objects, config, the four cloud functions `AddItem`, `ReplaceItemV3`, `GetItemV2` and `FixInventories`, and the `Items`, `VersionInfo`, `ArcadeUser`, `PlayerMovement` and leaderboard classes) on Kestrel, with storage in an embedded LiteDB file, and
- **an installer** that patches `Assembly-CSharp-firstpass.dll` (`ParseInitBehavior`, which hard-codes `http://34.198.149.161:4040/parse/`) and `Assembly-CSharp.dll` (the "server online" check) with Mono.Cecil. Both now call `QuiVrRevival.Hook.ServerUrl()` from a small .NET 3.5 DLL placed next to them. The hook reads `<game>/QuiVrRevival/config.ini` and, for local play, starts `QuiVrRevival.exe serve --parent-pid …` if nothing is listening yet. The server exits when the game does. Originals are kept as `*.orig`.

Workarounds for client bugs live in the server:

- After a failed first login the game never reaches its sign-up code, so unknown players are signed up on login.
- The account's `name` is read unchecked right after login, so new accounts get a placeholder.
- The next-legendary counter `NxtLgdr` is created as an array but read as a number; it's stored as a number.

Why not the Steam Workshop? QuiVr's Workshop support is only an uploader for 3D models that the developer reviewed by hand; the game never loads Workshop content, so a Workshop item can't change anything.

### Item data

`items.json` is the drop table `Assets/Resources/ItemDropData.asset` from the developer's public [QuiVr-PSVR source](https://github.com/KeithKirby/QuiVr-PSVR), plus *Tenser's Ocular Jacket*, which is defined in the Steam build but newer than that table. *Battle Shell* appears on an armory rack in the Steam build but its effect isn't recorded anywhere, so it isn't in the pool. Bows were never random drops; achievements and leaderboards grant them in the client.

## Building

Needs the .NET 8 SDK.

```sh
./build.sh 1.0.0                         # release zips in dist/
dotnet run --project src/QuiVrRevival     # interactive menu
tests/smoke.sh http://127.0.0.1:4040/parse   # replays the game's requests against a running server (needs curl, jq)
```

Releases are built by GitHub Actions when a `v*` tag is pushed (`.github/workflows/release.yml`), which also publishes the Docker image to `ghcr.io`.

Moving from the earlier Parse Server + MongoDB version: export the classes with `mongosh` as EJSON (`{"_User":[…],"FFKDiff":[…],…}`) and run `QuiVrRevival import-parse export.json --data DIR`.

## License

MIT, see [LICENSE](LICENSE). QuiVr and its assets belong to Alvios, Inc.; this repository contains no game files.
