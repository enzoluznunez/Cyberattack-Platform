using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using UnityEngine;

public class MainThread : MonoBehaviour {

    private static MainThread _instance;
    private static readonly ConcurrentQueue<Action> Queue = new ConcurrentQueue<Action>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap() => Spawn.Once(ref _instance);

    public static Task Run(Action action) {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() => {
            try {
                action();
                tcs.SetResult(true);
            }
            catch (Exception e) {
                tcs.SetException(e);
            }
        });
        return tcs.Task;
    }

    private void Update() {
        while (Queue.TryDequeue(out var action)) action();
    }
}
