// FreeSync Unity example: shares this GameObject's position with everyone in the room and shows the others.
//
// Setup: put FreeSync.dll (the netstandard2.0 build) in your mod/plugin folder or Assets/Plugins,
// add this component to a GameObject, and fill in the Worker URL and read key in the Inspector.
//
// Unity APIs are not thread-safe, so the client is created with EventDispatchMode.Queued and
// db.Dispatch() runs all callbacks inside Update() on the main thread.

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using FreeSync;
using UnityEngine;

public class FreeSyncExample : MonoBehaviour
{
    [SerializeField] private string workerUrl = "wss://freesync.your-account.workers.dev";
    [SerializeField] private string room = "global";
    [SerializeField] private string readKey = "public-read-key";
    [SerializeField] private string playerName = "Player";
    [Tooltip("Seconds between position updates. Positions are ephemeral (see the RULES var), so they cost no storage writes.")]
    [SerializeField] private float sendInterval = 0.1f;
    [SerializeField] private GameObject? otherPlayerPrefab = null;

    private FreeSyncClient? _db;
    private IDisposable? _playersListener;
    private float _nextSend;
    private readonly Dictionary<string, GameObject> _others = new Dictionary<string, GameObject>();

    /// <summary>Plain data class for the built-in serializer (public fields, parameterless constructor).</summary>
    [Serializable]
    public class Pos
    {
        public float x, y, z;
    }

    private void Start()
    {
        var dataDir = Path.Combine(Application.persistentDataPath, "freesync");
        _db = new FreeSyncClient(workerUrl, room, readKey, new FreeSyncOptions
        {
            // A secret per-install token gives this player a stable id across restarts (rules use it as $cid).
            ClientToken = FreeSyncClient.LoadOrCreateToken(Path.Combine(dataDir, "token.txt")),
            EventDispatch = EventDispatchMode.Queued,
            CacheDirectory = dataDir, // show last known data immediately on the next start
            Log = msg => Debug.Log(msg),
        });
        _db.StateChanged += OnConnectionState;
        _playersListener = _db.Ref("players").OnValue(OnPlayers, e => Debug.LogWarning($"players: {e.Message}"));
    }

    private void Update()
    {
        if (_db == null) return;
        _db.Dispatch(); // runs FreeSync callbacks here, on the main thread

        if (_db.State == ConnectionState.Connected && Time.time >= _nextSend && transform.hasChanged)
        {
            _nextSend = Time.time + sendInterval;
            transform.hasChanged = false;
            var p = transform.position;
            Fire(_db.Ref($"players/{_db.ClientId}/pos").SetAsync(new Pos { x = p.x, y = p.y, z = p.z }));
        }
    }

    private void OnConnectionState(ConnectionState state)
    {
        Debug.Log($"FreeSync: {state}");
        if (state == ConnectionState.QuotaExceeded)
            Debug.LogWarning("FreeSync: the server's free daily quota is used up; showing last known data until 00:00 UTC.");
        if (state != ConnectionState.Connected || _db == null) return;

        // The server removes presence when we disconnect, so set it again on every (re)connect.
        var me = _db.Ref($"players/{_db.ClientId}");
        Fire(me.OnDisconnect().Remove());
        Fire(me.Child("name").SetAsync(playerName));
    }

    private void OnPlayers(DataSnapshot players)
    {
        var seen = new HashSet<string>();
        foreach (var player in players.Children)
        {
            if (player.Key == _db?.ClientId || player.Key == null) continue;
            seen.Add(player.Key);
            var pos = player.Child("pos").GetValue<Pos>();
            if (pos == null) continue;
            if (!_others.TryGetValue(player.Key, out var go))
            {
                go = otherPlayerPrefab != null ? Instantiate(otherPlayerPrefab) : GameObject.CreatePrimitive(PrimitiveType.Capsule);
                go.name = "Player " + (player.Child("name").GetValue<string>() ?? player.Key);
                _others[player.Key] = go;
            }
            go.transform.position = new Vector3(pos.x, pos.y, pos.z);
        }
        foreach (var key in new List<string>(_others.Keys))
        {
            if (seen.Contains(key)) continue;
            Destroy(_others[key]);
            _others.Remove(key);
        }
    }

    private void OnDestroy()
    {
        _playersListener?.Dispose();
        _db?.Dispose(); // closes the socket; the server then runs our onDisconnect writes
        _db = null;
    }

    /// <summary>Fire-and-forget a FreeSync task, logging failures (for example permission or quota errors).</summary>
    private static void Fire(Task task) =>
        task.ContinueWith(t => Debug.LogWarning($"FreeSync: {t.Exception?.GetBaseException().Message}"), TaskContinuationOptions.OnlyOnFaulted);
}
