QuiVr Revival
=============

QuiVr's online server is gone, so the game can no longer save your items,
relic fragments or leaderboard scores ("User Data Corrupted, please restart").
QuiVr Revival brings that back with a small server that runs on your own PC
(or a friend's), plus a one-time change to the game so it uses it.


PLAY (Windows)
--------------
1. Double-click QuiVrRevival.exe.
   If Windows shows "Windows protected your PC": click "More info", then "Run anyway".
2. Press Enter to choose "1  Play on this PC".
3. Start QuiVr from Steam as usual. That's it - the server starts and stops with the game.

Your progress is stored in %APPDATA%\QuiVrRevival.


PLAY (Linux / Steam Deck, Proton)
---------------------------------
1. Run ./QuiVrRevival in a terminal and choose 1.
   (Keep QuiVrRevival.exe next to it - the game starts that one itself under Proton.)
2. Start QuiVr from Steam as usual.

Your progress is stored inside the game's Proton prefix:
  steamapps/compatdata/489380/pfx/drive_c/users/steamuser/AppData/Roaming/QuiVrRevival


PLAY TOGETHER
-------------
Everyone who should share items and leaderboards uses the same server:
- One person runs QuiVrRevival.exe and chooses "3  Host a server for others".
  The window shows the address to give out. Keep it open while people play.
  For players outside your home network, forward TCP port 4040 on your router.
- Everyone else runs QuiVrRevival.exe and chooses "2  Play on someone's server".
Multiplayer matches themselves work without this (they never went down).


UNDO
----
Run QuiVrRevival.exe and choose "4  Uninstall", or use Steam's
"Verify integrity of game files" for QuiVr. Your saved progress is kept.
After Steam updates or verifies the game, just choose 1 (or 2) again.


Notes
-----
- Accounts from the old official server are gone; everyone starts fresh.
- Not affiliated with Alvios, Inc. Source code, help and updates: the project
  page you downloaded this from.
