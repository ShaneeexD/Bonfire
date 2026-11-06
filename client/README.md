# Bonfire client

A Firebase-style **realtime JSON database** client for game mods and hobby projects. It talks to a
Bonfire server, which you deploy for free on Cloudflare's Workers plan. The server, a step-by-step
guide and a Windows database editor are in the project repository (see "Source repository" on this
package's page).

- One assembly with **no dependencies**.
- `netstandard2.0` for Unity, BepInEx and MelonLoader mods (Mono) and .NET Framework 4.6.1+; `net10.0`
  for modern .NET.
- Live subscriptions with minimal patches, and optimistic writes that roll back if refused.
- Atomic multi-path updates, presence via `OnDisconnect()`, and auto-reconnect with resync.
- An offline queue, a disk cache, and graceful handling of the free plan's daily quotas.

## Install

```bash
dotnet add package Bonfire
```

For mods that can't use NuGet, download `Bonfire-Client-<version>.zip` from the repository's
Releases page and reference `netstandard2.0/Bonfire.dll`.

## Quick start

```csharp
using Bonfire;

var db = new BonfireClient("wss://bonfire.<you>.workers.dev", room: "global", key: "<read key>",
    new BonfireOptions { ClientToken = BonfireClient.LoadOrCreateToken("bonfire.token") });

// Current value now, then every change.
db.Ref("players").OnValue(snap =>
{
    foreach (var p in snap.Children)
        Console.WriteLine($"{p.Child("name").GetValue<string>()}: {p.Child("score").GetValue<int>()}");
});

// Writes show up locally at once; the task completes when the server confirms.
var me = db.Ref($"players/{db.ClientId}");
await me.SetAsync(new { name = "Ann", score = 0 });
await me.OnDisconnect().Remove();   // the server removes it when this client leaves
```

## Unity and other single-threaded engines

Create the client with `EventDispatch = EventDispatchMode.Queued` and call `db.Dispatch()` from
`Update()`. All callbacks then run on the main thread.

## API at a glance

| Member | Purpose |
|---|---|
| `db.Ref(path)`, `.Child(path)`, `.Parent`, `.Push()` | Locations in the JSON document |
| `.OnValue(onValue, onError)` | Subscribe; dispose the result to stop |
| `.SetAsync(value)`, `.UpdateAsync(dict)`, `.RemoveAsync()` | Write (multi-path updates are atomic) |
| `.GetAsync()` | One-off read |
| `.OnDisconnect().Set/Update/Remove/Cancel` | Writes the server applies when you disconnect |
| `DataSnapshot.Value`, `.GetValue<T>()`, `.Child()`, `.Children`, `.Exists`, `.Rev` | Reading results |
| `db.State`, `db.StateChanged`, `db.ErrorOccurred`, `db.WritesBlockedUntil` | Connection and quota status |
| `BonfireOptions` | Token, write batching interval, dispatch mode, disk cache, serializer, reconnect timing |
| `BonfireOptions.IdentityToken` / `IdentityTokenProvider`, `db.IsVerified` | Verified player ids from your backend (`$uid` in server rules) |
| `BonfireIdentity.Create(secret, playerId, validFor)` | Sign an identity token. **Backends only**: never ship the secret in a mod |

Refused writes throw `BonfireException` with a `Code` such as `permission_denied`, `rate_limited` or
`write_quota`.

## Licence

MIT.
