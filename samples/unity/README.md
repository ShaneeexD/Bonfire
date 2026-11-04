# Unity example

[`FreeSyncExample.cs`](FreeSyncExample.cs) is a MonoBehaviour that:

- connects to a room with a per-install client token (stable `$cid`);
- sends this GameObject's position to `players/<cid>/pos` (an **ephemeral** path in the example rules,
  so it costs no storage writes);
- shows every other player as a capsule (or your prefab) and removes them when they leave;
- registers `OnDisconnect().Remove()` so the server deletes your player entry if the game closes or crashes;
- runs all callbacks on the main thread through `EventDispatchMode.Queued` + `db.Dispatch()` in `Update()`.

## Using it

1. Build the client: `dotnet build client/FreeSync -c Release`. Take
   `client/FreeSync/bin/Release/netstandard2.0/FreeSync.dll`.
2. **Unity project:** put the DLL in `Assets/Plugins/` and the script anywhere under `Assets/`.
   **BepInEx / MelonLoader mod:** reference the DLL from your mod project and ship it next to your mod
   DLL in the plugins/Mods folder.
3. Add the component to a GameObject. In the Inspector, set the Worker URL (`wss://...workers.dev`),
   room and **read key**.

## Notes

- **Platforms:** desktop and mobile Mono/IL2CPP builds use `System.Net.WebSockets.ClientWebSocket`.
  **WebGL is not supported** (browsers need a JavaScript WebSocket bridge).
- **IL2CPP code stripping:** the built-in serializer uses reflection on your data classes. Keep them with
  `[UnityEngine.Scripting.Preserve]` or a `link.xml`, or use the Newtonsoft adapter
  (`client/adapters/NewtonsoftJsonSerializer.cs`) if your game already ships Newtonsoft.Json.
- `await` works too: Unity's SynchronizationContext resumes continuations on the main thread. Awaiting
  a write never depends on `Dispatch()`.
- The file includes `#nullable enable` because it uses nullable annotations. Remove the `?`s if your
  Unity version's C# compiler complains.

`compile-check/` builds this script against stub `UnityEngine` types and FreeSync's netstandard2.0 build,
to catch API mistakes without Unity installed. It was not run inside the Unity editor.
