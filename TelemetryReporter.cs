using System;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Newtonsoft.Json;

namespace ZSlayerHeadlessTelemetry;

public class TelemetryReporter : IDisposable
{
    private readonly string _baseUrl;
    public string BaseUrl => _baseUrl;
    private readonly HttpClient _http;
    public HttpClient Http => _http;
    private readonly ConcurrentQueue<QueuedPost> _queue = new();
    private readonly Thread _drainThread;
    private readonly CancellationTokenSource _cts = new();
    private const int MaxQueueSize = 64;
    private int _consecutiveFailures;
    private const int MaxConsecutiveFailures = 10;
    private int _dropCount;

    public TelemetryReporter(string baseUrl)
    {
        _baseUrl = baseUrl;

        // Ignore SSL errors (SPT uses self-signed certs)
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true
        };
        _http = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        // Background thread to drain the queue
        _drainThread = new Thread(DrainLoop)
        {
            IsBackground = true,
            Name = "ZSlayer-Telemetry"
        };
        _drainThread.Start();
    }

    /// <summary>
    /// Enqueue a POST to be sent on the background thread. Never blocks the game thread.
    /// </summary>
    public void Post<T>(string endpoint, T data)
    {
        // If too many consecutive failures, skip to avoid log spam / wasted work.
        // Resets on any successful send — self-healing if server comes back.
        if (_consecutiveFailures >= MaxConsecutiveFailures) return;

        // Ring buffer — drop oldest if full
        while (_queue.Count >= MaxQueueSize)
        {
            _queue.TryDequeue(out _);
            Interlocked.Increment(ref _dropCount);
        }

        try
        {
            var json = JsonConvert.SerializeObject(data);
            _queue.Enqueue(new QueuedPost
            {
                Url = $"{_baseUrl}/{endpoint}",
                Json = json
            });
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZSlayerHQ] Failed to serialize telemetry: {ex.Message}");
        }
    }

    /// <summary>
    /// Async ping to check if server is reachable. Non-blocking.
    /// No longer gates all future sends — consecutive failure counter handles that.
    /// </summary>
    public async void PingAsync()
    {
        try
        {
            var response = await _http.GetAsync($"{_baseUrl}/current");
            Plugin.Log.LogInfo($"[ZSlayerHQ] Server ping: {response.StatusCode}");
        }
        catch (Exception ex)
        {
            Plugin.Log.LogWarning($"[ZSlayerHQ] Server ping failed (will retry on next send): {ex.Message}");
        }
    }

    private void DrainLoop()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                if (_queue.TryDequeue(out var item))
                {
                    SendSync(item);
                }
                else
                {
                    Thread.Sleep(50); // Idle wait
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[ZSlayerHQ] Drain error: {ex.Message}");
                Thread.Sleep(1000);
            }
        }
    }

    private void SendSync(QueuedPost item)
    {
        try
        {
            var content = new StringContent(item.Json, Encoding.UTF8, "application/json");
            var task = _http.PostAsync(item.Url, content);
            task.Wait(3000); // 3s timeout

            if (task.IsCompleted && !task.Result.IsSuccessStatusCode)
            {
                Plugin.Log.LogWarning($"[ZSlayerHQ] POST {item.Url} → {task.Result.StatusCode}");
            }

            // Success — reset failure counter (self-healing)
            Interlocked.Exchange(ref _consecutiveFailures, 0);
        }
        catch (Exception)
        {
            var fails = Interlocked.Increment(ref _consecutiveFailures);
            if (fails == MaxConsecutiveFailures)
                Plugin.Log.LogWarning($"[ZSlayerHQ] {MaxConsecutiveFailures} consecutive send failures — pausing telemetry until server responds");
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _http.Dispose();
    }

    private struct QueuedPost
    {
        public string Url;
        public string Json;
    }
}
